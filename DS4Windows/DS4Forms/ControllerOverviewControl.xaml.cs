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
        private bool controllerAudioSourceSelectionCommitted;
        private string pendingControllerAudioSourceId;
        private bool outputControllerDropDownOpen;
        private bool outputControllerSelectionCommitted;
        private OutContType? pendingOutputController;

        public ControllerOverviewControl()
        {
            InitializeComponent();
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

        private void ControllerAudioSourceCombo_SelectionChanged(object sender,
            SelectionChangedEventArgs e)
        {
            // WPF briefly selects the first item while an asynchronously
            // refreshed ItemsSource is installed. Only an open dropdown is a
            // user selection; accepting the synthetic selection here cleared
            // the saved app source several seconds after the user chose it.
            if (!controllerAudioSourceDropDownOpen ||
                controllerAudioSourceSelectionCommitted ||
                e.AddedItems.Count == 0 || sender is not ComboBox comboBox ||
                comboBox.SelectedValue is not string endpointId)
            {
                return;
            }

            pendingControllerAudioSourceId = endpointId;
            controllerAudioSourceSelectionCommitted = true;
            CommitControllerAudioSource(endpointId);
        }

        private void ControllerAudioSourceCombo_DropDownOpened(object sender,
            EventArgs e)
        {
            controllerAudioSourceDropDownOpen = true;
            controllerAudioSourceSelectionCommitted = false;
            pendingControllerAudioSourceId = DataContext is
                MainWindowsViewModel viewModel
                    ? viewModel.ControllerAudioSourceId : null;
        }

        private void ControllerAudioSourceCombo_DropDownClosed(object sender,
            EventArgs e)
        {
            if (pendingControllerAudioSourceId != null)
            {
                CommitControllerAudioSource(pendingControllerAudioSourceId);
            }

            controllerAudioSourceDropDownOpen = false;
            controllerAudioSourceSelectionCommitted = false;
            pendingControllerAudioSourceId = null;
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

        private void OutputControllerCombo_SelectionChanged(object sender,
            SelectionChangedEventArgs e)
        {
            // Profile refresh raises SelectedValue notifications while the
            // virtual output is being replaced. A TwoWay binding treated the
            // ComboBox's temporary first-item selection as user input and
            // wrote Xbox 360 back into the profile. Only commit a selection
            // made while the user has this dropdown open.
            if (!outputControllerDropDownOpen ||
                outputControllerSelectionCommitted ||
                e.AddedItems.Count == 0 || sender is not ComboBox comboBox ||
                comboBox.SelectedValue is not OutContType outputType)
            {
                return;
            }

            pendingOutputController = outputType.Normalize();
            outputControllerSelectionCommitted = true;
            CommitOutputController(pendingOutputController.Value);
        }

        private void OutputControllerCombo_DropDownOpened(object sender,
            EventArgs e)
        {
            outputControllerDropDownOpen = true;
            outputControllerSelectionCommitted = false;
            pendingOutputController = DataContext is MainWindowsViewModel viewModel
                ? viewModel.SelectedOutputController
                : null;
        }

        private void OutputControllerCombo_DropDownClosed(object sender,
            EventArgs e)
        {
            if (pendingOutputController.HasValue)
            {
                CommitOutputController(pendingOutputController.Value);
            }

            outputControllerDropDownOpen = false;
            outputControllerSelectionCommitted = false;
            pendingOutputController = null;
        }

        private void OutputControllerCombo_PreviewKeyDown(object sender,
            KeyEventArgs e)
        {
            if (sender is not ComboBox comboBox ||
                e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or
                    Key.PageUp or Key.PageDown))
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() =>
                {
                    if (comboBox.SelectedValue is OutContType outputType)
                    {
                        CommitOutputController(outputType.Normalize());
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
