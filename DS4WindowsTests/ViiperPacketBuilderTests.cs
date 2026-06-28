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
        public void DualSenseDropsTransportMarkerFragmentInMotionFields()
        {
            DS4State state = new DS4State
            {
                LX = 255,
            };
            state.Motion.gyroPitchFull = 0x5056;
            state.Motion.gyroYawFull = -0x43;

            bool[] previous = Global.DualSenseEnableMicrophonePassthrough;
            try
            {
                bool[] enabled = new bool[Math.Max(previous?.Length ?? 0, 1)];
                if (previous != null)
                {
                    Array.Copy(previous, enabled, previous.Length);
                }

                enabled[0] = true;
                Global.DualSenseEnableMicrophonePassthrough = enabled;

                byte[] packet = BuildViiperStatePacket(ViiperVirtualDeviceType.DualSense, state, 0);

                Assert.AreEqual(0, packet[0], "neutral packet should replace the left stick X value");
                Assert.AreEqual(0x80, packet[15], "neutral packet should mark touch 1 inactive");
                Assert.AreEqual(0x80, packet[20], "neutral packet should mark touch 2 inactive");
                Assert.AreEqual((short)-8192, ReadInt16(packet, 31), "neutral packet should preserve rest accel Z");
            }
            finally
            {
                Global.DualSenseEnableMicrophonePassthrough = previous;
            }
        }

        [TestMethod]
        public void DualSenseDropsMicTransportScarInMotionFields()
        {
            DS4State state = new DS4State
            {
                LX = 255,
            };
            state.Motion.gyroPitchFull = 0x4D43;
            state.Motion.gyroYawFull = -0x0101;
            state.Motion.gyroRollFull = -0x0021;

            bool[] previous = Global.DualSenseEnableMicrophonePassthrough;
            try
            {
                bool[] enabled = new bool[Math.Max(previous?.Length ?? 0, 1)];
                if (previous != null)
                {
                    Array.Copy(previous, enabled, previous.Length);
                }

                enabled[0] = true;
                Global.DualSenseEnableMicrophonePassthrough = enabled;

                byte[] packet = BuildViiperStatePacket(ViiperVirtualDeviceType.DualSense, state, 0);

                Assert.AreEqual(0, packet[0], "neutral packet should replace the left stick X value");
                Assert.AreEqual(0x80, packet[15], "neutral packet should mark touch 1 inactive");
                Assert.AreEqual(0x80, packet[20], "neutral packet should mark touch 2 inactive");
                Assert.AreEqual((short)-8192, ReadInt16(packet, 31), "neutral packet should preserve rest accel Z");
            }
            finally
            {
                Global.DualSenseEnableMicrophonePassthrough = previous;
            }
        }

        [TestMethod]
        public void DualSenseDetectsShiftedMicTransportScars()
        {
            AssertMicTransportScarDetected(new byte[] { 0x4D, 0x01, 0x01, 0x21 });
            AssertMicTransportScarDetected(new byte[] { 0x01, 0x01, 0x21 });
            AssertMicTransportScarDetected(new byte[] { 0x50, 0x80, 0x87, 0x43 });
            AssertMicTransportScarDetected(new byte[] { 0x80, 0x87, 0x43 });
            Assert.IsFalse(ContainsMicTransportScar(new byte[] { 0x80, 0x87, 0x42 }));
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
            return BuildViiperStatePacket(type, state, -1);
        }

        private static byte[] BuildViiperStatePacket(ViiperVirtualDeviceType type, DS4State state, int device)
        {
            Type builderType = typeof(ViiperVirtualDeviceType).Assembly.GetType(
                "DS4Windows.ViiperStatePacketBuilder",
                throwOnError: true);
            MethodInfo buildMethod = builderType.GetMethod(
                "Build",
                BindingFlags.Public | BindingFlags.Static);

            return (byte[])buildMethod.Invoke(null, new object[] { type, state, device });
        }

        private static short ReadInt16(byte[] data, int offset)
        {
            return unchecked((short)(data[offset] | (data[offset + 1] << 8)));
        }

        private static void AssertMicTransportScarDetected(byte[] pattern)
        {
            byte[] packet = new byte[33];
            Array.Copy(pattern, 0, packet, 11, pattern.Length);
            Assert.IsTrue(ContainsMicTransportScar(packet),
                $"expected shifted mic transport scar to be detected: {BitConverter.ToString(pattern)}");
        }

        private static bool ContainsMicTransportScar(byte[] packet)
        {
            Type builderType = typeof(ViiperVirtualDeviceType).Assembly.GetType(
                "DS4Windows.ViiperStatePacketBuilder",
                throwOnError: true);
            MethodInfo containsMethod = builderType.GetMethod(
                "ContainsViiperMicTransportLeakPattern",
                BindingFlags.NonPublic | BindingFlags.Static);

            return (bool)containsMethod.Invoke(null, new object[] { packet, 11, packet.Length - 11 });
        }
    }
}
