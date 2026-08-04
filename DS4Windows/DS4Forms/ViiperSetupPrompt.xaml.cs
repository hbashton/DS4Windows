using System.Windows;
using System.Windows.Input;

namespace DS4WinWPF.DS4Forms
{
    public enum ViiperSetupPromptDecision
    {
        NotNow,
        InstallStandard,
        InstallPortable,
        UseExisting,
    }

    public partial class ViiperSetupPrompt : Window
    {
        private readonly bool verifiedUpdateRequired;

        public ViiperSetupPromptDecision Decision { get; private set; } =
            ViiperSetupPromptDecision.NotNow;

        public bool SuppressFuturePrompts =>
            suppressPromptCheck.IsChecked == true;

        public bool ExitApplicationRequested => verifiedUpdateRequired &&
            Decision == ViiperSetupPromptDecision.NotNow;

        public ViiperSetupPrompt(string currentStatus,
            string existingViiperPath, bool citrixUsbMonitorConflict = false,
            bool portableMigration = false,
            bool verifiedUpdateRequired = false)
        {
            this.verifiedUpdateRequired = verifiedUpdateRequired;
            InitializeComponent();
            statusText.Text = currentStatus;

            if (citrixUsbMonitorConflict)
            {
                headingText.Text = "VIIPER paused for system safety";
                summaryText.Text =
                    "A conflicting Citrix USB monitor is active.";
                requirementsText.Text =
                    "• Disable Citrix generic USB redirection only\n" +
                    "• Restart Windows before VIIPER starts again";
                installButton.Content = "Disable conflicting USB monitor";
                installPortableButton.Visibility = Visibility.Collapsed;
                portableWarningPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (!string.IsNullOrWhiteSpace(existingViiperPath))
            {
                existingViiperPathText.Text = existingViiperPath;
                existingViiperPanel.Visibility = Visibility.Visible;
                useExistingButton.Visibility = Visibility.Visible;
            }

            if (portableMigration)
            {
                // A verified portable backend is usable. This is only a
                // security recommendation, so the user may dismiss it
                // permanently. Hash failures override this below.
                suppressPromptCheck.Visibility = Visibility.Visible;
                headingText.Text = "Move VIIPER to its safer home?";
                summaryText.Text =
                    "Your portable DS4Windows can stay exactly where it is.";
                requirementsHeadingText.Text = "Recommended setup";
                requirementsText.Text =
                    "• Standard: install DS4Windows and VIIPER in Program Files\n" +
                    "• Portable: keep DS4Windows here and put VIIPER in LocalAppData\n" +
                    "• Keep both startup tasks aligned with your choice";
                existingViiperHeadingText.Text = "Current portable VIIPER";
                existingViiperDescriptionText.Text =
                    "It is working and will remain usable if you keep it. " +
                    "The standard location is safer for updates and prevents " +
                    "two VIIPER copies from competing.";
                useExistingButton.Content = "Keep portable VIIPER";
                installPortableButton.Content = "Repair portable install";
                installButton.Content = "Install standard";
            }

            if (verifiedUpdateRequired)
            {
                headingText.Text = "VIIPER verification failed";
                summaryText.Text =
                    "The installed VIIPER does not match this DS4Windows package.";
                requirementsHeadingText.Text = "Verified update required";
                requirementsText.Text =
                    "• Install the exact bundled VIIPER build\n" +
                    "• Choose Standard or Portable installation\n" +
                    "• The unverified backend will not be started";
                existingViiperPanel.Visibility = Visibility.Collapsed;
                useExistingButton.Visibility = Visibility.Collapsed;
                suppressPromptCheck.Visibility = Visibility.Collapsed;
                notNowButton.Content = "Exit DS4Windows";
                closeButton.ToolTip = "Exit DS4Windows";
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

        private void UseExistingButton_Click(object sender,
            RoutedEventArgs e)
        {
            Decision = ViiperSetupPromptDecision.UseExisting;
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
