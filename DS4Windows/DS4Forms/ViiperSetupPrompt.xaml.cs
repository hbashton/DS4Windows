using System.Windows;
using System.Windows.Input;

namespace DS4WinWPF.DS4Forms
{
    public enum ViiperSetupPromptDecision
    {
        NotNow,
        InstallStandard,
        UseExisting,
    }

    public partial class ViiperSetupPrompt : Window
    {
        public ViiperSetupPromptDecision Decision { get; private set; } =
            ViiperSetupPromptDecision.NotNow;

        public bool SuppressFuturePrompts =>
            suppressPromptCheck.IsChecked == true;

        public ViiperSetupPrompt(string currentStatus,
            string existingViiperPath, bool citrixUsbMonitorConflict = false,
            bool portableMigration = false)
        {
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
                headingText.Text = "Move VIIPER to its safer home?";
                summaryText.Text =
                    "Your portable DS4Windows can stay exactly where it is.";
                requirementsHeadingText.Text = "Recommended setup";
                requirementsText.Text =
                    "• Keep DS4Windows portable\n" +
                    "• Install only VIIPER in Program Files\n" +
                    "• Let DS4Windows own the VIIPER startup task";
                existingViiperHeadingText.Text = "Current portable VIIPER";
                existingViiperDescriptionText.Text =
                    "It is working and will remain usable if you keep it. " +
                    "The standard location is safer for updates and prevents " +
                    "two VIIPER copies from competing.";
                useExistingButton.Content = "Keep portable VIIPER";
                installButton.Content = "Move VIIPER to Program Files";
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
