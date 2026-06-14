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
            DS4State state = new DS4State();
            state.Motion.gyroYawFull = 1234;
            state.Motion.gyroPitchFull = -2345;
            state.Motion.gyroRollFull = 3456;
            state.Motion.accelXFull = 111;
            state.Motion.accelYFull = -222;
            state.Motion.accelZFull = -333;

            byte[] packet = BuildViiperStatePacket(ViiperVirtualDeviceType.DualSense, state);

            Assert.AreEqual((short)-2345, ReadInt16(packet, 21), "gyro X should carry pitch");
            Assert.AreEqual((short)-1234, ReadInt16(packet, 23), "gyro Y should carry negative yaw");
            Assert.AreEqual((short)-3456, ReadInt16(packet, 25), "gyro Z should carry negative roll");
            Assert.AreEqual((short)-111, ReadInt16(packet, 27), "accel X should carry negative X");
            Assert.AreEqual((short)222, ReadInt16(packet, 29), "accel Y should carry negative Y");
            Assert.AreEqual((short)-333, ReadInt16(packet, 31), "accel Z should carry Z");
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

        private static short ReadInt16(byte[] data, int offset)
        {
            return unchecked((short)(data[offset] | (data[offset + 1] << 8)));
        }
    }
}
