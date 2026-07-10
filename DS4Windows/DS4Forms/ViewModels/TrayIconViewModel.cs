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
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class TrayIconViewModel
    {
        private string tooltipText = "DS4Windows";
        private string iconSource;
        public const string ballonTitle = "DS4Windows";
        public const string trayTitle = "DS4Windows";
        private ContextMenu contextMenu;
        private MenuItem changeServiceItem;
        private MenuItem openItem;
        private MenuItem minimizeItem;
        private MenuItem openProgramItem;
        private MenuItem closeItem;

        public string TooltipText
        {
            get => tooltipText;
            set
            {
                string temp = value;
                if (value.Length > 63) temp = value.Substring(0, 63);
                if (tooltipText == temp) return;
                tooltipText = temp;
                //Trace.WriteLine(tooltipText);
                try
                {
                    TooltipTextChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (InvalidOperationException) { }
            }
        }
        public event EventHandler TooltipTextChanged;

        public string IconSource { get => iconSource;
            set
            {
                if (iconSource == value) return;
                iconSource = value;
                IconSourceChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public ContextMenu ContextMenu { get => contextMenu; }

        public event EventHandler IconSourceChanged;
        public event EventHandler RequestShutdown;
        public event EventHandler RequestOpen;
        public event EventHandler RequestMinimize;
        public event EventHandler RequestServiceChange;

        private ReaderWriterLockSlim _colLocker = new ReaderWriterLockSlim();
        private List<ControllerHolder> controllerList = new List<ControllerHolder>();
        private ProfileList profileListHolder;
        private ControlService controlService;

        public delegate void ProfileSelectedHandler(TrayIconViewModel sender,
            ControllerHolder item, string profile);
        public event ProfileSelectedHandler ProfileSelected;

        //public TrayIconViewModel(Tester tester)
        public TrayIconViewModel(ControlService service, ProfileList profileListHolder)
        {
            this.profileListHolder = profileListHolder;
            this.controlService = service;
            contextMenu = new ContextMenu();
            iconSource = Global.iconChoiceResources[Global.UseIconChoice];
            Global.BatteryChanged += UpdateTrayBattery;
            changeServiceItem = new MenuItem() { Header = "Start" };
            changeServiceItem.Click += ChangeControlServiceItem_Click;
            changeServiceItem.IsEnabled = false;

            openItem = new MenuItem() { Header = "Open",
                FontWeight = FontWeights.Bold };
            openItem.Click += OpenMenuItem_Click;
            minimizeItem = new MenuItem() { Header = "Minimize" };
            minimizeItem.Click += MinimizeMenuItem_Click;
            openProgramItem = new MenuItem() { Header = "Open Program Folder" };
            openProgramItem.Click += OpenProgramFolderItem_Click;
            closeItem = new MenuItem() { Header = "Exit" };
            closeItem.Click += ExitMenuItem_Click;

            PopulateControllerList();
            PopulateToolText();
            PopulateContextMenu();
            SetupEvents();
            profileListHolder.ProfileListCol.CollectionChanged += ProfileListCol_CollectionChanged;

            service.ServiceStarted += BuildControllerList;
            service.ServiceStarted += HookEvents;
            service.ServiceStarted += StartPopulateText;
            service.PreServiceStop += ClearToolText;
            service.PreServiceStop += UnhookEvents;
            service.PreServiceStop += ClearControllerList;
            service.RunningChanged += Service_RunningChanged;
            service.HotplugController += Service_HotplugController;
            /*tester.StartControllers += HookBatteryUpdate;
            tester.StartControllers += StartPopulateText;
            tester.PreRemoveControllers += ClearToolText;
            tester.HotplugControllers += HookBatteryUpdate;
            tester.HotplugControllers += StartPopulateText;
            */
        }

        private void Service_RunningChanged(object sender, EventArgs e)
        {
            string temp = controlService.running ? "Stop" : "Start";
            App.Current.Dispatcher.BeginInvoke((Action)(() =>
            {
                changeServiceItem.Header = temp;
                changeServiceItem.IsEnabled = true;
            }));
        }

        private void ClearControllerList(object sender, EventArgs e)
        {
            using (WriteLocker locker = new WriteLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    RemoveDeviceEvents(holder.Device);
                }

                controllerList.Clear();
            }
        }

        private void UnhookEvents(object sender, EventArgs e)
        {
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    RemoveDeviceEvents(holder.Device);
                }
            }
        }

        private void Service_HotplugController(ControlService sender, DS4Device device, int index)
        {
            try
            {
                if (device == null)
                {
                    return;
                }

                using (WriteLocker locker = new WriteLocker(_colLocker))
                {
                    controllerList.RemoveAll(holder => holder.Device == device || holder.Index == index);
                    controllerList.Add(new ControllerHolder(device, index));
                    SetupDeviceEvents(device);
                }
            }
            catch (Exception ex)
            {
                DS4Windows.AppLogger.LogToGui($"Tray hotplug update failed: {ex.Message}", true);
            }
        }

        private void ProfileListCol_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            PopulateContextMenu();
        }

        private void BuildControllerList(object sender, EventArgs e)
        {
            try
            {
                PopulateControllerList();
            }
            catch (Exception ex)
            {
                DS4Windows.AppLogger.LogToGui($"Tray controller list update failed: {ex.Message}", true);
            }
        }

        public void PopulateContextMenu()
        {
            contextMenu.Items.Clear();
            ItemCollection items = contextMenu.Items;
            MenuItem item;
            int idx = 0;

            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device currentDev = holder.Device;
                    item = new MenuItem() { Header = $"Controller {idx + 1}" };
                    item.Tag = idx;
                    //item.ContextMenu = new ContextMenu();
                    ItemCollection subitems = item.Items;
                    string currentProfile = Global.ProfilePath[idx];
                    foreach (ProfileEntity entry in profileListHolder.ProfileListCol)
                    {
                        // Need to escape profile name to disable Access Keys for control
                        string name = entry.Name;
                        name = Regex.Replace(name, "_{1}", "__");
                        MenuItem temp = new MenuItem() { Header = name };
                        temp.Tag = idx;
                        temp.Click += ProfileItem_Click;
                        if (entry.Name == currentProfile)
                        {
                            temp.IsChecked = true;
                        }

                        subitems.Add(temp);
                    }

                    items.Add(item);
                    idx++;
                }

                item = new MenuItem() { Header = "Disconnect Menu" };
                idx = 0;
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device tempDev = holder.Device;
                    if (tempDev.Synced && !tempDev.Charging)
                    {
                        MenuItem subitem = new MenuItem() { Header = $"Disconnect Controller {idx + 1}" };
                        subitem.Click += DisconnectMenuItem_Click;
                        subitem.Tag = idx;
                        item.Items.Add(subitem);
                    }

                    idx++;
                }

                if (idx == 0)
                {
                    item.IsEnabled = false;
                }
            }

            items.Add(item);
            items.Add(new Separator());
            PopulateStaticItems();
        }

        private void ChangeControlServiceItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            changeServiceItem.IsEnabled = false;
            RequestServiceChange?.Invoke(this, EventArgs.Empty);
        }

        private void OpenProgramFolderItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(Global.exedirpath);
            startInfo.UseShellExecute = true;
            using (Process temp = Process.Start(startInfo))
            {
            }
        }

        private void OpenMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestOpen?.Invoke(this, EventArgs.Empty);
        }

        private void MinimizeMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestMinimize?.Invoke(this, EventArgs.Empty);
        }

        private void ProfileItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            int idx = Convert.ToInt32(item.Tag);
            ControllerHolder holder = GetControllerHolderByListIndex(idx);
            if (holder == null)
            {
                return;
            }

            // Un-escape underscores is MenuItem header. Header holds the profile name
            string tempProfileName = Regex.Replace(item.Header.ToString(),
                "_{2}", "_");
            ProfileSelected?.Invoke(this, holder, tempProfileName);
        }

        private void DisconnectMenuItem_Click(object sender,
            System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            int idx = Convert.ToInt32(item.Tag);
            ControllerHolder holder = GetControllerHolderByListIndex(idx);
            DS4Device tempDev = holder?.Device;
            if (tempDev != null && tempDev.Synced && !tempDev.Charging)
            {
                if (tempDev.ConnectionType == ConnectionType.BT)
                {
                    //tempDev.StopUpdate();
                    tempDev.DisconnectBT();
                }
                else if (tempDev.ConnectionType == ConnectionType.SONYWA)
                {
                    tempDev.DisconnectDongle();
                }
            }

            //controllerList[idx] = null;
        }

        private void PopulateControllerList()
        {
            List<ControllerHolder> holders = new List<ControllerHolder>();
            using (ReadLocker slotLocker = new ReadLocker(controlService.slotManager.CollectionLocker))
            {
                int idx = 0;
                foreach (DS4Device currentDev in controlService.slotManager.ControllerColl)
                {
                    holders.Add(new ControllerHolder(currentDev, idx));
                    idx++;
                }
            }

            using (WriteLocker locker = new WriteLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    RemoveDeviceEvents(holder.Device);
                }

                controllerList.Clear();
                foreach (ControllerHolder holder in holders)
                {
                    controllerList.Add(holder);
                    SetupDeviceEvents(holder.Device);
                }
            }
        }

        private void StartPopulateText(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PopulateToolText();
            });
            //PopulateContextMenu();
        }

        private void PopulateToolText()
        {
            TooltipText = trayTitle;
        }

        private void SetupEvents()
        {
            //IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
            //foreach (DS4Device currentDev in devices)
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    SetupDeviceEvents(holder.Device);
                }
            }
        }

        private void SetupDeviceEvents(DS4Device device)
        {
            if (device == null)
            {
                return;
            }

            RemoveDeviceEvents(device);
            device.BatteryChanged += UpdateForBattery;
            device.ChargingChanged += UpdateForBattery;
            device.Removal += CurrentDev_Removal;
        }

        private void RemoveDeviceEvents(DS4Device device)
        {
            if (device == null)
            {
                return;
            }

            device.BatteryChanged -= UpdateForBattery;
            device.ChargingChanged -= UpdateForBattery;
            device.Removal -= CurrentDev_Removal;
        }

        private void CurrentDev_Removal(object sender, EventArgs e)
        {
            DS4Device currentDev = sender as DS4Device;
            ControllerHolder item = null;
            int idx = 0;

            using (WriteLocker locker = new WriteLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    if (currentDev == holder.Device)
                    {
                        item = holder;
                        break;
                    }

                    idx++;
                }

                if (item != null)
                {
                    controllerList.RemoveAt(idx);
                    RemoveDeviceEvents(currentDev);
                }
            }

            PopulateToolText();
        }

        private ControllerHolder GetControllerHolderByListIndex(int idx)
        {
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                return idx >= 0 && idx < controllerList.Count ? controllerList[idx] : null;
            }
        }

        private void HookEvents(object sender, EventArgs e)
        {
            SetupEvents();
        }

        private void UpdateForBattery(object sender, EventArgs e)
        {
            // Force invoke from GUI thread
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PopulateToolText();
            });
        }

        private void ClearToolText(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                TooltipText = trayTitle;
            });
            //contextMenu.Items.Clear();
        }

        private void PopulateStaticItems()
        {
            ItemCollection items = contextMenu.Items;
            items.Add(changeServiceItem);
            items.Add(openItem);
            items.Add(minimizeItem);
            items.Add(openProgramItem);
            items.Add(new Separator());
            items.Add(closeItem);
        }

        public void ClearContextMenu()
        {
            contextMenu.Items.Clear();
            PopulateStaticItems();
        }

        private void UpdateTrayBattery(object sender, byte percentage)
        {
            if (Application.Current?.Dispatcher != null &&
                !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke((Action)(() => UpdateTrayBattery(sender, percentage)));
                return;
            }

            IconSource = percentage switch
            {
                < 10 => $"{Global.RESOURCES_PREFIX}/0.ico",
                >= 10 and < 20 => $"{Global.RESOURCES_PREFIX}/10.ico",
                >= 20 and < 30 => $"{Global.RESOURCES_PREFIX}/20.ico",
                >= 30 and < 40 => $"{Global.RESOURCES_PREFIX}/30.ico",
                >= 40 and < 50 => $"{Global.RESOURCES_PREFIX}/40.ico",
                >= 50 and < 60 => $"{Global.RESOURCES_PREFIX}/50.ico",
                >= 60 and < 70 => $"{Global.RESOURCES_PREFIX}/60.ico",
                >= 70 and < 80 => $"{Global.RESOURCES_PREFIX}/70.ico",
                >= 80 and < 90 => $"{Global.RESOURCES_PREFIX}/80.ico",
                >= 90 and < 100 => $"{Global.RESOURCES_PREFIX}/90.ico",
                100 => $"{Global.RESOURCES_PREFIX}/100.ico",
                _ => $"{Global.RESOURCES_PREFIX}/DS4W.ico"
            };
        }

        private void ExitMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestShutdown?.Invoke(this, EventArgs.Empty);
        }
    }

    public class ControllerHolder
    {
        private DS4Device device;
        private int index;
        public DS4Device Device { get => device; }
        public int Index { get => index; }

        public ControllerHolder(DS4Device device, int index)
        {
            this.device = device;
            this.index = index;
        }
    }
}
