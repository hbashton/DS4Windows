using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperSwitch2DualSenseHdRumbleTests
    {
        [TestMethod]
        public void RightTriggerProgramStaysOnPressedRightSide()
        {
            byte[] feedback = new byte[28];
            feedback[0] = 40;
            feedback[1] = 20;
            WritePackedVibrationEffect(feedback, 6, 8, 28);

            Assert.IsTrue(ViiperOutDevice.
                TryBuildSwitch2DualSenseHdRumbleGroups(feedback,
                    feedback.Length, 76, leftTriggerActive: false,
                    rightTriggerActive: true,
                    out Switch2HdRumbleGroup left,
                    out Switch2HdRumbleGroup right,
                    out Switch2HdRumbleFeedbackFidelity fidelity));

            Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
                DualSenseAdaptiveTriggerApproximation, fidelity);
            Assert.IsTrue(right.First.Oscillator0AmplitudeCode >
                left.First.Oscillator0AmplitudeCode);
            Assert.AreEqual(left.First.Oscillator1AmplitudeCode,
                right.First.Oscillator1AmplitudeCode,
                "The trigger approximation must not erase body-low rumble.");
        }

        [TestMethod]
        public void UnpressedAdaptiveProgramFallsBackToCanonicalBodyPath()
        {
            byte[] feedback = new byte[28];
            feedback[0] = 40;
            feedback[1] = 20;
            WritePackedVibrationEffect(feedback, 6, 8, 28);

            Assert.IsFalse(ViiperOutDevice.
                TryBuildSwitch2DualSenseHdRumbleGroups(feedback,
                    feedback.Length, 76, leftTriggerActive: false,
                    rightTriggerActive: false, out _, out _, out _));
        }

        [TestMethod]
        public void SilentPcmCarrierCannotEraseCompatibilityMotors()
        {
            const int reportOffset = 28;
            byte[] feedback = new byte[reportOffset + 141];
            feedback[0] = 80;
            feedback[1] = 60;
            feedback[reportOffset] = 0x32;

            Assert.IsTrue(ViiperOutDevice.
                TryBuildSwitch2DualSenseHdRumbleGroups(feedback,
                    feedback.Length, reportOffset,
                    leftTriggerActive: false,
                    rightTriggerActive: false,
                    out Switch2HdRumbleGroup left,
                    out Switch2HdRumbleGroup right,
                    out Switch2HdRumbleFeedbackFidelity fidelity));

            Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
                DualSensePcmDualBand, fidelity);
            Assert.IsTrue(left.First.Oscillator0AmplitudeCode > 0);
            Assert.IsTrue(left.First.Oscillator1AmplitudeCode > 0);
            Assert.AreEqual(left, right);
        }

        [TestMethod]
        public void PcmBodyAndLeftTriggerAreComposedAtomically()
        {
            const int reportOffset = 28;
            const int sampleOffset = reportOffset + 13;
            byte[] feedback = new byte[reportOffset + 141];
            feedback[0] = 15;
            feedback[1] = 10;
            feedback[reportOffset] = 0x32;
            for (int sample = 0; sample < 32; sample++)
            {
                feedback[sampleOffset + sample * 2] =
                    unchecked((byte)(sbyte)(sample < 10 ? 80 : 0));
                feedback[sampleOffset + sample * 2 + 1] =
                    unchecked((byte)(sbyte)(sample >= 21 ? 80 : 0));
            }
            WritePackedVibrationEffect(feedback, 17, 8, 28);

            Assert.IsTrue(ViiperOutDevice.
                TryBuildSwitch2DualSenseHdRumbleGroups(feedback,
                    feedback.Length, reportOffset,
                    leftTriggerActive: true,
                    rightTriggerActive: false,
                    out Switch2HdRumbleGroup left,
                    out Switch2HdRumbleGroup right,
                    out Switch2HdRumbleFeedbackFidelity fidelity));

            Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
                DualSenseAdaptiveTriggerApproximation, fidelity);
            Assert.IsTrue(left.First.Oscillator0AmplitudeCode >
                right.First.Oscillator0AmplitudeCode,
                "The left trigger program must be added only to the left PCM lane.");
            Assert.IsTrue(right.Third.Oscillator1AmplitudeCode >
                right.First.Oscillator1AmplitudeCode,
                "The right PCM channel must retain its chronological envelope.");
        }

        [TestMethod]
        public void AtomicCompositionAllocatesNothingAfterWarmup()
        {
            byte[] feedback = new byte[28];
            feedback[0] = 40;
            feedback[1] = 20;
            WritePackedVibrationEffect(feedback, 6, 8, 28);
            for (int index = 0; index < 128; index++)
            {
                ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(
                    feedback, feedback.Length, 76, false, true,
                    out _, out _, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool valid = true;
            for (int index = 0; index < 10_000; index++)
            {
                valid &= ViiperOutDevice.
                    TryBuildSwitch2DualSenseHdRumbleGroups(feedback,
                        feedback.Length, 76, false, true,
                        out _, out _, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(valid);
            Assert.AreEqual(0L, allocated);
        }

        private static void WritePackedVibrationEffect(byte[] feedback,
            int offset, int strength, byte frequency)
        {
            feedback[offset] = 0x26;
            feedback[offset + 1] = 0xFF;
            feedback[offset + 2] = 0x03;
            uint packed = 0;
            for (int zone = 0; zone < 10; zone++)
            {
                packed |= (uint)(strength - 1) << (zone * 3);
            }
            feedback[offset + 3] = (byte)packed;
            feedback[offset + 4] = (byte)(packed >> 8);
            feedback[offset + 5] = (byte)(packed >> 16);
            feedback[offset + 6] = (byte)(packed >> 24);
            feedback[offset + 9] = frequency;
        }
    }
}
