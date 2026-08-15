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
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DS4WinWPF.DS4Forms
{
    public partial class WelcomeDialog : Window
    {
        private const string HidHideInstaller =
            "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";
        private const string FakerInputX64 =
            "https://github.com/Ryochan7/FakerInput/releases/download/v0.1.0/FakerInput_0.1.0_x64.msi";
        private const string FakerInputX86 =
            "https://github.com/Ryochan7/FakerInput/releases/download/v0.1.0/FakerInput_0.1.0_x86.msi";

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

        private async void HidHideInstall_Click(object sender, RoutedEventArgs e)
        {
            await DownloadAndRunInstallerAsync(HidHideInstaller,
                hidHideInstallBtn, "HidHide");
        }

        private async void FakerInputInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            string url = Environment.Is64BitOperatingSystem ?
                FakerInputX64 : FakerInputX86;
            await DownloadAndRunInstallerAsync(url, fakerInputInstallBtn,
                "FakerInput");
        }

        private async Task DownloadAndRunInstallerAsync(string url,
            System.Windows.Controls.Button button, string componentName)
        {
            string target = Path.Combine(Path.GetTempPath(),
                Path.GetFileName(new Uri(url).AbsolutePath));
            try
            {
                SetInstallerControlsEnabled(false);
                button.Content = $"Downloading {componentName}…";
                byte[] payload = await App.requestClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(target, payload);

                button.Content = $"Installing {componentName}…";
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true,
                    Verb = "runas",
                });
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }

                DS4Windows.Global.RefreshHidHideInfo();
                button.Content = $"{componentName} setup complete";
            }
            catch (Exception ex)
            {
                button.Content = $"{componentName} setup failed";
                MessageBox.Show(this,
                    $"Could not install {componentName}: {ex.Message}",
                    $"{componentName} setup", MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                catch { }

                SetInstallerControlsEnabled(true);
            }
        }

        private void SetInstallerControlsEnabled(bool enabled)
        {
            viiperInstallBtn.IsEnabled = enabled;
            step4HidHidePanel.IsEnabled = enabled && IsHidHideCompatible();
            step5FakerInputPanel.IsEnabled = enabled &&
                DS4Windows.Global.IsWin8OrGreater();
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
