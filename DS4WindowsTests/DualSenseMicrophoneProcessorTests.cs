using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseMicrophoneProcessorTests
    {
        [TestMethod]
        public void DefaultLevelCannotClipAfterCalibratedMakeup()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = CreateAlternatingFrame(short.MaxValue);

            processor.Process(frame, frame.Length, 128,
                DualSenseMicrophoneNoiseSuppression.Off);

            Assert.IsTrue(MaxAbsolute(frame) <= 29205,
                "The default microphone level must retain limiter headroom.");
            Assert.IsTrue(MaxAbsolute(frame) > 28000,
                "A full-scale input should reach the limiter after calibrated makeup.");
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
        public void MaximumLevelPreservesLegacyTwoTimesGain()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = CreateAlternatingFrame(4000);

            processor.Process(frame, frame.Length, byte.MaxValue,
                DualSenseMicrophoneNoiseSuppression.Off);

            Assert.IsTrue(MaxAbsolute(frame) > 7600,
                "Maximum profile volume must preserve the legacy two-times gain response.");
            Assert.IsTrue(MaxAbsolute(frame) < 8400,
                "Legacy profile gain should remain predictable below the limiter threshold.");
        }

        [TestMethod]
        public void HighPassFilterRejectsSteadyDc()
        {
            using var processor = new DualSenseMicrophoneProcessor();
            short[] frame = new short[DualSenseMicrophoneProcessor.FrameSize];

            for (int pass = 0; pass < 4; pass++)
            {
                Array.Fill(frame, (short)12000);
                processor.Process(frame, frame.Length, 128,
                    DualSenseMicrophoneNoiseSuppression.Off);
            }

            int residual = MaxAbsolute(frame);
            Assert.IsTrue(residual <= 3,
                $"A constant offset should decay to silence across successive frames. Residual={residual}.");
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

            for (int pass = 0; pass < 6; pass++)
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

            int residual = MaxAbsolute(frame);
            Assert.IsTrue(residual <= 6,
                $"Unmuting must resume from continuously advanced filter state. Residual={residual}.");
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
