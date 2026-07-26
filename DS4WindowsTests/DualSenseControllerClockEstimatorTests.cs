using DS4Windows.InputDevices;
using System.Diagnostics;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseControllerClockEstimatorTests
    {
        [TestMethod]
        public void LongFitRejectsBluetoothArrivalJitter()
        {
            const double expectedRatio = 1.000240;
            var estimator = new DualSenseControllerClockEstimator();
            long host = 10 * Stopwatch.Frequency;
            double exactController = 100_000.0;
            uint controller = (uint)exactController;
            estimator.Observe(controller, host);

            for (int sample = 1; sample <= 8_000; sample++)
            {
                double hostSeconds = sample * 0.004;
                double arrivalJitterSeconds = ((sample * 37) % 17 - 8) *
                    0.000035;
                host = 10 * Stopwatch.Frequency + (long)Math.Round(
                    (hostSeconds + arrivalJitterSeconds) *
                    Stopwatch.Frequency);
                exactController = 100_000.0 + hostSeconds *
                    DualSenseControllerClockEstimator.
                        NominalSensorTicksPerSecond * expectedRatio;
                controller = (uint)Math.Round(exactController);
                estimator.Observe(controller, host);
            }

            Assert.IsTrue(estimator.IsStable);
            Assert.AreEqual(expectedRatio, estimator.Ratio, 0.000005);
        }

        [TestMethod]
        public void TimestampWrapDoesNotResetMeasurement()
        {
            const double expectedRatio = 0.999800;
            var estimator = new DualSenseControllerClockEstimator();
            long hostOrigin = 20 * Stopwatch.Frequency;
            double raw = uint.MaxValue - 1_000_000.0;
            estimator.Observe((uint)raw, hostOrigin);

            for (int sample = 1; sample <= 8_000; sample++)
            {
                double elapsed = sample * 0.004;
                long host = hostOrigin + (long)Math.Round(elapsed *
                    Stopwatch.Frequency);
                ulong advanced = (ulong)Math.Round(elapsed *
                    DualSenseControllerClockEstimator.
                        NominalSensorTicksPerSecond * expectedRatio);
                uint controller = unchecked((uint)((ulong)(uint)raw +
                    advanced));
                estimator.Observe(controller, host);
            }

            Assert.IsTrue(estimator.IsStable);
            Assert.AreEqual(expectedRatio, estimator.Ratio, 0.000005);
        }
    }
}
