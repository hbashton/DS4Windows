using System.Reflection;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperPacketBuilderTests
    {
        [TestMethod]
        public void DualSenseMotionUsesControllerAxisOrder()
        {
            DS4State state = CreateMotionState();

            byte[] packet = BuildViiperStatePacket(ViiperVirtualDeviceType.DualSense, state);

            AssertSonyMotion(packet, 21);
        }

        [TestMethod]
        public void DualShock4MotionUsesControllerAxisOrder()
        {
            DS4State state = CreateMotionState();

            byte[] packet = BuildViiperStatePacket(ViiperVirtualDeviceType.DualShock4, state);

            AssertSonyMotion(packet, 19);
        }

        [TestMethod]
        public void Xbox360PacketMatchesTheXusbInputContract()
        {
            DS4State state = new DS4State
            {
                DpadUp = true,
                DpadDown = true,
                DpadLeft = true,
                DpadRight = true,
                Options = true,
                Share = true,
                L3 = true,
                R3 = true,
                L1 = true,
                R1 = true,
                PS = true,
                Cross = true,
                Circle = true,
                Square = true,
                Triangle = true,
                L2 = 37,
                R2 = 219,
                LX = 0,
                LY = 0,
                RX = 255,
                RY = 255,
            };

            byte[] packet = BuildViiperStatePacket(
                ViiperVirtualDeviceType.Xbox360, state);

            Assert.AreEqual(20, packet.Length);
            Assert.AreEqual(0x0000F7FFu, ReadUInt32(packet, 0));
            Assert.AreEqual(37, packet[4]);
            Assert.AreEqual(219, packet[5]);
            Assert.AreEqual(short.MinValue, ReadInt16(packet, 6));
            Assert.AreEqual(short.MaxValue, ReadInt16(packet, 8));
            Assert.AreEqual(short.MaxValue, ReadInt16(packet, 10));
            Assert.AreEqual(short.MinValue, ReadInt16(packet, 12));
            CollectionAssert.AreEqual(new byte[6], packet[14..20]);
        }

        [TestMethod]
        public void Xbox360NeutralPacketIsCenteredAndReusable()
        {
            byte[] packet = BuildNeutralViiperStatePacket(
                ViiperVirtualDeviceType.Xbox360);

            Assert.AreEqual(20, packet.Length);
            Assert.AreEqual(0u, ReadUInt32(packet, 0));
            Assert.AreEqual((short)0, ReadInt16(packet, 6));
            Assert.AreEqual((short)0, ReadInt16(packet, 8));
            Assert.AreEqual((short)0, ReadInt16(packet, 10));
            Assert.AreEqual((short)0, ReadInt16(packet, 12));
        }

        [DataTestMethod]
        [DataRow(ViiperVirtualDeviceType.DualSense)]
        [DataRow(ViiperVirtualDeviceType.DualSenseEdge)]
        [DataRow(ViiperVirtualDeviceType.DualShock4)]
        public void SonyNeutralPacketCentersEveryStick(ViiperVirtualDeviceType type)
        {
            byte[] packet = BuildNeutralViiperStatePacket(type);

            Assert.AreEqual(0, packet[0], "LX should be centered");
            Assert.AreEqual(0, packet[1], "LY should be centered");
            Assert.AreEqual(0, packet[2], "RX should be centered");
            Assert.AreEqual(0, packet[3], "RY should be centered");
        }

        [DataTestMethod]
        [DataRow(ViiperVirtualDeviceType.DualSense,
            "dualsensecombinedaudioduplexv5")]
        [DataRow(ViiperVirtualDeviceType.DualSenseEdge,
            "dualsenseedgecombinedaudioduplexv5")]
        public void DualSenseFamiliesSelectOnlyV5Contract(
            ViiperVirtualDeviceType type, string expectedName)
        {
            Type builderType = typeof(ViiperVirtualDeviceType).Assembly.GetType(
                "DS4Windows.ViiperStatePacketBuilder",
                throwOnError: true);
            MethodInfo method = builderType.GetMethod(
                "GetViiperDeviceName",
                BindingFlags.Public | BindingFlags.Static);

            Assert.AreEqual(expectedName,
                (string)method.Invoke(null, new object[] { type }));
        }

        private static DS4State CreateMotionState()
        {
            DS4State state = new DS4State();
            state.Motion.gyroYawFull = 1234;
            state.Motion.gyroPitchFull = -2345;
            state.Motion.gyroRollFull = 3456;
            state.Motion.accelXFull = 111;
            state.Motion.accelYFull = -222;
            state.Motion.accelZFull = -333;
            return state;
        }

        private static void AssertSonyMotion(byte[] packet, int offset)
        {
            Assert.AreEqual((short)-2345, ReadInt16(packet, offset), "gyro X should carry pitch");
            Assert.AreEqual((short)-1234, ReadInt16(packet, offset + 2), "gyro Y should carry negative yaw");
            Assert.AreEqual((short)-3456, ReadInt16(packet, offset + 4), "gyro Z should carry negative roll");
            Assert.AreEqual((short)-111, ReadInt16(packet, offset + 6), "accel X should carry negative X");
            Assert.AreEqual((short)222, ReadInt16(packet, offset + 8), "accel Y should carry negative Y");
            Assert.AreEqual((short)-333, ReadInt16(packet, offset + 10), "accel Z should carry Z");
        }

        private static byte[] BuildViiperStatePacket(ViiperVirtualDeviceType type, DS4State state)
        {
            Type builderType = typeof(ViiperVirtualDeviceType).Assembly.GetType(
                "DS4Windows.ViiperStatePacketBuilder",
                throwOnError: true);
            MethodInfo buildMethod = builderType.GetMethod(
                "Build",
                BindingFlags.Public | BindingFlags.Static);

            return (byte[])buildMethod.Invoke(null, new object[] { type, state, -1 });
        }

        private static byte[] BuildNeutralViiperStatePacket(ViiperVirtualDeviceType type)
        {
            Type builderType = typeof(ViiperVirtualDeviceType).Assembly.GetType(
                "DS4Windows.ViiperStatePacketBuilder",
                throwOnError: true);
            MethodInfo buildMethod = builderType.GetMethod(
                "BuildNeutral",
                BindingFlags.Public | BindingFlags.Static);

            return (byte[])buildMethod.Invoke(null, new object[] { type });
        }

        private static short ReadInt16(byte[] data, int offset)
        {
            return unchecked((short)(data[offset] | (data[offset + 1] << 8)));
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return unchecked((uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24)));
        }
    }
}
