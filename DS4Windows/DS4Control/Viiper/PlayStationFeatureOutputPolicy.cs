using DS4Windows.InputDevices;

namespace DS4Windows
{
    /// <summary>
    /// Keeps PlayStation hardware features independent from the controller
    /// persona presented to games. PlayStation profiles use one composite USB
    /// device so games can associate its HID and audio interfaces; Xbox and
    /// Switch profiles retain the controller's audio through a HID-free
    /// companion.
    /// </summary>
    internal static class PlayStationFeatureOutputPolicy
    {
        internal static bool IsPlayStationAudioOutput(
            OutContType outputType)
        {
            outputType = outputType.Normalize();
            return outputType == OutContType.ViiperDS4 ||
                outputType == OutContType.ViiperDualSense ||
                outputType == OutContType.ViiperDualSenseEdge;
        }

        internal static bool NeedsAudioOnlySidecar(OutContType outputType)
        {
            outputType = outputType.Normalize();
            return outputType == OutContType.ViiperX360 ||
                outputType == OutContType.ViiperXboxOne ||
                outputType == OutContType.ViiperSwitch2Pro;
        }

        internal static OutContType GetAudioOnlySidecarType(
            DS4Device source, OutContType primaryOutputType,
            bool dInputOnly)
        {
            if (source?.HidDevice?.Attributes == null)
            {
                return OutContType.None;
            }

            return GetAudioOnlySidecarType(source.DeviceType,
                source.ConnectionType,
                source.HidDevice.Attributes.VendorId,
                source.HidDevice.Attributes.ProductId,
                primaryOutputType, dInputOnly);
        }

        internal static OutContType GetAudioOnlySidecarType(
            InputDeviceType deviceType, ConnectionType connectionType,
            int vendorId, int productId, OutContType primaryOutputType,
            bool dInputOnly)
        {
            if (dInputOnly || connectionType != ConnectionType.BT ||
                vendorId != DS4Devices.SONY_VID ||
                !NeedsAudioOnlySidecar(primaryOutputType))
            {
                return OutContType.None;
            }

            return deviceType switch
            {
                InputDeviceType.DS4 when productId == 0x05C4 ||
                    productId == 0x09CC => OutContType.ViiperDS4,
                InputDeviceType.DualSense when productId == 0x0CE6 ||
                    productId == 0x0DF2 => OutContType.ViiperDualSense,
                _ => OutContType.None,
            };
        }

        internal static ViiperVirtualDeviceType GetViiperType(
            OutContType outputType)
        {
            return outputType.Normalize() switch
            {
                OutContType.ViiperDS4 => ViiperVirtualDeviceType.DualShock4,
                OutContType.ViiperDualSense => ViiperVirtualDeviceType.DualSense,
                _ => throw new System.ArgumentOutOfRangeException(
                    nameof(outputType), outputType,
                    "Audio-only sidecars are available only for PlayStation outputs."),
            };
        }
    }
}
