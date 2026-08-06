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
            ViiperVirtualDeviceType.DualShock4,
            ViiperVirtualDeviceType.DualSense,
            ViiperVirtualDeviceType.DualSenseEdge,
            ViiperVirtualDeviceType.Switch2Pro,
            ViiperVirtualDeviceType.XboxSeries,
        };

        [DataTestMethod]
        [DataRow(InputDeviceType.DS4)]
        [DataRow(InputDeviceType.DualSense)]
        [DataRow(InputDeviceType.SwitchPro)]
        [DataRow(InputDeviceType.JoyConL)]
        [DataRow(InputDeviceType.JoyConR)]
        [DataRow(InputDeviceType.JoyConGrip)]
        public void EverySupportedPhysicalFamilyCanFeedEveryViiperOutput(
            InputDeviceType physicalType)
        {
            // Every physical parser converges on DS4State before virtual output.
            // Exercising the complete output matrix from a non-neutral canonical
            // state catches packet-builder assumptions tied to one input family.
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

        [DataTestMethod]
        [DataRow(ViiperVirtualDeviceType.Xbox360, 20)]
        [DataRow(ViiperVirtualDeviceType.DualShock4, 31)]
        [DataRow(ViiperVirtualDeviceType.DualSense, 33)]
        [DataRow(ViiperVirtualDeviceType.DualSenseEdge, 33)]
        [DataRow(ViiperVirtualDeviceType.Switch2Pro, 24)]
        [DataRow(ViiperVirtualDeviceType.XboxSeries, 20)]
        public void ViiperOutputReportsKeepTheirProtocolLength(
            ViiperVirtualDeviceType outputType, int expectedLength)
        {
            Assert.AreEqual(expectedLength,
                Build(outputType, CreateCanonicalMappedState(
                    InputDeviceType.DS4)).Length);
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
