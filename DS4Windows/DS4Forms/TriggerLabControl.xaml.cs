using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WinWPF.DS4Forms
{
    public partial class TriggerLabControl : UserControl
    {
        private sealed class SideUi
        {
            public bool IsLeft;
            public ComboBox Profile;
            public TextBlock ProfileDescription;
            public TextBlock ActiveLabel;
            public CheckBox Active;
            public Button RenameProfile;
            public Button DeleteProfile;
            public Button Feedback;
            public Button Weapon;
            public Button Vibration;
            public Slider Start;
            public Slider Wall;
            public Slider Force;
            public TextBlock StartValue;
            public TextBlock WallValue;
            public TextBlock ForceValue;
        }

        private sealed class ProfileChoice
        {
            public string Id { get; init; }
            public string Name { get; init; }
            public string Description { get; init; }
            public bool IsCustom { get; init; }
            public override string ToString() => Name;
        }

        private int deviceIndex = -1;
        private int physicalDeviceIndex = -1;
        private bool liveApplyPersistent;
        private bool loading;
        private readonly SideUi leftUi;
        private readonly SideUi rightUi;
        private readonly DispatcherTimer previewResetTimer;

        public TriggerLabControl()
        {
            InitializeComponent();
            leftUi = BuildSide(true);
            rightUi = BuildSide(false);
            leftCard.Content = BuildSideCard(leftUi);
            rightCard.Content = BuildSideCard(rightUi);
            previewResetTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2800),
            };
            previewResetTimer.Tick += (_, _) =>
            {
                previewResetTimer.Stop();
                if (liveApplyPersistent)
                {
                    ApplyPersistentEffects();
                }
                else
                {
                    RestorePhysicalProfileEffects();
                }
            };
            Loaded += (_, _) => RefreshSettings();
            Unloaded += (_, _) => previewResetTimer.Stop();
            RefreshSettings();
        }

        public event EventHandler<ProfileFeatureSettingsChangedEventArgs> SettingsChanged;
        public int DeviceIndex => deviceIndex;

        public void SetDevice(int index, int previewDeviceIndex = -1)
        {
            previewResetTimer.Stop();
            deviceIndex = index >= 0 && index < Global.TEST_PROFILE_ITEM_COUNT ? index : -1;
            physicalDeviceIndex = previewDeviceIndex >= 0 &&
                previewDeviceIndex < ControlService.CURRENT_DS4_CONTROLLER_LIMIT
                    ? previewDeviceIndex
                    : deviceIndex >= 0 &&
                        deviceIndex < ControlService.CURRENT_DS4_CONTROLLER_LIMIT
                            ? deviceIndex
                            : -1;
            liveApplyPersistent = deviceIndex >= 0 &&
                deviceIndex == physicalDeviceIndex;
            RefreshSettings();
        }

        public void RefreshSettings()
        {
            loading = true;
            try
            {
                TriggerLabProfileSettings settings = CurrentSettings;
                bool available = settings != null;
                IsEnabled = available;
                if (!available)
                {
                    labStatusText.Text = "Select a controller or profile to open Trigger Lab.";
                    return;
                }

                settings.Normalize();
                labEnabledToggle.IsChecked = settings.Enabled;
                linkedButton.Content = settings.Linked ? "Linked" : "Split";
                linkedButton.Style = FindResource(settings.Linked ? "BridgePrimaryButtonStyle" : "BridgeSecondaryButtonStyle") as Style;
                LoadSide(leftUi, settings.Left, settings.LeftActive,
                    settings.Enabled, settings.CustomProfiles);
                LoadSide(rightUi, settings.Right, settings.RightActive,
                    settings.Enabled, settings.CustomProfiles);
                UpdateStatus(settings);
            }
            finally
            {
                loading = false;
            }
        }

        private TriggerLabProfileSettings CurrentSettings =>
            deviceIndex >= 0 && deviceIndex < Global.TEST_PROFILE_ITEM_COUNT
                ? Global.store.triggerLabSettings[deviceIndex]
                : null;

        private static SideUi BuildSide(bool left) => new SideUi { IsLeft = left };

        private UIElement BuildSideCard(SideUi ui)
        {
            StackPanel root = new StackPanel();
            Grid heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border triggerGraphic = new Border
            {
                Width = 46, Height = 54, CornerRadius = new CornerRadius(9),
                BorderBrush = FindBrush("AccentColor", Brushes.DodgerBlue), BorderThickness = new Thickness(2),
                Background = FindBrush("RaisedBackgroundColor", Brushes.Transparent),
                Child = new TextBlock
                {
                    Text = ui.IsLeft ? "L2" : "R2", FontWeight = FontWeights.Bold, FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            heading.Children.Add(triggerGraphic);
            StackPanel title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = ui.IsLeft ? "Left Trigger" : "Right Trigger", FontSize = 17, FontWeight = FontWeights.SemiBold });
            title.Children.Add(new TextBlock { Text = $"Shape the {(ui.IsLeft ? "L2" : "R2")} trigger feel", Foreground = FindBrush("MutedForegroundColor", Brushes.Gray) });
            Grid.SetColumn(title, 1); heading.Children.Add(title);
            StackPanel active = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            ui.ActiveLabel = new TextBlock
            {
                Text = "Active",
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            active.Children.Add(ui.ActiveLabel);
            ui.Active = new CheckBox { Style = FindResource("LabToggle") as Style };
            ui.Active.Click += (_, _) => SideActiveChanged(ui);
            active.Children.Add(ui.Active); Grid.SetColumn(active, 2); heading.Children.Add(active);
            root.Children.Add(heading);

            Grid profileRow = new Grid { Margin = new Thickness(0, 18, 0, 12) };
            profileRow.ColumnDefinitions.Add(new ColumnDefinition());
            profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ui.Profile = new ComboBox { MinHeight = 36, MinWidth = 150 };
            ui.Profile.SelectionChanged += (_, _) => ProfileChanged(ui);
            profileRow.Children.Add(ui.Profile);
            StackPanel profileActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(profileActions, 1);
            profileActions.Children.Add(MakeIconButton("\uE74E", "Save new trigger profile", (_, _) => SaveCustomProfile(ui)));
            ui.RenameProfile = MakeIconButton("\uE70F", "Rename trigger profile", (_, _) => RenameCustomProfile(ui));
            ui.DeleteProfile = MakeIconButton("\uE74D", "Delete trigger profile", (_, _) => DeleteCustomProfile(ui));
            profileActions.Children.Add(ui.RenameProfile);
            profileActions.Children.Add(ui.DeleteProfile);
            profileRow.Children.Add(profileActions);
            root.Children.Add(profileRow);
            ui.ProfileDescription = new TextBlock
            {
                Margin = new Thickness(0, -4, 0, 12),
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(ui.ProfileDescription);

            Grid modes = new Grid();
            for (int i = 0; i < 3; i++) modes.ColumnDefinitions.Add(new ColumnDefinition());
            ui.Feedback = MakeModeButton("Feedback", ui, TriggerLabMode.Feedback, 0);
            ui.Weapon = MakeModeButton("Weapon", ui, TriggerLabMode.Weapon, 1);
            ui.Vibration = MakeModeButton("Vibration", ui, TriggerLabMode.Vibration, 2);
            modes.Children.Add(ui.Feedback); modes.Children.Add(ui.Weapon); modes.Children.Add(ui.Vibration);
            root.Children.Add(modes);

            root.Children.Add(MakeMeter("Start", ui, out ui.Start, out ui.StartValue));
            root.Children.Add(MakeMeter("Wall", ui, out ui.Wall, out ui.WallValue));
            root.Children.Add(MakeMeter("Force", ui, out ui.Force, out ui.ForceValue));

            Grid actions = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
            Button preview = new Button { Content = "\u25B6  Preview", MinHeight = 38, Style = FindResource("BridgeSecondaryButtonStyle") as Style };
            preview.Click += (_, _) => Preview(ui);
            Button reset = new Button { Content = $"\u21BB  Reset {(ui.IsLeft ? "L2" : "R2")}", MinHeight = 38, Style = FindResource("BridgeSecondaryButtonStyle") as Style };
            reset.Click += (_, _) => ResetSide(ui); Grid.SetColumn(reset, 2);
            actions.Children.Add(preview); actions.Children.Add(reset); root.Children.Add(actions);
            return root;
        }

        private Button MakeIconButton(string glyph, string tooltip, RoutedEventHandler click)
        {
            Button button = new Button { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Width = 34, Height = 34, Margin = new Thickness(0, 0, 5, 0), ToolTip = tooltip };
            button.Click += click;
            return button;
        }

        private Button MakeModeButton(string text, SideUi ui, TriggerLabMode mode, int column)
        {
            Button button = new Button { Content = text, Style = FindResource("LabModeButton") as Style, Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 2 ? 0 : 4, 0) };
            Grid.SetColumn(button, column);
            button.Click += (_, _) => ChangeMode(ui, mode);
            return button;
        }

        private Grid MakeMeter(string label, SideUi ui, out Slider slider, out TextBlock value)
        {
            Grid grid = new Grid { Margin = new Thickness(0, 15, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = FindBrush("MutedForegroundColor", Brushes.Gray), FontWeight = FontWeights.SemiBold });
            slider = new Slider { Style = FindResource("LabSlider") as Style };
            Grid.SetColumn(slider, 1); grid.Children.Add(slider);
            value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(value, 2); grid.Children.Add(value);
            string property = label;
            slider.ValueChanged += (_, e) => MeterChanged(ui, property, (int)e.NewValue);
            return grid;
        }

        private void LoadSide(SideUi ui, TriggerLabEffectSettings effect,
            bool active, bool labEnabled,
            IList<TriggerLabCustomProfile> customProfiles)
        {
            List<ProfileChoice> profiles = TriggerLabPresetCatalog.Presets
                .Select(preset => new ProfileChoice
                {
                    Id = preset.Id,
                    Name = preset.Name,
                    Description = preset.Description,
                }).ToList();
            profiles.AddRange(customProfiles.Select(profile => new ProfileChoice
            {
                Id = profile.Id,
                Name = profile.Name,
                Description = "Saved custom effect.",
                IsCustom = true,
            }));
            ui.Profile.ItemsSource = profiles;
            ui.Profile.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == effect.ProfileId) ?? profiles[0];
            ProfileChoice selected = (ProfileChoice)ui.Profile.SelectedItem;
            ui.ProfileDescription.Text = selected.Description;
            ui.Profile.ToolTip = selected.Description;
            ui.RenameProfile.IsEnabled = selected.IsCustom;
            ui.DeleteProfile.IsEnabled = selected.IsCustom;
            ui.ActiveLabel.Text = labEnabled ? "Active" : "Armed";
            ui.Active.IsChecked = active;
            ui.Active.ToolTip = labEnabled
                ? $"Enable or disable this effect for {(ui.IsLeft ? "L2" : "R2")} only."
                : $"This {(ui.IsLeft ? "L2" : "R2")} choice is saved, but Trigger Lab is globally paused.";
            ui.Start.Value = effect.StartPercent; ui.Wall.Value = effect.WallPercent; ui.Force.Value = effect.ForcePercent;
            ui.StartValue.Text = $"{effect.StartPercent}%"; ui.WallValue.Text = $"{effect.WallPercent}%"; ui.ForceValue.Text = $"{effect.ForcePercent}%";
            SetModeVisuals(ui, effect.Mode);
        }

        private TriggerLabEffectSettings CurrentEffect(SideUi ui) => ui.IsLeft ? CurrentSettings.Left : CurrentSettings.Right;
        private void Commit(Action<TriggerLabProfileSettings> update, bool apply = true,
            bool refresh = true)
        {
            if (loading || CurrentSettings == null) return;
            update(CurrentSettings);
            if (!CurrentSettings.Linked)
            {
                SaveSplitState(CurrentSettings);
            }
            CurrentSettings.Normalize();
            SettingsChanged?.Invoke(this, new ProfileFeatureSettingsChangedEventArgs(deviceIndex));
            if (refresh)
            {
                RefreshSettings();
            }
            else
            {
                UpdateStatus(CurrentSettings);
            }
            if (apply && liveApplyPersistent) ApplyPersistentEffects();
        }

        private void SideActiveChanged(SideUi ui) => Commit(settings =>
        {
            TriggerLabEffectSettings effect = CurrentEffect(ui);
            bool value = ui.Active.IsChecked == true && effect.ForcePercent > 0;
            if (ui.IsLeft) settings.LeftActive = value;
            else settings.RightActive = value;
            if (value) settings.Enabled = true;
            SetSelectedProfileActive(settings, effect.ProfileId, value);
        });

        private void LabEnabledToggle_Click(object sender, RoutedEventArgs e) => Commit(settings =>
        {
            bool enable = labEnabledToggle.IsChecked == true;
            settings.Enabled = enable;
            if (enable && !settings.LeftActive && !settings.RightActive)
            {
                settings.LeftActive = settings.Left.ForcePercent > 0;
                SetSelectedProfileActive(settings, settings.Left.ProfileId, settings.LeftActive);
            }
        });

        private void LinkedButton_Click(object sender, RoutedEventArgs e) => Commit(settings =>
        {
            if (!settings.Linked)
            {
                SaveSplitState(settings);
                settings.Linked = true;
                settings.Right = settings.Left.Clone();
            }
            else
            {
                settings.Linked = false;
                if (settings.HasSplitState)
                {
                    settings.Left = settings.SplitLeft.Clone();
                    settings.Right = settings.SplitRight.Clone();
                    settings.LeftActive = settings.SplitLeftActive;
                    settings.RightActive = settings.SplitRightActive;
                }
            }
        });

        private void ChangeMode(SideUi ui, TriggerLabMode mode) => Commit(settings =>
        {
            CurrentEffect(ui).Mode = mode;
            CurrentEffect(ui).ProfileId = EnsureAutoCustomProfile(settings, CurrentEffect(ui));
            SetSelectedProfileActive(settings, CurrentEffect(ui).ProfileId,
                ui.Active.IsChecked == true);
            MirrorIfLinked(settings, ui);
        });

        private void MeterChanged(SideUi ui, string property, int value)
        {
            if (property == "Start") ui.StartValue.Text = $"{value}%";
            else if (property == "Wall") ui.WallValue.Text = $"{value}%";
            else ui.ForceValue.Text = $"{value}%";
            bool refresh = CurrentEffect(ui).ProfileId == TriggerLabProfileSettings.DefaultProfileId ||
                property == "Force" && value == 0;
            Commit(settings =>
            {
                TriggerLabEffectSettings effect = CurrentEffect(ui);
                if (property == "Start") effect.StartPercent = value;
                else if (property == "Wall") effect.WallPercent = value;
                else effect.ForcePercent = value;
                effect.ProfileId = EnsureAutoCustomProfile(settings, effect);
                SetSelectedProfileActive(settings, effect.ProfileId,
                    ui.Active.IsChecked == true && effect.ForcePercent > 0);
                MirrorIfLinked(settings, ui);
            }, refresh: refresh);
        }

        private void ProfileChanged(SideUi ui)
        {
            if (loading || ui.Profile.SelectedItem is not ProfileChoice choice) return;
            Commit(settings =>
            {
                TriggerLabEffectSettings selected;
                if (!TriggerLabPresetCatalog.TryCreateEffect(choice.Id,
                    out selected))
                {
                    TriggerLabCustomProfile custom = settings.CustomProfiles
                        .FirstOrDefault(profile => profile.Id == choice.Id);
                    if (custom == null) return;
                    selected = ToEffect(custom);
                }
                bool active = (ui.IsLeft ? settings.LeftActive :
                    settings.RightActive) && selected.ForcePercent > 0;
                if (ui.IsLeft) settings.Left = selected; else settings.Right = selected;
                if (ui.IsLeft) settings.LeftActive = active; else settings.RightActive = active;
                MirrorIfLinked(settings, ui);
            });
        }

        private static TriggerLabEffectSettings ToEffect(TriggerLabCustomProfile profile) => new TriggerLabEffectSettings
        {
            ProfileId = profile.Id, Mode = profile.Mode, StartPercent = profile.StartPercent,
            WallPercent = profile.WallPercent, ForcePercent = profile.ForcePercent,
        }.Normalize();

        private static void MirrorIfLinked(TriggerLabProfileSettings settings, SideUi ui)
        {
            if (!settings.Linked) return;
            if (ui.IsLeft) settings.Right = settings.Left.Clone(); else settings.Left = settings.Right.Clone();
        }

        private static void SaveSplitState(TriggerLabProfileSettings settings)
        {
            settings.HasSplitState = true;
            settings.SplitLeft = settings.Left.Clone();
            settings.SplitRight = settings.Right.Clone();
            settings.SplitLeftActive = settings.LeftActive;
            settings.SplitRightActive = settings.RightActive;
        }

        private static void SetSelectedProfileActive(TriggerLabProfileSettings settings,
            string profileId, bool active)
        {
            TriggerLabCustomProfile profile = settings.CustomProfiles
                .FirstOrDefault(item => item.Id == profileId);
            if (profile != null)
            {
                profile.Active = active;
            }
        }

        private static string EnsureAutoCustomProfile(TriggerLabProfileSettings settings, TriggerLabEffectSettings effect)
        {
            string profileId = TriggerLabPresetCatalog.IsBuiltIn(effect.ProfileId)
                ? "custom"
                : effect.ProfileId;
            TriggerLabCustomProfile custom = settings.CustomProfiles
                .FirstOrDefault(profile => profile.Id == profileId);
            if (custom == null)
            {
                custom = new TriggerLabCustomProfile { Id = "custom", Name = "Custom" };
                settings.CustomProfiles.Insert(0, custom);
            }
            custom.Mode = effect.Mode; custom.StartPercent = effect.StartPercent; custom.WallPercent = effect.WallPercent; custom.ForcePercent = effect.ForcePercent;
            return custom.Id;
        }

        private void SaveCustomProfile(SideUi ui)
        {
            string name = PromptName("Save trigger profile", $"Custom Trigger {CurrentSettings.CustomProfiles.Count + 1}");
            if (string.IsNullOrWhiteSpace(name)) return;
            Commit(settings =>
            {
                string id = $"custom-{Guid.NewGuid():N}";
                TriggerLabEffectSettings effect = CurrentEffect(ui);
                settings.CustomProfiles.Add(new TriggerLabCustomProfile { Id = id, Name = name, Mode = effect.Mode, StartPercent = effect.StartPercent, WallPercent = effect.WallPercent, ForcePercent = effect.ForcePercent, Active = ui.Active.IsChecked == true });
                effect.ProfileId = id;
                MirrorIfLinked(settings, ui);
            });
        }

        private void RenameCustomProfile(SideUi ui)
        {
            TriggerLabCustomProfile profile = CurrentSettings.CustomProfiles.FirstOrDefault(item => item.Id == CurrentEffect(ui).ProfileId);
            if (profile == null) return;
            string name = PromptName("Rename trigger profile", profile.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            Commit(settings => profile.Name = name);
        }

        private void DeleteCustomProfile(SideUi ui)
        {
            TriggerLabCustomProfile profile = CurrentSettings.CustomProfiles.FirstOrDefault(item => item.Id == CurrentEffect(ui).ProfileId);
            if (profile == null) return;
            if (MessageBox.Show($"Delete {profile.Name}?", "Trigger Lab", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Commit(settings =>
            {
                settings.CustomProfiles.Remove(profile);
                if (settings.Left.ProfileId == profile.Id) { settings.Left = new TriggerLabEffectSettings(); settings.LeftActive = false; }
                if (settings.Right.ProfileId == profile.Id) { settings.Right = new TriggerLabEffectSettings(); settings.RightActive = false; }
                if (settings.SplitLeft.ProfileId == profile.Id) { settings.SplitLeft = new TriggerLabEffectSettings(); settings.SplitLeftActive = false; }
                if (settings.SplitRight.ProfileId == profile.Id) { settings.SplitRight = new TriggerLabEffectSettings(); settings.SplitRightActive = false; }
            });
        }

        private string PromptName(string title, string initial)
        {
            Window dialog = new Window { Title = title, Width = 390, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize, Background = FindBrush("SurfaceBackgroundColor", Brushes.Black) };
            Grid grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBox text = new TextBox { Text = initial, MaxLength = 48, MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center };
            grid.Children.Add(text);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button cancel = new Button { Content = "Cancel", Width = 86, Height = 34, IsCancel = true };
            Button save = new Button { Content = "Save", Width = 86, Height = 34, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Style = FindResource("BridgePrimaryButtonStyle") as Style };
            save.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(cancel); buttons.Children.Add(save); Grid.SetRow(buttons, 1); grid.Children.Add(buttons);
            dialog.Content = grid; text.SelectAll(); text.Focus();
            return dialog.ShowDialog() == true ? text.Text.Trim() : null;
        }

        private void Preview(SideUi ui)
        {
            if (CurrentSettings == null) return;
            previewResetTimer.Stop();
            ApplyEffect(ui.IsLeft ? TriggerId.LeftTrigger : TriggerId.RightTrigger, CurrentEffect(ui), true);
            if (CurrentSettings.Linked) ApplyEffect(ui.IsLeft ? TriggerId.RightTrigger : TriggerId.LeftTrigger, CurrentEffect(ui), true);
            previewResetTimer.Start();
        }

        private void ResetSide(SideUi ui) => Commit(settings =>
        {
            TriggerLabEffectSettings effect = CurrentEffect(ui);
            SetSelectedProfileActive(settings, effect.ProfileId, false);
            TriggerLabEffectSettings reset = TriggerLabPresetCatalog.Presets[0]
                .CreateEffect();
            if (ui.IsLeft)
            {
                settings.Left = reset;
                settings.LeftActive = false;
            }
            else
            {
                settings.Right = reset;
                settings.RightActive = false;
            }
            MirrorIfLinked(settings, ui);
        });

        public void ApplyPersistentEffects()
        {
            TriggerLabProfileSettings settings = CurrentSettings;
            if (settings == null) return;
            ApplyEffect(TriggerId.LeftTrigger, settings.Left, settings.Enabled && settings.LeftActive);
            ApplyEffect(TriggerId.RightTrigger, settings.Right, settings.Enabled && settings.RightActive);
        }

        public void RestorePhysicalProfileEffects()
        {
            if (physicalDeviceIndex < 0 ||
                physicalDeviceIndex >= Global.TEST_PROFILE_ITEM_COUNT)
            {
                return;
            }

            previewResetTimer.Stop();
            TriggerLabProfileSettings settings =
                Global.store.triggerLabSettings[physicalDeviceIndex];
            if (settings == null) return;
            ApplyEffect(TriggerId.LeftTrigger, settings.Left,
                settings.Enabled && settings.LeftActive);
            ApplyEffect(TriggerId.RightTrigger, settings.Right,
                settings.Enabled && settings.RightActive);
        }

        private void ApplyEffect(TriggerId trigger, TriggerLabEffectSettings settings, bool active)
        {
            if (physicalDeviceIndex < 0 ||
                physicalDeviceIndex >= ControlService.CURRENT_DS4_CONTROLLER_LIMIT) return;
            if (App.rootHub?.DS4Controllers[physicalDeviceIndex] is not DualSenseDevice device) return;
            TriggerLabEffectEncoder.ApplyToDevice(device, trigger, settings, active);
        }

        private void SetModeVisuals(SideUi ui, TriggerLabMode mode)
        {
            ui.Feedback.Style = FindResource(mode == TriggerLabMode.Feedback ? "BridgePrimaryButtonStyle" : "LabModeButton") as Style;
            ui.Weapon.Style = FindResource(mode == TriggerLabMode.Weapon ? "BridgePrimaryButtonStyle" : "LabModeButton") as Style;
            ui.Vibration.Style = FindResource(mode == TriggerLabMode.Vibration ? "BridgePrimaryButtonStyle" : "LabModeButton") as Style;
        }

        private void UpdateStatus(TriggerLabProfileSettings settings)
        {
            overrideBadge.Visibility = settings.HasActiveOverride
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!settings.Enabled)
            {
                bool anyArmed = settings.LeftActive || settings.RightActive;
                labStatusText.Text = anyArmed
                    ? $"Trigger Lab is paused. {ActiveTriggerLabel(settings)} " +
                        $"{(settings.LeftActive && settings.RightActive ? "are" : "is")} armed and will resume when Enabled is turned on."
                    : "Trigger Lab is paused. Choose an effect and arm L2 or R2, then turn Enabled on.";
                labBehaviorText.Text =
                    "Armed effects are saved per trigger and do not override the game while paused.";
                return;
            }

            labStatusText.Text = settings.HasActiveOverride
                ? $"Made with Trigger Lab - {ActiveTriggerLabel(settings)} overrides incoming game trigger effects."
                : "Trigger Lab is enabled. Arm L2 or R2 to persist an effect in this profile.";
            labBehaviorText.Text =
                "Active lab effects override incoming game adaptive-trigger output.";
        }

        private static string ActiveTriggerLabel(TriggerLabProfileSettings settings)
        {
            if (settings.LeftActive && settings.RightActive) return "L2 and R2";
            return settings.LeftActive ? "L2" : "R2";
        }

        private Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    }
}
