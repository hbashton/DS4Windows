using DS4Windows.InputDevices;

namespace DS4Windows
{
    /// <summary>
    /// Defines when a physical Bluetooth controller microphone has a real
    /// VIIPER capture consumer. Keep profile application, the overview UI, and
    /// the realtime microphone worker on the same eligibility rules so an
    /// unsupported virtual output can never arm and discard physical audio.
    /// </summary>
    internal static class ControllerMicrophoneRoutePolicy
    {
        internal static bool SupportsVirtualMicrophoneOutput(
            OutContType outputType)
        {
            outputType = outputType.Normalize();
            return outputType == OutContType.ViiperDS4 ||
                outputType == OutContType.ViiperDualSense ||
                outputType == OutContType.ViiperDualSenseEdge ||
                outputType == OutContType.ViiperX360 ||
                outputType == OutContType.ViiperXboxOne ||
                outputType == OutContType.ViiperSwitch2Pro;
        }

        internal static bool IsEligibleBluetoothSource(DS4Device source)
        {
            if (source?.HidDevice?.Attributes == null ||
                source.ConnectionType != ConnectionType.BT ||
                source.HidDevice.Attributes.VendorId != DS4Devices.SONY_VID)
            {
                return false;
            }

            int productId = source.HidDevice.Attributes.ProductId;
            return source.DeviceType switch
            {
                InputDeviceType.DS4 => productId == 0x05C4 ||
                    productId == 0x09CC,
                InputDeviceType.DualSense => productId == 0x0CE6 ||
                    productId == 0x0DF2,
                _ => false,
            };
        }

        internal static bool CanRouteDirectViiperMicrophone(
            bool profileEnabled, DS4Device source, OutContType outputType,
            ViiperOutDevice outputDevice)
        {
            return CanRouteDirectViiperMicrophone(profileEnabled,
                IsEligibleBluetoothSource(source), outputType,
                outputDevice?.SupportsActiveVirtualMicrophone == true);
        }

        internal static bool CanRouteDirectViiperMicrophone(
            bool profileEnabled, bool eligibleBluetoothSource,
            OutContType outputType, bool activeStreamSupportsMicrophone)
        {
            return profileEnabled && eligibleBluetoothSource &&
                SupportsVirtualMicrophoneOutput(outputType) &&
                activeStreamSupportsMicrophone;
        }

        internal static bool ShouldArmPhysicalBluetoothMicrophone(
            bool profileEnabled, DS4Device source, OutContType outputType,
            ViiperOutDevice outputDevice)
        {
            return CanRouteDirectViiperMicrophone(profileEnabled, source,
                outputType, outputDevice) &&
                outputDevice.IsVirtualMicrophoneInterfaceActive;
        }

        internal static bool ShouldArmPhysicalBluetoothMicrophone(
            bool profileEnabled, bool eligibleBluetoothSource,
            OutContType outputType, bool activeStreamSupportsMicrophone,
            bool virtualMicrophoneInterfaceActive)
        {
            return CanRouteDirectViiperMicrophone(profileEnabled,
                eligibleBluetoothSource, outputType,
                activeStreamSupportsMicrophone) &&
                virtualMicrophoneInterfaceActive;
        }
    }
}
