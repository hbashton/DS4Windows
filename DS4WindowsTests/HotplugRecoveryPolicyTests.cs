using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class HotplugRecoveryPolicyTests
    {
        [DataTestMethod]
        [DataRow(HotplugRecoveryPolicy.DeviceArrival, false, true)]
        [DataRow(HotplugRecoveryPolicy.DeviceArrival, true, true)]
        [DataRow(HotplugRecoveryPolicy.DeviceRemoveComplete, false, true)]
        [DataRow(HotplugRecoveryPolicy.DeviceRemoveComplete, true, true)]
        [DataRow(HotplugRecoveryPolicy.DeviceNodesChanged, false, true)]
        [DataRow(HotplugRecoveryPolicy.DeviceNodesChanged, true, false)]
        [DataRow(0x0018, false, false)]
        public void SelectsDeviceChangesThatNeedAHotplugScan(int changeType,
            bool hasManagedController, bool expected)
        {
            Assert.AreEqual(expected,
                HotplugRecoveryPolicy.ShouldQueueForDeviceChange(changeType,
                    hasManagedController));
        }

        [DataTestMethod]
        [DataRow(true, false, 0, true)]
        [DataRow(true, false, 2, true)]
        [DataRow(true, false, 3, false)]
        [DataRow(true, true, 0, false)]
        [DataRow(false, false, 0, false)]
        public void BoundsRecoveryToAnEmptyRunningService(bool serviceRunning,
            bool hasManagedController, int completedAttempts, bool expected)
        {
            Assert.AreEqual(expected,
                HotplugRecoveryPolicy.ShouldContinueRecovery(serviceRunning,
                    hasManagedController, completedAttempts));
        }
    }
}
