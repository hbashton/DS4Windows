using System.Reflection;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseBluetoothMicrophoneProtocolTests
    {
        private static readonly MethodInfo IsMicrophoneFrameMethod =
            typeof(DualSenseDevice).GetMethod(
                "IsBluetoothMicrophoneFrame",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo IsNormalFrameMethod =
            typeof(DualSenseDevice).GetMethod(
                "IsBluetoothNormalInputFrame",
                BindingFlags.NonPublic | BindingFlags.Static);

        [TestMethod]
        public void MicrophoneBitTakesPriorityOverNormalInputBit()
        {
            Assert.IsTrue(IsMicrophoneFrame(0x12));
            Assert.IsTrue(IsMicrophoneFrame(0x13));
            Assert.IsFalse(IsNormalFrame(0x12));
            Assert.IsFalse(IsNormalFrame(0x13));
        }

        [TestMethod]
        public void NormalInputRequiresNormalBitWithoutMicrophoneBit()
        {
            Assert.IsTrue(IsNormalFrame(0x11));
            Assert.IsFalse(IsMicrophoneFrame(0x11));
            Assert.IsFalse(IsNormalFrame(0x10));
        }

        private static bool IsMicrophoneFrame(byte tag)
        {
            byte[] report = BuildInputReport(tag);
            Assert.IsNotNull(IsMicrophoneFrameMethod);
            return (bool)IsMicrophoneFrameMethod.Invoke(null, new object[] { report });
        }

        private static bool IsNormalFrame(byte tag)
        {
            byte[] report = BuildInputReport(tag);
            Assert.IsNotNull(IsNormalFrameMethod);
            return (bool)IsNormalFrameMethod.Invoke(null, new object[] { report });
        }

        private static byte[] BuildInputReport(byte tag)
        {
            byte[] report = new byte[78];
            report[0] = 0x31;
            report[1] = tag;
            return report;
        }

    }
}
