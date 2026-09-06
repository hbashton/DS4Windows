using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseAdaptiveTriggerHdRumbleTranslatorTests
    {
        [TestMethod]
        public void OffAndUnknownEffectsFailClosed()
        {
            byte[] effect = new byte[11];
            Assert.IsFalse(DualSenseAdaptiveTriggerHdRumbleTranslator.
                TryTranslate(effect, out _));

            effect[0] = 0x05;
            Assert.IsFalse(DualSenseAdaptiveTriggerHdRumbleTranslator.
                TryTranslate(effect, out _));

            effect[0] = 0x7F;
            Assert.IsFalse(DualSenseAdaptiveTriggerHdRumbleTranslator.
                TryTranslate(effect, out _));

            Assert.IsFalse(DualSenseAdaptiveTriggerHdRumbleTranslator.
                TryTranslate(effect.AsSpan(0, 10), out _));
        }

        [TestMethod]
        public void FeedbackModePreservesThreeRegionStrengthEnvelope()
        {
            byte[] effect = new byte[11];
            effect[0] = 0x21;
            effect[1] = 0xFF;
            effect[2] = 0x03;
            SetZoneStrength(effect, 0, 1);
            SetZoneStrength(effect, 1, 1);
            SetZoneStrength(effect, 2, 1);
            SetZoneStrength(effect, 3, 4);
            SetZoneStrength(effect, 4, 4);
            SetZoneStrength(effect, 5, 4);
            SetZoneStrength(effect, 6, 8);
            SetZoneStrength(effect, 7, 8);
            SetZoneStrength(effect, 8, 8);
            SetZoneStrength(effect, 9, 8);

            Assert.IsTrue(DualSenseAdaptiveTriggerHdRumbleTranslator.
                TryTranslate(effect, out Switch2HdRumbleGroup group));
            Assert.IsTrue(group.First.Oscillator0AmplitudeCode <
                group.Second.Oscillator0AmplitudeCode);
            Assert.IsTrue(group.Second.Oscillator0AmplitudeCode <
                group.Third.Oscillator0AmplitudeCode);
            Assert.IsTrue(group.First.Oscillator0ControlCode <
                group.Second.Oscillator0ControlCode);
            Assert.IsTrue(group.Second.Oscillator0ControlCode <
                group.Third.Oscillator0ControlCode);
            Assert.IsTrue(group.Third.Oscillator0AmplitudeCode <=
                Switch2HdRumbleFeedbackTranslator.
                    MaximumPackedCompatibilityAmplitude);
            Assert.AreEqual((ushort)0,
                group.First.Oscillator1AmplitudeCode);
        }

        [TestMethod]
        public void VibrationModePreservesFrequencyAndZoneTiming()
        {
            byte[] effect = new byte[11];
            effect[0] = 0x26;
            effect[1] = 0x38; // Zones 3, 4, and 5: middle subframe only.
            effect[9] = 200;
            SetZoneStrength(effect, 3, 8);
            SetZoneStrength(effect, 4, 6);
            SetZoneStrength(effect, 5, 4);

            Assert.IsTrue(DualSenseAdaptiveTriggerHdRumbleTranslator.
                TryTranslate(effect, out Switch2HdRumbleGroup group));
            Assert.IsFalse(group.First.HasNonzeroAmplitude);
            Assert.IsTrue(group.Second.HasNonzeroAmplitude);
            Assert.IsFalse(group.Third.HasNonzeroAmplitude);
            Assert.IsTrue(group.Second.Oscillator0ControlCode >
                DualSenseAdaptiveTriggerHdRumbleTranslator.
                    MinimumControlCode);
            Assert.IsTrue(group.Second.Oscillator0ControlCode <=
                DualSenseAdaptiveTriggerHdRumbleTranslator.
                    MaximumControlCode);
        }

        [TestMethod]
        public void WeaponModeKeepsBothBreakPositions()
        {
            byte[] effect = new byte[11];
            effect[0] = 0x25;
            effect[1] = (byte)((1 << 2) | (1 << 7));
            effect[3] = 7;

            Assert.IsTrue(DualSenseAdaptiveTriggerHdRumbleTranslator.
                TryTranslate(effect, out Switch2HdRumbleGroup group));
            Assert.IsTrue(group.First.HasNonzeroAmplitude);
            Assert.IsFalse(group.Second.HasNonzeroAmplitude);
            Assert.IsTrue(group.Third.HasNonzeroAmplitude);
            Assert.AreNotEqual(group.First.Oscillator0ControlCode,
                group.Third.Oscillator0ControlCode);
        }

        [TestMethod]
        public void MixPrioritizesIncomingCarrierAndPreservesHeadroom()
        {
            var basisSubframe = new Switch2HdRumbleSubframe(
                400, 900, 250, 100);
            var additionSubframe = new Switch2HdRumbleSubframe(
                225, 300, 300, 950);
            var basis = new Switch2HdRumbleGroup(basisSubframe,
                basisSubframe, basisSubframe);
            var addition = new Switch2HdRumbleGroup(additionSubframe,
                additionSubframe, additionSubframe);

            Switch2HdRumbleGroup mixed =
                DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(basis,
                    addition);

            Assert.AreEqual((ushort)225,
                mixed.First.Oscillator0ControlCode);
            Assert.AreEqual((ushort)300,
                mixed.First.Oscillator1ControlCode);
            Assert.AreEqual((ushort)936,
                mixed.First.Oscillator0AmplitudeCode);
            Assert.AreEqual((ushort)957,
                mixed.First.Oscillator1AmplitudeCode);
        }

        [TestMethod]
        public void TranslationAllocatesNothingAfterWarmup()
        {
            byte[] effect = new byte[11];
            effect[0] = 0x26;
            effect[1] = 0xFF;
            effect[2] = 0x03;
            effect[9] = 28;
            for (int zone = 0; zone < 10; zone++)
            {
                SetZoneStrength(effect, zone, zone % 8 + 1);
            }
            for (int index = 0; index < 128; index++)
            {
                DualSenseAdaptiveTriggerHdRumbleTranslator.TryTranslate(
                    effect, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool valid = true;
            for (int index = 0; index < 10_000; index++)
            {
                valid &= DualSenseAdaptiveTriggerHdRumbleTranslator.
                    TryTranslate(effect, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(valid);
            Assert.AreEqual(0L, allocated);
        }

        private static void SetZoneStrength(byte[] effect, int zone,
            int strength)
        {
            uint packed = (uint)(effect[3] | effect[4] << 8 |
                effect[5] << 16 | effect[6] << 24);
            int shift = zone * 3;
            packed &= ~(0x07u << shift);
            packed |= (uint)(strength - 1) << shift;
            effect[3] = (byte)packed;
            effect[4] = (byte)(packed >> 8);
            effect[5] = (byte)(packed >> 16);
            effect[6] = (byte)(packed >> 24);
        }
    }
}
