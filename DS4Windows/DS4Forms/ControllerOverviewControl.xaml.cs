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

namespace DS4WinWPF.DS4Forms
{
    public partial class ControllerOverviewControl : UserControl
    {
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
    }
}
