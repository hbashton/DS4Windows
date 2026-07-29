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
        private static readonly MethodInfo MapMicrophoneVolumeMethod =
            typeof(DualSenseDevice).GetMethod(
                "MapDualSenseMicrophoneVolume",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo ApplyVolumeMethod =
            typeof(DualSenseDevice).GetMethod(
                "ApplyBluetoothSpeakerVolumeAndRouting",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo ApplyVolumeAndRoutingMethod =
            typeof(DualSenseDevice).GetMethod(
                "ApplyBluetoothSpeakerVolumeAndRoutingCore",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo MapHeadphoneVolumeMethod =
            typeof(DualSenseDevice).GetMethod(
                "MapDualSenseHeadphoneVolume",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo GetSpeakerPacketTypeMethod =
            typeof(DualSenseDevice).GetMethod(
                "GetBluetoothCombinedSpeakerPacketType",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo SanitizeAudioSnapshotMethod =
            typeof(DualSenseDevice).GetMethod(
                "SanitizeBluetoothSpeakerAudioSnapshot",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo BuildAudioRouteStateMethod =
            typeof(DualSenseDevice).GetMethod(
                "BuildBluetoothAudioRouteStateReport",
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
            Assert.AreEqual((byte)0x20, report[20]);
            Assert.AreEqual((byte)0x03, report[50]);
        }

        [TestMethod]
        public void HeadsetRouteUsesAuxDacAndHeadsetAudioPacket()
        {
            byte[] report = new byte[64];
            report[13] = 0xFF;
            report[14] = 0xFF;
            report[50] = 0x07;

            Assert.IsNotNull(ApplyVolumeAndRoutingMethod);
            ApplyVolumeAndRoutingMethod.Invoke(null, new object[]
            {
                report, (byte)255, true, (byte)255,
            });

            Assert.AreEqual((byte)0xDF, report[13],
                "Headset routing retained the speaker-volume validity bit.");
            Assert.AreEqual((byte)0x7F, report[17]);
            Assert.AreEqual((byte)0x00, report[18]);
            Assert.AreEqual((byte)0x00, report[20]);
            Assert.AreEqual((byte)0xFF, report[14],
                "Headset routing cleared the shared audio gain validity bit.");
            Assert.AreEqual((byte)0x03, report[50]);
            Assert.AreEqual((byte)0x96,
                InvokePacketType(headsetOnlyAudio: true));
            Assert.AreEqual((byte)0x93,
                InvokePacketType(headsetOnlyAudio: false));
        }

        [TestMethod]
        public void HeadphoneProfileRangeMapsToFirmwareRange()
        {
            Assert.IsNotNull(MapHeadphoneVolumeMethod);
            Assert.AreEqual((byte)0x00,
                MapHeadphoneVolumeMethod.Invoke(null,
                    new object[] { (byte)0 }));
            Assert.AreEqual((byte)0x7F,
                MapHeadphoneVolumeMethod.Invoke(null,
                    new object[] { (byte)255 }));
        }

        [TestMethod]
        public void SpeakerRouteMutesAuxAndMapsSpeakerGain()
        {
            byte[] report = new byte[64];

            Assert.IsNotNull(ApplyVolumeAndRoutingMethod);
            ApplyVolumeAndRoutingMethod.Invoke(null, new object[]
            {
                report, (byte)255, false, (byte)255,
            });

            Assert.AreEqual((byte)0x00, report[17],
                "The speaker route left the AUX DAC audible.");
            Assert.AreEqual((byte)0xA0, report[13],
                "The speaker route did not exclusively validate speaker gain.");
            Assert.AreEqual((byte)0x64, report[18]);
            Assert.AreEqual((byte)0x20, report[20]);
            Assert.AreEqual((byte)0x03, report[50]);
        }

        [TestMethod]
        public void HeadsetRoutePrimerMatchesDualSenseControlProtocol()
        {
            Assert.IsNotNull(BuildAudioRouteStateMethod);
            byte[] speaker = (byte[])BuildAudioRouteStateMethod.Invoke(null,
                new object[] { (byte)5, false });
            byte[] headset = (byte[])BuildAudioRouteStateMethod.Invoke(null,
                new object[] { (byte)6, true });

            Assert.AreEqual((byte)0x31, speaker[0]);
            Assert.AreEqual((byte)0x50, speaker[1]);
            Assert.AreEqual((byte)0xA0, speaker[3]);
            Assert.AreEqual((byte)0x80, speaker[4]);
            Assert.AreEqual((byte)0x64, speaker[8]);
            Assert.AreEqual((byte)0x20, speaker[10]);
            Assert.AreEqual((byte)0x03, speaker[40]);

            Assert.AreEqual((byte)0x31, headset[0]);
            Assert.AreEqual((byte)0x60, headset[1]);
            Assert.AreEqual((byte)0x90, headset[3]);
            Assert.AreEqual((byte)0x00, headset[4]);
            Assert.AreEqual((byte)0x7F, headset[7]);
            Assert.AreEqual((byte)0x00, headset[8]);
            Assert.AreEqual((byte)0x00, headset[10]);
            Assert.AreEqual((byte)0x00, headset[40]);
        }

        [TestMethod]
        public void MicrophoneProfileRangeMapsToPhysicalAdcRange()
        {
            Assert.AreEqual((byte)0x00, MapMicrophoneVolume(0));
            Assert.AreEqual((byte)0x20, MapMicrophoneVolume(128));
            Assert.AreEqual((byte)0x40, MapMicrophoneVolume(255));

            byte previous = MapMicrophoneVolume(0);
            for (int value = 1; value <= byte.MaxValue; value++)
            {
                byte mapped = MapMicrophoneVolume((byte)value);
                Assert.IsTrue(mapped >= previous,
                    $"Mapped microphone volume decreased at profile value {value}.");
                Assert.IsTrue(mapped <= 0x40,
                    "Physical DualSense ADC gain exceeded the protocol ceiling.");
                previous = mapped;
            }
        }

        [TestMethod]
        public void SpeakerSnapshotDoesNotRepeatMicrophoneAndDspControls()
        {
            byte[] report = new byte[64];
            report[4] = 0xFF; // 0x36 header: microphone stream remains enabled.
            report[13] = 0xFF;
            report[14] = 0xFF;
            report[19] = 0xFF;
            report[21] = 0x01;
            report[22] = 0x50;
            report[57] = 0x12;
            report[58] = 0x34;
            report[59] = 0x56;

            Assert.IsNotNull(SanitizeAudioSnapshotMethod);
            SanitizeAudioSnapshotMethod.Invoke(null, new object[] { report });
            ApplyVolumeMethod.Invoke(null, new object[] { report, (byte)255 });

            Assert.AreEqual((byte)0xFF, report[4],
                "Speaker snapshot sanitation disabled the microphone stream header.");
            Assert.AreEqual((byte)0xAF, report[13]);
            Assert.AreEqual((byte)0xFE, report[14]);
            Assert.AreEqual((byte)0x00, report[19]);
            Assert.AreEqual((byte)0x20, report[20]);
            Assert.AreEqual((byte)0x00, report[21]);
            Assert.AreEqual((byte)0x40, report[22]);
            Assert.AreEqual((byte)0x03, report[50]);
            Assert.AreEqual((byte)0x12, report[57]);
            Assert.AreEqual((byte)0x34, report[58]);
            Assert.AreEqual((byte)0x56, report[59]);
        }

        [TestMethod]
        public void MicOnlyPowerSaveControlIsRemovedFromSpeakerSnapshot()
        {
            byte[] report = new byte[64];
            report[14] = 0x03;
            report[22] = 0x10;

            Assert.IsNotNull(SanitizeAudioSnapshotMethod);
            SanitizeAudioSnapshotMethod.Invoke(null, new object[] { report });

            Assert.AreEqual((byte)0x00, report[14]);
            Assert.AreEqual((byte)0x00, report[22]);
        }

        private static byte MapVolume(byte value)
        {
            Assert.IsNotNull(MapVolumeMethod);
            return (byte)MapVolumeMethod.Invoke(null, new object[] { value });
        }

        private static byte MapMicrophoneVolume(byte value)
        {
            Assert.IsNotNull(MapMicrophoneVolumeMethod);
            return (byte)MapMicrophoneVolumeMethod.Invoke(null,
                new object[] { value });
        }

        private static byte InvokePacketType(bool headsetOnlyAudio)
        {
            Assert.IsNotNull(GetSpeakerPacketTypeMethod);
            return (byte)GetSpeakerPacketTypeMethod.Invoke(null,
                new object[] { headsetOnlyAudio });
        }
    }
}
