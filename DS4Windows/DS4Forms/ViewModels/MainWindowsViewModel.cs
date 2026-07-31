/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using DS4Windows;
using DS4WinWPF.ApiDTO;
using HttpProgress;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using DS4Windows.InputDevices;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public sealed class OverviewOutputControllerChoice
    {
        public OverviewOutputControllerChoice(string name, OutContType type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public OutContType Type { get; }
    }

    public sealed class QuickProfileSettingChangedEventArgs : EventArgs
    {
        public QuickProfileSettingChangedEventArgs(int deviceIndex,
            bool requiresProfileReload)
        {
            DeviceIndex = deviceIndex;
            RequiresProfileReload = requiresProfileReload;
        }

        public int DeviceIndex { get; }
        public bool RequiresProfileReload { get; }
    }

    public class MainWindowsViewModel
    {
        private static readonly int[] dualSenseHapticPercentages =
            { 100, 87, 75, 62, 50, 37, 25, 12 };

        private ObservableCollection<CompositeDeviceModel> controllerCol = new();

        public MainWindowsViewModel()
        {
            _ = RefreshControllerAudioChoicesAsync();
        }

        public ObservableCollection<CompositeDeviceModel> ControllerCol
        {
            get => controllerCol;
            set
            {
                if (ReferenceEquals(controllerCol, value)) return;
                controllerCol = value;
                ControllerColChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler ControllerColChanged;

        public IReadOnlyList<OverviewOutputControllerChoice> OutputControllerChoices { get; } =
            new List<OverviewOutputControllerChoice>
            {
                new("Xbox 360", OutContType.ViiperX360),
                new("DualShock 4", OutContType.ViiperDS4),
                new("DualSense", OutContType.ViiperDualSense),
                new("DualSense Edge", OutContType.ViiperDualSenseEdge),
                new("Switch 2 Pro", OutContType.ViiperSwitch2Pro),
            };

        private CompositeDeviceModel selectedController;
        private OverviewRuntimeSnapshot lastRuntimeSnapshot;
        private bool hasRuntimeSnapshot;
        private ControllerStartupStatus selectedControllerStartupStatus =
            ControllerRuntimeStatusPolicy.Evaluate(
                new ControllerRuntimeSignals(false, false, false, false,
                    false, false, ControllerRuntimeLaneState.NotRequired,
                    ControllerRuntimeLaneState.NotRequired,
                    ControllerRuntimeLaneState.NotRequired,
                    ControllerRuntimeLaneState.NotRequired,
                    "virtual controller"));

        public CompositeDeviceModel SelectedController
        {
            get => selectedController;
            set
            {
                if (ReferenceEquals(selectedController, value)) return;

                HookSelectedController(selectedController, false);
                selectedController = value;
                HookSelectedController(selectedController, true);
                hasRuntimeSnapshot = false;
                RefreshSelectedControllerProperties();
            }
        }

        public event EventHandler SelectedControllerChanged;
        public event EventHandler HasSelectedControllerChanged;
        public event EventHandler CurrentProfileNameChanged;
        public event EventHandler SelectedControllerConnectionChanged;
        public event EventHandler SelectedControllerLatencyChanged;
        public event EventHandler SelectedControllerBatteryChanged;
        public event EventHandler SelectedControllerStartupTitleChanged;
        public event EventHandler SelectedControllerStartupDetailChanged;
        public event EventHandler SelectedControllerIsReadyChanged;
        public event EventHandler SelectedControllerNeedsAttentionChanged;
        public event EventHandler SelectedControllerSupportsAudioChanged;
        public event EventHandler SelectedControllerSupportsMicrophoneChanged;
        public event EventHandler MicrophoneAvailabilityTextChanged;
        public event EventHandler ShowMicrophoneAvailabilityMessageChanged;
        public event EventHandler CanChangeMicrophoneInputChanged;
        public event EventHandler MicrophoneLevelControlsEnabledChanged;
        public event EventHandler SelectedControllerIsWirelessChanged;
        public event EventHandler SelectedOutputControllerChanged;
        public event EventHandler SelectedOutputControllerNameChanged;
        public event EventHandler HapticStrengthPercentChanged;
        public event EventHandler SpeakerOutputEnabledChanged;
        public event EventHandler HeadsetOnlyAudioChanged;
        public event EventHandler ControllerAudioSourceChoicesChanged;
        public event EventHandler ControllerAudioSourceIdChanged;
        public event EventHandler AudioHapticsSpeakerOverrideActiveChanged;
        public event EventHandler MicrophoneInputEnabledChanged;
        public event EventHandler SpeakerVolumePercentChanged;
        public event EventHandler HeadphoneVolumePercentChanged;
        public event EventHandler SpeakerCompressionIndexChanged;
        public event EventHandler SpeakerBassBoostDbChanged;
        public event EventHandler MicrophoneVolumePercentChanged;
        public event EventHandler MicrophoneNoiseSuppressionIndexChanged;
        public event EventHandler<QuickProfileSettingChangedEventArgs> QuickProfileSettingChanged;

        public bool HasSelectedController => selectedController != null;

        public string CurrentProfileName =>
            string.IsNullOrWhiteSpace(selectedController?.SelectedProfile)
                ? "No profile selected"
                : selectedController.SelectedProfile;

        public string SelectedControllerConnection => selectedController?.ConnectionText ?? "Not connected";

        public string SelectedControllerLatency => selectedController?.LatencyText ?? "--";

        public string SelectedControllerBattery =>
            selectedController?.BatteryState ?? "--";

        public string SelectedControllerStartupTitle =>
            selectedControllerStartupStatus.Title;

        public string SelectedControllerStartupDetail =>
            selectedControllerStartupStatus.Detail;

        public ControllerStartupStage SelectedControllerStartupStage =>
            selectedControllerStartupStatus.Stage;

        public bool SelectedControllerIsReady =>
            selectedControllerStartupStatus.IsReady;

        public bool SelectedControllerNeedsAttention =>
            selectedControllerStartupStatus.NeedsAttention;

        public bool SelectedControllerSupportsAudio =>
            selectedController?.SupportsControllerAudio == true;

        private ControllerMicrophoneUiState SelectedControllerMicrophoneUiState
        {
            get
            {
                if (!HasValidSelectedDevice)
                {
                    return new ControllerMicrophoneUiState(
                        ControllerMicrophoneUiStatus.RequiresCompatibleController,
                        canEnable: false,
                        "Select a compatible PlayStation controller to configure microphone input.");
                }

                int deviceIndex = selectedController.DevIndex;
                ViiperOutDevice outputDevice = App.rootHub?
                    .GetPlayStationFeatureOutput(deviceIndex);
                OutContType outputType = outputDevice?.OutputType ??
                    OutContType.None;
                return ControllerUiCapabilities.ForDevice(selectedController.Device)
                    .GetMicrophoneUiState(outputType,
                        outputDevice?.SupportsActiveVirtualMicrophone == true,
                        requireActiveStream: true);
            }
        }

        public bool SelectedControllerSupportsMicrophone =>
            SelectedControllerMicrophoneUiState.CanEnable;

        public string MicrophoneAvailabilityText =>
            SelectedControllerMicrophoneUiState.Message;

        public bool ShowMicrophoneAvailabilityMessage =>
            SelectedControllerMicrophoneUiState.ShowMessage;

        // Keep an already-enabled but no-longer-supported profile switch
        // actionable so the user can turn it off from Overview.
        public bool CanChangeMicrophoneInput => HasValidSelectedDevice &&
            SelectedControllerMicrophoneUiState.CanChange(
                MicrophoneInputEnabled);

        public bool MicrophoneLevelControlsEnabled =>
            SelectedControllerMicrophoneUiState.CanAdjustLevel(
                MicrophoneInputEnabled);

        public bool SelectedControllerIsWireless => selectedController?.IsWireless == true;

        public OutContType SelectedOutputController
        {
            get => HasValidSelectedDevice ?
                Global.OutContType[selectedController.DevIndex].Normalize() :
                OutContType.None;
            set
            {
                value = value.Normalize();
                if (!HasValidSelectedDevice || value == OutContType.None ||
                    Global.OutContType[selectedController.DevIndex].Normalize() == value)
                {
                    return;
                }

                int deviceIndex = selectedController.DevIndex;
                Global.OutContType[deviceIndex] = value;
                Global.outDevTypeTemp[deviceIndex] = value;
                SelectedOutputControllerChanged?.Invoke(this, EventArgs.Empty);
                SelectedOutputControllerNameChanged?.Invoke(this, EventArgs.Empty);
                RaiseMicrophoneCapabilityChanged();
                RaiseQuickProfileSettingChanged(deviceIndex,
                    requiresProfileReload: true);
            }
        }

        public string SelectedOutputControllerName
        {
            get
            {
                OutContType selectedType = SelectedOutputController;
                foreach (OverviewOutputControllerChoice choice in OutputControllerChoices)
                {
                    if (choice.Type == selectedType)
                    {
                        return choice.Name;
                    }
                }

                return "No emulated device";
            }
        }

        public int HapticStrengthPercent
        {
            get
            {
                if (!HasValidSelectedDevice) return 0;

                int deviceIndex = selectedController.DevIndex;
                if (selectedController.Device.DeviceType == InputDeviceType.DualSense)
                {
                    int levelIndex = Math.Clamp(Global.DualSenseHapticPowerLevel[deviceIndex],
                        0, dualSenseHapticPercentages.Length - 1);
                    return dualSenseHapticPercentages[levelIndex];
                }

                return Math.Clamp((int)Global.RumbleBoost[deviceIndex], 0, 100);
            }
            set
            {
                if (!HasValidSelectedDevice) return;

                int deviceIndex = selectedController.DevIndex;
                int requested = Math.Clamp(value, 0, 100);
                if (selectedController.Device.DeviceType == InputDeviceType.DualSense)
                {
                    int nearestIndex = 0;
                    int nearestDistance = int.MaxValue;
                    for (int i = 0; i < dualSenseHapticPercentages.Length; i++)
                    {
                        int distance = Math.Abs(dualSenseHapticPercentages[i] - requested);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestIndex = i;
                        }
                    }

                    if (Global.DualSenseHapticPowerLevel[deviceIndex] == nearestIndex) return;
                    Global.DualSenseHapticPowerLevel[deviceIndex] = (byte)nearestIndex;
                }
                else
                {
                    if (Global.RumbleBoost[deviceIndex] == requested) return;
                    Global.RumbleBoost[deviceIndex] = (byte)requested;
                }

                HapticStrengthPercentChanged?.Invoke(this, EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public bool SpeakerOutputEnabled
        {
            get => HasValidSelectedDevice && Global.DualSenseEnableSpeakerOutput[selectedController.DevIndex];
            set
            {
                if (!HasValidSelectedDevice ||
                    Global.DualSenseEnableSpeakerOutput[selectedController.DevIndex] == value) return;

                int deviceIndex = selectedController.DevIndex;
                Global.DualSenseEnableSpeakerOutput[deviceIndex] = value;
                SpeakerOutputEnabledChanged?.Invoke(this, EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public bool HeadsetOnlyAudio
        {
            get => HasValidSelectedDevice &&
                Global.DualSenseHeadsetOnlyAudio[selectedController.DevIndex];
            set
            {
                if (!HasValidSelectedDevice ||
                    Global.DualSenseHeadsetOnlyAudio[
                        selectedController.DevIndex] == value)
                {
                    return;
                }

                int deviceIndex = selectedController.DevIndex;
                Global.DualSenseHeadsetOnlyAudio[deviceIndex] = value;
                HeadsetOnlyAudioChanged?.Invoke(this, EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public bool MicrophoneInputEnabled
        {
            get => HasValidSelectedDevice && Global.DualSenseEnableMicrophonePassthrough[selectedController.DevIndex];
            set
            {
                if (!HasValidSelectedDevice ||
                    Global.DualSenseEnableMicrophonePassthrough[selectedController.DevIndex] == value) return;

                int deviceIndex = selectedController.DevIndex;
                Global.DualSenseEnableMicrophonePassthrough[deviceIndex] = value;
                MicrophoneInputEnabledChanged?.Invoke(this, EventArgs.Empty);
                CanChangeMicrophoneInputChanged?.Invoke(this, EventArgs.Empty);
                MicrophoneLevelControlsEnabledChanged?.Invoke(this,
                    EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public int SpeakerVolumePercent
        {
            get => HasValidSelectedDevice
                ? ByteToPercent(Global.DualSenseSpeakerVolume[selectedController.DevIndex])
                : 0;
            set
            {
                if (!HasValidSelectedDevice) return;

                int deviceIndex = selectedController.DevIndex;
                byte converted = PercentToByte(value);
                if (Global.DualSenseSpeakerVolume[deviceIndex] == converted) return;
                Global.DualSenseSpeakerVolume[deviceIndex] = converted;
                SpeakerVolumePercentChanged?.Invoke(this, EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public List<AudioEndpointChoice> ControllerAudioSourceChoices =>
            AudioEndpointChoiceCache.BuildControllerAudioChoices(
                ControllerAudioSourceId);

        public string ControllerAudioSourceId
        {
            get => HasValidSelectedDevice
                ? Global.DualSenseAudioCaptureEndpointId[
                    selectedController.DevIndex]
                : string.Empty;
            set
            {
                if (!HasValidSelectedDevice) return;

                int deviceIndex = selectedController.DevIndex;
                string normalized = value ?? string.Empty;
                if (AudioEndpointChoiceCache.RenderEndpoints.Any(
                        endpoint => endpoint.IsControllerAudio &&
                            string.Equals(endpoint.EndpointId, normalized,
                                StringComparison.Ordinal)))
                {
                    normalized = string.Empty;
                }

                AudioHapticsProfileSettings audioHaptics =
                    Global.store.audioHapticsSettings[deviceIndex];
                bool releasedOverride =
                    audioHaptics?.StreamAppAudioToController == true;
                bool sourceChanged = !string.Equals(
                    Global.DualSenseAudioCaptureEndpointId[deviceIndex],
                    normalized, StringComparison.Ordinal);
                if (!sourceChanged && !releasedOverride) return;

                Global.DualSenseAudioCaptureEndpointId[deviceIndex] =
                    normalized;
                if (releasedOverride)
                {
                    audioHaptics.StreamAppAudioToController = false;
                    audioHaptics.StreamAppAudioToHeadsetOnly = false;
                    AudioHapticsSpeakerOverrideActiveChanged?.Invoke(this,
                        EventArgs.Empty);
                }

                ControllerAudioSourceIdChanged?.Invoke(this,
                    EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public bool AudioHapticsSpeakerOverrideActive =>
            HasValidSelectedDevice &&
            ControlService.IsAudioHapticsSpeakerOverrideActive(
                selectedController.DevIndex);

        public int HeadphoneVolumePercent
        {
            get => HasValidSelectedDevice
                ? ByteToPercent(Global.DualSenseHeadphoneVolume[
                    selectedController.DevIndex])
                : 0;
            set
            {
                if (!HasValidSelectedDevice) return;

                int deviceIndex = selectedController.DevIndex;
                byte converted = PercentToByte(value);
                if (Global.DualSenseHeadphoneVolume[deviceIndex] == converted)
                {
                    return;
                }

                Global.DualSenseHeadphoneVolume[deviceIndex] = converted;
                HeadphoneVolumePercentChanged?.Invoke(this, EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public int SpeakerCompressionIndex
        {
            get => HasValidSelectedDevice
                ? Global.DualSenseSpeakerCompression[
                    selectedController.DevIndex]
                : 0;
            set
            {
                if (!HasValidSelectedDevice) return;
                int deviceIndex = selectedController.DevIndex;
                byte converted = (byte)Math.Clamp(value,
                    (int)DualSenseSpeakerCompression.Off,
                    (int)DualSenseSpeakerCompression.Strong);
                if (Global.DualSenseSpeakerCompression[deviceIndex] ==
                    converted) return;
                Global.DualSenseSpeakerCompression[deviceIndex] = converted;
                SpeakerCompressionIndexChanged?.Invoke(this,
                    EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public int SpeakerBassBoostDb
        {
            get => HasValidSelectedDevice
                ? Global.DualSenseSpeakerBassBoost[
                    selectedController.DevIndex]
                : 0;
            set
            {
                if (!HasValidSelectedDevice) return;
                int deviceIndex = selectedController.DevIndex;
                byte converted = (byte)Math.Clamp(value, 0,
                    DualSenseSpeakerProcessor.MaximumBassBoostDb);
                if (Global.DualSenseSpeakerBassBoost[deviceIndex] ==
                    converted) return;
                Global.DualSenseSpeakerBassBoost[deviceIndex] = converted;
                SpeakerBassBoostDbChanged?.Invoke(this, EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public int MicrophoneVolumePercent
        {
            get => HasValidSelectedDevice
                ? ByteToPercent(Global.DualSenseMicrophoneVolume[selectedController.DevIndex])
                : 0;
            set
            {
                if (!HasValidSelectedDevice) return;

                int deviceIndex = selectedController.DevIndex;
                byte converted = PercentToByte(value);
                if (Global.DualSenseMicrophoneVolume[deviceIndex] == converted) return;
                Global.DualSenseMicrophoneVolume[deviceIndex] = converted;
                MicrophoneVolumePercentChanged?.Invoke(this, EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public void RefreshSelectedControllerProperties(
            bool refreshControllerAudioChoices = true)
        {
            CaptureRuntimeSnapshot(App.rootHub);
            SelectedControllerChanged?.Invoke(this, EventArgs.Empty);
            HasSelectedControllerChanged?.Invoke(this, EventArgs.Empty);
            CurrentProfileNameChanged?.Invoke(this, EventArgs.Empty);
            SelectedControllerConnectionChanged?.Invoke(this, EventArgs.Empty);
            SelectedControllerLatencyChanged?.Invoke(this, EventArgs.Empty);
            SelectedControllerBatteryChanged?.Invoke(this, EventArgs.Empty);
            RaiseControllerStartupStatusChanged();
            SelectedControllerSupportsAudioChanged?.Invoke(this, EventArgs.Empty);
            RaiseMicrophoneCapabilityChanged();
            SelectedControllerIsWirelessChanged?.Invoke(this, EventArgs.Empty);
            SelectedOutputControllerChanged?.Invoke(this, EventArgs.Empty);
            SelectedOutputControllerNameChanged?.Invoke(this, EventArgs.Empty);
            HapticStrengthPercentChanged?.Invoke(this, EventArgs.Empty);
            SpeakerOutputEnabledChanged?.Invoke(this, EventArgs.Empty);
            HeadsetOnlyAudioChanged?.Invoke(this, EventArgs.Empty);
            if (refreshControllerAudioChoices)
            {
                ControllerAudioSourceChoicesChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            ControllerAudioSourceIdChanged?.Invoke(this, EventArgs.Empty);
            AudioHapticsSpeakerOverrideActiveChanged?.Invoke(this,
                EventArgs.Empty);
            MicrophoneInputEnabledChanged?.Invoke(this, EventArgs.Empty);
            SpeakerVolumePercentChanged?.Invoke(this, EventArgs.Empty);
            HeadphoneVolumePercentChanged?.Invoke(this, EventArgs.Empty);
            SpeakerCompressionIndexChanged?.Invoke(this, EventArgs.Empty);
            SpeakerBassBoostDbChanged?.Invoke(this, EventArgs.Empty);
            MicrophoneVolumePercentChanged?.Invoke(this, EventArgs.Empty);
            MicrophoneNoiseSuppressionIndexChanged?.Invoke(this,
                EventArgs.Empty);
        }

        public void RefreshRuntimeState(ControlService controlService)
        {
            // Controller discovery can add/remove an item while this UI timer
            // is firing. ObservableCollection's enumerator is fail-fast, so
            // an otherwise harmless startup overlap used to terminate the
            // whole application. Index a captured collection reference and
            // tolerate a concurrent removal; the next timer tick reconciles
            // anything that moved.
            ObservableCollection<CompositeDeviceModel> controllers =
                controllerCol;
            int count = controllers.Count;
            for (int index = 0; index < count; index++)
            {
                CompositeDeviceModel controller;
                try
                {
                    if (index >= controllers.Count)
                    {
                        break;
                    }
                    controller = controllers[index];
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                controller?.SynchronizeRuntimeProfile();
            }

            OverviewRuntimeSnapshot snapshot =
                CreateRuntimeSnapshot(controlService);
            if (!hasRuntimeSnapshot)
            {
                lastRuntimeSnapshot = snapshot;
                hasRuntimeSnapshot = true;
                RefreshSelectedControllerProperties();
                return;
            }

            OverviewRuntimeSnapshot previous = lastRuntimeSnapshot;
            lastRuntimeSnapshot = snapshot;

            if (previous.ProfileName != snapshot.ProfileName)
            {
                CurrentProfileNameChanged?.Invoke(this, EventArgs.Empty);
            }
            if (previous.Connection != snapshot.Connection)
            {
                SelectedControllerConnectionChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            if (previous.Latency != snapshot.Latency)
            {
                SelectedControllerLatencyChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            if (previous.Battery != snapshot.Battery)
            {
                SelectedControllerBatteryChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            if (previous.OutputController != snapshot.OutputController)
            {
                SelectedOutputControllerChanged?.Invoke(this, EventArgs.Empty);
                SelectedOutputControllerNameChanged?.Invoke(this,
                    EventArgs.Empty);
                RaiseMicrophoneCapabilityChanged();
            }
            if (previous.HapticStrength != snapshot.HapticStrength)
            {
                HapticStrengthPercentChanged?.Invoke(this, EventArgs.Empty);
            }
            if (previous.SpeakerEnabled != snapshot.SpeakerEnabled)
            {
                SpeakerOutputEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
            if (previous.HeadsetOnlyAudio != snapshot.HeadsetOnlyAudio)
            {
                HeadsetOnlyAudioChanged?.Invoke(this, EventArgs.Empty);
            }
            if (previous.MicrophoneEnabled != snapshot.MicrophoneEnabled)
            {
                MicrophoneInputEnabledChanged?.Invoke(this, EventArgs.Empty);
                RaiseMicrophoneCapabilityChanged();
            }
            if (previous.SpeakerVolume != snapshot.SpeakerVolume)
            {
                SpeakerVolumePercentChanged?.Invoke(this, EventArgs.Empty);
            }
            if (!string.Equals(previous.ControllerAudioSourceId,
                    snapshot.ControllerAudioSourceId,
                    StringComparison.Ordinal))
            {
                ControllerAudioSourceIdChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            if (previous.AudioHapticsSpeakerOverrideActive !=
                snapshot.AudioHapticsSpeakerOverrideActive)
            {
                AudioHapticsSpeakerOverrideActiveChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            if (previous.HeadphoneVolume != snapshot.HeadphoneVolume)
            {
                HeadphoneVolumePercentChanged?.Invoke(this, EventArgs.Empty);
            }
            if (previous.SpeakerCompression != snapshot.SpeakerCompression)
            {
                SpeakerCompressionIndexChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            if (previous.SpeakerBassBoost != snapshot.SpeakerBassBoost)
            {
                SpeakerBassBoostDbChanged?.Invoke(this, EventArgs.Empty);
            }
            if (previous.MicrophoneVolume != snapshot.MicrophoneVolume)
            {
                MicrophoneVolumePercentChanged?.Invoke(this, EventArgs.Empty);
            }
            if (previous.MicrophoneNoiseSuppression !=
                snapshot.MicrophoneNoiseSuppression)
            {
                MicrophoneNoiseSuppressionIndexChanged?.Invoke(this,
                    EventArgs.Empty);
            }
            if (previous.StartupStatus != snapshot.StartupStatus)
            {
                selectedControllerStartupStatus = snapshot.StartupStatus;
                RaiseControllerStartupStatusChanged();
            }
        }

        private void RaiseControllerStartupStatusChanged()
        {
            SelectedControllerStartupTitleChanged?.Invoke(this,
                EventArgs.Empty);
            SelectedControllerStartupDetailChanged?.Invoke(this,
                EventArgs.Empty);
            SelectedControllerIsReadyChanged?.Invoke(this,
                EventArgs.Empty);
            SelectedControllerNeedsAttentionChanged?.Invoke(this,
                EventArgs.Empty);
        }

        private void RaiseMicrophoneCapabilityChanged()
        {
            SelectedControllerSupportsMicrophoneChanged?.Invoke(this,
                EventArgs.Empty);
            MicrophoneAvailabilityTextChanged?.Invoke(this, EventArgs.Empty);
            ShowMicrophoneAvailabilityMessageChanged?.Invoke(this,
                EventArgs.Empty);
            CanChangeMicrophoneInputChanged?.Invoke(this, EventArgs.Empty);
            MicrophoneLevelControlsEnabledChanged?.Invoke(this,
                EventArgs.Empty);
        }

        private bool HasValidSelectedDevice => selectedController != null &&
            selectedController.DevIndex >= 0 &&
            selectedController.DevIndex < ControlService.CURRENT_DS4_CONTROLLER_LIMIT;

        private void HookSelectedController(CompositeDeviceModel controller, bool hook)
        {
            if (controller == null) return;

            if (hook)
            {
                controller.SelectedProfileChanged += SelectedController_ProfileChanged;
                controller.BatteryStateChanged += SelectedController_StatusChanged;
                controller.IdTextChanged += SelectedController_StatusChanged;
            }
            else
            {
                controller.SelectedProfileChanged -= SelectedController_ProfileChanged;
                controller.BatteryStateChanged -= SelectedController_StatusChanged;
                controller.IdTextChanged -= SelectedController_StatusChanged;
            }
        }

        private void SelectedController_ProfileChanged(object sender, EventArgs e)
        {
            RefreshSelectedControllerProperties();
        }

        private void SelectedController_StatusChanged(object sender, EventArgs e)
        {
            RefreshRuntimeState(App.rootHub);
        }

        private void CaptureRuntimeSnapshot(ControlService controlService)
        {
            OverviewRuntimeSnapshot snapshot =
                CreateRuntimeSnapshot(controlService);
            lastRuntimeSnapshot = snapshot;
            hasRuntimeSnapshot = true;
            selectedControllerStartupStatus = snapshot.StartupStatus;
        }

        private OverviewRuntimeSnapshot CreateRuntimeSnapshot(
            ControlService controlService)
        {
            ControllerStartupStatus startupStatus =
                ControllerRuntimeStatusPolicy.Evaluate(
                    HasValidSelectedDevice && controlService != null
                        ? controlService.GetControllerRuntimeSignals(
                            selectedController.DevIndex)
                        : new ControllerRuntimeSignals(false, false, false,
                            false, false, false,
                            ControllerRuntimeLaneState.NotRequired,
                            ControllerRuntimeLaneState.NotRequired,
                            ControllerRuntimeLaneState.NotRequired,
                            ControllerRuntimeLaneState.NotRequired,
                            "virtual controller"));

            return new OverviewRuntimeSnapshot(CurrentProfileName,
                SelectedControllerConnection, SelectedControllerLatency,
                SelectedControllerBattery, SelectedOutputController,
                HapticStrengthPercent, SpeakerOutputEnabled,
                HeadsetOnlyAudio, ControllerAudioSourceId,
                AudioHapticsSpeakerOverrideActive, MicrophoneInputEnabled,
                SpeakerVolumePercent,
                HeadphoneVolumePercent, SpeakerCompressionIndex,
                SpeakerBassBoostDb, MicrophoneVolumePercent,
                MicrophoneNoiseSuppressionIndex, startupStatus);
        }

        private readonly struct OverviewRuntimeSnapshot
        {
            public OverviewRuntimeSnapshot(string profileName,
                string connection, string latency, string battery,
                OutContType outputController, int hapticStrength,
                bool speakerEnabled, bool headsetOnlyAudio,
                string controllerAudioSourceId,
                bool audioHapticsSpeakerOverrideActive,
                bool microphoneEnabled,
                int speakerVolume, int headphoneVolume,
                int speakerCompression, int speakerBassBoost,
                int microphoneVolume, int microphoneNoiseSuppression,
                ControllerStartupStatus startupStatus)
            {
                ProfileName = profileName;
                Connection = connection;
                Latency = latency;
                Battery = battery;
                OutputController = outputController;
                HapticStrength = hapticStrength;
                SpeakerEnabled = speakerEnabled;
                HeadsetOnlyAudio = headsetOnlyAudio;
                ControllerAudioSourceId = controllerAudioSourceId ??
                    string.Empty;
                AudioHapticsSpeakerOverrideActive =
                    audioHapticsSpeakerOverrideActive;
                MicrophoneEnabled = microphoneEnabled;
                SpeakerVolume = speakerVolume;
                HeadphoneVolume = headphoneVolume;
                SpeakerCompression = speakerCompression;
                SpeakerBassBoost = speakerBassBoost;
                MicrophoneVolume = microphoneVolume;
                MicrophoneNoiseSuppression = microphoneNoiseSuppression;
                StartupStatus = startupStatus;
            }

            public string ProfileName { get; }
            public string Connection { get; }
            public string Latency { get; }
            public string Battery { get; }
            public OutContType OutputController { get; }
            public int HapticStrength { get; }
            public bool SpeakerEnabled { get; }
            public bool HeadsetOnlyAudio { get; }
            public string ControllerAudioSourceId { get; }
            public bool AudioHapticsSpeakerOverrideActive { get; }
            public bool MicrophoneEnabled { get; }
            public int SpeakerVolume { get; }
            public int HeadphoneVolume { get; }
            public int SpeakerCompression { get; }
            public int SpeakerBassBoost { get; }
            public int MicrophoneVolume { get; }
            public int MicrophoneNoiseSuppression { get; }
            public ControllerStartupStatus StartupStatus { get; }
        }

        public int MicrophoneNoiseSuppressionIndex
        {
            get => HasValidSelectedDevice
                ? Global.DualSenseMicrophoneNoiseSuppression[
                    selectedController.DevIndex]
                : 0;
            set
            {
                if (!HasValidSelectedDevice) return;
                int deviceIndex = selectedController.DevIndex;
                byte converted = (byte)Math.Clamp(value,
                    (int)DualSenseMicrophoneNoiseSuppression.Off,
                    (int)DualSenseMicrophoneNoiseSuppression.NvidiaAi);
                if (Global.DualSenseMicrophoneNoiseSuppression[deviceIndex] ==
                    converted) return;
                Global.DualSenseMicrophoneNoiseSuppression[deviceIndex] =
                    converted;
                MicrophoneNoiseSuppressionIndexChanged?.Invoke(this,
                    EventArgs.Empty);
                RaiseQuickProfileSettingChanged(deviceIndex);
            }
        }

        public bool NvidiaNoiseSuppressionAvailable =>
            NvidiaAudioNoiseSuppressor.IsRuntimeInstalled;

        public string NvidiaNoiseSuppressionAvailability =>
            NvidiaAudioNoiseSuppressor.RuntimeAvailability;

        private async Task RefreshControllerAudioChoicesAsync()
        {
            await AudioEndpointChoiceCache.RefreshAsync();
            ControllerAudioSourceChoicesChanged?.Invoke(this,
                EventArgs.Empty);
            ControllerAudioSourceIdChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseQuickProfileSettingChanged(int deviceIndex,
            bool requiresProfileReload = false)
        {
            QuickProfileSettingChanged?.Invoke(this,
                new QuickProfileSettingChangedEventArgs(deviceIndex,
                    requiresProfileReload));
        }

        private static int ByteToPercent(byte value) =>
            (int)Math.Round(value / 255.0 * 100.0);

        private static byte PercentToByte(int value) =>
            (byte)Math.Round(Math.Clamp(value, 0, 100) / 100.0 * 255.0);

        private bool fullTabsEnabled = true;

        public bool FullTabsEnabled
        {
            get => fullTabsEnabled;
            set
            {
                fullTabsEnabled = value;
                FullTabsEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler FullTabsEnabledChanged;

        private bool profileEditorMode;

        public bool ProfileEditorMode
        {
            get => profileEditorMode;
            set
            {
                if (profileEditorMode == value) return;
                profileEditorMode = value;
                ProfileEditorModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ProfileEditorModeChanged;

        private string editingProfileName = "Profile";

        public string EditingProfileName
        {
            get => editingProfileName;
            set
            {
                string nextValue = string.IsNullOrWhiteSpace(value) ? "New profile" : value;
                if (editingProfileName == nextValue) return;
                editingProfileName = nextValue;
                EditingProfileNameChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler EditingProfileNameChanged;

        private string editingControllerName = "Generic profile";

        public string EditingControllerName
        {
            get => editingControllerName;
            private set
            {
                string nextValue = string.IsNullOrWhiteSpace(value)
                    ? "Generic profile"
                    : value;
                if (editingControllerName == nextValue) return;
                editingControllerName = nextValue;
                EditingControllerNameChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler EditingControllerNameChanged;

        private string editingControllerImageSource;

        public string EditingControllerImageSource
        {
            get => editingControllerImageSource;
            private set
            {
                if (editingControllerImageSource == value) return;
                editingControllerImageSource = value;
                EditingControllerImageSourceChanged?.Invoke(this,
                    EventArgs.Empty);
            }
        }
        public event EventHandler EditingControllerImageSourceChanged;

        private string editingControllerConnection;

        public string EditingControllerConnection
        {
            get => editingControllerConnection;
            private set
            {
                if (editingControllerConnection == value) return;
                editingControllerConnection = value;
                EditingControllerConnectionChanged?.Invoke(this,
                    EventArgs.Empty);
            }
        }
        public event EventHandler EditingControllerConnectionChanged;

        public void SetEditingControllerContext(CompositeDeviceModel controller)
        {
            EditingControllerName = controller?.ControllerDisplayName;
            EditingControllerImageSource = controller?.ControllerImageSource;
            EditingControllerConnection = controller == null
                ? "No physical controller selected"
                : controller.ConnectionText;
        }

        private int profileEditorNavigationIndex = 1;

        public int ProfileEditorNavigationIndex
        {
            get => profileEditorNavigationIndex;
            set
            {
                if (profileEditorNavigationIndex == value) return;
                profileEditorNavigationIndex = value;
                ProfileEditorNavigationIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ProfileEditorNavigationIndexChanged;

        private string profileEditorSectionTitle = "Button Mapping";

        public string ProfileEditorSectionTitle
        {
            get => profileEditorSectionTitle;
            set
            {
                if (profileEditorSectionTitle == value) return;
                profileEditorSectionTitle = value;
                ProfileEditorSectionTitleChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ProfileEditorSectionTitleChanged;

        private string profileEditorSectionDescription =
            "Assign controller buttons, sticks, touch gestures, and shortcuts.";

        public string ProfileEditorSectionDescription
        {
            get => profileEditorSectionDescription;
            set
            {
                if (profileEditorSectionDescription == value) return;
                profileEditorSectionDescription = value;
                ProfileEditorSectionDescriptionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ProfileEditorSectionDescriptionChanged;

        public string updaterExe = Environment.Is64BitProcess ? "DS4Updater.exe" : "DS4Updater_x86.exe";

        private string DownloadUpstreamUpdaterVersion()
        {
            Uri url = new Uri("https://api.github.com/repos/hbashton/DS4Updater/releases/latest");

            Task<System.Net.Http.HttpResponseMessage> requestTask = App.requestClient.GetAsync(url.ToString());
            requestTask.Wait();
            if (requestTask.Result.IsSuccessStatusCode)
            {
                var gitHubReleaseTask = requestTask.Result.Content.ReadFromJsonAsync<GithubRelease>();
                gitHubReleaseTask.Wait();
                if (!gitHubReleaseTask.IsFaulted &&
                    gitHubReleaseTask.Result is not null &&
                    Changelog.TryParseReleaseVersion(gitHubReleaseTask.Result.TagName, out var updaterVersion))
                {
                    return updaterVersion.ToString();
                }
            }
            return string.Empty;
        }

        public bool RunUpdaterCheck(bool launch, out string upstreamVersion)
        {
            string destPath = Path.Combine(Global.exedirpath, "DS4Updater.exe");
            bool updaterExists = File.Exists(destPath);
            upstreamVersion = DownloadUpstreamUpdaterVersion();
            if (string.IsNullOrEmpty(upstreamVersion)) return false;

            if (!updaterExists ||
                (!string.IsNullOrEmpty(upstreamVersion) && FileVersionInfo.GetVersionInfo(destPath).FileVersion.CompareTo(upstreamVersion) != 0))
            {
                launch = false;
                Uri url2 = new Uri($"https://github.com/hbashton/DS4Updater/releases/download/v{upstreamVersion}/{updaterExe}");
                string filename = Path.Combine(Path.GetTempPath(), "DS4Updater.exe");
                using (var downloadStream = new FileStream(filename, FileMode.Create))
                {
                    Task<System.Net.Http.HttpResponseMessage> temp =
                        App.requestClient.GetAsync(url2.ToString(), downloadStream);
                    temp.Wait();
                    if (temp.Result.IsSuccessStatusCode) launch = true;
                }

                if (launch)
                {
                    if (Global.AdminNeeded())
                    {
                        int copyStatus = DS4Windows.Util.ElevatedCopyUpdater(filename);
                        if (copyStatus != 0) launch = false;
                    }
                    else
                    {
                        if (updaterExists) File.Delete(destPath);
                        File.Move(filename, destPath);
                    }
                }
            }

            return launch;
        }

        public void DownloadUpstreamVersionInfo()
        {
            Uri url = new Uri("https://api.github.com/repos/hbashton/DS4Windows/releases/latest");
            string filename = Global.appdatapath + "\\version.txt";
            bool success = false;
            using (StreamWriter streamWriter = new(filename, false))
            {
                Task<System.Net.Http.HttpResponseMessage> requestTask = App.requestClient.GetAsync(url.ToString());
                try
                {
                    requestTask.Wait();
                    if (requestTask.Result.IsSuccessStatusCode)
                    {
                        var gitHubReleaseTask = requestTask.Result.Content.ReadFromJsonAsync<GithubRelease>();
                        gitHubReleaseTask.Wait();
                        if (!gitHubReleaseTask.IsFaulted &&
                            gitHubReleaseTask.Result is not null &&
                            Changelog.TryParseReleaseVersion(gitHubReleaseTask.Result.TagName, out var releaseVersion))
                        {
                            streamWriter.Write(releaseVersion);
                            success = true;
                        }
                    }
                }
                catch (AggregateException) { }
            }

            if (!success && File.Exists(filename))
            {
                File.Delete(filename);
            }
        }

        public void CheckDrivers()
        {
            ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
            if (!status.Ready)
            {
                ViiperSetupManager.EnsureReadyWithPrompt(null, forcePrompt: true);
            }
        }

        public bool LauchDS4Updater(string releaseTag = null)
        {
            bool launch = false;
            using (Process p = new Process())
            {
                p.StartInfo.FileName = Path.Combine(Global.exedirpath, "DS4Updater.exe");
                bool isAdmin = Global.IsAdministrator();
                List<string> argList = new List<string>();
                argList.Add("-autolaunch");
                if (!isAdmin)
                {
                    argList.Add("-user");
                }

                if (!string.IsNullOrWhiteSpace(releaseTag))
                {
                    argList.Add("--releaseTag");
                    argList.Add(releaseTag);
                }

                // Specify current exe to have DS4Updater launch
                argList.Add("--launchExe");
                argList.Add(Global.exeFileName);

                foreach (string argument in argList)
                {
                    p.StartInfo.ArgumentList.Add(argument);
                }
                if (Global.AdminNeeded())
                    p.StartInfo.Verb = "runas";

                try { launch = p.Start(); }
                catch (InvalidOperationException) { }
            }

            return launch;
        }

        public bool IsNET8Available()
        {
            return DS4Windows.Util.IsNet8DesktopRuntimeAvailable();
        }
    }
}
