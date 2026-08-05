using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using WixToolset.BootstrapperApplicationApi;

namespace DS4Windows.Bootstrapper
{
    public partial class InstallerWindow : Window
    {
        private readonly InstallerApplication application;
        private InstallerMode mode;
        private bool applying;

        internal InstallerWindow(InstallerApplication application)
        {
            this.application = application;
            InitializeComponent();
            ApplyWindowsTheme();
            Closing += (_, e) =>
            {
                if (applying) e.Cancel = true;
            };
            Closed += (_, __) => application.OnWindowClosed();
        }

        internal void ShowConfirmation(InstallerMode detectedMode, IReadOnlyDictionary<string, PackageState> packages, bool infrastructureHealthy)
        {
            mode = detectedMode;
            HidePages();
            ConfirmationPage.Visibility = Visibility.Visible;
            OptionsCard.Visibility = Visibility.Visible;
            applying = false;

            switch (mode)
            {
                case InstallerMode.Update:
                    ModeTitle.Text = "Update DS4Windows";
                    ModeDescription.Text = "A managed DS4Windows installation was found. Only package-owned files will be replaced.";
                    ActionButton.Content = "Update";
                    break;
                case InstallerMode.Repair:
                    ModeTitle.Text = "Repair DS4Windows";
                    ModeDescription.Text = "This version is already installed. Setup will verify and repair its managed components.";
                    ActionButton.Content = "Repair";
                    break;
                case InstallerMode.Uninstall:
                    ModeTitle.Text = "Uninstall DS4Windows";
                    ModeDescription.Text = "DS4Windows and its managed VIIPER installation will be removed. Profiles, settings, and shared system drivers are preserved.";
                    ActionButton.Content = "Uninstall";
                    OptionsCard.Visibility = Visibility.Collapsed;
                    break;
                default:
                    ModeTitle.Text = "Install DS4Windows";
                    ModeDescription.Text = "Everything needed for a standard x64 installation is included and works offline.";
                    ActionButton.Content = "Install";
                    break;
            }

            Ds4Status.Text = PackageStatus(packages, "DS4WindowsMsi");
            ViiperStatus.Text = infrastructureHealthy ? "Ready" : "Will install or repair";
            UsbipStatus.Text = infrastructureHealthy ? "Ready" : "Will verify before changing";
        }

        internal void ShowPlanning()
        {
            HidePages();
            ProgressPage.Visibility = Visibility.Visible;
            ProgressTitle.Text = "Preparing installation…";
            ProgressDetail.Text = "Building a safe installation plan";
            OverallProgress.IsIndeterminate = true;
            applying = true;
        }

        internal void ShowApplying()
        {
            OverallProgress.IsIndeterminate = false;
            ProgressTitle.Text = mode == InstallerMode.Uninstall ? "Removing DS4Windows…" : "Installing DS4Windows…";
            ProgressDetail.Text = "Administrator permission is requested once";
        }

        internal void SetCurrentPackage(string packageId)
        {
            switch (packageId)
            {
                case "CloseRunningApplications": ProgressDetail.Text = "Closing running DS4Windows and VIIPER processes"; break;
                case "DS4WindowsMsi": ProgressDetail.Text = "Installing DS4Windows"; break;
                case "ViiperUsbipSetup": ProgressDetail.Text = "Verifying VIIPER and USB-IP"; break;
                case "HidHide": ProgressDetail.Text = "Installing optional HidHide"; break;
                case "FakerInput": ProgressDetail.Text = "Installing optional FakerInput"; break;
                default: ProgressDetail.Text = "Verifying installation"; break;
            }
        }

        internal void SetProgress(int percent)
        {
            OverallProgress.Value = Math.Max(0, Math.Min(100, percent));
            ProgressPercent.Text = percent + "%";
        }

        internal void ShowComplete(LaunchAction action)
        {
            HidePages();
            CompletePage.Visibility = Visibility.Visible;
            LaunchCheckBox.Visibility = Visibility.Visible;
            applying = false;
            if (action == LaunchAction.Uninstall)
            {
                CompleteTitle.Text = "DS4Windows was removed";
                CompleteDescription.Text = "Profiles, settings, and shared system drivers were preserved.";
                LaunchCheckBox.Visibility = Visibility.Collapsed;
            }
        }

        internal void ShowRestart()
        {
            HidePages();
            RestartPage.Visibility = Visibility.Visible;
            RestartDescription.Text = "Windows must restart before setup can safely continue. Setup will resume after you sign in.";
            RestartNowButton.IsEnabled = true;
            applying = false;
        }

        internal void ShowFailure(string message)
        {
            HidePages();
            FailurePage.Visibility = Visibility.Visible;
            FailureMessage.Text = message;
            applying = false;
        }

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            var action = mode == InstallerMode.Uninstall ? LaunchAction.Uninstall :
                         mode == InstallerMode.Repair ? LaunchAction.Repair : LaunchAction.Install;
            application.Begin(action, DesktopShortcutCheckBox.IsChecked == true,
                HidHideCheckBox.IsChecked == true, FakerInputCheckBox.IsChecked == true);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => application.Close(1223);
        private void CloseFailure_Click(object sender, RoutedEventArgs e) => application.CloseWithCurrentResult();
        private void Retry_Click(object sender, RoutedEventArgs e) { HidePages(); DetectingPage.Visibility = Visibility.Visible; application.Retry(); }
        private void OpenLog_Click(object sender, RoutedEventArgs e) => application.OpenLog();
        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(application.Diagnostics()); }
            catch
            {
                FailureMessage.Text += "\r\n\r\nWindows could not access the clipboard. Use Open log instead.";
            }
        }
        private void RestartLater_Click(object sender, RoutedEventArgs e) => application.Close(3010);
        private void RestartNow_Click(object sender, RoutedEventArgs e)
        {
            if (application.RestartWindows())
            {
                application.Close(3010);
            }
            else
            {
                RestartDescription.Text = "Windows could not start the restart automatically. Restart manually; setup will resume after you sign in.";
                RestartNowButton.IsEnabled = false;
            }
        }
        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (LaunchCheckBox.Visibility == Visibility.Visible && LaunchCheckBox.IsChecked == true) application.LaunchDs4Windows();
            application.Close();
        }

        private void HidePages()
        {
            DetectingPage.Visibility = Visibility.Collapsed;
            ConfirmationPage.Visibility = Visibility.Collapsed;
            ProgressPage.Visibility = Visibility.Collapsed;
            CompletePage.Visibility = Visibility.Collapsed;
            RestartPage.Visibility = Visibility.Collapsed;
            FailurePage.Visibility = Visibility.Collapsed;
        }

        private static string PackageStatus(IReadOnlyDictionary<string, PackageState> packages, string id)
        {
            return packages.TryGetValue(id, out var state) && state == PackageState.Present ? "Installed" : "Will install";
        }

        private void ApplyWindowsTheme()
        {
            var light = true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    light = Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) != 0;
                }
            }
            catch { }

            Resources["WindowBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#F4F7FB" : "#08121F"));
            Resources["CardBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#FFFFFF" : "#0E1B2A"));
            Resources["HoverBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#EAF2FC" : "#17283B"));
            Resources["BorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#D8E2EE" : "#22354A"));
            Resources["TextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#101D2D" : "#F4F8FC"));
            Resources["MutedBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#56708D" : "#9FB8D3"));
        }
    }
}
