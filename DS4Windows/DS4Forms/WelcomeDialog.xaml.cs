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
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;

namespace DS4WinWPF.DS4Forms
{
    public partial class WelcomeDialog : Window
    {
        private const string HidHideInstallerFileName =
            "HidHide_1.5.230_x64.exe";
        private const string HidHideInstallerSha256 =
            "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6";
        private const string FakerInputX64FileName =
            "FakerInput_0.1.0_x64.msi";
        private const string FakerInputX64Sha256 =
            "30CF218B624740A91BE4FCCA3ADFB4550BA8CC8F31AC9625FE39D238E64D13EA";
        private const string FakerInputX86FileName =
            "FakerInput_0.1.0_x86.msi";
        private const string FakerInputX86Sha256 =
            "0C0A01EEF8C57C9B3DB917131995A10ADB3599CC643D2F27AD28D9511B96DEC1";

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
                viiperInstallBtn.Content = "VIIPER is ready";
            }
        }

        private void ViiperInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            DS4Windows.ViiperPrerequisiteStatus status =
                DS4Windows.ViiperSetupManager.GetStatus(tryStartServer: true);
            if (status.Ready)
            {
                viiperInstallBtn.Content = "VIIPER is ready";
                return;
            }

            bool launched = DS4Windows.ViiperSetupManager.
                EnsureReadyWithPrompt(this, forcePrompt: true);
            viiperInstallBtn.Content = launched
                ? "Setup opened — finish it, then click here to verify"
                : "VIIPER setup needs attention";
        }

        private async void HidHideInstall_Click(object sender, RoutedEventArgs e)
        {
            await RunBundledInstallerAsync(hidHideInstallBtn, "HidHide",
                HidHideInstallerFileName,
                HidHideInstallerSha256);
        }

        private async void FakerInputInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            bool useX64 = Environment.Is64BitOperatingSystem;
            string fileName = useX64 ? FakerInputX64FileName :
                FakerInputX86FileName;
            string sha256 = useX64 ? FakerInputX64Sha256 :
                FakerInputX86Sha256;
            await RunBundledInstallerAsync(fakerInputInstallBtn,
                "FakerInput", fileName, sha256);
        }

        private async Task RunBundledInstallerAsync(
            System.Windows.Controls.Button button, string componentName,
            string bundledFileName, string expectedSha256)
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            string target = Path.Combine(AppContext.BaseDirectory, "extras",
                bundledFileName);
            try
            {
                SetInstallerControlsEnabled(false);

                if (!File.Exists(target))
                {
                    throw new FileNotFoundException(
                        $"The offline DS4Windows package is incomplete: " +
                        $"{bundledFileName} is missing.", target);
                }

                button.Content = $"Verifying bundled {componentName}…";
                if (!await InstallerMatchesSha256Async(target,
                    expectedSha256))
                {
                    throw new InvalidDataException(
                        $"The bundled {componentName} installer failed its " +
                        "SHA-256 integrity check.");
                }

                button.Content = $"Installing {componentName}…";
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true,
                    Verb = "runas",
                }) ?? throw new InvalidOperationException(
                    $"Windows did not start the {componentName} installer.");
                await process.WaitForExitAsync();

                const int RestartRequiredExitCode = 3010;
                bool restartRequired = process.ExitCode ==
                    RestartRequiredExitCode;
                if (process.ExitCode != 0 && !restartRequired)
                {
                    throw new InvalidOperationException(
                        $"The {componentName} installer exited with code {process.ExitCode}.");
                }

                if (componentName == "HidHide")
                {
                    DS4Windows.Global.RefreshHidHideInfo();
                }
                else if (componentName == "FakerInput")
                {
                    DS4Windows.Global.RefreshFakerInputInfo();
                }
                button.Content = restartRequired ?
                    $"{componentName} setup complete — restart required" :
                    $"{componentName} setup complete";
                if (restartRequired)
                {
                    MessageBox.Show(this,
                        $"{componentName} was installed successfully. Restart Windows to finish setup.",
                        $"{componentName} setup", MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
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
                SetInstallerControlsEnabled(true);
            }
        }

        private static async Task<bool> InstallerMatchesSha256Async(
            string path, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256)) return true;

            using FileStream stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 81920,
                useAsync: true);
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = await sha256.ComputeHashAsync(stream);
            return string.Equals(Convert.ToHexString(hash), expectedSha256,
                StringComparison.OrdinalIgnoreCase);
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
