using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class PlayStationFeatureOutputPolicyTests
    {
        [DataTestMethod]
        [DataRow((int)OutContType.ViiperDS4)]
        [DataRow((int)OutContType.ViiperDualSense)]
        [DataRow((int)OutContType.ViiperDualSenseEdge)]
        [DataRow((int)OutContType.ViiperX360)]
        [DataRow((int)OutContType.ViiperSwitch2Pro)]
        public void EveryViiperPersonaKeepsPersistentAudioOwner(
            int outputType)
        {
            Assert.IsTrue(PlayStationFeatureOutputPolicy
                .SupportsPersistentAudioSidecar((OutContType)outputType));
        }

        [TestMethod]
        public void AudioAndGamepadRolesAreMutuallyExclusive()
        {
            Assert.ThrowsException<System.ArgumentException>(() =>
                new ViiperOutDevice(OutContType.ViiperDualSense,
                    ViiperVirtualDeviceType.DualSense,
                    audioOnlySidecar: true, gamepadOnly: true));
        }

        [DataTestMethod]
        [DataRow((int)OutContType.ViiperDS4)]
        [DataRow((int)OutContType.ViiperDualSense)]
        [DataRow((int)OutContType.ViiperDualSenseEdge)]
        public void PlayStationPrimaryOutputsAreGamepadOnly(int outputType)
        {
            var manager = new OutputSlotManager();
            var output = manager.AllocateController((OutContType)outputType)
                as ViiperOutDevice;

            Assert.IsNotNull(output);
            Assert.IsTrue(output.IsGamepadOnly);
            Assert.IsFalse(output.IsAudioOnlySidecar);
        }

        [DataTestMethod]
        [DataRow((int)OutContType.ViiperX360)]
        [DataRow((int)OutContType.ViiperSwitch2Pro)]
        public void NonPlayStationPrimaryOutputsDoNotOwnAudio(int outputType)
        {
            var manager = new OutputSlotManager();
            var output = manager.AllocateController((OutContType)outputType)
                as ViiperOutDevice;

            Assert.IsNotNull(output);
            Assert.IsFalse(output.IsGamepadOnly);
            Assert.IsFalse(output.IsAudioOnlySidecar);
        }

        [DataTestMethod]
        [DataRow((int)InputDeviceType.DS4, 0x05C4,
            (int)OutContType.ViiperX360, (int)OutContType.ViiperDS4)]
        [DataRow((int)InputDeviceType.DS4, 0x09CC,
            (int)OutContType.ViiperSwitch2Pro, (int)OutContType.ViiperDS4)]
        [DataRow((int)InputDeviceType.DualSense, 0x0CE6,
            (int)OutContType.ViiperX360,
            (int)OutContType.ViiperDualSense)]
        [DataRow((int)InputDeviceType.DualSense, 0x0DF2,
            (int)OutContType.ViiperSwitch2Pro,
            (int)OutContType.ViiperDualSense)]
        [DataRow((int)InputDeviceType.DS4, 0x05C4,
            (int)OutContType.ViiperDS4, (int)OutContType.ViiperDS4)]
        [DataRow((int)InputDeviceType.DualSense, 0x0CE6,
            (int)OutContType.ViiperDualSense,
            (int)OutContType.ViiperDualSense)]
        [DataRow((int)InputDeviceType.DualSense, 0x0CE6,
            (int)OutContType.ViiperDualSenseEdge,
            (int)OutContType.ViiperDualSense)]
        public void GenuineBluetoothPlayStationPadsGetAudioOnlySidecar(
            int deviceType, int productId, int primaryType, int expectedType)
        {
            OutContType actual = PlayStationFeatureOutputPolicy
                .GetAudioOnlySidecarType((InputDeviceType)deviceType,
                    ConnectionType.BT, DS4Devices.SONY_VID, productId,
                    (OutContType)primaryType, dInputOnly: false);

            Assert.AreEqual((OutContType)expectedType, actual);
        }

        [DataTestMethod]
        [DataRow((int)ConnectionType.USB, 0x054C, 0x0CE6,
            (int)OutContType.ViiperX360, false)]
        [DataRow((int)ConnectionType.BT, 0x1234, 0x0CE6,
            (int)OutContType.ViiperX360, false)]
        [DataRow((int)ConnectionType.BT, 0x054C, 0x0CE6,
            (int)OutContType.ViiperX360, true)]
        public void SidecarIsNotCreatedOutsideSupportedMatrix(
            int connectionType, int vendorId, int productId,
            int primaryType, bool dInputOnly)
        {
            OutContType actual = PlayStationFeatureOutputPolicy
                .GetAudioOnlySidecarType(InputDeviceType.DualSense,
                    (ConnectionType)connectionType, vendorId, productId,
                    (OutContType)primaryType, dInputOnly);

            Assert.AreEqual(OutContType.None, actual);
        }
    }
}
