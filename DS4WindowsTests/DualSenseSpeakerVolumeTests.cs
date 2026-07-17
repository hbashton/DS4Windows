using System.Reflection;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseSpeakerVolumeTests
    {
        private static readonly MethodInfo MapVolumeMethod =
            typeof(DualSenseDevice).GetMethod(
                "MapDualSenseSpeakerVolume",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo ApplyVolumeMethod =
            typeof(DualSenseDevice).GetMethod(
                "ApplyBluetoothSpeakerVolumeAndRouting",
                BindingFlags.NonPublic | BindingFlags.Static);

        [TestMethod]
        public void ZeroProfileVolumeMutesSpeaker()
        {
            Assert.AreEqual((byte)0, MapVolume(0));
        }

        [TestMethod]
        public void ProfileRangeMapsToEffectiveFirmwareRange()
        {
            Assert.AreEqual((byte)0x3D, MapVolume(1));
            Assert.AreEqual((byte)0x64, MapVolume(255));
            Assert.IsTrue(MapVolume(20) < MapVolume(128));
        }

        [TestMethod]
        public void ProfileVolumeMappingIsMonotonic()
        {
            byte previous = MapVolume(0);
            for (int value = 1; value <= byte.MaxValue; value++)
            {
                byte mapped = MapVolume((byte)value);
                Assert.IsTrue(mapped >= previous,
                    $"Mapped volume decreased at profile value {value}.");
                previous = mapped;
            }
        }

        [TestMethod]
        public void CombinedReportAssertsVolumeRouteAndPreGain()
        {
            byte[] report = new byte[64];
            report[13] = 0x05;
            report[14] = 0x04;
            report[20] = 0xC7;
            report[50] = 0xA8;

            Assert.IsNotNull(ApplyVolumeMethod);
            ApplyVolumeMethod.Invoke(null, new object[] { report, (byte)20 });

            Assert.AreEqual((byte)0xA5, report[13]);
            Assert.AreEqual((byte)0x84, report[14]);
            Assert.AreEqual(MapVolume(20), report[18]);
            Assert.AreEqual((byte)0xF7, report[20]);
            Assert.AreEqual((byte)0xAB, report[50]);
        }

        private static byte MapVolume(byte value)
        {
            Assert.IsNotNull(MapVolumeMethod);
            return (byte)MapVolumeMethod.Invoke(null, new object[] { value });
        }
    }
}
