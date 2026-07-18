using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerAudioEndpointTests
    {
        [DataTestMethod]
        [DataRow(@"USB\VID_054C&PID_09CC&MI_01", (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow(@"USB\VID_054C&PID_05C4&MI_01", (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow(@"USB\VID_054C&PID_0CE6&MI_01", (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow(@"USB\VID_054C&PID_0DF2&MI_01", (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow("DualSense Wireless Controller", (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow("DualShock 4 Controller", (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow("Wireless Controller", (int)ControllerAudioEndpointKind.Any)]
        public void ClassifiesControllerAudioEndpointIdentity(string identity, int expected)
        {
            Assert.AreEqual((ControllerAudioEndpointKind)expected,
                DualSenseAudioPassthrough.ClassifyEndpointIdentity(identity));
        }

        [DataTestMethod]
        [DataRow(OutContType.ViiperDS4, (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow(OutContType.ViiperDualSense, (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow(OutContType.ViiperDualSenseEdge, (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow(OutContType.DS4, (int)ControllerAudioEndpointKind.Any)]
        public void MapsVirtualOutputToPreferredAudioEndpoint(OutContType output, int expected)
        {
            Assert.AreEqual((ControllerAudioEndpointKind)expected,
                DualSenseAudioPassthrough.GetEndpointKind(output));
        }
    }

    [TestClass]
    public class ViiperMicrophoneFormatTests
    {
        [TestMethod]
        public void DualShock4MicrophoneDownsamples48kMonoTo16kMono()
        {
            short[] source = new short[480];
            for (int frame = 0; frame < 160; frame++)
            {
                source[frame * 3] = (short)(frame - 80);
                source[frame * 3 + 1] = (short)(frame + 20);
                source[frame * 3 + 2] = (short)(frame + 120);
            }

            byte[] destination = new byte[320];
            int frames = ViiperOutDevice.ConvertMicrophoneMono48kToDualShock4Pcm(
                source, source.Length, destination);

            Assert.AreEqual(160, frames);
            for (int frame = 0; frame < frames; frame++)
            {
                short actual = (short)(destination[frame * 2] |
                    destination[frame * 2 + 1] << 8);
                Assert.AreEqual((short)(frame + 20), actual);
            }
        }

        [TestMethod]
        public void DualShock4MicrophonePadsPartialAndMutedFramesWithSilence()
        {
            short[] source = new short[480];
            Array.Fill(source, (short)900);
            byte[] destination = new byte[320];
            Array.Fill(destination, (byte)0xFF);

            int frames = ViiperOutDevice.ConvertMicrophoneMono48kToDualShock4Pcm(
                source, 6, destination);

            Assert.AreEqual(2, frames);
            Assert.AreEqual(900, (short)(destination[0] | destination[1] << 8));
            Assert.AreEqual(900, (short)(destination[2] | destination[3] << 8));
            for (int index = 4; index < destination.Length; index++)
            {
                Assert.AreEqual(0, destination[index]);
            }

            frames = ViiperOutDevice.ConvertMicrophoneMono48kToDualShock4Pcm(
                source, 0, destination);
            Assert.AreEqual(0, frames);
            CollectionAssert.AreEqual(new byte[destination.Length], destination);
        }
    }
}
