using DS4Windows;
using System.Diagnostics;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseSourceClockEstimatorTests
    {
        [TestMethod]
        public void LongFitRejectsBlockCallbackJitter()
        {
            const double sampleRate = 48_000.0;
            const double expectedRatio = 0.999670;
            const int blockFrames = 512;
            var estimator = new DualSenseSourceClockEstimator(sampleRate);
            long hostOrigin = 10 * Stopwatch.Frequency;
            long totalFrames = blockFrames;
            estimator.Observe(totalFrames, hostOrigin);

            for (int callback = 1; callback <= 3_100; callback++)
            {
                totalFrames += blockFrames;
                double elapsed = callback * blockFrames /
                    (sampleRate * expectedRatio);
                double callbackJitter = ((callback * 37) % 19 - 9) *
                    0.000035;
                long host = hostOrigin + (long)Math.Round(
                    (elapsed + callbackJitter) * Stopwatch.Frequency);
                estimator.Observe(totalFrames, host);
            }

            Assert.IsTrue(estimator.IsStable);
            Assert.AreEqual(expectedRatio, estimator.Ratio, 0.000005);
        }

        [TestMethod]
        public void LongHostGapRestartsMeasurementWindow()
        {
            const double sampleRate = 48_000.0;
            const int blockFrames = 512;
            var estimator = new DualSenseSourceClockEstimator(sampleRate);
            long host = 10 * Stopwatch.Frequency;
            long totalFrames = blockFrames;
            estimator.Observe(totalFrames, host);

            for (int callback = 1; callback <= 1_000; callback++)
            {
                totalFrames += blockFrames;
                host += (long)Math.Round(blockFrames / sampleRate *
                    Stopwatch.Frequency);
                estimator.Observe(totalFrames, host);
            }

            host += 3 * Stopwatch.Frequency;
            totalFrames += blockFrames;
            estimator.Observe(totalFrames, host);
            for (int callback = 0; callback < 1_000; callback++)
            {
                totalFrames += blockFrames;
                host += (long)Math.Round(blockFrames / sampleRate *
                    Stopwatch.Frequency);
                estimator.Observe(totalFrames, host);
            }

            Assert.IsFalse(estimator.IsStable);
            Assert.AreEqual(1.0, estimator.Ratio, 0.0);
        }
    }
}
