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
        public void ThirtyPpmSurplusLearnsHalfCorrection()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(30.0);

            Assert.IsTrue(RunUntilWindow(servo, state));
            Assert.AreEqual(30.0, servo.LastMeasuredErrorPpm, 0.1);
            Assert.AreEqual(15.0, servo.TargetTrimPpm, 0.1);
            Assert.AreEqual(1, servo.CompletedWindows);
        }

        [TestMethod]
        public void CallbackSawtoothDoesNotBiasLongWindowSlope()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(30.0);

            Assert.IsTrue(RunUntilWindow(servo, state,
                callbackSawtoothFrames: 320));

            Assert.AreEqual(30.0, servo.LastMeasuredErrorPpm, 0.2);
            Assert.AreEqual(15.0, servo.TargetTrimPpm, 0.2);
        }

        [TestMethod]
        public void DeadbandDoesNotRetuneNominalStream()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(2.0);

            Assert.IsTrue(RunUntilWindow(servo, state));
            Assert.AreEqual(0.0, servo.TargetTrimPpm, 0.0);
        }

        [TestMethod]
        public void OutlierWindowIsRejected()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(600.0);

            Assert.IsFalse(RunUntilWindow(servo, state));
            Assert.AreEqual(0, servo.CompletedWindows);
            Assert.AreEqual(1, servo.RejectedWindows);
        }

        [TestMethod]
        public void AppliedTrimSlewsAtFivePpmPerSecond()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(30.0);
            Assert.IsTrue(RunUntilWindow(servo, state));

            servo.AdvanceAppliedTrim(1.0);
            Assert.AreEqual(5.0, servo.AppliedTrimPpm, 1.0e-9);
            servo.AdvanceAppliedTrim(2.0);
            Assert.AreEqual(15.0, servo.AppliedTrimPpm, 0.1);
        }

        [TestMethod]
        public void WindowResetKeepsLearnedTrimButDropsPartialFit()
        {
            var servo = new DualSenseDirectPcmBalanceClockServo();
            var state = new SimulationState(30.0);
            Assert.IsTrue(RunUntilWindow(servo, state));
            servo.AdvanceAppliedTrim(1.0);
            double target = servo.TargetTrimPpm;
            double applied = servo.AppliedTrimPpm;

            RunForSeconds(servo, state, 15.0);
            servo.ResetWindow();

            Assert.AreEqual(target, servo.TargetTrimPpm, 0.0);
            Assert.AreEqual(applied, servo.AppliedTrimPpm, 0.0);
            Assert.IsFalse(RunForSeconds(servo, state, 16.0));
        }

        private static bool RunUntilWindow(
            DualSenseDirectPcmBalanceClockServo servo,
            SimulationState state, int callbackSawtoothFrames = 0)
        {
            int completed = servo.CompletedWindows;
            int rejected = servo.RejectedWindows;
            long end = state.HostTimestamp +
                32L * Stopwatch.Frequency;
            bool accepted = false;
            while (state.HostTimestamp <= end &&
                servo.CompletedWindows == completed &&
                servo.RejectedWindows == rejected)
            {
                accepted = Step(servo, state, callbackSawtoothFrames);
            }

            return accepted && servo.CompletedWindows > completed;
        }

        private static bool RunForSeconds(
            DualSenseDirectPcmBalanceClockServo servo,
            SimulationState state, double seconds)
        {
            int completed = servo.CompletedWindows;
            long end = state.HostTimestamp + (long)Math.Ceiling(
                seconds * Stopwatch.Frequency);
            while (state.HostTimestamp <= end)
            {
                Step(servo, state, 0);
            }

            return servo.CompletedWindows > completed;
        }

        private static bool Step(
            DualSenseDirectPcmBalanceClockServo servo,
            SimulationState state, int callbackSawtoothFrames)
        {
            state.ConsumedFrames += ConsumerBlockFrames;
            double exactProduced = state.ConsumedFrames *
                (1.0 + state.ErrorPpm / 1_000_000.0);
            long sawtooth = callbackSawtoothFrames == 0 ? 0 :
                (state.Steps++ % 2 == 0 ? callbackSawtoothFrames : 0);
            state.ProducedFrames = (long)Math.Round(exactProduced) +
                sawtooth;
            state.HostTimestamp = state.HostOrigin + (long)Math.Round(
                state.ConsumedFrames / NominalFramesPerSecond *
                Stopwatch.Frequency);
            return servo.Observe(state.ProducedFrames,
                state.ConsumedFrames, state.HostTimestamp);
        }

        private sealed class SimulationState
        {
            internal SimulationState(double errorPpm)
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
            internal int Steps { get; set; }
        }
    }
}
