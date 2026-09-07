/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WinWPF.DS4Forms
{
    public partial class ControllerOverviewControl : UserControl
    {
        private bool controllerAudioSourceDropDownOpen;
        private string initialControllerAudioSourceId;
        private string pendingControllerAudioSourceId;
        private bool outputControllerDropDownOpen;
        private bool outputControllerKeyboardSelectionPending;
        private OutContType? initialOutputController;
        private OutContType? pendingOutputController;
        private MainWindowsViewModel outputControllerViewModel;

        public ControllerOverviewControl()
        {
            InitializeComponent();
            DataContextChanged += ControllerOverviewControl_DataContextChanged;
            Unloaded += ControllerOverviewControl_Unloaded;
        }

        private void ControllerOverviewControl_DataContextChanged(object sender,
            DependencyPropertyChangedEventArgs e)
        {
            HookOutputControllerViewModel(e.OldValue as MainWindowsViewModel,
                false);
            HookOutputControllerViewModel(e.NewValue as MainWindowsViewModel,
                true);
            RefreshOutputControllerSelection();
        }

        private void ControllerOverviewControl_Unloaded(object sender,
            RoutedEventArgs e) =>
            HookOutputControllerViewModel(outputControllerViewModel, false);

        private void HookOutputControllerViewModel(
            MainWindowsViewModel viewModel, bool hook)
        {
            if (viewModel == null)
            {
                return;
            }

            if (hook)
            {
                if (ReferenceEquals(outputControllerViewModel, viewModel))
                {
                    return;
                }
                outputControllerViewModel = viewModel;
                viewModel.SelectedOutputControllerChanged +=
                    ViewModel_SelectedOutputControllerChanged;
            }
            else
            {
                viewModel.SelectedOutputControllerChanged -=
                    ViewModel_SelectedOutputControllerChanged;
                if (ReferenceEquals(outputControllerViewModel, viewModel))
                {
                    outputControllerViewModel = null;
                }
            }
        }

        private void ViewModel_SelectedOutputControllerChanged(object sender,
            EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                    new Action(RefreshOutputControllerSelection));
                return;
            }
            RefreshOutputControllerSelection();
        }

        private void RefreshOutputControllerSelection()
        {
            if (outputControllerDropDownOpen || outputControllerCombo == null ||
                DataContext is not MainWindowsViewModel viewModel)
            {
                return;
            }

            // The profile reload is asynchronous, while this ComboBox is
            // deliberately one-way to reject WPF's synthetic first-item
            // selections. Reconcile its target explicitly whenever runtime
            // state changes so a successful DualSense/DS4 transition cannot
            // remain visually stuck on Xbox 360.
            outputControllerCombo.GetBindingExpression(
                ComboBox.SelectedValueProperty)?.UpdateTarget();
            outputControllerCombo.SetCurrentValue(
                ComboBox.SelectedValueProperty,
                viewModel.SelectedOutputController);
        }

        public event EventHandler EditProfileRequested;
        public event EventHandler ControllerDetailsRequested;
        public event EventHandler LightbarRequested;
        public event EventHandler DisconnectRequested;

        private void EditProfileBtn_Click(object sender, RoutedEventArgs e) =>
            EditProfileRequested?.Invoke(this, EventArgs.Empty);

        private void ControllerDetailsBtn_Click(object sender, RoutedEventArgs e) =>
            ControllerDetailsRequested?.Invoke(this, EventArgs.Empty);

        private void LightbarBtn_Click(object sender, RoutedEventArgs e) =>
            LightbarRequested?.Invoke(this, EventArgs.Empty);

        private void DisconnectBtn_Click(object sender, RoutedEventArgs e) =>
            DisconnectRequested?.Invoke(this, EventArgs.Empty);

        private void OpenWindowsSoundSettings_Click(object sender, RoutedEventArgs e) =>
            ControllerSoundSettingsNavigation.Open();

        private void ControllerAudioSourceCombo_SelectionChanged(object sender,
            SelectionChangedEventArgs e)
        {
            // WPF can report more than one selection while an asynchronously
            // refreshed ItemsSource is being reconciled. Keep the last visible
            // value for the entire gesture and serialize it only after the
            // popup closes. Committing the first event reloaded the profile
            // underneath the open popup and made reselecting the same app look
            // successful while the old source remained active.
            if (!controllerAudioSourceDropDownOpen ||
                e.AddedItems.Count == 0 || sender is not ComboBox comboBox ||
                comboBox.SelectedValue is not string endpointId)
            {
                return;
            }

            pendingControllerAudioSourceId = endpointId;
        }

        private void ControllerAudioSourceCombo_DropDownOpened(object sender,
            EventArgs e)
        {
            controllerAudioSourceDropDownOpen = true;
            initialControllerAudioSourceId = DataContext is
                MainWindowsViewModel viewModel
                    ? viewModel.ControllerAudioSourceId : null;
            pendingControllerAudioSourceId = initialControllerAudioSourceId;
        }

        private void ControllerAudioSourceCombo_DropDownClosed(object sender,
            EventArgs e)
        {
            // SelectedValue is authoritative at close. This also covers a
            // mouse selection that did not raise SelectionChanged because the
            // same app was present under a freshly rebuilt choice instance.
            if (sender is ComboBox comboBox &&
                comboBox.SelectedValue is string endpointId)
            {
                pendingControllerAudioSourceId = endpointId;
            }

            controllerAudioSourceDropDownOpen = false;
            string finalEndpointId = pendingControllerAudioSourceId;
            string initialEndpointId = initialControllerAudioSourceId;
            initialControllerAudioSourceId = null;
            pendingControllerAudioSourceId = null;
            if (finalEndpointId != null && !string.Equals(finalEndpointId,
                    initialEndpointId, StringComparison.Ordinal))
            {
                CommitControllerAudioSource(finalEndpointId);
            }
        }

        private void ControllerAudioSourceCombo_PreviewKeyDown(object sender,
            KeyEventArgs e)
        {
            if (sender is not ComboBox comboBox ||
                e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or
                    Key.PageUp or Key.PageDown))
            {
                return;
            }

            // A closed ComboBox also supports keyboard selection. Commit after
            // WPF applies the key so keyboard and mouse users get identical
            // profile serialization without reopening the ItemsSource race.
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() =>
                {
                    if (comboBox.SelectedValue is string endpointId)
                    {
                        CommitControllerAudioSource(endpointId);
                    }
                }));
        }

        private void CommitControllerAudioSource(string endpointId)
        {
            if (!IsLoaded || DataContext is not MainWindowsViewModel viewModel)
            {
                return;
            }

            viewModel.ControllerAudioSourceId = endpointId;
        }

        private async void RefreshControllerAudioSources_Click(object sender,
            RoutedEventArgs e)
        {
            if (DataContext is not MainWindowsViewModel viewModel ||
                sender is not Button button)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await viewModel.ForceRefreshControllerAudioChoicesAsync();

                // The selector is intentionally one-way so an ItemsSource
                // rebuild cannot accidentally serialize WPF's temporary first
                // item. Reapply the saved selection after the forced refresh.
                controllerAudioSourceCombo.GetBindingExpression(
                    ItemsControl.ItemsSourceProperty)?.UpdateTarget();
                controllerAudioSourceCombo.GetBindingExpression(
                    ComboBox.SelectedValueProperty)?.UpdateTarget();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private void OutputControllerCombo_SelectionChanged(object sender,
            SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox comboBox)
            {
                return;
            }

            if (!outputControllerDropDownOpen)
            {
                if (outputControllerKeyboardSelectionPending)
                {
                    return;
                }

                // A focused, closed ComboBox changes its local SelectedValue
                // when the wheel is turned. This binding is deliberately
                // one-way, so that visual-only value used to remain on Xbox
                // 360 even though neither the profile nor runtime changed.
                // Reconcile any closed-popup selection that did not come from
                // the explicit keyboard commit path.
                if (DataContext is MainWindowsViewModel viewModel &&
                    comboBox.SelectedValue is OutContType selectedType &&
                    selectedType.Normalize() !=
                        viewModel.SelectedOutputController)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                        new Action(RefreshOutputControllerSelection));
                }
                return;
            }

            // Keep the final visible selection locally until the popup closes.
            // Committing the first event immediately started a profile reload
            // while WPF was still processing the same dropdown gesture; that
            // refresh could select Xbox 360 and suppress the user's real click.
            if (e.AddedItems.Count == 0 ||
                comboBox.SelectedValue is not OutContType outputType)
            {
                return;
            }

            pendingOutputController = outputType.Normalize();
        }

        private void OutputControllerCombo_DropDownOpened(object sender,
            EventArgs e)
        {
            outputControllerDropDownOpen = true;
            initialOutputController = DataContext is
                MainWindowsViewModel viewModel
                    ? viewModel.SelectedOutputController
                    : null;
            pendingOutputController = initialOutputController;
        }

        private void OutputControllerCombo_DropDownClosed(object sender,
            EventArgs e)
        {
            OutContType? selection = pendingOutputController;
            OutContType? initialSelection = initialOutputController;
            outputControllerDropDownOpen = false;
            initialOutputController = null;
            pendingOutputController = null;
            // Output choices are static, so every genuine pointer/keyboard
            // choice raises SelectionChanged while the popup is open. Trust
            // that captured choice instead of rereading SelectedValue during
            // close, when WPF can briefly expose the first item (Xbox 360).
            if (selection.HasValue && selection != initialSelection)
            {
                CommitOutputController(selection.Value);
            }
            RefreshOutputControllerSelection();
        }

        private void OutputControllerCombo_PreviewKeyDown(object sender,
            KeyEventArgs e)
        {
            if (outputControllerDropDownOpen || sender is not ComboBox comboBox ||
                e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or
                    Key.PageUp or Key.PageDown))
            {
                return;
            }

            outputControllerKeyboardSelectionPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() =>
                {
                    try
                    {
                        if (comboBox.SelectedValue is OutContType outputType)
                        {
                            CommitOutputController(outputType.Normalize());
                        }
                    }
                    finally
                    {
                        outputControllerKeyboardSelectionPending = false;
                        RefreshOutputControllerSelection();
                    }
                }));
        }

        private void CommitOutputController(OutContType outputType)
        {
            if (!IsLoaded || DataContext is not MainWindowsViewModel viewModel)
            {
                return;
            }

            viewModel.SelectedOutputController = outputType;
        }
    }
}
