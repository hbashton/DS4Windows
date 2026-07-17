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
        private static readonly MethodInfo BuildControlReportMethod =
            typeof(DualSenseDevice).GetMethod(
                "BuildBluetoothMicrophoneControlReport",
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

        [TestMethod]
        public void MicOnUsesDedicatedStateFreeControlPacket()
        {
            byte[] report = BuildControlReport(0x0A, true);

            Assert.AreEqual(142, report.Length);
            Assert.AreEqual((byte)0x32, report[0]);
            Assert.AreEqual((byte)0xA0, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)0x01, report[3]);
            Assert.AreEqual((byte)0x03, report[4]);
            AssertPayloadIsEmpty(report);
            AssertCrcIsValid(report);
        }

        [TestMethod]
        public void MicOffClearsOnlyTheStreamEnableBit()
        {
            byte[] report = BuildControlReport(0x1F, false);

            Assert.AreEqual((byte)0xF0, report[1]);
            Assert.AreEqual((byte)0x02, report[4]);
            AssertPayloadIsEmpty(report);
            AssertCrcIsValid(report);
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

        private static byte[] BuildControlReport(byte sequence, bool enabled)
        {
            Assert.IsNotNull(BuildControlReportMethod);
            return (byte[])BuildControlReportMethod.Invoke(null,
                new object[] { sequence, enabled });
        }

        private static void AssertPayloadIsEmpty(byte[] report)
        {
            for (int index = 5; index < report.Length - sizeof(uint); index++)
            {
                Assert.AreEqual((byte)0, report[index],
                    $"Unexpected control payload at byte {index}.");
            }
        }

        private static void AssertCrcIsValid(byte[] report)
        {
            uint expected = ComputeCrc(report, report.Length - sizeof(uint));
            uint actual = (uint)(report[^4] |
                (report[^3] << 8) |
                (report[^2] << 16) |
                (report[^1] << 24));
            Assert.AreEqual(expected, actual);
        }

        private static uint ComputeCrc(byte[] data, int length)
        {
            uint crc = ~0xEADA2D49u;
            for (int index = 0; index < length; index++)
            {
                crc ^= data[index];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^
                        ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
            }

            return ~crc;
        }
    }
}
