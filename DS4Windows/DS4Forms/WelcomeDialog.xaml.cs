/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Windows;

namespace DS4WinWPF.DS4Forms
{
    public partial class WelcomeDialog : Window
    {
        private const string HidHideReleasePage =
            "https://github.com/nefarius/HidHide/releases";
        private const string FakerInputReleasePage =
            "https://github.com/Ryochan7/FakerInput/releases";

        public WelcomeDialog(bool loadConfig = false)
        {
            if (loadConfig)
            {
                DS4Windows.Global.FindConfigLocation();
                DS4Windows.Global.Load();
            }

            InitializeComponent();
            step4HidHidePanel.IsEnabled = IsHidHideCompatible();
            step5FakerInputPanel.IsEnabled = DS4Windows.Global.IsWin8OrGreater();

            DS4Windows.ViiperPrerequisiteStatus status =
                DS4Windows.ViiperSetupManager.GetStatus(tryStartServer: true);
            if (status.Ready)
            {
                viiperInstallBtn.Content = "VIIPER Native UDE is ready";
            }
        }

        private void ViiperInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            DS4Windows.ViiperPrerequisiteStatus status =
                DS4Windows.ViiperSetupManager.GetStatus(tryStartServer: true);
            if (status.Ready)
            {
                viiperInstallBtn.Content = "VIIPER Native UDE is ready";
                return;
            }

            bool launched = DS4Windows.ViiperSetupManager.LaunchInstaller(status, this);
            viiperInstallBtn.Content = launched
                ? "Setup opened — finish it, then click here to verify"
                : "VIIPER setup needs attention";
        }

        private void HidHideInstall_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalDriverReleasePage(HidHideReleasePage, "HidHide");
        }

        private void FakerInputInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalDriverReleasePage(FakerInputReleasePage, "FakerInput");
        }

        private void OpenExternalDriverReleasePage(string url,
            string componentName)
        {
            try
            {
                DS4Windows.Util.StartProcessHelper(url);
                MessageBox.Show(this,
                    $"The portable DS4Windows runtime does not download or " +
                    $"elevate mutable {componentName} installers. Verify the " +
                    "publisher and signature on the official release page, " +
                    "or use the signed DS4Windows installer when available.",
                    $"{componentName} setup", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not open the {componentName} release page: " +
                    ex.Message,
                    $"{componentName} setup", MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static bool IsHidHideCompatible() =>
            DS4Windows.Global.IsWin10OrGreater() &&
            Environment.Is64BitOperatingSystem;

        private void Step2Btn_Click(object sender, RoutedEventArgs e) =>
            DS4Windows.Util.StartProcessHelper(
                "https://support.xbox.com/help/hardware-network/controller/connect-xbox-wireless-controller-to-pc");

        private void BluetoothSetLink_Click(object sender,
            RoutedEventArgs e) => Process.Start("control", "bthprops.cpl");

        private void FinishedBtn_Click(object sender, RoutedEventArgs e) =>
            Close();
    }

    public class WelcomeDialogResourcePaths
    {
        public string PairmodePNG =>
            $"{DS4Windows.Global.RESOURCES_PREFIX}/Pairmode.png";
    }
}
