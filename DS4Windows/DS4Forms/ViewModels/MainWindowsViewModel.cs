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

using DS4Windows;
using DS4WinWPF.ApiDTO;
using HttpProgress;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class MainWindowsViewModel
    {
        private bool fullTabsEnabled = true;

        public bool FullTabsEnabled
        {
            get => fullTabsEnabled;
            set
            {
                fullTabsEnabled = value;
                FullTabsEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler FullTabsEnabledChanged;

        public string updaterExe = Environment.Is64BitProcess ? "DS4Updater.exe" : "DS4Updater_x86.exe";

        private string DownloadUpstreamUpdaterVersion()
        {
            // Sorry other devs, gonna have to find your own server
            Uri url = new Uri("https://api.github.com/repos/schmaldeo/DS4Updater/releases/latest");

            Task<System.Net.Http.HttpResponseMessage> requestTask = App.requestClient.GetAsync(url.ToString());
            requestTask.Wait();
            if (requestTask.Result.IsSuccessStatusCode)
            {
                var gitHubReleaseTask = requestTask.Result.Content.ReadFromJsonAsync<GithubRelease>();
                gitHubReleaseTask.Wait();
                if (!gitHubReleaseTask.IsFaulted)
                {
                    return gitHubReleaseTask.Result.TagName.Substring(1);
                }
            }
            return string.Empty;
        }

        public bool RunUpdaterCheck(bool launch, out string upstreamVersion)
        {
            string destPath = Path.Combine(Global.exedirpath, "DS4Updater.exe");
            bool updaterExists = File.Exists(destPath);
            upstreamVersion = DownloadUpstreamUpdaterVersion();
            if (!updaterExists ||
                (!string.IsNullOrEmpty(upstreamVersion) && FileVersionInfo.GetVersionInfo(destPath).FileVersion.CompareTo(upstreamVersion) != 0))
            {
                launch = false;
                Uri url2 = new Uri($"https://github.com/schmaldeo/DS4Updater/releases/download/v{upstreamVersion}/{updaterExe}");
                string filename = Path.Combine(Path.GetTempPath(), "DS4Updater.exe");
                using (var downloadStream = new FileStream(filename, FileMode.Create))
                {
                    Task<System.Net.Http.HttpResponseMessage> temp =
                        App.requestClient.GetAsync(url2.ToString(), downloadStream);
                    temp.Wait();
                    if (temp.Result.IsSuccessStatusCode) launch = true;
                }

                if (launch)
                {
                    if (Global.AdminNeeded())
                    {
                        int copyStatus = DS4Windows.Util.ElevatedCopyUpdater(filename);
                        if (copyStatus != 0) launch = false;
                    }
                    else
                    {
                        if (updaterExists) File.Delete(destPath);
                        File.Move(filename, destPath);
                    }
                }
            }

            return launch;
        }

        public void DownloadUpstreamVersionInfo()
        {
            // Sorry other devs, gonna have to find your own server
            Uri url = new Uri("https://api.github.com/repos/schmaldeo/DS4Windows/releases/latest");
            string filename = Global.appdatapath + "\\version.txt";
            bool success = false;
            using (StreamWriter streamWriter = new(filename, false))
            {
                Task<System.Net.Http.HttpResponseMessage> requestTask = App.requestClient.GetAsync(url.ToString());
                try
                {
                    requestTask.Wait();
                    if (requestTask.Result.IsSuccessStatusCode)
                    {
                        var gitHubReleaseTask = requestTask.Result.Content.ReadFromJsonAsync<GithubRelease>();
                        gitHubReleaseTask.Wait();
                        if (!gitHubReleaseTask.IsFaulted)
                        {
                            streamWriter.Write(gitHubReleaseTask.Result.TagName.Substring(1));
                            success = true;
                        }
                    }
                }
                catch (AggregateException) { }
            }

            if (!success && File.Exists(filename))
            {
                File.Delete(filename);
            }
        }

        public void CheckDrivers()
        {
            bool deriverinstalled = Global.IsViGEmBusInstalled();
            if (!deriverinstalled || !Global.IsRunningSupportedViGEmBus())
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = $"{Global.exelocation}";
                startInfo.Arguments = "-driverinstall";
                startInfo.Verb = "runas";
                startInfo.UseShellExecute = true;
                try
                {
                    using (Process temp = Process.Start(startInfo))
                    {
                    }
                }
                catch { }
            }
        }

        public bool LauchDS4Updater()
        {
            bool launch = false;
            using (Process p = new Process())
            {
                p.StartInfo.FileName = Path.Combine(Global.exedirpath, "DS4Updater.exe");
                bool isAdmin = Global.IsAdministrator();
                List<string> argList = new List<string>();
                argList.Add("-autolaunch");
                if (!isAdmin)
                {
                    argList.Add("-user");
                }

                // Specify current exe to have DS4Updater launch
                argList.Add("--launchExe");
                argList.Add(Global.exeFileName);

                p.StartInfo.Arguments = string.Join(" ", argList);
                if (Global.AdminNeeded())
                    p.StartInfo.Verb = "runas";

                try { launch = p.Start(); }
                catch (InvalidOperationException) { }
            }

            return launch;
        }

        public bool IsNET8Available()
        {
            return DS4Windows.Util.IsNet8DesktopRuntimeAvailable();
        }
    }
}
