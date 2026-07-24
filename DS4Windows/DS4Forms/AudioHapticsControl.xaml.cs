using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DS4Windows;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DS4WinWPF.DS4Forms
{
    public sealed class ProfileFeatureSettingsChangedEventArgs : EventArgs
    {
        public ProfileFeatureSettingsChangedEventArgs(int deviceIndex) => DeviceIndex = deviceIndex;
        public int DeviceIndex { get; }
    }

    public partial class AudioHapticsControl : UserControl
    {
        private sealed class AudioSourceChoice
        {
            public string DisplayName { get; init; }
            public AudioHapticsSourceKind Kind { get; init; }
            public int ProcessId { get; init; }
            public string ExecutableName { get; init; } = string.Empty;
            public string ProcessPath { get; init; } = string.Empty;
            public string SessionIdentifier { get; init; } = string.Empty;
            public string SessionInstanceIdentifier { get; init; } = string.Empty;
        }

        private int deviceIndex = -1;
        private bool loading;
        private readonly DispatcherTimer statusRefreshTimer;

        public AudioHapticsControl()
        {
            InitializeComponent();
            statusRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            statusRefreshTimer.Tick += (_, _) => UpdateStatus(CurrentSettings);
            Loaded += (_, _) =>
            {
                RefreshSourcesAndSettings();
                statusRefreshTimer.Start();
            };
            Unloaded += (_, _) => statusRefreshTimer.Stop();
            SetEditorEnabled(false);
        }

        public event EventHandler<ProfileFeatureSettingsChangedEventArgs> SettingsChanged;

        public int DeviceIndex => deviceIndex;

        public void SetDevice(int index)
        {
            deviceIndex = index >= 0 && index < Global.TEST_PROFILE_ITEM_COUNT ? index : -1;
            RefreshSourcesAndSettings();
        }

        public void RefreshSourcesAndSettings()
        {
            loading = true;
            try
            {
                AudioHapticsProfileSettings settings = CurrentSettings;
                PopulateAudioSources(settings);
                if (settings == null)
                {
                    SetEditorEnabled(false);
                    enabledToggle.IsChecked = false;
                    UpdateStatus(null);
                    return;
                }

                settings.Normalize();
                enabledToggle.IsChecked = settings.Enabled;
                gainSlider.Value = settings.GainPercent;
                gainValueText.Text = $"{settings.GainPercent}%";
                UpdateGainPresetVisuals(settings.GainPercent);
                bassFocusCombo.SelectedIndex = (int)settings.BassFocus;
                responseCombo.SelectedIndex = (int)settings.Response;
                attackCombo.SelectedIndex = (int)settings.Attack;
                releaseCombo.SelectedIndex = (int)settings.Release;
                SelectStoredSource(settings);
                automaticGameDetectionToggle.IsChecked =
                    settings.AutomaticGameDetection;
                streamAppToSpeakerToggle.IsChecked =
                    settings.StreamAppAudioToController;
                SetEditorEnabled(true);
                UpdateAppSpeakerOption(settings);
                UpdateModeVisuals(settings.Mode);
                UpdateStatus(settings);
            }
            finally
            {
                loading = false;
            }
        }

        private AudioHapticsProfileSettings CurrentSettings =>
            deviceIndex >= 0 && deviceIndex < Global.TEST_PROFILE_ITEM_COUNT
                ? Global.store.audioHapticsSettings[deviceIndex]
                : null;

        private void PopulateAudioSources(AudioHapticsProfileSettings settings)
        {
            List<AudioSourceChoice> choices = new List<AudioSourceChoice>
            {
                new AudioSourceChoice { DisplayName = "System audio", Kind = AudioHapticsSourceKind.SystemAudio },
                new AudioSourceChoice { DisplayName = "Controller audio", Kind = AudioHapticsSourceKind.ControllerAudio },
            };
            if (settings?.AutomaticGameDetection == true)
            {
                choices.Add(new AudioSourceChoice
                {
                    DisplayName = "No fallback app selected",
                    Kind = AudioHapticsSourceKind.AppSession,
                });
            }

            try
            {
                using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                AudioSessionManager sessionManager = endpoint.AudioSessionManager;
                try
                {
                    SessionCollection sessions = sessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using AudioSessionControl session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                        uint processId = session.GetProcessID;
                        if (processId == 0) continue;
                        string executableName = string.Empty;
                        string processPath = string.Empty;
                        string displayName = session.DisplayName;
                        try
                        {
                            using Process process = Process.GetProcessById((int)processId);
                            executableName = process.ProcessName;
                            processPath = process.MainModule?.FileName ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(displayName)) displayName = process.MainWindowTitle;
                            if (string.IsNullOrWhiteSpace(displayName)) displayName = executableName;
                        }
                        catch
                        {
                            if (string.IsNullOrWhiteSpace(displayName)) displayName = $"Process {processId}";
                        }

                        choices.Add(new AudioSourceChoice
                        {
                            DisplayName = $"{displayName}  ·  App",
                            Kind = AudioHapticsSourceKind.AppSession,
                            ProcessId = (int)processId,
                            ExecutableName = executableName,
                            ProcessPath = processPath,
                            SessionIdentifier = session.GetSessionIdentifier ?? string.Empty,
                            SessionInstanceIdentifier = session.GetSessionInstanceIdentifier ?? string.Empty,
                        });
                    }
                }
                finally
                {
                    sessionManager.Dispose();
                }
            }
            catch
            {
                // Core Audio can briefly reject session enumeration while an
                // endpoint is being replaced. The refresh button retries it.
            }

            if (settings?.Source == AudioHapticsSourceKind.AppSession &&
                !choices.Any(choice => SourceMatches(choice, settings)))
            {
                choices.Add(new AudioSourceChoice
                {
                    DisplayName = $"{(string.IsNullOrWhiteSpace(settings.DisplayName) ? settings.ExecutableName : settings.DisplayName)}  ·  Unavailable",
                    Kind = AudioHapticsSourceKind.AppSession,
                    ProcessId = settings.ProcessId,
                    ExecutableName = settings.ExecutableName,
                    ProcessPath = settings.ProcessPath,
                    SessionIdentifier = settings.SessionIdentifier,
                    SessionInstanceIdentifier = settings.SessionInstanceIdentifier,
                });
            }

            sourceCombo.ItemsSource = choices
                .GroupBy(choice => $"{choice.Kind}:{choice.ProcessId}:{choice.SessionInstanceIdentifier}")
                .Select(group => group.First())
                .ToList();
        }

        private void SelectStoredSource(AudioHapticsProfileSettings settings)
        {
            sourceCombo.SelectedItem = sourceCombo.Items.Cast<AudioSourceChoice>()
                .FirstOrDefault(choice => SourceMatches(choice, settings))
                ?? (settings.AutomaticGameDetection
                    ? sourceCombo.Items.Cast<AudioSourceChoice>()
                        .FirstOrDefault(choice => choice.Kind ==
                            AudioHapticsSourceKind.AppSession)
                    : null)
                ?? sourceCombo.Items.Cast<AudioSourceChoice>().FirstOrDefault();
        }

        private static bool SourceMatches(AudioSourceChoice choice, AudioHapticsProfileSettings settings)
        {
            if (choice.Kind != settings.Source) return false;
            if (choice.Kind != AudioHapticsSourceKind.AppSession) return true;
            if (settings.AutomaticGameDetection && settings.ProcessId == 0 &&
                string.IsNullOrWhiteSpace(settings.ExecutableName) &&
                choice.ProcessId == 0 &&
                string.IsNullOrWhiteSpace(choice.ExecutableName)) return true;
            if (!string.IsNullOrEmpty(settings.SessionInstanceIdentifier) &&
                choice.SessionInstanceIdentifier == settings.SessionInstanceIdentifier) return true;
            if (!string.IsNullOrEmpty(settings.SessionIdentifier) &&
                choice.SessionIdentifier == settings.SessionIdentifier) return true;
            if (!string.IsNullOrEmpty(settings.ProcessPath) &&
                string.Equals(choice.ProcessPath, settings.ProcessPath, StringComparison.OrdinalIgnoreCase)) return true;
            return settings.ProcessId > 0 && choice.ProcessId == settings.ProcessId;
        }

        private void SetEditorEnabled(bool hasDevice)
        {
            enabledToggle.IsEnabled = hasDevice;
            gainSlider.IsEnabled = hasDevice;
            sourceCombo.IsEnabled = hasDevice;
            mixModeButton.IsEnabled = hasDevice;
            replaceModeButton.IsEnabled = hasDevice;
            bassFocusCombo.IsEnabled = hasDevice;
            responseCombo.IsEnabled = hasDevice;
            attackCombo.IsEnabled = hasDevice;
            releaseCombo.IsEnabled = hasDevice;
            streamAppToSpeakerToggle.IsEnabled = hasDevice;
            automaticGameDetectionToggle.IsEnabled = hasDevice;
        }

        private void UpdateAppSpeakerOption(
            AudioHapticsProfileSettings settings)
        {
            bool appSelected = settings?.Source ==
                AudioHapticsSourceKind.AppSession;
            streamAppToSpeakerPanel.Visibility = appSelected
                ? Visibility.Visible
                : Visibility.Collapsed;
            streamAppToSpeakerToggle.IsEnabled =
                CurrentSettings != null && appSelected;
            streamAppToSpeakerToggle.IsChecked = appSelected &&
                settings.StreamAppAudioToController;
        }

        private void UpdateModeVisuals(AudioHapticsMode mode)
        {
            mixModeButton.Style = mode == AudioHapticsMode.Mix
                ? FindResource("BridgePrimaryButtonStyle") as Style
                : FindResource("BridgeSecondaryButtonStyle") as Style;
            replaceModeButton.Style = mode == AudioHapticsMode.Replace
                ? FindResource("BridgePrimaryButtonStyle") as Style
                : FindResource("BridgeSecondaryButtonStyle") as Style;
            modeHelpText.Text = mode == AudioHapticsMode.Mix
                ? "Mix adds audio-driven detail while preserving game-provided advanced haptics."
                : "Replace ignores game-provided advanced haptics and uses only the selected audio source.";
        }

        private void UpdateGainPresetVisuals(int gainPercent)
        {
            if (lowGainButton == null || mediumGainButton == null ||
                highGainButton == null)
            {
                return;
            }
            Style primary = FindResource("ActivePresetButton") as Style;
            Style secondary = FindResource("PresetButton") as Style;
            lowGainButton.Style = gainPercent == 50 ? primary : secondary;
            mediumGainButton.Style = gainPercent == 100 ? primary : secondary;
            highGainButton.Style = gainPercent == 150 ? primary : secondary;
        }

        private void UpdateStatus(AudioHapticsProfileSettings settings)
        {
            if (settings == null)
            {
                statusText.Text = "Select a controller";
                sourceStatusText.Text = "No source";
                statusDot.Fill = FindBrush("MutedForegroundColor", Brushes.Gray);
                return;
            }

            sourceStatusText.Text = SourceDisplayName(settings);
            if (!settings.Enabled)
            {
                statusText.Text = "Off";
                statusDot.Fill = FindBrush("MutedForegroundColor", Brushes.Gray);
                return;
            }

            bool liveController = deviceIndex >= 0 &&
                deviceIndex < ControlService.CURRENT_DS4_CONTROLLER_LIMIT &&
                Program.rootHub?.DS4Controllers[deviceIndex] != null;
            if (!liveController)
            {
                statusText.Text = "Saved to profile";
                statusDot.Fill = FindBrush("AccentColor", Brushes.DodgerBlue);
                return;
            }

            AudioHapticsRuntimeStatus runtime =
                Program.rootHub.GetAudioHapticsStatus(deviceIndex);
            statusText.Text = runtime.Active &&
                !settings.AutomaticGameDetection ? "Active" : runtime.Message;
            statusDot.Fill = runtime.Active
                ? FindBrush("SuccessColor", Brushes.LimeGreen)
                : FindBrush("AccentColor", Brushes.DodgerBlue);
        }

        private static string SourceDisplayName(AudioHapticsProfileSettings settings) => settings.Source switch
        {
            AudioHapticsSourceKind.ControllerAudio => "Controller audio",
            AudioHapticsSourceKind.AppSession when
                settings.AutomaticGameDetection => "Automatic game detection",
            AudioHapticsSourceKind.AppSession => string.IsNullOrWhiteSpace(settings.DisplayName)
                ? (string.IsNullOrWhiteSpace(settings.ExecutableName) ? "Selected app" : settings.ExecutableName)
                : settings.DisplayName,
            _ => "System audio",
        };

        private Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        private void Commit(Action<AudioHapticsProfileSettings> update)
        {
            if (loading || CurrentSettings == null) return;
            update(CurrentSettings);
            CurrentSettings.Normalize();
            UpdateModeVisuals(CurrentSettings.Mode);
            UpdateAppSpeakerOption(CurrentSettings);
            UpdateStatus(CurrentSettings);
            SettingsChanged?.Invoke(this, new ProfileFeatureSettingsChangedEventArgs(deviceIndex));
        }

        private void EnabledToggle_Click(object sender, RoutedEventArgs e) => Commit(settings => settings.Enabled = enabledToggle.IsChecked == true);
        private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int value = (int)Math.Round(e.NewValue);
            gainValueText.Text = $"{value}%";
            UpdateGainPresetVisuals(value);
            Commit(settings => settings.GainPercent = value);
        }
        private void GainPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int value)) gainSlider.Value = value;
        }
        private void RefreshSources_Click(object sender, RoutedEventArgs e) => RefreshSourcesAndSettings();
        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sourceCombo.SelectedItem is not AudioSourceChoice choice) return;
            Commit(settings =>
            {
                settings.Source = choice.Kind;
                if (choice.Kind != AudioHapticsSourceKind.AppSession)
                {
                    settings.AutomaticGameDetection = false;
                }
                settings.ProcessId = choice.ProcessId;
                settings.DisplayName = settings.AutomaticGameDetection &&
                    choice.ProcessId == 0 &&
                    string.IsNullOrWhiteSpace(choice.ExecutableName)
                    ? string.Empty
                    : choice.DisplayName.Replace("  ·  App", string.Empty)
                        .Replace("  ·  Unavailable", string.Empty);
                settings.ExecutableName = choice.ExecutableName;
                settings.ProcessPath = choice.ProcessPath;
                settings.SessionIdentifier = choice.SessionIdentifier;
                settings.SessionInstanceIdentifier = choice.SessionInstanceIdentifier;
            });
        }
        private void AutomaticGameDetectionToggle_Click(object sender,
            RoutedEventArgs e)
        {
            Commit(settings =>
            {
                settings.AutomaticGameDetection =
                    automaticGameDetectionToggle.IsChecked == true;
                if (settings.AutomaticGameDetection)
                {
                    settings.Source = AudioHapticsSourceKind.AppSession;
                }
            });
            RefreshSourcesAndSettings();
        }
        private void StreamAppToSpeakerToggle_Click(object sender,
            RoutedEventArgs e) => Commit(settings =>
            {
                settings.StreamAppAudioToController =
                    streamAppToSpeakerToggle.IsChecked == true &&
                    settings.Source == AudioHapticsSourceKind.AppSession;
                if (settings.StreamAppAudioToController && deviceIndex >= 0 &&
                    deviceIndex < Global.TEST_PROFILE_ITEM_COUNT)
                {
                    Global.DualSenseEnableSpeakerOutput[deviceIndex] = true;
                }
            });
        private void MixMode_Click(object sender, RoutedEventArgs e) => Commit(settings => settings.Mode = AudioHapticsMode.Mix);
        private void ReplaceMode_Click(object sender, RoutedEventArgs e) => Commit(settings => settings.Mode = AudioHapticsMode.Replace);
        private void BassFocusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Commit(settings => settings.BassFocus = (AudioHapticsBassFocus)Math.Max(0, bassFocusCombo.SelectedIndex));
        private void ResponseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Commit(settings => settings.Response = (AudioHapticsResponse)Math.Max(0, responseCombo.SelectedIndex));
        private void AttackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Commit(settings => settings.Attack = (AudioHapticsAttack)Math.Max(0, attackCombo.SelectedIndex));
        private void ReleaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Commit(settings => settings.Release = (AudioHapticsRelease)Math.Max(0, releaseCombo.SelectedIndex));
    }
}
