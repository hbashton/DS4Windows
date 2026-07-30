using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseControllerMediaBufferServoTests
    {
        private const long Frequency = 10_000_000;
        private const long SampleInterval = Frequency / 20;

        [TestMethod]
        public void SustainedLowReserveSmoothlyIncreasesCadence()
        {
            var servo = new DualSenseControllerMediaBufferServo(Frequency);
            double previous = 1.0;
            long now = Frequency;
            for (int index = 0; index < 200; index++)
            {
                now += SampleInterval;
                double ratio = servo.Update(50, now, now);
                Assert.IsTrue(ratio >= previous - 1.0e-12,
                    "A persistently low reserve must not slow the media clock.");
                Assert.IsTrue(ratio - previous <= 0.000011,
                    "The cadence correction must slew instead of jumping.");
                previous = ratio;
            }

            Assert.IsTrue(servo.CompletedBuckets >= 8);
            Assert.IsTrue(servo.CurrentRatio > 1.0005);
            Assert.IsTrue(servo.CurrentRatio <=
                DualSenseControllerMediaBufferServo.MaximumRatio);
        }

        [TestMethod]
        public void SustainedHighReserveSmoothlyDecreasesCadence()
        {
            var servo = new DualSenseControllerMediaBufferServo(Frequency);
            long now = Frequency;
            for (int index = 0; index < 200; index++)
            {
                now += SampleInterval;
                servo.Update(96, now, now);
            }

            Assert.IsTrue(servo.CurrentRatio < 0.9995);
            Assert.IsTrue(servo.CurrentRatio >=
                DualSenseControllerMediaBufferServo.MinimumRatio);
        }

        [TestMethod]
        public void DeadbandAndIsolatedHapticsOutliersDoNotMoveCadence()
        {
            var servo = new DualSenseControllerMediaBufferServo(Frequency);
            long now = Frequency;
            for (int index = 0; index < 200; index++)
            {
                now += SampleInterval;
                int level = index % 17 == 0 ? 6 :
                    index % 29 == 0 ? 120 : 68;
                servo.Update(level, now, now);
            }

            Assert.AreEqual(1.0, servo.CurrentRatio, 1.0e-9);
            Assert.AreEqual(68.0, servo.LastMedian, 0.001);
        }

        [TestMethod]
        public void SustainedLevelJumpRelocksAfterDebounce()
        {
            var servo = new DualSenseControllerMediaBufferServo(Frequency);
            long now = Frequency;
            for (int index = 0; index < 30; index++)
            {
                now += SampleInterval;
                servo.Update(68, now, now);
            }
            for (int index = 0; index < 160; index++)
            {
                now += SampleInterval;
                servo.Update(48, now, now);
            }

            Assert.IsTrue(servo.LastMedian <
                DualSenseControllerMediaBufferServo.LowerDeadband);
            Assert.IsTrue(servo.CurrentRatio > 1.0002);
        }

        [TestMethod]
        public void StaleFeedbackReturnsCorrectionTowardNominal()
        {
            var servo = new DualSenseControllerMediaBufferServo(Frequency);
            long now = Frequency;
            for (int index = 0; index < 160; index++)
            {
                now += SampleInterval;
                servo.Update(48, now, now);
            }

            double corrected = servo.CurrentRatio;
            long staleObservation = now;
            for (int index = 0; index < 4; index++)
            {
                now += Frequency;
                servo.Update(64, staleObservation, now);
            }

            Assert.IsTrue(corrected > 1.0);
            Assert.IsTrue(servo.CurrentRatio < corrected);
            Assert.AreEqual(1.0, servo.DesiredRatio, 1.0e-12);
        }

        [TestMethod]
        public void EmpiricalSteadyReserveDoesNotForceAnOverclock()
        {
            var servo = new DualSenseControllerMediaBufferServo(Frequency);
            long now = Frequency;
            for (int index = 0; index < 600; index++)
            {
                now += SampleInterval;
                int level = 68 + index % 7;
                servo.Update(level, now, now);
            }

            Assert.AreEqual(1.0, servo.CurrentRatio, 1.0e-9);
            Assert.IsTrue(servo.LastMedian >= 68.0 &&
                servo.LastMedian <= 74.0);
        }

        [TestMethod]
        public void FreshInvalidSamplesCannotLatchPriorCorrection()
        {
            var servo = new DualSenseControllerMediaBufferServo(Frequency);
            long now = Frequency;
            for (int index = 0; index < 240; index++)
            {
                now += SampleInterval;
                servo.Update(48, now, now);
            }

            double corrected = servo.CurrentRatio;
            for (int index = 0; index < 120; index++)
            {
                now += SampleInterval;
                servo.Update(index % 2 == 0 ? 0 : 127, now, now);
            }

            Assert.IsTrue(corrected > 1.0);
            Assert.IsTrue(servo.CurrentRatio < corrected);
            Assert.AreEqual(1.0, servo.DesiredRatio, 1.0e-12);
        }
    }
}
