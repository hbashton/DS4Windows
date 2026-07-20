using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseMicrophoneProcessorTests
    {
        [TestMethod]
        public void DefaultLevelLeavesTwelveDecibelsOfHeadroom()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = CreateAlternatingFrame(short.MaxValue);

            processor.Process(frame, frame.Length, 128,
                DualSenseMicrophoneNoiseSuppression.Off);

            Assert.IsTrue(MaxAbsolute(frame) < 9000,
                "The default microphone level should not approach full scale.");
            Assert.IsTrue(MaxAbsolute(frame) > 7000,
                "The default microphone level should retain useful speech amplitude.");
        }

        [TestMethod]
        public void MaximumLevelCannotClipTheVirtualEndpoint()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = CreateAlternatingFrame(short.MaxValue);

            processor.Process(frame, frame.Length, byte.MaxValue,
                DualSenseMicrophoneNoiseSuppression.Off);

            Assert.IsTrue(MaxAbsolute(frame) <= 29205,
                "The limiter must retain at least one decibel of peak headroom.");
        }

        [TestMethod]
        public void HighPassFilterRejectsSteadyDc()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = new short[DualSenseMicrophoneProcessor.FrameSize];

            for (int pass = 0; pass < 3; pass++)
            {
                Array.Fill(frame, (short)12000);
                processor.Process(frame, frame.Length, 128,
                    DualSenseMicrophoneNoiseSuppression.Off);
            }

            Assert.IsTrue(MaxAbsolute(frame) < 2,
                "A constant offset should decay to silence across successive frames.");
        }

        [TestMethod]
        public void ResetClearsFilterHistory()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = new short[DualSenseMicrophoneProcessor.FrameSize];
            Array.Fill(frame, (short)12000);
            processor.Process(frame, frame.Length, 128,
                DualSenseMicrophoneNoiseSuppression.Off);

            processor.Reset();
            Array.Clear(frame);
            processor.Process(frame, frame.Length, 128,
                DualSenseMicrophoneNoiseSuppression.Off);

            Assert.AreEqual(0, MaxAbsolute(frame));
        }

        [TestMethod]
        public void MutedOutputIsSilentWhileProcessorHistoryContinues()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = new short[DualSenseMicrophoneProcessor.FrameSize];

            for (int pass = 0; pass < 3; pass++)
            {
                Array.Fill(frame, (short)12000);
                processor.Process(frame, frame.Length, 128,
                    DualSenseMicrophoneNoiseSuppression.Off,
                    muteOutput: true);
                Assert.AreEqual(0, MaxAbsolute(frame),
                    "Muted frames must never leak into the virtual endpoint.");
            }

            Array.Fill(frame, (short)12000);
            processor.Process(frame, frame.Length, 128,
                DualSenseMicrophoneNoiseSuppression.Off,
                muteOutput: false);

            Assert.IsTrue(MaxAbsolute(frame) < 2,
                "Unmuting must resume from continuously advanced filter state.");
        }

        [TestMethod]
        public void BalancedModeLoadsPackagedRnnoiseOnX64()
        {
            if (!Environment.Is64BitProcess)
            {
                Assert.Inconclusive("The upstream package does not provide a native x86 RNNoise binary.");
            }

            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = CreateAlternatingFrame(4000);

            processor.Process(frame, frame.Length, 128,
                DualSenseMicrophoneNoiseSuppression.Balanced);

            Assert.IsTrue(processor.NoiseSuppressionAvailable,
                processor.NoiseSuppressionFailure);
        }

        private static short[] CreateAlternatingFrame(short amplitude)
        {
            short[] frame = new short[DualSenseMicrophoneProcessor.FrameSize];
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = i % 2 == 0 ? amplitude : (short)-amplitude;
            }

            return frame;
        }

        private static int MaxAbsolute(short[] samples)
        {
            int result = 0;
            foreach (short sample in samples)
            {
                result = Math.Max(result, Math.Abs((int)sample));
            }

            return result;
        }
    }
}
