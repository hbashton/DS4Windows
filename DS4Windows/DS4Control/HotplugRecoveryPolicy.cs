namespace DS4Windows
{
    internal static class HotplugRecoveryPolicy
    {
        internal const int DeviceNodesChanged = 0x0007;
        internal const int DeviceArrival = 0x8000;
        internal const int DeviceRemoveComplete = 0x8004;

        internal const int RecoveryIntervalMilliseconds = 10_000;
        internal const int MaximumRecoveryAttempts = 3;

        internal static bool ShouldQueueForDeviceChange(int changeType,
            bool hasManagedController)
        {
            if (changeType == DeviceArrival || changeType == DeviceRemoveComplete)
            {
                return true;
            }

            // Windows can keep the paired HID interface present after a Bluetooth
            // disconnect. A device-tree change is the reliable signal when that
            // interface becomes usable again, but virtual-device changes can emit
            // the same notification. Only use it while no input pad is managed.
            return changeType == DeviceNodesChanged && !hasManagedController;
        }

        internal static bool ShouldContinueRecovery(bool serviceRunning,
            bool hasManagedController, int completedAttempts)
        {
            return serviceRunning && !hasManagedController &&
                completedAttempts < MaximumRecoveryAttempts;
        }
    }
}
