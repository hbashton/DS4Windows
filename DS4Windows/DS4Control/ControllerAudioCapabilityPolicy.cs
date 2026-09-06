using DS4Windows.InputDevices;

namespace DS4Windows;

/// <summary>
/// Physical speaker/headset and microphone applicability shared by runtime
/// status and the UI. Saved profile settings and the selected virtual persona
/// do not grant audio capabilities to an unrelated physical controller.
/// </summary>
internal static class ControllerAudioCapabilityPolicy
{
    internal static bool SupportsControllerAudio(DS4Device device) =>
        device != null && SupportsControllerAudio(device.DeviceType,
            device.ConnectionType, device.HidDevice?.Attributes?.VendorId,
            device.HidDevice?.Attributes?.ProductId);

    internal static bool SupportsControllerAudio(InputDeviceType? deviceType,
        ConnectionType? connectionType, int? vendorId, int? productId)
    {
        if (!IsSupportedSonyController(deviceType, vendorId, productId))
        {
            return false;
        }

        return deviceType == InputDeviceType.DualSense ?
            connectionType is ConnectionType.BT or ConnectionType.USB :
            deviceType == InputDeviceType.DS4 &&
                connectionType == ConnectionType.BT;
    }

    internal static bool IsSupportedSonyController(InputDeviceType? deviceType,
        int? vendorId, int? productId) =>
        vendorId == DS4Devices.SONY_VID && (deviceType switch
        {
            InputDeviceType.DS4 => productId is 0x05C4 or 0x09CC,
            InputDeviceType.DualSense => productId is 0x0CE6 or 0x0DF2,
            _ => false,
        });
}
