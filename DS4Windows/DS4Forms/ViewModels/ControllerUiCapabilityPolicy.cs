using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    internal enum ControllerMicrophoneUiStatus
    {
        Ready,
        OfflineConfiguration,
        UsbLegacyRoute,
        RequiresCompatibleController,
        RequiresBluetooth,
        RequiresPlayStationOutput,
        OutputStarting,
    }

    internal readonly struct ControllerMicrophoneUiState
    {
        internal ControllerMicrophoneUiState(ControllerMicrophoneUiStatus status,
            bool canEnable, string message)
        {
            Status = status;
            CanEnable = canEnable;
            Message = message ?? string.Empty;
        }

        internal ControllerMicrophoneUiStatus Status { get; }
        internal bool CanEnable { get; }
        internal string Message { get; }
        internal bool ShowMessage => !string.IsNullOrEmpty(Message);
        internal bool CanChange(bool currentlyEnabled) =>
            CanEnable || currentlyEnabled;
        internal bool CanAdjustLevel(bool currentlyEnabled) =>
            CanEnable && currentlyEnabled;
    }

    /// <summary>
    /// Keeps physical-controller presentation decisions in one place. Profile
    /// settings remain stored for every controller, while the frontend only
    /// advertises hardware features that the selected physical pad can use.
    /// </summary>
    internal sealed class ControllerUiCapabilities
    {
        private ControllerUiCapabilities(InputDeviceType? deviceType,
            string controllerName, string imageResourceName,
            bool isPlayStationController, bool showControllerAudioSettings,
            bool showDualSenseHardwareControls, string feedbackLabel,
            string audioHeader, string audioDescription,
            string microphoneToggleLabel,
            string microphoneDescription)
        {
            DeviceType = deviceType;
            ControllerName = controllerName;
            ImageResourceName = imageResourceName;
            IsPlayStationController = isPlayStationController;
            ShowControllerAudioSettings = showControllerAudioSettings;
            ShowDualSenseHardwareControls = showDualSenseHardwareControls;
            FeedbackLabel = feedbackLabel;
            AudioHeader = audioHeader;
            AudioDescription = audioDescription;
            MicrophoneToggleLabel = microphoneToggleLabel;
            MicrophoneDescription = microphoneDescription;
        }

        internal InputDeviceType? DeviceType { get; }
        internal string ControllerName { get; }
        internal string ImageResourceName { get; }
        internal bool HasControllerArtwork => ImageResourceName != null;
        internal bool IsPlayStationController { get; }
        internal bool IsDualShock4 => DeviceType == InputDeviceType.DS4;
        internal bool IsDualSense => DeviceType == InputDeviceType.DualSense;
        internal ConnectionType? ConnectionType { get; private set; }
        internal int? VendorId { get; private set; }
        internal int? ProductId { get; private set; }
        internal bool PhysicalIdentityKnown { get; private set; }
        internal bool ShowControllerAudioSettings { get; }
        internal bool ShowDualSenseHardwareControls { get; }
        internal bool ShowPlayStationControllerSettings =>
            ShowControllerAudioSettings || ShowDualSenseHardwareControls;
        // With no physical controller selected, keep the complete profile
        // surface available so offline profile editing never loses features.
        internal bool SupportsAdaptiveTriggers => DeviceType == null || IsDualSense;
        internal bool SupportsAdvancedHaptics => DeviceType == null || IsDualSense;
        internal bool SupportsMuteButton => DeviceType == null || IsDualSense;
        internal string FeedbackLabel { get; }
        internal string AudioHeader { get; }
        internal string AudioDescription { get; }
        internal string MicrophoneToggleLabel { get; }
        internal string MicrophoneDescription { get; }

        internal bool IsGenuineSonyController => PhysicalIdentityKnown &&
            VendorId == DS4Devices.SONY_VID && ProductId.HasValue &&
            IsSupportedSonyProduct(DeviceType, ProductId.Value);

        internal bool SupportsControllerAudio
        {
            get
            {
                if (!PhysicalIdentityKnown)
                {
                    return DeviceType == InputDeviceType.DS4 || IsDualSense;
                }

                if (!IsGenuineSonyController)
                {
                    return false;
                }

                return IsDualSense ?
                    ConnectionType == DS4Windows.ConnectionType.BT ||
                        ConnectionType == DS4Windows.ConnectionType.USB :
                    IsDualShock4 && ConnectionType == DS4Windows.ConnectionType.BT;
            }
        }

        internal bool ShowUsbDualSenseSpeakerSelector => IsDualSense &&
            PhysicalIdentityKnown && IsGenuineSonyController &&
            ConnectionType == DS4Windows.ConnectionType.USB;

        internal bool ShowLegacyMicrophoneRouting => DeviceType == null ||
            IsDualSense && PhysicalIdentityKnown && IsGenuineSonyController &&
            ConnectionType == DS4Windows.ConnectionType.USB;

        internal ControllerMicrophoneUiState GetMicrophoneUiState(
            OutContType outputType, bool activeStreamSupportsMicrophone,
            bool requireActiveStream)
        {
            if (DeviceType == null || !PhysicalIdentityKnown)
            {
                return new ControllerMicrophoneUiState(
                    ControllerMicrophoneUiStatus.OfflineConfiguration,
                    canEnable: true,
                    "These settings will activate when the profile is used with a compatible PlayStation controller.");
            }

            if (!IsGenuineSonyController ||
                (!IsDualShock4 && !IsDualSense))
            {
                return new ControllerMicrophoneUiState(
                    ControllerMicrophoneUiStatus.RequiresCompatibleController,
                    canEnable: false,
                    "Controller microphone routing requires a genuine Sony DualShock 4 or DualSense.");
            }

            if (IsDualSense &&
                ConnectionType == DS4Windows.ConnectionType.USB)
            {
                return new ControllerMicrophoneUiState(
                    ControllerMicrophoneUiStatus.UsbLegacyRoute,
                    canEnable: true,
                    "USB DualSense microphone routing uses the capture and virtual-output endpoints in Advanced audio.");
            }

            if (ConnectionType != DS4Windows.ConnectionType.BT)
            {
                return new ControllerMicrophoneUiState(
                    ControllerMicrophoneUiStatus.RequiresBluetooth,
                    canEnable: false,
                    "Direct controller microphone input requires a Bluetooth DualShock 4 or DualSense.");
            }

            if (!ControllerMicrophoneRoutePolicy
                .SupportsVirtualMicrophoneOutput(outputType))
            {
                return new ControllerMicrophoneUiState(
                    ControllerMicrophoneUiStatus.RequiresPlayStationOutput,
                    canEnable: false,
                    "A VIIPER PlayStation audio interface is required to expose the controller microphone.");
            }

            if (requireActiveStream && !activeStreamSupportsMicrophone)
            {
                return new ControllerMicrophoneUiState(
                    ControllerMicrophoneUiStatus.OutputStarting,
                    canEnable: false,
                    "The VIIPER microphone interface is starting. This control will become available automatically.");
            }

            return new ControllerMicrophoneUiState(
                ControllerMicrophoneUiStatus.Ready, canEnable: true,
                string.Empty);
        }

        internal bool IsMappingControlAvailable(DS4Controls control,
            bool isDualSenseEdge)
        {
            if (DeviceType == null)
            {
                return true;
            }

            if (IsDualShock4)
            {
                return control != DS4Controls.Mute &&
                    control != DS4Controls.Capture &&
                    control != DS4Controls.SideL &&
                    control != DS4Controls.SideR &&
                    control != DS4Controls.FnL &&
                    control != DS4Controls.FnR &&
                    control != DS4Controls.BLP &&
                    control != DS4Controls.BRP;
            }

            if (IsDualSense)
            {
                if (control == DS4Controls.Capture ||
                    control == DS4Controls.SideL ||
                    control == DS4Controls.SideR)
                {
                    return false;
                }

                if (!isDualSenseEdge &&
                    (control == DS4Controls.FnL ||
                        control == DS4Controls.FnR ||
                        control == DS4Controls.BLP ||
                        control == DS4Controls.BRP))
                {
                    return false;
                }
            }

            return true;
        }

        internal bool IsControllerMapListOnlyControl(DS4Controls control,
            bool isDualSenseEdge)
        {
            if (!IsDualSense || !isDualSenseEdge)
            {
                return false;
            }

            // The current controller artwork is the standard DualSense front
            // view. Edge-only controls remain fully remappable from the list,
            // but must not claim inaccurate hit targets on that diagram.
            return control == DS4Controls.FnL ||
                control == DS4Controls.FnR ||
                control == DS4Controls.BLP ||
                control == DS4Controls.BRP;
        }

        internal static ControllerUiCapabilities ForDevice(DS4Device device)
        {
            if (device == null)
            {
                return For(null);
            }

            int? vendorId = device.HidDevice?.Attributes?.VendorId;
            int? productId = device.HidDevice?.Attributes?.ProductId;
            return For(device.DeviceType, device.ConnectionType, vendorId,
                productId, physicalIdentityKnown: true);
        }

        internal static ControllerUiCapabilities For(InputDeviceType? deviceType)
        {
            return For(deviceType, null, null, null,
                physicalIdentityKnown: false);
        }

        internal static ControllerUiCapabilities For(InputDeviceType? deviceType,
            ConnectionType? connectionType, int? vendorId, int? productId)
        {
            return For(deviceType, connectionType, vendorId, productId,
                physicalIdentityKnown: true);
        }

        private static ControllerUiCapabilities For(InputDeviceType? deviceType,
            ConnectionType? connectionType, int? vendorId, int? productId,
            bool physicalIdentityKnown)
        {
            ControllerUiCapabilities capabilities = deviceType switch
            {
                InputDeviceType.DS4 => new ControllerUiCapabilities(
                    deviceType,
                    "DualShock 4",
                    "DualShock 4 Controller.png",
                    isPlayStationController: true,
                    showControllerAudioSettings: true,
                    showDualSenseHardwareControls: false,
                    feedbackLabel: "Rumble strength",
                    audioHeader: "DualShock 4 audio",
                    audioDescription:
                        "Bluetooth speaker output and headset-mic input through the controller's 3.5 mm jack.",
                    microphoneToggleLabel: "Enable headset microphone input",
                    microphoneDescription:
                        "DualShock 4 microphone input comes from a headset connected to the controller's 3.5 mm jack."),
                InputDeviceType.DualSense => new ControllerUiCapabilities(
                    deviceType,
                    productId == 0x0DF2 ? "DualSense Edge" : "DualSense",
                    productId == 0x0DF2
                        ? "DualSense Edge Controller.png"
                        : "DualSense Controller.png",
                    isPlayStationController: true,
                    showControllerAudioSettings: true,
                    showDualSenseHardwareControls: true,
                    feedbackLabel: "Haptic feedback strength",
                    audioHeader: "DualSense audio",
                    audioDescription:
                        "Speaker and microphone routing for the selected physical DualSense.",
                    microphoneToggleLabel: "Enable controller microphone input",
                    microphoneDescription:
                        "Uses the DualSense built-in microphone or a headset connected to the controller."),
                InputDeviceType.SwitchPro => new ControllerUiCapabilities(
                    deviceType,
                    productId == 0x2069 ? "Switch 2 Pro" : "Switch Pro",
                    "Switch 2 Pro Controller.png",
                    isPlayStationController: false,
                    showControllerAudioSettings: false,
                    showDualSenseHardwareControls: false,
                    feedbackLabel: "Rumble strength",
                    audioHeader: "Controller audio",
                    audioDescription: "Controller audio is not available on this device.",
                    microphoneToggleLabel: "Enable controller microphone input",
                    microphoneDescription: "Controller microphone input is not available on this device."),
                InputDeviceType.JoyConL or
                InputDeviceType.JoyConR or
                InputDeviceType.JoyConGrip => new ControllerUiCapabilities(
                    deviceType,
                    deviceType == InputDeviceType.JoyConL ? "Joy-Con (L)" :
                        deviceType == InputDeviceType.JoyConR ? "Joy-Con (R)" :
                        "Joy-Con Grip",
                    "Switch 2 Pro Controller.png",
                    isPlayStationController: false,
                    showControllerAudioSettings: false,
                    showDualSenseHardwareControls: false,
                    feedbackLabel: "Rumble strength",
                    audioHeader: "Controller audio",
                    audioDescription: "Controller audio is not available on this device.",
                    microphoneToggleLabel: "Enable controller microphone input",
                    microphoneDescription: "Controller microphone input is not available on this device."),
                InputDeviceType.DS3 => new ControllerUiCapabilities(
                    deviceType,
                    "DualShock 3",
                    "DualShock 4 Controller.png",
                    isPlayStationController: true,
                    showControllerAudioSettings: false,
                    showDualSenseHardwareControls: false,
                    feedbackLabel: "Rumble strength",
                    audioHeader: "Controller audio",
                    audioDescription: "Controller audio is not available on DualShock 3.",
                    microphoneToggleLabel: "Enable controller microphone input",
                    microphoneDescription: "Controller microphone input is not available on DualShock 3."),
                null => new ControllerUiCapabilities(
                    null,
                    "Generic profile",
                    null,
                    isPlayStationController: false,
                    showControllerAudioSettings: true,
                    showDualSenseHardwareControls: true,
                    feedbackLabel: "Feedback strength",
                    audioHeader: "PlayStation controller audio",
                    audioDescription:
                        "Speaker and microphone settings for compatible PlayStation controllers.",
                    microphoneToggleLabel: "Enable controller microphone input",
                    microphoneDescription:
                        "These settings become active when the profile is used with a compatible PlayStation controller."),
                _ => new ControllerUiCapabilities(
                    deviceType,
                    "Controller",
                    null,
                    isPlayStationController: false,
                    showControllerAudioSettings: false,
                    showDualSenseHardwareControls: false,
                    feedbackLabel: "Rumble strength",
                    audioHeader: "PlayStation controller audio",
                    audioDescription:
                        "Select a compatible PlayStation controller to use controller audio.",
                    microphoneToggleLabel: "Enable controller microphone input",
                    microphoneDescription:
                        "Controller audio is available on compatible PlayStation controllers."),
            };

            capabilities.ConnectionType = connectionType;
            capabilities.VendorId = vendorId;
            capabilities.ProductId = productId;
            capabilities.PhysicalIdentityKnown = physicalIdentityKnown;
            return capabilities;
        }

        private static bool IsSupportedSonyProduct(InputDeviceType? deviceType,
            int productId)
        {
            return deviceType switch
            {
                InputDeviceType.DS4 => productId == 0x05C4 ||
                    productId == 0x09CC,
                InputDeviceType.DualSense => productId == 0x0CE6 ||
                    productId == 0x0DF2,
                _ => false,
            };
        }
    }
}
