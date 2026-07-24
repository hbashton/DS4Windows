using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms.ViewModels;
using IntegerUpDown = Xceed.Wpf.Toolkit.IntegerUpDown;

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
            public MappedControl FullPullMapping;
            public Button FullPullAction;
            public TextBlock FullPullActionName;
            public ComboBox FullPullMode;
            public TextBlock FullPullModeDescription;
            public Grid HipFireDelayRow;
            public IntegerUpDown HipFireDelay;
            public CheckBox GameRumbleVibration;
        }

        private sealed class ProfileChoice
        {
            public string Id { get; init; }
            public string Name { get; init; }
            public string Description { get; init; }
            public bool IsCustom { get; init; }
            public override string ToString() => Name;
        }

        private sealed class FullPullModeChoice
        {
            public string Name { get; init; }
            public string Description { get; init; }
            public TwoStageTriggerMode Mode { get; init; }
            public bool UsesDelay => Mode == TwoStageTriggerMode.HipFire ||
                Mode == TwoStageTriggerMode.HipFireExclusiveButtons;
        }

        private static readonly IReadOnlyList<FullPullModeChoice> FullPullModes =
            new List<FullPullModeChoice>
            {
                new FullPullModeChoice
                {
                    Name = "Off",
                    Mode = TwoStageTriggerMode.Disabled,
                    Description = "Only the regular trigger action runs.",
                },
                new FullPullModeChoice
                {
                    Name = "Add at full pull",
                    Mode = TwoStageTriggerMode.Normal,
                    Description = "Keep the regular trigger action active and also run the full-pull action at the end of travel.",
                },
                new FullPullModeChoice
                {
                    Name = "Replace at full pull",
                    Mode = TwoStageTriggerMode.ExclusiveButtons,
                    Description = "Release the regular trigger action and replace it with the full-pull action at the end of travel.",
                },
                new FullPullModeChoice
                {
                    Name = "Hair trigger",
                    Mode = TwoStageTriggerMode.HairTrigger,
                    Description = "Run both actions at full pull, then release both as soon as the trigger backs away from the end stop.",
                },
                new FullPullModeChoice
                {
                    Name = "Hip fire",
                    Mode = TwoStageTriggerMode.HipFire,
                    Description = "Wait for the chosen delay, then run the regular or full-pull action based on how far the trigger was pressed.",
                },
                new FullPullModeChoice
                {
                    Name = "Hip fire (exclusive)",
                    Mode = TwoStageTriggerMode.HipFireExclusiveButtons,
                    Description = "Wait for the chosen delay and run only one action: regular pull or full pull.",
                },
            };

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
            InitializeFullPullMappings();
            RefreshSettings();
        }

        private void InitializeFullPullMappings()
        {
            leftUi.FullPullMapping = null;
            rightUi.FullPullMapping = null;
            if (deviceIndex < 0 || deviceIndex >= Global.TEST_PROFILE_ITEM_COUNT)
            {
                return;
            }

            OutContType outputType = Global.outDevTypeTemp[deviceIndex].Normalize();
            leftUi.FullPullMapping = new MappedControl(deviceIndex,
                DS4Controls.L2FullPull, "L2 Full Pull", outputType, true);
            rightUi.FullPullMapping = new MappedControl(deviceIndex,
                DS4Controls.R2FullPull, "R2 Full Pull", outputType, true);
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
                linkedButton.Style = FindResource(settings.Linked ? "BridgePrimaryButtonStyle" : "BridgeSecondaryButtonStyle") as Style;
                splitButton.Style = FindResource(settings.Linked ? "BridgeSecondaryButtonStyle" : "BridgePrimaryButtonStyle") as Style;
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

            root.Children.Add(BuildFullPullSection(ui));
            root.Children.Add(BuildGameRumbleVibrationSection(ui));

            Grid actions = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
            Button preview = new Button { Content = "\u25B6  Preview", MinHeight = 38, Style = FindResource("BridgeSecondaryButtonStyle") as Style };
            preview.Click += (_, _) => Preview(ui);
            Button reset = new Button { Content = $"\u21BB  Reset {(ui.IsLeft ? "L2" : "R2")}", MinHeight = 38, Style = FindResource("BridgeSecondaryButtonStyle") as Style };
            reset.Click += (_, _) => ResetSide(ui); Grid.SetColumn(reset, 2);
            actions.Children.Add(preview); actions.Children.Add(reset); root.Children.Add(actions);
            return root;
        }

        private Border BuildGameRumbleVibrationSection(SideUi ui)
        {
            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
            });

            StackPanel copy = new StackPanel();
            copy.Children.Add(new TextBlock
            {
                Text = "Game rumble vibration",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
            });
            copy.Children.Add(new TextBlock
            {
                Text = $"Stream the game's {(ui.IsLeft ? "heavy / left" : "light / right")} HID rumble motor to {(ui.IsLeft ? "L2" : "R2")} as a low-latency vibration effect.",
                Margin = new Thickness(0, 4, 14, 0),
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(copy);

            ui.GameRumbleVibration = new CheckBox
            {
                Style = FindResource("LabToggle") as Style,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Independently enable game-rumble vibration for {(ui.IsLeft ? "L2" : "R2")}. This does not link the other trigger.",
            };
            ui.GameRumbleVibration.Click += (_, _) =>
                GameRumbleVibrationChanged(ui);
            System.Windows.Automation.AutomationProperties.SetName(
                ui.GameRumbleVibration,
                $"Stream game rumble to {(ui.IsLeft ? "L2" : "R2")}");
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                ui.GameRumbleVibration,
                ui.IsLeft ? "l2GameRumbleVibration" :
                    "r2GameRumbleVibration");
            Grid.SetColumn(ui.GameRumbleVibration, 1);
            content.Children.Add(ui.GameRumbleVibration);

            return new Border
            {
                Background = FindBrush("RaisedBackgroundColor", Brushes.Transparent),
                BorderBrush = FindBrush("BorderColor", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 16, 0, 0),
                Child = content,
            };
        }

        private Border BuildFullPullSection(SideUi ui)
        {
            StackPanel content = new StackPanel();
            Grid heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Full-pull action",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Border badge = new Border
            {
                Background = FindBrush("RaisedBackgroundColor", Brushes.Transparent),
                BorderBrush = FindBrush("BorderColor", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                Child = new TextBlock
                {
                    Text = "END OF TRAVEL",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
                },
            };
            Grid.SetColumn(badge, 1);
            heading.Children.Add(badge);
            content.Children.Add(heading);
            content.Children.Add(new TextBlock
            {
                Text = $"When {(ui.IsLeft ? "L2" : "R2")} is pressed completely, run the action below using the selected behavior.",
                Margin = new Thickness(0, 4, 0, 10),
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            });

            Grid actionContent = new Grid();
            actionContent.ColumnDefinitions.Add(new ColumnDefinition());
            actionContent.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
            });
            StackPanel actionText = new StackPanel();
            actionText.Children.Add(new TextBlock
            {
                Text = "Assigned action",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
            });
            ui.FullPullActionName = new TextBlock
            {
                Text = "Unassigned",
                Margin = new Thickness(0, 2, 10, 0),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            actionText.Children.Add(ui.FullPullActionName);
            actionContent.Children.Add(actionText);
            TextBlock changeLabel = new TextBlock
            {
                Text = "Change  ›",
                Foreground = FindBrush("AccentColor", Brushes.DodgerBlue),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(changeLabel, 1);
            actionContent.Children.Add(changeLabel);
            ui.FullPullAction = new Button
            {
                Content = actionContent,
                MinHeight = 54,
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Style = FindResource("BridgeSecondaryButtonStyle") as Style,
            };
            System.Windows.Automation.AutomationProperties.SetName(
                ui.FullPullAction,
                $"Change {(ui.IsLeft ? "L2" : "R2")} full-pull action");
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                ui.FullPullAction,
                ui.IsLeft ? "l2FullPullActionButton" : "r2FullPullActionButton");
            ui.FullPullAction.Click += (_, _) => EditFullPullAction(ui);
            content.Children.Add(ui.FullPullAction);

            Grid modeRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            modeRow.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(90),
            });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition());
            modeRow.Children.Add(new TextBlock
            {
                Text = "Behavior",
                FontWeight = FontWeights.SemiBold,
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            });
            ui.FullPullMode = new ComboBox
            {
                ItemsSource = FullPullModes,
                DisplayMemberPath = nameof(FullPullModeChoice.Name),
                MinHeight = 34,
            };
            System.Windows.Automation.AutomationProperties.SetName(
                ui.FullPullMode,
                $"{(ui.IsLeft ? "L2" : "R2")} full-pull behavior");
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                ui.FullPullMode,
                ui.IsLeft ? "l2FullPullModeCombo" : "r2FullPullModeCombo");
            ui.FullPullMode.SelectionChanged += (_, _) => FullPullModeChanged(ui);
            Grid.SetColumn(ui.FullPullMode, 1);
            modeRow.Children.Add(ui.FullPullMode);
            content.Children.Add(modeRow);
            ui.FullPullModeDescription = new TextBlock
            {
                Margin = new Thickness(90, 6, 0, 0),
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            };
            content.Children.Add(ui.FullPullModeDescription);

            ui.HipFireDelayRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            ui.HipFireDelayRow.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(90),
            });
            ui.HipFireDelayRow.ColumnDefinitions.Add(new ColumnDefinition());
            ui.HipFireDelayRow.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
            });
            ui.HipFireDelayRow.Children.Add(new TextBlock
            {
                Text = "Delay",
                FontWeight = FontWeights.SemiBold,
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            });
            ui.HipFireDelay = new IntegerUpDown
            {
                Minimum = 0,
                Maximum = 5000,
                Increment = 10,
                Value = TriggerOutputSettings.DEFAULT_HIP_TIME,
                MinHeight = 34,
            };
            System.Windows.Automation.AutomationProperties.SetName(
                ui.HipFireDelay,
                $"{(ui.IsLeft ? "L2" : "R2")} hip-fire delay");
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                ui.HipFireDelay,
                ui.IsLeft ? "l2HipFireDelay" : "r2HipFireDelay");
            ui.HipFireDelay.ValueChanged += (_, _) => FullPullDelayChanged(ui);
            Grid.SetColumn(ui.HipFireDelay, 1);
            ui.HipFireDelayRow.Children.Add(ui.HipFireDelay);
            TextBlock milliseconds = new TextBlock
            {
                Text = "ms",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = FindBrush("MutedForegroundColor", Brushes.Gray),
            };
            Grid.SetColumn(milliseconds, 2);
            ui.HipFireDelayRow.Children.Add(milliseconds);
            content.Children.Add(ui.HipFireDelayRow);

            return new Border
            {
                Background = FindBrush("RaisedBackgroundColor", Brushes.Transparent),
                BorderBrush = FindBrush("BorderColor", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 16, 0, 0),
                Child = content,
            };
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
            ui.GameRumbleVibration.IsChecked = ui.IsLeft
                ? CurrentSettings.LeftGameRumbleVibration
                : CurrentSettings.RightGameRumbleVibration;
            SetModeVisuals(ui, effect.Mode);
            RefreshFullPullUi(ui);
        }

        private TriggerOutputSettings FullPullOutputSettings(SideUi ui)
        {
            if (deviceIndex < 0 || deviceIndex >= Global.TEST_PROFILE_ITEM_COUNT)
            {
                return null;
            }

            return ui.IsLeft
                ? Global.L2OutputSettings[deviceIndex]
                : Global.R2OutputSettings[deviceIndex];
        }

        private void RefreshFullPullUi(SideUi ui)
        {
            TriggerOutputSettings outputSettings = FullPullOutputSettings(ui);
            bool available = outputSettings != null && ui.FullPullMapping != null;
            ui.FullPullAction.IsEnabled = available;
            ui.FullPullMode.IsEnabled = available;
            ui.HipFireDelay.IsEnabled = available;
            if (!available)
            {
                ui.FullPullActionName.Text = "Select a controller or profile";
                ui.FullPullModeDescription.Text = string.Empty;
                ui.HipFireDelayRow.Visibility = Visibility.Collapsed;
                return;
            }

            ui.FullPullMapping.UpdateMappingName();
            ui.FullPullActionName.Text = ui.FullPullMapping.MappingName;
            ui.FullPullAction.ToolTip =
                $"Choose what happens when {(ui.IsLeft ? "L2" : "R2")} reaches full travel.";
            FullPullModeChoice mode = FullPullModes.FirstOrDefault(choice =>
                choice.Mode == outputSettings.twoStageMode) ?? FullPullModes[0];
            ui.FullPullMode.SelectedItem = mode;
            ui.FullPullModeDescription.Text = mode.Description;
            ui.HipFireDelay.Value = Math.Max(0, Math.Min(5000,
                outputSettings.hipFireMS));
            ui.HipFireDelayRow.Visibility = mode.UsesDelay
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void FullPullModeChanged(SideUi ui)
        {
            if (loading || ui.FullPullMode.SelectedItem is not FullPullModeChoice choice)
            {
                return;
            }

            TriggerOutputSettings outputSettings = FullPullOutputSettings(ui);
            if (outputSettings == null || outputSettings.twoStageMode == choice.Mode)
            {
                return;
            }

            outputSettings.TwoStageMode = choice.Mode;
            ui.FullPullModeDescription.Text = choice.Description;
            ui.HipFireDelayRow.Visibility = choice.UsesDelay
                ? Visibility.Visible
                : Visibility.Collapsed;
            NotifyFullPullSettingsChanged();
        }

        private void FullPullDelayChanged(SideUi ui)
        {
            if (loading)
            {
                return;
            }

            TriggerOutputSettings outputSettings = FullPullOutputSettings(ui);
            int value = ui.HipFireDelay.Value ?? TriggerOutputSettings.DEFAULT_HIP_TIME;
            if (outputSettings == null || outputSettings.hipFireMS == value)
            {
                return;
            }

            outputSettings.hipFireMS = value;
            NotifyFullPullSettingsChanged();
        }

        private void EditFullPullAction(SideUi ui)
        {
            if (ui.FullPullMapping == null)
            {
                return;
            }

            DS4ControlSettings setting = ui.FullPullMapping.Setting;
            string before = FullPullBindingSignature(setting);
            BindingWindow window = new BindingWindow(deviceIndex, setting)
            {
                Owner = Window.GetWindow(this) ?? App.Current.MainWindow,
            };
            window.ShowDialog();
            string after = FullPullBindingSignature(setting);
            ui.FullPullMapping.UpdateMappingName();
            ui.FullPullActionName.Text = ui.FullPullMapping.MappingName;
            if (before == after)
            {
                return;
            }

            TriggerOutputSettings outputSettings = FullPullOutputSettings(ui);
            if (outputSettings != null)
            {
                if (setting.IsDefault && setting.IsShiftDefault)
                {
                    outputSettings.TwoStageMode = TwoStageTriggerMode.Disabled;
                }
                else if (outputSettings.twoStageMode == TwoStageTriggerMode.Disabled)
                {
                    outputSettings.TwoStageMode = TwoStageTriggerMode.Normal;
                }
            }

            NotifyFullPullSettingsChanged();
            loading = true;
            try
            {
                RefreshFullPullUi(ui);
            }
            finally
            {
                loading = false;
            }
        }

        private static string FullPullBindingSignature(DS4ControlSettings setting)
        {
            string actionMacro = setting.action.actionMacro == null
                ? string.Empty
                : string.Join(",", setting.action.actionMacro);
            string shiftMacro = setting.shiftAction.actionMacro == null
                ? string.Empty
                : string.Join(",", setting.shiftAction.actionMacro);
            return string.Join("|",
                setting.actionType, setting.action.actionKey,
                setting.action.actionBtn, actionMacro, setting.extras,
                setting.keyType, setting.LightbarMacroString,
                setting.shiftActionType, setting.shiftAction.actionKey,
                setting.shiftAction.actionBtn, shiftMacro,
                setting.shiftExtras, setting.shiftKeyType,
                setting.shiftTrigger);
        }

        private void NotifyFullPullSettingsChanged()
        {
            if (deviceIndex < 0 || deviceIndex >= Global.TEST_PROFILE_ITEM_COUNT)
            {
                return;
            }

            Global.CacheProfileCustomsFlags(deviceIndex);
            SettingsChanged?.Invoke(this,
                new ProfileFeatureSettingsChangedEventArgs(deviceIndex));
        }

        private TriggerLabEffectSettings CurrentEffect(SideUi ui) => ui.IsLeft ? CurrentSettings.Left : CurrentSettings.Right;
        private void Commit(Action<TriggerLabProfileSettings> update, bool apply = true,
            bool refresh = true)
        {
            if (loading || CurrentSettings == null) return;
            update(CurrentSettings);
            if (!CurrentSettings.Linked)
            {
                CurrentSettings.RememberSplitState();
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

        private void GameRumbleVibrationChanged(SideUi ui) =>
            Commit(settings =>
            {
                bool enabled = ui.GameRumbleVibration.IsChecked == true;
                if (ui.IsLeft)
                {
                    settings.LeftGameRumbleVibration = enabled;
                }
                else
                {
                    settings.RightGameRumbleVibration = enabled;
                }

                if (enabled)
                {
                    settings.Enabled = true;
                }
            });

        private void LabEnabledToggle_Click(object sender, RoutedEventArgs e) => Commit(settings =>
        {
            bool enable = labEnabledToggle.IsChecked == true;
            settings.Enabled = enable;
            if (enable && !settings.LeftActive && !settings.RightActive &&
                !settings.LeftGameRumbleVibration &&
                !settings.RightGameRumbleVibration)
            {
                settings.LeftActive = settings.Left.ForcePercent > 0;
                SetSelectedProfileActive(settings, settings.Left.ProfileId, settings.LeftActive);
            }
        });

        private void LinkedButton_Click(object sender, RoutedEventArgs e) =>
            Commit(settings => settings.SetLinkedMode(true));

        private void SplitButton_Click(object sender, RoutedEventArgs e) =>
            Commit(settings => settings.SetLinkedMode(false));

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
            settings.MirrorLinkedEffect(ui.IsLeft);
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
                bool anyArmed = settings.LeftActive || settings.RightActive ||
                    settings.LeftGameRumbleVibration ||
                    settings.RightGameRumbleVibration;
                labStatusText.Text = anyArmed
                    ? $"Trigger Lab is paused. {ArmedTriggerLabel(settings)} " +
                        $"{(AreBothTriggersArmed(settings) ? "are" : "is")} armed and will resume when Enabled is turned on."
                    : "Trigger Lab is paused. Choose an effect and arm L2 or R2, then turn Enabled on.";
                labBehaviorText.Text =
                    "Armed effects are saved per trigger and do not override the game while paused.";
                return;
            }

            labStatusText.Text = settings.HasActiveOverride
                ? $"Made with Trigger Lab - {ActiveTriggerLabel(settings)} overrides incoming game trigger effects."
                : settings.HasGameRumbleVibration
                    ? $"Game rumble is streaming to {GameRumbleTriggerLabel(settings)} as trigger vibration."
                    : "Trigger Lab is enabled. Arm L2 or R2 to persist an effect in this profile.";
            labBehaviorText.Text =
                "Active lab effects override incoming game adaptive-trigger output.";
        }

        private static string ActiveTriggerLabel(TriggerLabProfileSettings settings)
        {
            if (settings.LeftActive && settings.RightActive) return "L2 and R2";
            return settings.LeftActive ? "L2" : "R2";
        }

        private static string GameRumbleTriggerLabel(
            TriggerLabProfileSettings settings)
        {
            if (settings.LeftGameRumbleVibration &&
                settings.RightGameRumbleVibration)
            {
                return "L2 and R2";
            }

            return settings.LeftGameRumbleVibration ? "L2" : "R2";
        }

        private static string ArmedTriggerLabel(
            TriggerLabProfileSettings settings)
        {
            bool left = settings.LeftActive ||
                settings.LeftGameRumbleVibration;
            bool right = settings.RightActive ||
                settings.RightGameRumbleVibration;
            return left && right ? "L2 and R2" : left ? "L2" : "R2";
        }

        private static bool AreBothTriggersArmed(
            TriggerLabProfileSettings settings) =>
            (settings.LeftActive || settings.LeftGameRumbleVibration) &&
            (settings.RightActive || settings.RightGameRumbleVibration);

        private Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    }
}
