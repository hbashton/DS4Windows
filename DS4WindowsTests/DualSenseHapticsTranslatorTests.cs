using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseHapticsTranslatorTests
    {
        [TestMethod]
        public void SilencePreservesBaseRumble()
        {
            byte[] feedback = new byte[141];
            feedback[0] = 20;
            feedback[1] = 30;
            feedback[28] = 0x32;

            DualSenseHapticsTranslator.Translate(feedback, feedback.Length, 28,
                out byte light, out byte heavy);

            Assert.AreEqual((byte)30, light);
            Assert.AreEqual((byte)20, heavy);
        }

        [TestMethod]
        public void LegacyHapticsDriveBothMotorsWithinBounds()
        {
            byte[] feedback = new byte[28 + 141];
            feedback[28] = 0x32;
            for (int index = 0; index < 64; index += 2)
            {
                byte value = (index / 2) % 2 == 0 ?
                    unchecked((byte)110) : unchecked((byte)-110);
                feedback[28 + 13 + index] = value;
                feedback[28 + 13 + index + 1] = value;
            }

            DualSenseHapticsTranslator.Translate(feedback, feedback.Length, 28,
                out byte light, out byte heavy);

            Assert.IsTrue(light > 0);
            Assert.IsTrue(heavy > 0);
            Assert.IsTrue(light <= byte.MaxValue);
            Assert.IsTrue(heavy <= byte.MaxValue);
        }

        [TestMethod]
        public void CombinedHapticsUsesCombinedPayloadOffset()
        {
            byte[] feedback = new byte[28 + 398];
            feedback[28] = 0x36;
            for (int index = 0; index < 64; index += 2)
            {
                feedback[28 + 78 + index] = 80;
                feedback[28 + 78 + index + 1] = 80;
            }

            DualSenseHapticsTranslator.Translate(feedback, feedback.Length, 28,
                out byte light, out byte heavy);

            Assert.AreEqual((byte)0, light);
            Assert.IsTrue(heavy > 0);
        }
    }
}
