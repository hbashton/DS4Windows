using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class MoonlightVirtualDevicePolicyTests
    {
        [TestMethod]
        public void PhysicalSupportedControllersAreAlwaysAccepted()
        {
            Assert.IsTrue(MoonlightVirtualDevicePolicy.ShouldAccept(
                false, false, false, false));
        }

        [TestMethod]
        public void OwnVirtualOutputsAreNeverAccepted()
        {
            Assert.IsFalse(MoonlightVirtualDevicePolicy.ShouldAccept(
                true, true, true, true));
        }

        [TestMethod]
        public void SunshineVirtualControllersAreAcceptedWhenEnabled()
        {
            Assert.IsTrue(MoonlightVirtualDevicePolicy.ShouldAccept(
                false, true, true, true));
        }

        [TestMethod]
        public void UnattributedVirtualControllersRemainRejected()
        {
            Assert.IsFalse(MoonlightVirtualDevicePolicy.ShouldAccept(
                false, true, true, false));
            Assert.IsFalse(MoonlightVirtualDevicePolicy.ShouldAccept(
                false, true, false, true));
        }

        [TestMethod]
        public void AdmissionHasNoGlobalSingleControllerCooldown()
        {
            for (int controller = 0; controller < 8; controller++)
            {
                Assert.IsTrue(MoonlightVirtualDevicePolicy.ShouldAccept(
                    false, true, true, true));
            }
        }
    }
}
