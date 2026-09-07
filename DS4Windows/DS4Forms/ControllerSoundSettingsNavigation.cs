using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using DS4Windows;

namespace DS4WinWPF.DS4Forms;

internal static class ControllerSoundSettingsNavigation
{
    internal static void Open()
    {
        try
        {
            // Navigation only: no default-device, volume, privacy or mix changes.
            using var process = Process.Start(new ProcessStartInfo("ms-settings:sound")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or
            InvalidOperationException or System.Security.SecurityException)
        {
            AppLogger.LogToGui($"Could not open Windows Sound settings: {exception.Message}", true);
            MessageBox.Show("Open Windows Settings > System > Sound to choose your Switch 2 Pro headphones or headset microphone.",
                "Headset audio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
