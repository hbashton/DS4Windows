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

using System;
using System.Threading.Tasks;
using DS4Windows;
namespace DS4WinWPF.DS4Forms.ViewModels
{
    class UpdaterWindowViewModel
    {
        private string Version { get; set; }

        private string markdown;

        public string Markdown
        {
            get => markdown;
            set
            {
                markdown = value;
                MarkdownChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler MarkdownChanged;

        public UpdaterWindowViewModel(string version)
        {
            Version = version;
        }

        public async Task DisplayChangelog()
        {
            var changelog = await Changelog.GetChangelogMarkdown();
            Markdown = changelog;
        }

        public void SetSkippedVersion()
        {
            if (!string.IsNullOrEmpty(Version))
            {
                Global.LastVersionChecked = Version;
            }
        }

        public void BlankSkippedVersion()
        {
            Global.LastVersionChecked = string.Empty;
        }
    }
}
