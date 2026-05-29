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
using System.Linq;
using System.Windows;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// A simple dialog that lets the user pick which profiles are included
    /// in the touchpad-swipe cycle. An empty selection means "all profiles".
    /// </summary>
    public partial class SwipeProfileSelectWindow : Window
    {
        public class ProfileSelectEntry
        {
            public string Name { get; set; }
            public bool IsSelected { get; set; }
        }

        private ObservableCollection<ProfileSelectEntry> entries =
            new ObservableCollection<ProfileSelectEntry>();

        /// <summary>
        /// The resulting list of selected profile names. Empty = use all profiles.
        /// </summary>
        public List<string> SelectedProfiles { get; private set; } = new List<string>();

        public SwipeProfileSelectWindow(IEnumerable<string> allProfiles,
                                        IEnumerable<string> currentSelection)
        {
            InitializeComponent();

            var currentSet = new HashSet<string>(currentSelection);
            foreach (string name in allProfiles)
            {
                entries.Add(new ProfileSelectEntry
                {
                    Name = name,
                    IsSelected = currentSet.Contains(name)
                });
            }

            profileItemsControl.ItemsSource = entries;
        }

        private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedProfiles = entries.Where(x => x.IsSelected).Select(x => x.Name).ToList();
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in entries)
                entry.IsSelected = true;

            // Refresh checkboxes
            profileItemsControl.ItemsSource = null;
            profileItemsControl.ItemsSource = entries;
        }

        private void ClearAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in entries)
                entry.IsSelected = false;

            profileItemsControl.ItemsSource = null;
            profileItemsControl.ItemsSource = entries;
        }
    }
}
