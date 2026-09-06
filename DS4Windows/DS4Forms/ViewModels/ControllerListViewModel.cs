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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class ControllerListViewModel
    {
        //private object _colLockobj = new object();
        private ReaderWriterLockSlim _colListLocker = new ReaderWriterLockSlim();
        private ObservableCollection<CompositeDeviceModel> controllerCol =
            new ObservableCollection<CompositeDeviceModel>();
        private Dictionary<int, CompositeDeviceModel> controllerDict =
            new Dictionary<int, CompositeDeviceModel>();

        public ObservableCollection<CompositeDeviceModel> ControllerCol
        { get => controllerCol; set => controllerCol = value; }

        private ProfileList profileListHolder;
        private ControlService controlService;
        private int currentIndex;
        public int CurrentIndex { get => currentIndex; set => currentIndex = value; }
        public CompositeDeviceModel CurrentItem {
            get
            {
                if (currentIndex == -1) return null;
                controllerDict.TryGetValue(currentIndex, out CompositeDeviceModel item);
                return item;
            }
        }

        public Dictionary<int, CompositeDeviceModel> ControllerDict { get => controllerDict; set => controllerDict = value; }

        //public ControllerListViewModel(Tester tester, ProfileList profileListHolder)
        public ControllerListViewModel(ControlService service, ProfileList profileListHolder)
            : this(profileListHolder)
        {
            this.controlService = service;
            service.ServiceStarted += ControllersChanged;
            service.PreServiceStop += ClearControllerList;
            service.HotplugController += Service_HotplugController;
            service.RemovedController += Service_RemovedController;
            //tester.StartControllers += ControllersChanged;
            //tester.ControllersRemoved += ClearControllerList;

            ControllersChanged(service, EventArgs.Empty);
        }

        // The same list model can be exercised without constructing a live
        // ControlService (and therefore without discovery or controller I/O).
        internal ControllerListViewModel(ProfileList profileListHolder)
        {
            this.profileListHolder = profileListHolder;
            BindingOperations.EnableCollectionSynchronization(controllerCol, _colListLocker,
                ColLockCallback);
        }

        private void ColLockCallback(IEnumerable collection, object context,
            Action accessMethod, bool writeAccess)
        {
            if (writeAccess)
            {
                using (WriteLocker locker = new WriteLocker(_colListLocker))
                {
                    accessMethod?.Invoke();
                }
            }
            else
            {
                using (ReadLocker locker = new ReadLocker(_colListLocker))
                {
                    accessMethod?.Invoke();
                }
            }
        }

        private void Service_HotplugController(ControlService sender,
            DS4Device device, int index)
        {
            if (!ReferenceEquals(sender.DS4Controllers[index], device)) return;
            AddController(device, index);
        }

        internal void AddController(DS4Device device, int index)
        {
            using (WriteLocker writeLock = new WriteLocker(_colListLocker))
            {
                if (device.IsRemoving || device.IsRemoved) return;
                if (controllerDict.TryGetValue(index, out CompositeDeviceModel existing))
                {
                    if (ReferenceEquals(existing.Device, device)) return;
                    // A delayed removal notification must neither hide the
                    // replacement nor keep the old row in a reused slot.
                    RemoveModelNoLock(existing);
                }
                CompositeDeviceModel temp = new CompositeDeviceModel(device,
                    index, Global.ProfilePath[index], profileListHolder);
                controllerDict.Add(index, temp);
                device.Removal += Controller_Removal;
                controllerCol.Add(temp);
            }
        }

        private void Service_RemovedController(ControlService sender,
            DS4Device device, int index)
        {
            if (RemoveController(device, index)) SaveAfterRemoval();
        }

        private void ClearControllerList(object sender, EventArgs e)
        {
            using (WriteLocker locker = new WriteLocker(_colListLocker))
            {
                foreach (CompositeDeviceModel temp in controllerCol)
                {
                    temp.Device.Removal -= Controller_Removal;
                }
                controllerCol.Clear();
                controllerDict.Clear();
            }
        }

        private void ControllersChanged(object sender, EventArgs e)
        {
            using (ReadLocker locker = new ReadLocker(controlService.slotManager.CollectionLocker))
            {
                foreach (DS4Device currentDev in controlService.slotManager.ControllerColl)
                {
                    AddController(currentDev,
                        controlService.slotManager.ReverseControllerDict[currentDev]);
                }
            }
        }

        private void Controller_Removal(object sender, EventArgs e)
        {
            DS4Device currentDev = sender as DS4Device;
            bool removed = false;
            using (WriteLocker locker = new WriteLocker(_colListLocker))
            {
                for (int index = 0; index < controllerCol.Count; index++)
                {
                    CompositeDeviceModel candidate = controllerCol[index];
                    if (ReferenceEquals(candidate.Device, currentDev))
                    {
                        RemoveModelNoLock(candidate);
                        Global.linkedProfileCheck[candidate.DevIndex] = false;
                        removed = true;
                        break;
                    }
                }
            }
            if (removed) SaveAfterRemoval();
        }

        internal bool RemoveController(DS4Device device, int index)
        {
            using (WriteLocker locker = new WriteLocker(_colListLocker))
            {
                if (!controllerDict.TryGetValue(index, out CompositeDeviceModel current) ||
                    !ReferenceEquals(current.Device, device)) return false;
                RemoveModelNoLock(current);
                Global.linkedProfileCheck[index] = false;
                return true;
            }
        }

        private void RemoveModelNoLock(CompositeDeviceModel model)
        {
            model.Device.Removal -= Controller_Removal;
            controllerDict.Remove(model.DevIndex);
            controllerCol.Remove(model);
        }

        private static void SaveAfterRemoval()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted &&
                !dispatcher.HasShutdownFinished)
            {
                // Never synchronously enter the UI dispatcher while holding
                // the collection lock: WPF may need that lock to render removal.
                dispatcher.BeginInvoke(new Action(() =>
                {
                    Global.Save();
                }));
            }
        }
    }

    public class CompositeDeviceModel
    {
        private DS4Device device;
        private ControllerUiCapabilities uiCapabilities;
        private string selectedProfile;
        private ProfileList profileListHolder;
        private ProfileEntity selectedEntity;
        private int selectedIndex = -1;
        private int devIndex;

        public bool IsSynchronizingRuntimeProfile { get; private set; }

        private ControllerUiCapabilities UiCapabilities =>
            uiCapabilities ??= ControllerUiCapabilities.ForDevice(device);

        public DS4Device Device
        {
            get => device;
            set
            {
                device = value;
                uiCapabilities = ControllerUiCapabilities.ForDevice(device);
            }
        }
        public string SelectedProfile
        {
            get => selectedProfile;
            private set
            {
                if (selectedProfile == value) return;
                selectedProfile = value;
                SelectedProfileChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler SelectedProfileChanged;

        public string ControllerDisplayName => device.DisplayName;

        public string ConnectionText => device.ConnectionType switch
        {
            ConnectionType.USB => "USB",
            ConnectionType.BT => "Bluetooth",
            ConnectionType.SONYWA => "Wireless adapter",
            _ => "Connected",
        };

        public string LatencyText
        {
            get
            {
                double intervalMilliseconds = device.Latency;
                if (intervalMilliseconds <= 0.0 ||
                    double.IsNaN(intervalMilliseconds) ||
                    double.IsInfinity(intervalMilliseconds))
                {
                    return "--";
                }

                double frequencyHz = 1000.0 / intervalMilliseconds;
                return $"{intervalMilliseconds:0.00} ms · {frequencyHz:0} Hz";
            }
        }

        public bool IsWireless => device.ConnectionType != ConnectionType.USB;

        public bool SupportsSwitch2StandaloneHoldMode =>
            device is Switch2RuntimeInputDevice runtime &&
            runtime.SupportsStandaloneJoyConHoldMode;

        public bool SupportsSwitch2Identification =>
            device is Switch2RuntimeInputDevice;

        public string Switch2StandaloneHoldModeText =>
            EffectiveSwitch2StandaloneHoldMode() ==
                Switch2JoyConHoldMode.Horizontal ? "Horizontal" :
                "Vertical";

        public event EventHandler Switch2StandaloneHoldModeTextChanged;

        public string Switch2StandaloneHoldModeToolTip =>
            "Switch between holding this Joy-Con upright or sideways. " +
            "Your choice is remembered for this controller.";

        public bool SupportsControllerAudio =>
            UiCapabilities.SupportsControllerAudio;
        public ProfileList ProfileEntities { get => profileListHolder; set => profileListHolder = value; }
        public ObservableCollection<ProfileEntity> ProfileListCol => profileListHolder.ProfileListCol;

        public string LightColor
        {
            get
            {
                DS4Color color;
                if (Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed)
                {
                    color = Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_CustomLed; //Global.CustomColor[devIndex];
                }
                else
                {
                    color = Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_Led;
                }
                return $"#FF{color.red.ToString("X2")}{color.green.ToString("X2")}{color.blue.ToString("X2")}";
            }
        }

        public event EventHandler LightColorChanged;

        public Color CustomLightColor
        {
            get
            {
                DS4Color color;
                color = Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_CustomLed;
                return new Color() { R = color.red, G = color.green, B = color.blue, A = 255 };
            }
        }

        public string BatteryState
        {
            get
            {
                string temp = $"{device.Battery}%{(device.Charging ? "+" : "")}";
                return temp;
            }
        }
        public event EventHandler BatteryStateChanged;

        public bool HasControllerArtwork => UiCapabilities.HasControllerArtwork;

        public bool IsDualShock4 => UiCapabilities.IsDualShock4;

        public bool IsDualSense => UiCapabilities.IsDualSense;

        public bool SupportsAdaptiveTriggers =>
            UiCapabilities.SupportsAdaptiveTriggers;

        public bool SupportsAdvancedHaptics =>
            UiCapabilities.SupportsAdvancedHaptics;

        public bool SupportsMuteButton => UiCapabilities.SupportsMuteButton;

        public string FeedbackControlLabel => UiCapabilities.FeedbackLabel;

        public string ControllerAudioHeader => UiCapabilities.AudioHeader;

        public string ControllerAudioDescription =>
            UiCapabilities.AudioDescription;

        public string MicrophoneToggleLabel =>
            UiCapabilities.MicrophoneToggleLabel;

        public string MicrophoneDescription =>
            UiCapabilities.MicrophoneDescription;

        public ImageSource ControllerImageSource
        {
            get
            {
                if (JoyConArtwork.ForDevice(device.DeviceType,
                        EffectiveSwitch2StandaloneHoldMode()) is ImageSource joyCon)
                    return joyCon;
                string imageName = UiCapabilities.ImageResourceName;

                return ControllerArtwork.LoadResource(imageName);
            }
        }

        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                if (selectedIndex == value) return;
                selectedIndex = value;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler SelectedIndexChanged;

        // WPF's property-change convention shares the existing holding-style
        // notification, including profile reloads and controller overrides.
        public event EventHandler ControllerImageSourceChanged
        {
            add => Switch2StandaloneHoldModeTextChanged += value;
            remove => Switch2StandaloneHoldModeTextChanged -= value;
        }

        public bool TryToggleSwitch2StandaloneHoldMode(out bool persisted)
        {
            persisted = false;
            if (device is not Switch2RuntimeInputDevice runtime ||
                !runtime.SupportsStandaloneJoyConHoldMode)
            {
                return false;
            }

            Switch2JoyConHoldMode next =
                EffectiveSwitch2StandaloneHoldMode() ==
                    Switch2JoyConHoldMode.Horizontal ?
                    Switch2JoyConHoldMode.Vertical :
                    Switch2JoyConHoldMode.Horizontal;
            if (!runtime.TrySetStandaloneJoyConHoldMode(next,
                    out persisted))
            {
                return false;
            }
            Switch2StandaloneHoldModeTextChanged?.Invoke(this,
                EventArgs.Empty);
            return true;
        }

        private Switch2JoyConHoldMode EffectiveSwitch2StandaloneHoldMode()
        {
            Switch2JoyConHoldMode fallback = devIndex >= 0 &&
                devIndex < Global.MAX_DS4_CONTROLLER_COUNT ?
                Global.Switch2JoyConStandaloneHoldMode[devIndex] :
                Switch2JoyConHoldMode.Vertical;
            return device is Switch2RuntimeInputDevice runtime ?
                runtime.ResolveStandaloneJoyConHoldMode(fallback) :
                Switch2JoyConHoldMode.Vertical;
        }

        public string StatusSource
        {
            get
            {
                string imgName = (string)App.Current.FindResource(device.ConnectionType == ConnectionType.USB ? "UsbImg" : "BtImg");
                string source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                return source;
            }
        }

        public string ExclusiveSource
        {
            get
            {
                string imgName = (string)App.Current.FindResource("CancelImg");
                string source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                switch(device.CurrentExclusiveStatus)
                {
                    case DS4Device.ExclusiveStatus.Exclusive:
                        imgName = (string)App.Current.FindResource("CheckedImg");
                        source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                        break;
                    case DS4Device.ExclusiveStatus.HidHideAffected:
                    case DS4Device.ExclusiveStatus.HidGuardAffected:
                        imgName = (string)App.Current.FindResource("KeyImageImg");
                        source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                        break;
                    default:
                        break;
                }

                return source;
            }
        }

        public bool LinkedProfile
        {
            get
            {
                return Global.linkedProfileCheck[devIndex];
            }
            set
            {
                bool temp = Global.linkedProfileCheck[devIndex];
                if (temp == value) return;
                Global.linkedProfileCheck[devIndex] = value;
                SaveLinked(value);
            }
        }

        public int DevIndex { get => devIndex; }
        public int DisplayDevIndex { get => devIndex + 1; }

        public string TooltipIDText
        {
            get
            {
                string temp = string.Format(Properties.Resources.InputDelay, device.Latency);
                return temp;
            }
        }

        public event EventHandler TooltipIDTextChanged;

        private bool useCustomColor;
        public bool UseCustomColor { get => useCustomColor; set => useCustomColor = value; }

        private ContextMenu lightContext;
        public ContextMenu LightContext { get => lightContext; set => lightContext = value; }

        public string IdText
        {
            get => $"{device.DisplayName} ({device.MacAddress})";
        }
        public event EventHandler IdTextChanged;

        public string IsExclusiveText
        {
            get
            {
                string temp = Translations.Strings.SharedAccess;
                switch(device.CurrentExclusiveStatus)
                {
                    case DS4Device.ExclusiveStatus.Exclusive:
                        temp = Translations.Strings.ExclusiveAccess;
                        break;
                    case DS4Device.ExclusiveStatus.HidHideAffected:
                        temp = Translations.Strings.HidHideAccess;
                        break;
                    case DS4Device.ExclusiveStatus.HidGuardAffected:
                        temp = Translations.Strings.HidGuardianAccess;
                        break;
                    default:
                        break;
                }

                return temp;
            }
        }

        public bool PrimaryDevice
        {
            get => device.PrimaryDevice;
        }

        public delegate void CustomColorHandler(CompositeDeviceModel sender);
        public event CustomColorHandler RequestColorPicker;

        public CompositeDeviceModel(DS4Device device, int devIndex, string profile,
            ProfileList collection)
        {
            this.device = device;
            uiCapabilities = ControllerUiCapabilities.ForDevice(device);
            device.BatteryChanged += (sender, e) => BatteryStateChanged?.Invoke(this, e);
            device.ChargingChanged += (sender, e) => BatteryStateChanged?.Invoke(this, e);
            device.MacAddressChanged += (sender, e) => IdTextChanged?.Invoke(this, e);
            this.devIndex = devIndex;
            this.selectedProfile = profile;
            profileListHolder = collection;
            if (!string.IsNullOrEmpty(selectedProfile))
            {
                this.selectedEntity = profileListHolder.ProfileListCol.SingleOrDefault(x => x.Name == selectedProfile);
            }

            if (this.selectedEntity != null)
            {
                selectedIndex = profileListHolder.ProfileListCol.IndexOf(this.selectedEntity);
                HookEvents(true);
            }

            useCustomColor = Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed;
        }

        public void ChangeSelectedProfile()
        {
            if (IsSynchronizingRuntimeProfile || selectedIndex < 0 ||
                selectedIndex >= ProfileListCol.Count)
            {
                return;
            }

            ProfileEntity targetEntity = ProfileListCol[selectedIndex];
            if (IsProfileSelectionApplied(SelectedProfile,
                    Global.ProfilePath[devIndex], selectedEntity,
                    targetEntity))
            {
                return;
            }

            if (this.selectedEntity != null)
            {
                HookEvents(false);
            }

            string prof = Global.ProfilePath[devIndex] = targetEntity.Name;
            if (LinkedProfile)
            {
                Global.changeLinkedProfile(device.getMacAddress(), Global.ProfilePath[devIndex]);
                Global.SaveLinkedProfiles();
            }
            else
            {
                Global.OlderProfilePath[devIndex] = Global.ProfilePath[devIndex];
            }

            SelectedProfile = prof;
            this.selectedEntity = targetEntity;
            if (this.selectedEntity != null)
            {
                selectedIndex = profileListHolder.ProfileListCol.IndexOf(this.selectedEntity);
                HookEvents(true);
            }

            Mapping.RequestRegularProfileReload(devIndex, true, App.rootHub,
                loaded => CompleteProfileReload(loaded, prof, logSelection: true), profileName: prof);
        }

        /// <summary>
        /// Publishes and applies a profile selection without relying on a
        /// realized ComboBox to turn the model change into a runtime reload.
        /// </summary>
        public void SelectAndApplyProfile(int profileIndex)
        {
            if (IsSynchronizingRuntimeProfile || profileIndex < 0 ||
                profileIndex >= ProfileListCol.Count)
            {
                return;
            }

            // A realized ComboBox can synchronously raise SelectionChanged
            // while the source index is published. Fence that UI callback;
            // this method owns the one direct application below. The core is
            // idempotent as an additional guard against deferred callbacks.
            IsSynchronizingRuntimeProfile = true;
            try
            {
                SelectedIndex = profileIndex;
            }
            finally
            {
                IsSynchronizingRuntimeProfile = false;
            }

            ChangeSelectedProfile();
        }

        internal static bool IsProfileSelectionApplied(
            string selectedProfile, string runtimeProfile,
            ProfileEntity selectedEntity, ProfileEntity targetEntity)
        {
            return targetEntity != null &&
                ReferenceEquals(selectedEntity, targetEntity) &&
                string.Equals(selectedProfile, targetEntity.Name,
                    StringComparison.Ordinal) &&
                string.Equals(runtimeProfile, targetEntity.Name,
                    StringComparison.Ordinal);
        }

        internal bool IsUsingProfile(ProfileEntity profile)
        {
            return profile != null &&
                (ReferenceEquals(selectedEntity, profile) ||
                 string.Equals(SelectedProfile, profile.Name,
                     StringComparison.Ordinal) ||
                 (!Global.useTempProfile[devIndex] &&
                  string.Equals(Global.ProfilePath[devIndex], profile.Name,
                     StringComparison.Ordinal)));
        }

        public bool SynchronizeRuntimeProfile()
        {
            string runtimeProfile = Global.useTempProfile[devIndex]
                ? Global.tempprofilename[devIndex] ?? string.Empty
                : Global.ProfilePath[devIndex] ?? string.Empty;
            ProfileEntity runtimeEntity = profileListHolder.ProfileListCol
                .SingleOrDefault(item => item.Name == runtimeProfile);
            int runtimeIndex = runtimeEntity == null
                ? -1
                : profileListHolder.ProfileListCol.IndexOf(runtimeEntity);
            if (IsRuntimeProfileSynchronized(SelectedProfile,
                    runtimeProfile, selectedEntity, runtimeEntity,
                    selectedIndex, runtimeIndex))
            {
                return false;
            }

            IsSynchronizingRuntimeProfile = true;
            try
            {
                if (selectedEntity != null)
                {
                    HookEvents(false);
                }

                selectedEntity = runtimeEntity;
                SelectedProfile = runtimeProfile;
                SelectedIndex = runtimeIndex;
                if (selectedEntity != null)
                {
                    HookEvents(true);
                }

                LightColorChanged?.Invoke(this, EventArgs.Empty);
                Switch2StandaloneHoldModeTextChanged?.Invoke(this,
                    EventArgs.Empty);
                return true;
            }
            finally
            {
                IsSynchronizingRuntimeProfile = false;
            }
        }

        internal static bool IsRuntimeProfileSynchronized(
            string selectedProfile, string runtimeProfile,
            ProfileEntity selectedEntity, ProfileEntity runtimeEntity,
            int selectedIndex, int runtimeIndex)
        {
            return string.Equals(selectedProfile, runtimeProfile,
                    StringComparison.Ordinal) &&
                ReferenceEquals(selectedEntity, runtimeEntity) &&
                selectedIndex == runtimeIndex;
        }

        private void HookEvents(bool state)
        {
            if (state)
            {
                selectedEntity.ProfileSaved += SelectedEntity_ProfileSaved;
            }
            else
            {
                selectedEntity.ProfileSaved -= SelectedEntity_ProfileSaved;
            }
        }

        internal void ApplyProfileDeletionFallback(
            ProfileEntity deletedEntity)
        {
            if (selectedEntity != null)
            {
                HookEvents(false);
            }

            selectedEntity = null;
            ProfileEntity fallback = FindProfileDeletionFallback(
                profileListHolder.ProfileListCol, deletedEntity);
            if (fallback != null)
            {
                int fallbackIndex =
                    profileListHolder.ProfileListCol.IndexOf(fallback);
                SelectAndApplyProfile(fallbackIndex);
            }
            else
            {
                ClearSelectedProfileAfterDeletion();
            }
        }

        internal static ProfileEntity FindProfileDeletionFallback(
            IEnumerable<ProfileEntity> profiles,
            ProfileEntity deletedEntity)
        {
            return profiles?.FirstOrDefault(entity => entity != null &&
                !ReferenceEquals(entity, deletedEntity));
        }

        private void ClearSelectedProfileAfterDeletion()
        {
            Mapping.ExecuteSerializedProfileMutation(devIndex, () =>
            {
                Global.BeginProfileSwitchRevision(devIndex);
                Global.ProfilePath[devIndex] = string.Empty;
                Global.OlderProfilePath[devIndex] = string.Empty;
                Global.LoadBlankDevProfile(devIndex, false, App.rootHub,
                    false);
            });

            SelectedProfile = string.Empty;
            IsSynchronizingRuntimeProfile = true;
            try
            {
                SelectedIndex = -1;
            }
            finally
            {
                IsSynchronizingRuntimeProfile = false;
            }
            Switch2StandaloneHoldModeTextChanged?.Invoke(this,
                EventArgs.Empty);
        }

        private void SelectedEntity_ProfileSaved(object sender, EventArgs e)
        {
            if (selectedEntity != null)
            {
                SelectedProfile = selectedEntity.Name;
            }

            Mapping.RequestRegularProfileReload(devIndex, false, App.rootHub,
                loaded => CompleteProfileReload(loaded, SelectedProfile,
                    logSelection: false));
        }

        private void CompleteProfileReload(bool loaded, string profileName,
            bool logSelection)
        {
            if (!loaded)
            {
                return;
            }

            void CompleteOnUiThread()
            {
                if (logSelection)
                {
                    string prolog = string.Format(
                        Properties.Resources.UsingProfile,
                        (devIndex + 1).ToString(), profileName,
                        $"{device?.Battery ?? 0}");
                    AppLogger.LogToGui(prolog, false);
                }

                LightColorChanged?.Invoke(this, EventArgs.Empty);
                Switch2StandaloneHoldModeTextChanged?.Invoke(this,
                    EventArgs.Empty);
            }

            System.Windows.Threading.Dispatcher dispatcher =
                System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                CompleteOnUiThread();
            }
            else
            {
                dispatcher.BeginInvoke((Action)CompleteOnUiThread);
            }
        }

        public void RequestUpdatedTooltipID()
        {
            TooltipIDTextChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SaveLinked(bool status)
        {
            if (device != null && device.isSynced())
            {
                if (status)
                {
                    if (device.isValidSerial())
                    {
                        Global.changeLinkedProfile(device.getMacAddress(), Global.ProfilePath[devIndex]);
                    }
                }
                else
                {
                    Global.removeLinkedProfile(device.getMacAddress());
                    Global.ProfilePath[devIndex] = Global.OlderProfilePath[devIndex];
                }

                Global.SaveLinkedProfiles();
            }
        }

        public void AddLightContextItems()
        {
            MenuItem thing = new MenuItem() { Header = "Use Profile Controls", IsChecked = !useCustomColor };
            thing.Click += ProfileColorMenuClick;
            lightContext.Items.Add(thing);
            thing = new MenuItem() { Header = "Use Custom Color", IsChecked = useCustomColor };
            thing.Click += CustomColorItemClick;
            lightContext.Items.Add(thing);
        }

        private void ProfileColorMenuClick(object sender, System.Windows.RoutedEventArgs e)
        {
            useCustomColor = false;
            RefreshLightContext();
            Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed = false;
            LightColorChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CustomColorItemClick(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestCustomColorPicker();
        }

        public void RequestCustomColorPicker()
        {
            useCustomColor = true;
            if (lightContext?.Items.Count >= 2)
            {
                RefreshLightContext();
            }
            Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed = true;
            LightColorChanged?.Invoke(this, EventArgs.Empty);
            RequestColorPicker?.Invoke(this);
        }

        private void RefreshLightContext()
        {
            (lightContext.Items[0] as MenuItem).IsChecked = !useCustomColor;
            (lightContext.Items[1] as MenuItem).IsChecked = useCustomColor;
        }

        public void UpdateCustomLightColor(Color color)
        {
            Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_CustomLed = new DS4Color() { red = color.R, green = color.G, blue = color.B };
            LightColorChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ChangeSelectedProfile(string loadprofile)
        {
            ProfileEntity temp = profileListHolder.ProfileListCol.SingleOrDefault(x => x.Name == loadprofile);
            if (temp != null)
            {
                int profileIndex =
                    profileListHolder.ProfileListCol.IndexOf(temp);
                SelectAndApplyProfile(profileIndex);
            }
        }

        public void RequestDisconnect()
        {
            if (device.Synced && !device.Charging)
            {
                if (device.ConnectionType == ConnectionType.BT)
                {
                    //device.StopUpdate();
                    device.queueEvent(() =>
                    {
                        device.DisconnectBT();
                    });
                }
                else if (device.ConnectionType == ConnectionType.SONYWA)
                {
                    device.DisconnectDongle();
                }
            }
        }
    }
}
