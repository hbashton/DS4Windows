using System.Windows;
using System.Windows.Input;

namespace DS4WinWPF.DS4Forms
{
    public enum ViiperSetupPromptDecision
    {
        NotNow,
        InstallStandard,
        InstallPortable,
    }

    public partial class ViiperSetupPrompt : Window
    {
        private readonly bool mandatoryRepairRequired;

        public ViiperSetupPromptDecision Decision { get; private set; } =
            ViiperSetupPromptDecision.NotNow;

        public bool SuppressFuturePrompts =>
            suppressPromptCheck.IsChecked == true;

        public bool ExitApplicationRequested => false;

        public ViiperSetupPrompt(string currentStatus,
            string existingViiperPath, bool citrixUsbMonitorConflict = false,
            bool verifiedUpdateRequired = false,
            bool usbipReplacementRequired = false,
            bool mandatoryRepairRequired = false)
        {
            this.mandatoryRepairRequired = mandatoryRepairRequired ||
                verifiedUpdateRequired;
            InitializeComponent();
            statusText.Text = currentStatus;

            if (citrixUsbMonitorConflict)
            {
                headingText.Text = Properties.Resources.VPCitrixHeading;
                summaryText.Text = Properties.Resources.VPCitrixSummary;
                requirementsText.Text = Properties.Resources.VPCitrixRequirements;
                installButton.Content = Properties.Resources.VPDisableConflictingUsbMonitor;
                installPortableButton.Visibility = Visibility.Collapsed;
                portableWarningPanel.Visibility = Visibility.Collapsed;
                suppressPromptCheck.Visibility = Visibility.Collapsed;
                notNowButton.Content = Properties.Resources.VPContinueWithoutVirtualOutput;
                closeButton.ToolTip = Properties.Resources.VPContinueWithoutVirtualOutput;
                return;
            }

            if (!string.IsNullOrWhiteSpace(existingViiperPath))
            {
                existingViiperPathText.Text = existingViiperPath;
                existingViiperPanel.Visibility = Visibility.Visible;
            }

            if (verifiedUpdateRequired)
            {
                headingText.Text = usbipReplacementRequired
                    ? Properties.Resources.VPUsbipReplaceHeading
                    : Properties.Resources.VPVerificationFailedHeading;
                summaryText.Text = usbipReplacementRequired
                    ? currentStatus
                    : Properties.Resources.VPVerificationFailedSummary;
                requirementsHeadingText.Text = Properties.Resources.VPVerifiedUpdateRequired;
                requirementsText.Text = usbipReplacementRequired
                    ? Properties.Resources.VPUsbipReplaceRequirements
                    : Properties.Resources.VPVerificationFailedRequirements;
                installButton.Content = usbipReplacementRequired
                    ? Properties.Resources.VPRepairViiperUsbip
                    : Properties.Resources.VPInstallStandard;
                installPortableButton.Content =
                    Properties.Resources.VPKeepPortable;
                existingViiperPanel.Visibility = Visibility.Collapsed;
                suppressPromptCheck.Visibility = Visibility.Collapsed;
                notNowButton.Content = Properties.Resources.VPContinueWithoutVirtualOutput;
                closeButton.ToolTip = Properties.Resources.VPContinueWithoutVirtualOutput;
            }
            else if (this.mandatoryRepairRequired)
            {
                headingText.Text = Properties.Resources.VPSetupRequiredHeading;
                summaryText.Text = currentStatus;
                requirementsHeadingText.Text = Properties.Resources.VPRequiredBeforeRun;
                requirementsText.Text = Properties.Resources.VPMandatoryRequirements;
                installButton.Content = Properties.Resources.VPInstallRepair;
                installPortableButton.Content = Properties.Resources.VPKeepPortable;
                suppressPromptCheck.Visibility = Visibility.Collapsed;
                notNowButton.Content = Properties.Resources.VPContinueWithoutVirtualOutput;
                closeButton.ToolTip = Properties.Resources.VPContinueWithoutVirtualOutput;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender,
            MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            Decision = ViiperSetupPromptDecision.InstallStandard;
            DialogResult = true;
        }

        private void InstallPortableButton_Click(object sender,
            RoutedEventArgs e)
        {
            Decision = ViiperSetupPromptDecision.InstallPortable;
            DialogResult = true;
        }

        private void NotNowButton_Click(object sender, RoutedEventArgs e)
        {
            Decision = ViiperSetupPromptDecision.NotNow;
            DialogResult = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Decision = ViiperSetupPromptDecision.NotNow;
            DialogResult = false;
        }
    }
}
