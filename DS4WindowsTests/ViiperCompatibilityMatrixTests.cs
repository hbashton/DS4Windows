using System.Reflection;
using System.Linq;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperCompatibilityMatrixTests
    {
        private static readonly ViiperVirtualDeviceType[] OutputTypes =
        {
            ViiperVirtualDeviceType.Xbox360,
            ViiperVirtualDeviceType.XboxOne,
            ViiperVirtualDeviceType.DualShock4,
            ViiperVirtualDeviceType.DualSense,
            ViiperVirtualDeviceType.DualSenseEdge,
            ViiperVirtualDeviceType.Switch2Pro,
        };

        [DataTestMethod]
        [DataRow(InputDeviceType.DS4)]
        [DataRow(InputDeviceType.DualSense)]
        [DataRow(InputDeviceType.SwitchPro)]
        [DataRow(InputDeviceType.JoyConL)]
        [DataRow(InputDeviceType.JoyConR)]
        [DataRow(InputDeviceType.JoyConGrip)]
        public void FamilySeededCanonicalStateSerializesForEveryViiperOutput(
            InputDeviceType physicalType)
        {
            // Packet-builder smoke only. physicalType supplies a numerical seed;
            // this does not instantiate that family's parser, device or mapper.
            // Actual Switch 2 registered mapping is tested separately by
            // Switch2ProductionInputMatrixTests; hardware/API routes remain separate.
            DS4State state = CreateCanonicalMappedState(physicalType);

            foreach (ViiperVirtualDeviceType outputType in OutputTypes)
            {
                byte[] packet = Build(outputType, state);
                Assert.IsNotNull(packet, $"{physicalType} -> {outputType}");
                Assert.IsTrue(packet.Length > 0,
                    $"{physicalType} -> {outputType} produced no report");
                Assert.IsTrue(packet.Any(value => value != 0),
                    $"{physicalType} -> {outputType} stayed neutral");
            }
        }

        [TestMethod]
        public void EveryFamilySeededCanonicalStateSerializesForXboxOne()
        {
            foreach (InputDeviceType physicalType in
                Enum.GetValues<InputDeviceType>())
            {
                byte[] packet = Build(ViiperVirtualDeviceType.XboxOne,
                    CreateCanonicalMappedState(physicalType));
                Assert.AreEqual(24, packet.Length,
                    $"{physicalType} -> XboxOne report length");
                Assert.IsTrue(packet.Any(value => value != 0),
                    $"{physicalType} -> XboxOne stayed neutral");
            }
        }

        [TestMethod]
        public void Switch2FamilySeededCanonicalStatesSerializeForEveryViiperOutput()
        {
            InputDeviceType[] switch2PhysicalTypes =
            {
                InputDeviceType.Switch2Pro,
                InputDeviceType.Switch2JoyConLeft,
                InputDeviceType.Switch2JoyConRight,
                InputDeviceType.Switch2JoyConJoined,
            };

            foreach (InputDeviceType physicalType in switch2PhysicalTypes)
            {
                DS4State state = CreateCanonicalMappedState(physicalType);
                foreach (ViiperVirtualDeviceType outputType in OutputTypes)
                {
                    byte[] packet = Build(outputType, state);
                    Assert.IsNotNull(packet,
                        $"{physicalType} -> {outputType}");
                    Assert.IsTrue(packet.Length > 0,
                        $"{physicalType} -> {outputType} produced no report");
                    Assert.IsTrue(packet.Any(value => value != 0),
                        $"{physicalType} -> {outputType} stayed neutral");
                }
            }
        }

        [DataTestMethod]
        [DataRow(ViiperVirtualDeviceType.Xbox360, 20)]
        [DataRow(ViiperVirtualDeviceType.XboxOne, 24)]
        [DataRow(ViiperVirtualDeviceType.DualShock4, 31)]
        [DataRow(ViiperVirtualDeviceType.DualSense, 33)]
        [DataRow(ViiperVirtualDeviceType.DualSenseEdge, 33)]
        [DataRow(ViiperVirtualDeviceType.Switch2Pro, 24)]
        public void ViiperOutputReportsKeepTheirProtocolLength(
            ViiperVirtualDeviceType outputType, int expectedLength)
        {
            Assert.AreEqual(expectedLength,
                Build(outputType, CreateCanonicalMappedState(
                    InputDeviceType.DS4)).Length);
        }

        [TestMethod]
        public void LegacyPlayStationFeedbackDecodesForSwitch2HdRumble()
        {
            byte[] ds4 = { 12, 34, 0, 0, 0, 0, 0 };
            Assert.IsTrue(ViiperOutDevice.
                TryDecodeCanonicalFeedbackForSwitch2(
                    ViiperVirtualDeviceType.DualShock4, ds4, ds4.Length,
                    out ControllerFeedbackActuatorState ds4State));
            Assert.AreEqual((ushort)(34 * 257), ds4State.BodyLow);
            Assert.AreEqual((ushort)(12 * 257), ds4State.BodyHigh);

            byte[] dualSense = { 56, 78, 0, 0, 0, 0 };
            Assert.IsTrue(ViiperOutDevice.
                TryDecodeCanonicalFeedbackForSwitch2(
                    ViiperVirtualDeviceType.DualSense, dualSense,
                    dualSense.Length,
                    out ControllerFeedbackActuatorState dualSenseState));
            Assert.AreEqual((ushort)(56 * 257), dualSenseState.BodyLow);
            Assert.AreEqual((ushort)(78 * 257), dualSenseState.BodyHigh);
        }

        private static DS4State CreateCanonicalMappedState(
            InputDeviceType physicalType)
        {
            int seed = ((int)physicalType + 1) * 7;
            return new DS4State
            {
                LX = (byte)(32 + seed),
                LY = (byte)(208 - seed),
                RX = (byte)(64 + seed),
                RY = (byte)(192 - seed),
                L2 = 73,
                R2 = 181,
                Cross = true,
                Triangle = true,
                L1 = true,
                R1 = true,
                DpadUp = true,
                PS = true,
            };
        }

        private static byte[] Build(ViiperVirtualDeviceType type,
            DS4State state)
        {
            Type builder = typeof(ViiperVirtualDeviceType).Assembly.GetType(
                "DS4Windows.ViiperStatePacketBuilder", true);
            MethodInfo method = builder.GetMethod("Build",
                BindingFlags.Public | BindingFlags.Static);
            return (byte[])method.Invoke(null, new object[] { type, state, -1 });
        }
    }
}
