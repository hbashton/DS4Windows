using DS4Windows;
using System.Diagnostics;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseDirectPcmBalanceClockServoTests
    {
        private const int ConsumerBlockFrames = 512;
        private const double NominalFramesPerSecond = 48000.0;

        [TestMethod]
        public void ThirtyPpmProductionSurplusLearnsHalfCorrection()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState();

            bool published = RunUntilWindow(servo, state, 30.0);

            Assert.IsTrue(published);
            Assert.AreEqual(30.0, servo.LastMeasuredErrorPpm, 0.1);
            Assert.AreEqual(15.0, servo.TargetTrimPpm, 0.1);
            Assert.AreEqual(1, servo.CompletedWindows);
            Assert.AreEqual(0, servo.RejectedWindows);
        }

        [TestMethod]
        public void ThreePpmDeadbandDoesNotMoveTrim()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(errorPpm: 2.0);

            Assert.IsTrue(RunUntilWindow(servo, state, 30.0));

            Assert.AreEqual(2.0, servo.LastMeasuredErrorPpm, 0.2);
            Assert.AreEqual(0.0, servo.TargetTrimPpm, 0.0);
        }

        [TestMethod]
        public void ImplausibleFiveHundredPpmExcessIsRejected()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(errorPpm: 600.0);

            Assert.IsFalse(RunUntilWindow(servo, state, 30.0));

            Assert.AreEqual(0, servo.CompletedWindows);
            Assert.AreEqual(1, servo.RejectedWindows);
            Assert.AreEqual(0.0, servo.TargetTrimPpm, 0.0);
        }

        [TestMethod]
        public void ConsecutiveWindowsDoNotOverlapAndTrimIsClamped()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(errorPpm: 400.0);

            Assert.IsTrue(RunUntilWindow(servo, state, 30.0));
            Assert.AreEqual(200.0, servo.TargetTrimPpm, 0.2);
            Assert.IsTrue(RunUntilWindow(servo, state, 30.0));

            Assert.AreEqual(2, servo.CompletedWindows);
            Assert.AreEqual(250.0, servo.TargetTrimPpm, 0.0);
        }

        [TestMethod]
        public void AppliedTrimSlewsAtFivePpmPerSecond()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState();
            Assert.IsTrue(RunUntilWindow(servo, state, 30.0));
            Assert.AreEqual(15.0, servo.TargetTrimPpm, 0.1);

            servo.AdvanceAppliedTrim(1.0);
            Assert.AreEqual(5.0, servo.AppliedTrimPpm, 1.0e-9);
            servo.AdvanceAppliedTrim(1.0);
            Assert.AreEqual(10.0, servo.AppliedTrimPpm, 1.0e-9);
            servo.AdvanceAppliedTrim(10.0);
            Assert.AreEqual(servo.TargetTrimPpm, servo.AppliedTrimPpm,
                1.0e-9);
        }

        [TestMethod]
        public void WindowResetDiscardsPartialFitButRetainsLearnedTrim()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState();
            Assert.IsTrue(RunUntilWindow(servo, state, 30.0));
            servo.AdvanceAppliedTrim(1.0);
            double targetBeforeReset = servo.TargetTrimPpm;
            double appliedBeforeReset = servo.AppliedTrimPpm;

            RunForSeconds(servo, state, 15.0);
            servo.ResetWindow();
            Assert.AreEqual(targetBeforeReset, servo.TargetTrimPpm, 0.0);
            Assert.AreEqual(appliedBeforeReset, servo.AppliedTrimPpm, 0.0);
            Assert.IsFalse(RunForSeconds(servo, state, 16.0),
                "A reset partial window leaked into the next regression.");
            Assert.IsTrue(RunUntilWindow(servo, state, 15.0));
        }

        [TestMethod]
        public void LifecycleResetClearsLearnedTrim()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState();
            Assert.IsTrue(RunUntilWindow(servo, state, 30.0));
            servo.AdvanceAppliedTrim(1.0);

            servo.ResetLifecycle();

            Assert.AreEqual(0.0, servo.TargetTrimPpm, 0.0);
            Assert.AreEqual(0.0, servo.AppliedTrimPpm, 0.0);
            Assert.AreEqual(0.0, servo.LastMeasuredErrorPpm, 0.0);
        }

        private static bool RunUntilWindow(
            DualSenseDirectPcmBalanceClockServo servo,
            SimulationState state, double minimumSeconds)
        {
            int completedBefore = servo.CompletedWindows;
            int rejectedBefore = servo.RejectedWindows;
            long endTimestamp = state.HostTimestamp + (long)Math.Ceiling(
                (minimumSeconds + 1.0) * Stopwatch.Frequency);
            bool accepted = false;
            while (state.HostTimestamp <= endTimestamp &&
                servo.CompletedWindows == completedBefore &&
                servo.RejectedWindows == rejectedBefore)
            {
                accepted = Step(servo, state);
            }

            return accepted && servo.CompletedWindows > completedBefore;
        }

        private static bool RunForSeconds(
            DualSenseDirectPcmBalanceClockServo servo,
            SimulationState state, double seconds)
        {
            int completedBefore = servo.CompletedWindows;
            long endTimestamp = state.HostTimestamp + (long)Math.Ceiling(
                seconds * Stopwatch.Frequency);
            while (state.HostTimestamp <= endTimestamp)
            {
                Step(servo, state);
            }

            return servo.CompletedWindows > completedBefore;
        }

        private static bool Step(
            DualSenseDirectPcmBalanceClockServo servo,
            SimulationState state)
        {
            state.ConsumedFrames += ConsumerBlockFrames;
            state.ProducedFrames = (long)Math.Round(
                state.ConsumedFrames *
                (1.0 + state.ErrorPpm / 1_000_000.0));
            state.HostTimestamp = state.HostOrigin + (long)Math.Round(
                state.ConsumedFrames / NominalFramesPerSecond *
                Stopwatch.Frequency);
            return servo.Observe(state.ProducedFrames,
                state.ConsumedFrames, state.HostTimestamp);
        }

        private sealed class SimulationState
        {
            internal SimulationState(double errorPpm = 30.0)
            {
                ErrorPpm = errorPpm;
                HostOrigin = 10L * Stopwatch.Frequency;
                HostTimestamp = HostOrigin;
            }

            internal double ErrorPpm { get; }
            internal long HostOrigin { get; }
            internal long ProducedFrames { get; set; }
            internal long ConsumedFrames { get; set; }
            internal long HostTimestamp { get; set; }
        }
    }
}
