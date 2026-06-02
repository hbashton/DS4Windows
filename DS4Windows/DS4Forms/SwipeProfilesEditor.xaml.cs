/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// Dialog that lets the user choose which profiles the two-finger touchpad
    /// swipe is permitted to switch between.
    /// </summary>
    public partial class SwipeProfilesEditor : Window
    {
        private readonly SwipeProfilesEditorViewModel viewModel;

        /// <summary>
        /// The profile names the user ticked. Only valid after the dialog returns true.
        /// </summary>
        public List<string> SelectedProfiles { get; private set; } = new List<string>();

        public SwipeProfilesEditor(ProfileList profileListHolder, IEnumerable<string> currentAllowList)
        {
            InitializeComponent();

            HashSet<string> allowed = currentAllowList != null
                ? new HashSet<string>(currentAllowList)
                : new HashSet<string>();

            viewModel = new SwipeProfilesEditorViewModel();
            if (profileListHolder != null)
            {
                foreach (ProfileEntity entity in profileListHolder.ProfileListCol)
                {
                    viewModel.Items.Add(new SwipeProfileItem
                    {
                        Name = entity.Name,
                        IsAllowed = allowed.Contains(entity.Name)
                    });
                }
            }

            DataContext = viewModel;
        }

        private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (SwipeProfileItem item in viewModel.Items)
            {
                item.IsAllowed = true;
            }
        }

        private void ClearAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (SwipeProfileItem item in viewModel.Items)
            {
                item.IsAllowed = false;
            }
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedProfiles = viewModel.Items
                .Where(x => x.IsAllowed)
                .Select(x => x.Name)
                .ToList();

            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class SwipeProfilesEditorViewModel
    {
        public ObservableCollection<SwipeProfileItem> Items { get; } =
            new ObservableCollection<SwipeProfileItem>();
    }

    public class SwipeProfileItem : INotifyPropertyChanged
    {
        private string name;
        public string Name
        {
            get => name;
            set
            {
                if (name == value) return;
                name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        private bool isAllowed;
        public bool IsAllowed
        {
            get => isAllowed;
            set
            {
                if (isAllowed == value) return;
                isAllowed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAllowed)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
