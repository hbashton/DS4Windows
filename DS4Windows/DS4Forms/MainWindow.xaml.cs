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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.Windows.Interop;
using System.Diagnostics;
using System.IO;
using System.Management;
using NonFormTimer = System.Timers.Timer;
using System.Runtime.InteropServices;
using System.ComponentModel;
using HttpProgress;
using System.Windows.Threading;

using DS4WinWPF.DS4Forms.ViewModels;
using DS4Windows;
using DS4WinWPF.DS4Control;
using DS4WinWPF.Translations;
using H.NotifyIcon.Core;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    [System.Security.SuppressUnmanagedCodeSecurity]
    public partial class MainWindow : Window
    {
        private const int DEFAULT_PROFILE_EDITOR_WIDTH = 1280;
        private const int DEFAULT_PROFILE_EDITOR_HEIGHT = 780;

        private const int POWER_RESUME = 7;
        private const int POWER_SUSPEND = 4;

        private MainWindowsViewModel mainWinVM;
        private StatusLogMsg lastLogMsg = new StatusLogMsg();
        private ProfileList profileListHolder = new ProfileList();
        private ListCollectionView profilesCollectionView;
        private LogViewModel logvm;
        private ControllerListViewModel conLvViewModel;
        private TrayIconViewModel trayIconVM;
        private SettingsViewModel settingsWrapVM;
        private IntPtr regHandle = new IntPtr();
        private bool showAppInTaskbar = false;
        private ManagementEventWatcher managementEvWatcher;
        private readonly object powerTransitionLock = new();
        private readonly PowerLifecyclePolicy powerLifecycle = new();
        private CancellationTokenSource powerResumeCancellation;
        private AutoProfileHolder autoProfileHolder;
        private NonFormTimer hotkeysTimer;
        private readonly object hotkeysTimerLock = new();
        private bool hotkeysTimerSubscribed;
        private bool hotkeysTimerEnabled;
        private NonFormTimer autoProfilesTimer;
        private readonly object autoProfilesTimerLock = new();
        private bool autoProfilesTimerSubscribed;
        private int autoProfilesTimerTickActive;
        private AutoProfileChecker autoprofileChecker;
        private ProfileEditor editor;
        private int shutdownRequested;
        private bool profileEditorLoading;
        private int profileEditorReturnTabIndex = -1;
        private bool profileEditorNavigationChanging;
        private readonly HashSet<int> overviewDirtyControllerIndices = new();
        private readonly HashSet<int> overviewProfileReloadControllerIndices =
            new();
        private readonly Dictionary<int, OutContType>
            overviewRequestedOutputControllers = new();
        private readonly Dictionary<int, bool>
            overviewRequestedSpeakerOutputStates = new();
        private readonly Dictionary<int, string>
            overviewRequestedAudioSourceIds = new();
        private readonly HashSet<int>
            overviewAudioHapticsOverrideReleaseRequests = new();
        private DispatcherTimer overviewProfileSaveTimer;
        private DispatcherTimer overviewStatusRefreshTimer;
        private bool preserveSize = true;
        private Size oldSize;
        private bool contextclose;
        private bool startMinimized;

        public ProfileList ProfileListHolder { get => profileListHolder; }

        public bool IsInitialShow { get; set; }

        public static List<ProcessPriorityClass> ProcessPriorityClasses =
        [
            ProcessPriorityClass.Normal, ProcessPriorityClass.AboveNormal,
            ProcessPriorityClass.High, ProcessPriorityClass.RealTime
        ];

        public MainWindow(ArgumentParser parser)
        {
            InitializeComponent();
            profileEditorReturnTabIndex = mainTabCon.Items.IndexOf(profilesTab);

            mainWinVM = new MainWindowsViewModel();
            DataContext = mainWinVM;
            mainWinVM.ProfileEditorNavigationIndexChanged += MainWinVM_ProfileEditorNavigationIndexChanged;
            mainWinVM.QuickProfileSettingChanged += MainWinVM_QuickProfileSettingChanged;
            mainWinVM.SelectedControllerChanged += MainWinVM_SelectedControllerChanged;

            overviewProfileSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(420),
            };
            overviewProfileSaveTimer.Tick += OverviewProfileSaveTimer_Tick;

            overviewStatusRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            overviewStatusRefreshTimer.Tick += (sender, e) =>
                mainWinVM.RefreshRuntimeState(App.rootHub);
            overviewStatusRefreshTimer.Start();

            App root = Application.Current as App;
            settingsWrapVM = new SettingsViewModel();
            settingsTab.DataContext = settingsWrapVM;
            RefreshViiperStatusText();
            logvm = new LogViewModel(App.rootHub);
            //logListView.ItemsSource = logvm.LogItems;
            logListView.DataContext = logvm;
            lastMsgLb.DataContext = lastLogMsg;
            ProcessPriorityComboBox.ItemsSource = ProcessPriorityClasses;

            profileListHolder.Refresh();
            profilesCollectionView = new ListCollectionView(profileListHolder.ProfileListCol)
            {
                Filter = ProfileMatchesSearch,
            };
            profilesListBox.ItemsSource = profilesCollectionView;

            StartStopBtn.Content = App.rootHub.running ? Translations.Strings.StopText :
                Translations.Strings.StartText;

            conLvViewModel = new ControllerListViewModel(App.rootHub, profileListHolder);
            mainWinVM.ControllerCol = conLvViewModel.ControllerCol;
            controllerLV.DataContext = conLvViewModel;
            controllerLV.ItemsSource = conLvViewModel.ControllerCol;
            mainWinVM.SelectedController = conLvViewModel.ControllerCol.FirstOrDefault();
            audioHapticsControl.SetDevice(mainWinVM.SelectedController?.DevIndex ?? -1);
            triggerLabControl.SetDevice(mainWinVM.SelectedController?.DevIndex ?? -1);
            ChangeControllerPanel();

            // Sort device by input slot number
            CollectionView view = (CollectionView)CollectionViewSource.GetDefaultView(controllerLV.ItemsSource);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription("DevIndex", ListSortDirection.Ascending));
            view.Refresh();

            trayIconVM = new TrayIconViewModel(App.rootHub, profileListHolder);

            // Need to define before calling TaskbarIcon.ForceCreate
            notifyIcon.DataContext = trayIconVM;
            notifyIcon.CustomName = Global.exelocation;
            notifyIcon.ContextMenu = trayIconVM.ContextMenu;

            // Remove TaskbarIcon from visual tree so Loaded and Unloaded events
            // are not fired for TaskbarIcon instance. Ignores early Dispose calls
            // when scaling changes or an RDP session is activated
            var parent = VisualTreeHelper.GetParent(notifyIcon) as Panel;
            if (parent != null)
            {
                parent.Children.Remove(notifyIcon);
                // Since Loaded event will not get fired from Window, need to
                // create the tray icon explicitly here
                EnsureTrayIconCreated();
            }

            startMinimized = Global.StartMinimized || parser.Mini;

            bool isElevated = Global.IsAdministrator();
            if (isElevated)
            {
                uacImg.Visibility = Visibility.Collapsed;
            }

            noContLb.Content = string.Format(Strings.NoControllersConnected,
                ControlService.CURRENT_DS4_CONTROLLER_LIMIT);

            autoProfileHolder = autoProfControl.AutoProfileHolder;
            autoProfControl.SetupDataContext(profileListHolder);

            autoprofileChecker = new AutoProfileChecker(autoProfileHolder);

            slotManControl.SetupDataContext(controlService: App.rootHub,
                App.rootHub.OutputslotMan);

            SetupEvents();
            foreach (CompositeDeviceModel controller in conLvViewModel.ControllerCol)
            {
                PrepareControllerItem(controller);
            }

            // Don't tie timers to main thread
            Thread timerThread = new Thread(() =>
            {
                hotkeysTimer = new NonFormTimer();
                hotkeysTimer.Interval = 20;
                hotkeysTimer.AutoReset = false;

                autoProfilesTimer = new NonFormTimer();
                autoProfilesTimer.Interval = 1000;
                autoProfilesTimer.AutoReset = false;
            });
            timerThread.IsBackground = true;
            timerThread.Priority = ThreadPriority.Lowest;
            timerThread.Start();
            // Wait for thread tasks to finish before continuing
            timerThread.Join();
        }

        public void LateChecks(ArgumentParser parser)
        {
            ControlService.StartupDiag($"MainWindow.LateChecks scheduled stopArg={parser.Stop}");
            Task tempTask = Task.Run(() =>
            {
                ControlService.StartupDiag("MainWindow.LateChecks task begin");
                ControlService.StartupDiag("MainWindow.CheckDrivers begin");
                bool driversReady = mainWinVM.CheckDrivers();
                ControlService.StartupDiag("MainWindow.CheckDrivers end");
                if (!driversReady)
                {
                    // Prerequisite state changed after the startup gate. The
                    // repair UI belongs to the STA dispatcher, and controller
                    // services must not start until a fresh probe is ready.
                    bool repaired = Dispatcher.Invoke(() =>
                        ViiperSetupManager.EnsureReadyWithPrompt(this));
                    if (!repaired)
                    {
                        ControlService.StartupDiag(
                            "MainWindow.CheckDrivers blocked service startup");
                        return;
                    }
                }
                if (!parser.Stop)
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        StartStopBtn.IsEnabled = false;
                    }));
                    Thread.Sleep(1000);
                    ControlService.StartupDiag("rootHub.Start begin from LateChecks");
                    App.rootHub.Start();
                    ControlService.StartupDiag("rootHub.Start end from LateChecks");
                    //root.rootHubtest.Start();
                }
                ControlService.StartupDiag("MainWindow.LateChecks task end");
            });

            // Log exceptions that might occur
            Util.LogAssistBackgroundTask(tempTask);
#if !BETA_VERSION
            tempTask = Task.Delay(100).ContinueWith(_ =>
            {
                int checkwhen = Global.CheckWhen;
                if (checkwhen > 0 && DateTime.Now >= Global.LastChecked + TimeSpan.FromHours(checkwhen))
                {
                    try
                    {
                        if (Changelog.CheckNewerReleaseExists(out string releaseTag, false))
                        {
                            DisplayUpdaterWindow(releaseTag);
                        }
                    }
                    catch
                    {
                        Dispatcher.Invoke(() => MessageBox.Show(Strings.FailedToRetrieveLatestVersion, "DS4Windows Updater"));
                        // bubble the exception up to allow to see what's wrong in the log
                        throw;
                    }

                    Global.LastChecked = DateTime.Now;
                }

                // Check if main window closing was requested from app update.
                // Quit task early
                //if (contextclose)
                //{
                //    return;
                //}
            });
#endif
            Util.LogAssistBackgroundTask(tempTask);
        }

        private void DisplayUpdaterWindow(string version)
        {
            MessageBoxResult result = MessageBoxResult.No;
            Dispatcher.Invoke(() =>
            {
                var updaterWin = new UpdaterWindow(version);
                updaterWin.ShowDialog();
                result = updaterWin.Result;
            });

            if (result == MessageBoxResult.Yes)
            {
                bool launch = true;
                launch = mainWinVM.RunUpdaterCheck(launch, out string newUpdaterVersion);

                if (launch)
                {
                    launch = mainWinVM.LauchDS4Updater(version);
                }

                if (launch)
                {
                    RequestApplicationShutdown();
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(Properties.Resources.PleaseDownloadUpdater);
                        if (!string.IsNullOrEmpty(newUpdaterVersion))
                        {
                            Util.StartProcessHelper(
                                $"https://github.com/hbashton/DS4Updater/releases/tag/v{newUpdaterVersion}");
                        }
                    });
                }
            }
        }

        private void Check_Version(bool showstatus = false)
        {
            string version = Global.exeversion;
            string newversion = string.Empty;
            string versionFilePath = Path.Combine(Global.appdatapath, "version.txt");
            ulong lastVersionNum = Global.LastVersionCheckedNum;
            //ulong lastVersion = Global.CompileVersionNumberFromString("2.1.1");

            bool versionFileExists = File.Exists(versionFilePath);
            if (versionFileExists)
            {
                newversion = File.ReadAllText(versionFilePath).Trim();
                //newversion = "2.1.3";
            }

            ulong newversionNum = !string.IsNullOrEmpty(newversion) ?
                Global.CompileVersionNumberFromString(newversion) : 0;

            if (!string.IsNullOrWhiteSpace(newversion) && version.CompareTo(newversion) != 0 &&
                lastVersionNum < newversionNum)
            {
                MessageBoxResult result = MessageBoxResult.No;
                Dispatcher.Invoke(() =>
                {
                    UpdaterWindow updaterWin = new UpdaterWindow(newversion);
                    updaterWin.ShowDialog();
                    result = updaterWin.Result;
                });

                if (result == MessageBoxResult.Yes)
                {
                    bool launch = true;
                    launch = mainWinVM.RunUpdaterCheck(launch, out string newUpdaterVersion);

                    if (launch)
                    {
                        launch = mainWinVM.LauchDS4Updater(newversion);
                    }

                    if (launch)
                    {
                        RequestApplicationShutdown();
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show(Properties.Resources.PleaseDownloadUpdater);
                            if (!string.IsNullOrEmpty(newUpdaterVersion))
                            {
                                Util.StartProcessHelper($"https://github.com/hbashton/DS4Updater/releases/tag/v{newUpdaterVersion}");
                            }
                        });
                    }
                }
                else
                {
                    if (versionFileExists)
                        File.Delete(versionFilePath);
                }
            }
            else
            {
                if (versionFileExists)
                    File.Delete(versionFilePath);

                if (showstatus)
                {
                    Dispatcher.Invoke(() => MessageBox.Show(Properties.Resources.UpToDate, "DS4Windows Updater"));
                }
            }
        }

        private void TrayIconVM_RequestMinimize(object sender, EventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void TrayIconVM_ProfileSelected(TrayIconViewModel sender,
            ControllerHolder item, string profile)
        {
            int idx = item.Index;
            CompositeDeviceModel devitem = conLvViewModel.ControllerDict[idx];
            if (devitem != null)
            {
                devitem.ChangeSelectedProfile(profile);
            }
        }

        private void ShowNotification(object sender, DS4Windows.DebugEventArgs e)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {

                if (!IsActive && (Global.Notifications == 2 ||
                    (Global.Notifications == 1 && e.Warning)))
                {
                    if (notifyIcon.IsCreated)
                    {
                        try
                        {
                            notifyIcon.ShowNotification(TrayIconViewModel.ballonTitle,
                            e.Data, !e.Warning ? H.NotifyIcon.Core.NotificationIcon.Info :
                            H.NotifyIcon.Core.NotificationIcon.Warning);
                        }
                        catch (System.InvalidOperationException)
                        {
                            // Ignore
                        }
                    }
                }
            }));
        }

        private void SetupEvents()
        {
            App root = Application.Current as App;
            App.rootHub.ServiceStarted += ControlServiceStarted;
            App.rootHub.RunningChanged += ControlServiceChanged;
            App.rootHub.PreServiceStop += PrepareForServiceStop;
            App.rootHub.OutputslotMan.SlotAssigned += OutputSlot_RuntimeChanged;
            App.rootHub.OutputslotMan.SlotUnassigned += OutputSlot_RuntimeChanged;
            //root.rootHubtest.RunningChanged += ControlServiceChanged;
            conLvViewModel.ControllerCol.CollectionChanged += ControllerCol_CollectionChanged;
            AppLogger.TrayIconLog += ShowNotification;
            AppLogger.GuiLog += UpdateLastStatusMessage;
            logvm.LogItems.CollectionChanged += LogItems_CollectionChanged;
            App.rootHub.Debug += UpdateLastStatusMessage;
            trayIconVM.RequestShutdown += TrayIconVM_RequestShutdown;
            trayIconVM.ProfileSelected += TrayIconVM_ProfileSelected;
            trayIconVM.RequestMinimize += TrayIconVM_RequestMinimize;
            trayIconVM.RequestOpen += TrayIconVM_RequestOpen;
            trayIconVM.RequestServiceChange += TrayIconVM_RequestServiceChange;
            settingsWrapVM.IconChoiceIndexChanged += SettingsWrapVM_IconChoiceIndexChanged;
            settingsWrapVM.AppChoiceIndexChanged += SettingsWrapVM_AppChoiceIndexChanged;

            autoProfControl.AutoDebugChanged += AutoProfControl_AutoDebugChanged;
            autoprofileChecker.RequestServiceChange += AutoprofileChecker_RequestServiceChange;
            autoProfileHolder.AutoProfileColl.CollectionChanged += AutoProfileColl_CollectionChanged;
            //autoProfControl.AutoProfVM.AutoProfileSystemChange += AutoProfVM_AutoProfileSystemChange;
            mainWinVM.FullTabsEnabledChanged += MainWinVM_FullTabsEnabledChanged;

            bool wmiConnected = false;
            WqlEventQuery q = new WqlEventQuery();
            ManagementScope scope = new ManagementScope("root\\CIMV2");
            q.EventClassName = "Win32_PowerManagementEvent";

            try
            {
                scope.Connect();
            }
            catch (COMException) { }
            catch (ManagementException) { }

            if (scope.IsConnected)
            {
                wmiConnected = true;
                managementEvWatcher = new ManagementEventWatcher(scope, q);
                managementEvWatcher.EventArrived += PowerEventArrive;
                try
                {
                    managementEvWatcher.Start();
                }
                catch (ManagementException) { wmiConnected = false; }
                catch (COMException) { wmiConnected = false; }
            }

            if (!wmiConnected)
            {
                AppLogger.LogToGui(@"Could not connect to Windows Management Instrumentation service.
Suspend support not enabled.", true);
            }
        }

        private void SettingsWrapVM_AppChoiceIndexChanged(object sender, EventArgs e)
        {
            AppThemeChoice choice = Global.UseCurrentTheme;
            App current = App.Current as App;
            current.ChangeTheme(choice);
            trayIconVM.PopulateContextMenu();
        }

        private void SettingsWrapVM_IconChoiceIndexChanged(object sender, EventArgs e)
        {
            trayIconVM.IconSource = Global.iconChoiceResources[Global.UseIconChoice];
        }

        private void MainWinVM_FullTabsEnabledChanged(object sender, EventArgs e)
        {
            settingsWrapVM.ViewEnabled = mainWinVM.FullTabsEnabled;
        }

        private void TrayIconVM_RequestServiceChange(object sender, EventArgs e)
        {
            ChangeService();
        }

        private void LogItems_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    int count = logListView.Items.Count;
                    if (count > 0)
                    {
                        logListView.ScrollIntoView(logvm.LogItems[count - 1]);
                    }
                }));
            }
        }

        private void ControlServiceStarted(object sender, EventArgs e)
        {
            if (Global.SwipeProfiles)
            {
                ChangeHotkeysStatus(true);
            }

            CheckAutoProfileStatus();

            if (!HasManagedInputController())
            {
                StartBoundedHotplugRecovery();
            }
        }

        private void AutoprofileChecker_RequestServiceChange(AutoProfileChecker sender, bool state)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (App.rootHub.running != state)
                {
                    ChangeService();
                }
            }));
        }

        private void AutoProfVM_AutoProfileSystemChange(AutoProfilesViewModel sender, bool state)
        {
            if (state)
            {
                ChangeAutoProfilesStatus(true);
                autoProfileHolder.AutoProfileColl.CollectionChanged += AutoProfileColl_CollectionChanged;
            }
            else
            {
                ChangeAutoProfilesStatus(false);
                autoProfileHolder.AutoProfileColl.CollectionChanged -= AutoProfileColl_CollectionChanged;
            }
        }

        private void AutoProfileColl_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            CheckAutoProfileStatus();
        }

        private void AutoProfControl_AutoDebugChanged(object sender, EventArgs e)
        {
            autoprofileChecker.AutoProfileDebugLogLevel = autoProfControl.AutoDebug == true ? 1 : 0;
        }

        private void PowerEventArrive(object sender, EventArrivedEventArgs e)
        {
            try
            {
                short evType = Convert.ToInt16(
                    e?.NewEvent?.GetPropertyValue("EventType"));
                switch (evType)
                {
                    case POWER_RESUME:
                        HandlePowerResume();
                        break;
                    case POWER_SUSPEND:
                        HandlePowerSuspend();
                        break;
                }
            }
            catch (Exception ex)
            {
                LogPowerTransitionFailure("processing a Windows power event", ex);
            }
        }

        private void HandlePowerSuspend()
        {
            lock (powerTransitionLock)
            {
                ControlService service = App.rootHub;
                PowerSuspendTransition transition = powerLifecycle.Suspend(
                    service?.running == true);
                if (!transition.Accepted)
                {
                    return;
                }

                powerResumeCancellation?.Cancel();
                powerResumeCancellation?.Dispose();
                powerResumeCancellation = null;
                DS4LightBar.shuttingdown = true;

                if (service == null)
                {
                    return;
                }

                service.suspending = true;
                if (!transition.StopService)
                {
                    return;
                }

                try
                {
                    service.Stop(immediateUnplug: true);
                }
                catch (Exception ex)
                {
                    LogPowerTransitionFailure(
                        "stopping the controller service for suspend", ex);
                }
            }
        }

        private void HandlePowerResume()
        {
            Task resumeTask = null;
            lock (powerTransitionLock)
            {
                DS4LightBar.shuttingdown = false;
                ControlService service = App.rootHub;
                if (service != null)
                {
                    service.suspending = false;
                }

                PowerResumeTransition transition = powerLifecycle.Resume();
                if (!transition.Accepted || !transition.RestartService ||
                    service == null)
                {
                    return;
                }

                powerResumeCancellation?.Cancel();
                powerResumeCancellation?.Dispose();
                powerResumeCancellation = new CancellationTokenSource();
                CancellationToken token = powerResumeCancellation.Token;
                resumeTask = ResumeServiceAfterPowerTransitionAsync(service,
                    transition.Generation, token);
            }

            Util.LogAssistBackgroundTask(resumeTask);
        }

        private async Task ResumeServiceAfterPowerTransitionAsync(
            ControlService service, long generation, CancellationToken token)
        {
            try
            {
                // Allow the Bluetooth/HID stacks to recreate their handles after
                // hibernation before controller enumeration begins.
                await Task.Delay(5000, token).ConfigureAwait(false);
                lock (powerTransitionLock)
                {
                    if (token.IsCancellationRequested ||
                        !powerLifecycle.IsCurrent(generation) ||
                        !ReferenceEquals(service, App.rootHub) ||
                        service.suspending || service.running)
                    {
                        return;
                    }

                    service.LogDebug(
                        DS4WinWPF.Translations.Strings.WakeupFromSuspend);
                    service.Start();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A new suspend or application shutdown superseded this resume.
            }
            catch (Exception ex)
            {
                LogPowerTransitionFailure(
                    "restarting the controller service after resume", ex);
            }
        }

        private static void LogPowerTransitionFailure(string operation,
            Exception ex)
        {
            try
            {
                App.rootHub?.LogDebug(
                    $"Power lifecycle error while {operation}: {ex}", true);
            }
            catch { }
        }

        private void DisposePowerLifecycle()
        {
            lock (powerTransitionLock)
            {
                powerLifecycle.Close();
                powerResumeCancellation?.Cancel();
                powerResumeCancellation?.Dispose();
                powerResumeCancellation = null;

                if (managementEvWatcher != null)
                {
                    try { managementEvWatcher.Stop(); }
                    catch (ManagementException) { }
                    catch (COMException) { }
                    managementEvWatcher.EventArrived -= PowerEventArrive;
                    managementEvWatcher.Dispose();
                    managementEvWatcher = null;
                }
            }
        }

        internal void PrepareForApplicationShutdown()
        {
            DisposePowerLifecycle();
        }

        private void ChangeHotkeysStatus(bool state)
        {
            lock (hotkeysTimerLock)
            {
                hotkeysTimerEnabled = state;
                if (state)
                {
                    if (!hotkeysTimerSubscribed)
                    {
                        hotkeysTimer.Elapsed += HotkeysTimer_Elapsed;
                        hotkeysTimerSubscribed = true;
                    }

                    hotkeysTimer.Start();
                }
                else
                {
                    hotkeysTimer.Stop();
                    if (hotkeysTimerSubscribed)
                    {
                        hotkeysTimer.Elapsed -= HotkeysTimer_Elapsed;
                        hotkeysTimerSubscribed = false;
                    }
                }
            }
        }

        private void HotkeysTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            lock (hotkeysTimerLock)
            {
                if (!hotkeysTimerEnabled)
                {
                    return;
                }

                hotkeysTimer.Stop();
            }

            try
            {
                // ControllerCol and SelectedIndex are UI-owned. Processing
                // them on the timer thread could race controller hotplug or a
                // profile refresh, throw once, and leave this one-shot timer
                // permanently stopped. The swipe direction itself is already
                // atomically latched, so it is safe to consume on the UI
                // dispatcher.
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        ProcessProfileSwipeHotkeys();
                    }
                    catch (Exception exception)
                    {
                        AppLogger.LogToGui(
                            $"Swipe profile switch recovered after an error: {exception.Message}",
                            true);
                    }
                    finally
                    {
                        lock (hotkeysTimerLock)
                        {
                            if (hotkeysTimerEnabled)
                            {
                                hotkeysTimer.Start();
                            }
                        }
                    }
                }));
            }
            catch
            {
                lock (hotkeysTimerLock)
                {
                    if (hotkeysTimerEnabled)
                    {
                        hotkeysTimer.Start();
                    }
                }
            }
        }

        private void ProcessProfileSwipeHotkeys()
        {
            if (!Global.SwipeProfiles)
            {
                return;
            }

            foreach (CompositeDeviceModel item in
                conLvViewModel.ControllerCol)
            {
                string slide = App.rootHub.TouchpadSlide(item.DevIndex);
                if (Global.useTempProfile[item.DevIndex])
                {
                    // Auto Profiles own the runtime profile while active.
                    // Consume rather than defer a gesture so it cannot switch
                    // unexpectedly when the temporary profile is released.
                    continue;
                }

                if (slide == "left")
                {
                    item.SelectedIndex = ComputeSwipeProfileIndex(item,
                        forward: false);
                }
                else if (slide == "right")
                {
                    item.SelectedIndex = ComputeSwipeProfileIndex(item,
                        forward: true);
                }
            }
        }

        /// <summary>
        /// Determines which profile index a two-finger touchpad swipe should move to,
        /// honouring the user-configured swipe profile allow-list.
        /// When the allow-list is empty (or none of its entries currently exist), the
        /// swipe cycles through every available profile, preserving legacy behaviour.
        /// </summary>
        /// <param name="item">The controller whose profile is being switched.</param>
        /// <param name="forward">True for a right swipe (next), false for a left swipe (previous).</param>
        /// <returns>The index within <see cref="CompositeDeviceModel.ProfileListCol"/> to select.</returns>
        private int ComputeSwipeProfileIndex(CompositeDeviceModel item, bool forward)
        {
            var profiles = item.ProfileListCol;
            int count = profiles.Count;
            if (count == 0)
            {
                return item.SelectedIndex;
            }

            List<string> allowList = Global.SwipeProfileList;
            List<int> allowedIndices = new List<int>();
            if (allowList != null && allowList.Count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (allowList.Contains(profiles[i].Name))
                    {
                        allowedIndices.Add(i);
                    }
                }
            }

            // No usable allow-list configured -> fall back to cycling through everything.
            if (allowedIndices.Count == 0)
            {
                for (int i = 0; i < count; i++)
                {
                    allowedIndices.Add(i);
                }
            }

            int current = item.SelectedIndex;
            int pos = allowedIndices.IndexOf(current);
            if (pos < 0)
            {
                // Currently selected profile is not part of the allow-list. Move to
                // the nearest allowed profile in the swipe direction, wrapping around
                // if there is none further along. allowedIndices is in ascending order.
                if (forward)
                {
                    foreach (int idx in allowedIndices)
                    {
                        if (idx > current)
                        {
                            return idx;
                        }
                    }
                    return allowedIndices[0];
                }
                else
                {
                    for (int k = allowedIndices.Count - 1; k >= 0; k--)
                    {
                        if (allowedIndices[k] < current)
                        {
                            return allowedIndices[k];
                        }
                    }
                    return allowedIndices[allowedIndices.Count - 1];
                }
            }

            if (forward)
            {
                pos = (pos + 1) % allowedIndices.Count;
            }
            else
            {
                pos = (pos - 1 + allowedIndices.Count) % allowedIndices.Count;
            }

            return allowedIndices[pos];
        }

        private void ConfigSwipeProfilesBtn_Click(object sender, RoutedEventArgs e)
        {
            SwipeProfilesEditor dialog = new SwipeProfilesEditor(profileListHolder, Global.SwipeProfileList)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                Global.SwipeProfileList = dialog.SelectedProfiles;
                Global.Save();
            }
        }

        private void ShowHotkeyNotification(string message)
        {
            if (!IsActive && (Global.Notifications == 2))
            {
                notifyIcon.ShowNotification(TrayIconViewModel.ballonTitle,
                    message, H.NotifyIcon.Core.NotificationIcon.Info);
            }
        }

        private void PrepareForServiceStop(object sender, EventArgs e)
        {
            CancelBoundedHotplugRecovery();

            Dispatcher.BeginInvoke((Action)(() =>
            {
                trayIconVM.ClearContextMenu();
            }));

            ChangeHotkeysStatus(false);
        }

        private void TrayIconVM_RequestOpen(object sender, EventArgs e)
        {
            if (!showAppInTaskbar)
            {
                Show();
            }

            WindowState = WindowState.Normal;
        }

        private void TrayIconVM_RequestShutdown(object sender, EventArgs e)
        {
            RequestApplicationShutdown();
        }

        private async void EnsureTrayIconCreated()
        {
            int[] retryDelaysMs = { 0, 250, 1000 };
            Exception lastFailure = null;
            foreach (int retryDelayMs in retryDelaysMs)
            {
                if (retryDelayMs > 0)
                {
                    await Task.Delay(retryDelayMs);
                }

                if (notifyIcon == null || Volatile.Read(ref shutdownRequested) != 0)
                {
                    return;
                }

                try
                {
                    if (!notifyIcon.IsCreated)
                    {
                        notifyIcon.ForceCreate(enablesEfficiencyMode: false);
                    }

                    notifyIcon.ContextMenu = trayIconVM.ContextMenu;
                    return;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                }
            }

            if (lastFailure != null)
            {
                AppLogger.LogToGui($"Tray icon creation failed: {lastFailure.Message}", true);
            }
        }

        private void RequestApplicationShutdown()
        {
            if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
            {
                return;
            }

            contextclose = true;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                editor?.Close();
                Close();
            }), DispatcherPriority.Send);
        }

        private void UpdateLastStatusMessage(object sender, DS4Windows.DebugEventArgs e)
        {
            lastLogMsg.Message = e.Data;
            lastLogMsg.Warning = e.Warning;
        }

        private void ChangeControllerPanel()
        {
            if (conLvViewModel.ControllerCol.Count == 0)
            {
                controllerLV.Visibility = Visibility.Hidden;
                noContLb.Visibility = Visibility.Visible;
            }
            else
            {
                controllerLV.Visibility = Visibility.Visible;
                noContLb.Visibility = Visibility.Hidden;
            }
        }

        private void ChangeAutoProfilesStatus(bool state)
        {
            lock (autoProfilesTimerLock)
            {
                autoprofileChecker.Running = state;
                if (state)
                {
                    if (!autoProfilesTimerSubscribed)
                    {
                        autoProfilesTimer.Elapsed += AutoProfilesTimer_Elapsed;
                        autoProfilesTimerSubscribed = true;
                    }
                    autoProfilesTimer.Start();
                }
                else
                {
                    autoProfilesTimer.Stop();
                    if (autoProfilesTimerSubscribed)
                    {
                        autoProfilesTimer.Elapsed -= AutoProfilesTimer_Elapsed;
                        autoProfilesTimerSubscribed = false;
                    }
                }
            }
        }

        private void CheckAutoProfileStatus()
        {
            int pathCount = autoProfileHolder.AutoProfileColl.Count;
            bool timerEnabled = autoprofileChecker.Running;
            if (pathCount > 0 && !timerEnabled)
            {
                ChangeAutoProfilesStatus(true);
            }
            else if (pathCount == 0 && timerEnabled)
            {
                ChangeAutoProfilesStatus(false);
            }
        }

        private void AutoProfilesTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (Interlocked.Exchange(ref autoProfilesTimerTickActive, 1) != 0)
            {
                return;
            }

            try
            {
                if (autoprofileChecker.Running)
                {
                    autoprofileChecker.Process();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"Auto-Profile watcher recovered from an error: {ex.Message}",
                    false, true);
            }
            finally
            {
                Interlocked.Exchange(ref autoProfilesTimerTickActive, 0);
                lock (autoProfilesTimerLock)
                {
                    if (autoprofileChecker.Running &&
                        autoProfilesTimerSubscribed)
                    {
                        autoProfilesTimer.Start();
                    }
                }
            }
        }

        private void ControllerCol_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null && e.NewItems.Count > 0)
            {
                CancelBoundedHotplugRecovery();
            }
            else if (App.rootHub.running && !HasManagedInputController())
            {
                StartBoundedHotplugRecovery();
            }

            Dispatcher.BeginInvoke((Action)(() =>
            {
                ChangeControllerPanel();
                if (mainWinVM.SelectedController == null ||
                    !conLvViewModel.ControllerCol.Contains(mainWinVM.SelectedController))
                {
                    mainWinVM.SelectedController = conLvViewModel.ControllerCol.FirstOrDefault();
                }

                System.Collections.IList newitems = e.NewItems;
                if (newitems != null)
                {
                    foreach (CompositeDeviceModel item in newitems)
                    {
                        PrepareControllerItem(item);
                        //item.LightContext.Items.Add(new MenuItem() { Header = "Use Profile Color", IsChecked = !item.UseCustomColor });
                        //item.LightContext.Items.Add(new MenuItem() { Header = "Use Custom Color", IsChecked = item.UseCustomColor });
                    }
                }

                if (App.rootHub.running)
                    trayIconVM.PopulateContextMenu();
            }));
        }

        private void PrepareControllerItem(CompositeDeviceModel item)
        {
            item.LightContext = new ContextMenu();
            item.AddLightContextItems();
            item.Device.SyncChange += DS4Device_SyncChange;
            item.RequestColorPicker += Item_RequestColorPicker;
        }

        private void Item_RequestColorPicker(CompositeDeviceModel sender)
        {
            ColorPickerWindow dialog = new ColorPickerWindow();
            dialog.Owner = this;
            dialog.colorPicker.SelectedColor = sender.CustomLightColor;
            dialog.ColorChanged += (sender2, color) =>
            {
                sender.UpdateCustomLightColor(color);
            };
            dialog.ShowDialog();
        }

        private void DS4Device_SyncChange(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                mainWinVM.RefreshRuntimeState(App.rootHub);
                trayIconVM.PopulateContextMenu();
            }));
        }

        private void OutputSlot_RuntimeChanged(OutputSlotManager sender,
            int slotNum, OutSlotDevice outSlotDevice)
        {
            if (!Dispatcher.HasShutdownStarted &&
                !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                    new Action(() =>
                        mainWinVM.RefreshRuntimeState(App.rootHub)));
            }
        }

        private void ControlServiceChanged(object sender, EventArgs e)
        {
            //Tester service = sender as Tester;
            ControlService service = sender as ControlService;
            if (!service.running)
            {
                CancelBoundedHotplugRecovery();
            }

            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (service.running)
                {
                    StartStopBtn.Content = Translations.Strings.StopText;
                }
                else
                {
                    StartStopBtn.Content = Translations.Strings.StartText;
                }

                StartStopBtn.IsEnabled = true;
                slotManControl.IsEnabled = service.running;
            }));
        }

        private void AboutBtn_Click(object sender, RoutedEventArgs e)
        {
            About aboutWin = new About();
            aboutWin.Owner = this;
            aboutWin.ShowDialog();
        }

        private void StartStopBtn_Click(object sender, RoutedEventArgs e)
        {
            ChangeService();
        }

        public async void ChangeService()
        {
            StartStopBtn.IsEnabled = false;
            App root = Application.Current as App;
            //Tester service = root.rootHubtest;
            ControlService service = App.rootHub;
            Task serviceTask = Task.Run(() =>
            {
                if (service.running)
                    service.Stop(immediateUnplug: true);
                else
                    service.Start();
            });

            // Log exceptions that might occur
            Util.LogAssistBackgroundTask(serviceTask);
            await serviceTask;
        }

        private void LogListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            int idx = logListView.SelectedIndex;
            if (idx > -1)
            {
                LogItem temp = logvm.LogItems[idx];
                LogMessageDisplay msgBox = new LogMessageDisplay(temp.Message);
                msgBox.Owner = this;
                msgBox.ShowDialog();
                //MessageBox.Show(temp.Message, "Log");
            }
        }

        private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
        {
            logvm.LogItems.Clear();
        }

        private void MainTabCon_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (mainWinVM?.ProfileEditorMode == true &&
                mainTabCon.SelectedItem != profilesTab && mainTabCon.SelectedItem != logTab)
            {
                mainTabCon.SelectedItem = profilesTab;
                return;
            }

            if (mainTabCon.SelectedItem == settingsTab)
            {
                lastMsgLb.Visibility = Visibility.Hidden;
            }
            else
            {
                lastMsgLb.Visibility = Visibility.Visible;
            }
        }

        private void SupportPayPalBtn_Click(object sender, RoutedEventArgs e)
        {
            Util.StartProcessHelper("https://www.paypal.com/paypalme/hbashton");
        }

        private void MainWinVM_QuickProfileSettingChanged(object sender,
            QuickProfileSettingChangedEventArgs e)
        {
            overviewDirtyControllerIndices.Add(e.DeviceIndex);
            if (e.RequiresProfileReload)
            {
                overviewProfileReloadControllerIndices.Add(e.DeviceIndex);
                if (e.RequestedOutputController != OutContType.None)
                {
                    overviewRequestedOutputControllers[e.DeviceIndex] =
                        e.RequestedOutputController.Normalize();
                }
            }
            if (e.RequestedSpeakerOutputEnabled.HasValue)
            {
                overviewRequestedSpeakerOutputStates[e.DeviceIndex] =
                    e.RequestedSpeakerOutputEnabled.Value;
            }
            if (e.RequestedAudioSourceId != null)
            {
                overviewRequestedAudioSourceIds[e.DeviceIndex] =
                    e.RequestedAudioSourceId;
            }
            if (e.ReleaseAudioHapticsSpeakerOverride)
            {
                overviewAudioHapticsOverrideReleaseRequests.Add(
                    e.DeviceIndex);
            }
            overviewProfileSaveTimer.Stop();
            // Save after the current binding pass, before the next status
            // refresh can reconcile controls against the active profile.
            // Slider bindings already debounce their source update by 220 ms.
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() =>
                    FlushOverviewQuickSettings(e.DeviceIndex)));
        }

        private void MainWinVM_SelectedControllerChanged(object sender, EventArgs e)
        {
            // Controller status notifications originate on the HID input
            // thread. Both feature controls update WPF dependency properties,
            // so marshal the complete selection refresh back to this window's
            // dispatcher instead of allowing a connect/status event to tear
            // down the process with VerifyAccess.
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted &&
                    !Dispatcher.HasShutdownFinished)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                        new Action(() =>
                            MainWinVM_SelectedControllerChanged(sender, e)));
                }
                return;
            }

            audioHapticsControl.SetDevice(mainWinVM.SelectedController?.DevIndex ?? -1);
            triggerLabControl.SetDevice(mainWinVM.SelectedController?.DevIndex ?? -1);
        }

        private void ProfileFeatureControl_SettingsChanged(object sender,
            ProfileFeatureSettingsChangedEventArgs e)
        {
            overviewDirtyControllerIndices.Add(e.DeviceIndex);
            mainWinVM.RefreshRuntimeState(App.rootHub);
            overviewProfileSaveTimer.Stop();
            overviewProfileSaveTimer.Start();
        }

        private void OverviewProfileSaveTimer_Tick(object sender, EventArgs e)
        {
            FlushOverviewQuickSettings();
        }

        private void FlushOverviewQuickSettings(int? onlyDeviceIndex = null, bool reloadProfile = true)
        {
            overviewProfileSaveTimer.Stop();
            int[] deviceIndices = onlyDeviceIndex.HasValue
                ? new[] { onlyDeviceIndex.Value }
                : overviewDirtyControllerIndices.ToArray();

            foreach (int deviceIndex in deviceIndices)
            {
                bool outputControllerChanged =
                    overviewProfileReloadControllerIndices.Remove(
                        deviceIndex);
                bool requiresProfileReload = reloadProfile &&
                    outputControllerChanged;
                overviewRequestedOutputControllers.TryGetValue(deviceIndex,
                    out OutContType requestedOutputController);
                bool hasRequestedSpeakerState =
                    overviewRequestedSpeakerOutputStates.TryGetValue(
                        deviceIndex, out bool requestedSpeakerState);
                bool hasRequestedAudioSource =
                    overviewRequestedAudioSourceIds.TryGetValue(deviceIndex,
                        out string requestedAudioSourceId);
                bool releaseAudioHapticsOverride =
                    overviewAudioHapticsOverrideReleaseRequests.Contains(
                        deviceIndex);
                if (!overviewDirtyControllerIndices.Remove(deviceIndex) ||
                    deviceIndex < 0 || deviceIndex >= ControlService.CURRENT_DS4_CONTROLLER_LIMIT)
                {
                    continue;
                }

                string profileName = Global.ProfilePath[deviceIndex];
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    continue;
                }

                Mapping.ExecuteSerializedProfileMutation(deviceIndex, () =>
                {
                    // A profile reload can finish between the ComboBox setter
                    // and this deferred save. Reassert the user's latest
                    // selection while holding the same lock used by profile
                    // parsing so stale XML can never win the race.
                    if (requestedOutputController != OutContType.None)
                    {
                        Global.OutContType[deviceIndex] =
                            requestedOutputController;
                        Global.outDevTypeTemp[deviceIndex] =
                            requestedOutputController;
                    }
                    if (hasRequestedSpeakerState)
                    {
                        Global.DualSenseEnableSpeakerOutput[deviceIndex] =
                            requestedSpeakerState;
                    }
                    if (hasRequestedAudioSource)
                    {
                        Global.DualSenseAudioCaptureEndpointId[deviceIndex] =
                            requestedAudioSourceId ?? string.Empty;
                    }
                    if (releaseAudioHapticsOverride)
                    {
                        AudioHapticsProfileSettings audioHaptics =
                            Global.store.audioHapticsSettings[deviceIndex];
                        if (audioHaptics != null)
                        {
                            audioHaptics.StreamAppAudioToController = false;
                            audioHaptics.StreamAppAudioToHeadsetOnly = false;
                        }
                    }

                    ProfileEntity profile = profileListHolder.ProfileListCol
                        .SingleOrDefault(item => item.Name == profileName);
                    if (profile != null)
                    {
                        profile.SaveProfile(deviceIndex);
                    }
                    else
                    {
                        Global.SaveProfile(deviceIndex, profileName);
                    }
                });

                overviewRequestedOutputControllers.Remove(deviceIndex);
                overviewRequestedSpeakerOutputStates.Remove(deviceIndex);
                overviewRequestedAudioSourceIds.Remove(deviceIndex);
                overviewAudioHapticsOverrideReleaseRequests.Remove(
                    deviceIndex);

                ProfileEntity savedProfile = profileListHolder.ProfileListCol
                    .SingleOrDefault(item => item.Name == profileName);
                if (requiresProfileReload)
                {
                    if (savedProfile != null)
                    {
                        savedProfile.FireSaved();
                    }
                    else
                    {
                        Mapping.RequestRegularProfileReload(deviceIndex, false,
                            App.rootHub);
                    }
                }

                if (reloadProfile && !requiresProfileReload)
                {
                    DS4Device device = App.rootHub.DS4Controllers[
                        deviceIndex];
                    if (device != null)
                    {
                        App.rootHub.CheckProfileOptions(deviceIndex, device);
                    }
                }
            }

            // The source dropdown's ItemsSource is stable while its explicit
            // selection is being saved. Replacing the collection here made
            // WPF synthesize a selection of the first item and erase the app
            // endpoint that had just been committed.
            mainWinVM.RefreshSelectedControllerProperties(
                refreshControllerAudioChoices: false);
            if (overviewDirtyControllerIndices.Count > 0)
            {
                overviewProfileSaveTimer.Start();
            }
        }

        private void ControllerOverview_EditProfileRequested(object sender, EventArgs e)
        {
            CompositeDeviceModel controller = mainWinVM.SelectedController;
            if (controller == null) return;

            FlushOverviewQuickSettings(controller.DevIndex);
            ProfileEntity profile = profileListHolder.ProfileListCol
                .SingleOrDefault(item => item.Name == controller.SelectedProfile);
            if (profile != null)
            {
                ShowProfileEditor(controller.DevIndex, profile);
            }
        }

        private void ControllerOverview_ControllerDetailsRequested(object sender, EventArgs e)
        {
            mainTabCon.SelectedItem = controllersTab;
            controllerLV.SelectedItem = mainWinVM.SelectedController;
            controllerLV.ScrollIntoView(mainWinVM.SelectedController);
        }

        private void ControllerOverview_LightbarRequested(object sender, EventArgs e)
        {
            CompositeDeviceModel controller = mainWinVM.SelectedController;
            if (controller?.LightContext == null)
            {
                return;
            }

            controller.LightContext.PlacementTarget = controllerOverviewControl;
            controller.LightContext.Placement = PlacementMode.MousePoint;
            controller.LightContext.IsOpen = true;
        }

        private void ControllerOverview_DisconnectRequested(object sender, EventArgs e)
        {
            mainWinVM.SelectedController?.RequestDisconnect();
        }

        private void MainWinVM_ProfileEditorNavigationIndexChanged(object sender, EventArgs e)
        {
            if (profileEditorNavigationChanging || !mainWinVM.ProfileEditorMode || editor == null)
            {
                return;
            }

            NavigateProfileEditor(mainWinVM.ProfileEditorNavigationIndex);
        }

        private void NavigateProfileEditor(int navigationIndex)
        {
            if (editor == null)
            {
                return;
            }

            if (navigationIndex == 0)
            {
                editor.CancelEdit();
                return;
            }

            string title;
            string description;
            switch (navigationIndex)
            {
                case 1:
                    title = "Button Mapping";
                    description = "Assign controller buttons, sticks, touch gestures, and shortcuts.";
                    break;
                case 2:
                    title = "Special Actions";
                    description = "Create macros, profile shifts, program launches, and multi-action shortcuts.";
                    break;
                case 3:
                    title = "Controller Readings";
                    description = "Inspect live sticks, triggers, motion sensors, and input calibration.";
                    break;
                case 4:
                    title = "Axis Config";
                    description = "Tune sticks, triggers, dead zones, curves, and motion axes.";
                    break;
                case 5:
                    title = "Lightbar";
                    description = "Set profile colors, battery feedback, flashing, and charging behavior.";
                    break;
                case 6:
                    title = "Touchpad";
                    description = "Configure mouse control, gestures, passthrough, and absolute positioning.";
                    break;
                case 7:
                    title = "Gyro";
                    description = "Configure motion aiming, steering, mouse control, and directional swipes.";
                    break;
                case 8:
                    title = "Audio Haptics";
                    description = "Turn system audio or one app session into advanced haptic feedback for this profile.";
                    break;
                case 9:
                    title = "Trigger Lab";
                    description = "Build persistent L2 and R2 adaptive-trigger effects saved directly in this profile.";
                    break;
                case 10:
                    title = "Advanced";
                    description = "Manage output devices, rumble, audio, latency, compatibility, and custom hooks.";
                    break;
                case 11:
                    title = "Log";
                    description = "View live service events without leaving the profile editing workspace.";
                    break;
                default:
                    return;
            }

            mainWinVM.ProfileEditorSectionTitle = title;
            mainWinVM.ProfileEditorSectionDescription = description;

            if (navigationIndex == 11)
            {
                editor.DeactivateLiveReadings();
                mainTabCon.SelectedItem = logTab;
            }
            else
            {
                mainTabCon.SelectedItem = profilesTab;
                editor.SelectWorkspaceSection(navigationIndex - 1);
            }
        }

        private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = profilesListBox.SelectedIndex >= 0;
            newProfListBtn.IsEnabled = true;
            editProfBtn.IsEnabled = hasSelection;
            deleteProfBtn.IsEnabled = hasSelection;
            renameProfBtn.IsEnabled = hasSelection;
            dupProfBtn.IsEnabled = hasSelection;
            importProfBtn.IsEnabled = true;
            exportProfBtn.IsEnabled = hasSelection;
        }

        private void RunAtStartCk_Click(object sender, RoutedEventArgs e)
        {
            settingsWrapVM.ShowRunStartPanel = runAtStartCk.IsChecked == true ? Visibility.Visible :
                Visibility.Collapsed;
        }

        private void ContStatusImg_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            Image img = sender as Image;
            int tag = Convert.ToInt32(img.Tag);
            conLvViewModel.CurrentIndex = tag;
            CompositeDeviceModel item = conLvViewModel.CurrentItem;
            //CompositeDeviceModel item = conLvViewModel.ControllerDict[tag];
            if (item != null)
            {
                item.RequestDisconnect();
            }
        }

        private void ExportLogBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.AddExtension = true;
            dialog.DefaultExt = ".txt";
            dialog.Filter = "Text Documents (*.txt)|*.txt";
            dialog.Title = "Select Export File";
            // TODO: Expose config dir
            dialog.InitialDirectory = Global.appdatapath;
            if (dialog.ShowDialog() == true)
            {
                LogWriter logWriter = new LogWriter(dialog.FileName, logvm.LogItems.ToList());
                logWriter.Process();
            }
        }

        private void IdColumnTxtB_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            TextBlock statusBk = sender as TextBlock;
            int idx = Convert.ToInt32(statusBk.Tag);
            if (idx >= 0)
            {
                CompositeDeviceModel item = conLvViewModel.ControllerDict[idx];
                item.RequestUpdatedTooltipID();
            }
        }

        /// <summary>
        /// Clear and re-populate tray context menu
        /// </summary>
        private void NotifyIcon_TrayRightMouseUp(object sender, RoutedEventArgs e)
        {
            notifyIcon.ContextMenu = trayIconVM.ContextMenu;
        }

        /// <summary>
        /// Change profile based on selection
        /// </summary>
        private void SelectProfCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox box = sender as ComboBox;
            int idx = Convert.ToInt32(box.Tag);
            if (idx > -1 && conLvViewModel.ControllerDict.ContainsKey(idx))
            {
                FlushOverviewQuickSettings(idx, false);
                CompositeDeviceModel item = conLvViewModel.ControllerDict[idx];
                if (!item.IsSynchronizingRuntimeProfile &&
                    item.SelectedIndex > -1)
                {
                    item.ChangeSelectedProfile();
                    mainWinVM.RefreshRuntimeState(App.rootHub);
                    trayIconVM.PopulateContextMenu();
                }
            }
        }

        private void CustomColorPick_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<Color?> e)
        {

        }

        private void LightColorBtn_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            int idx = Convert.ToInt32(button.Tag);
            CompositeDeviceModel item = conLvViewModel.ControllerDict[idx];
            //(button.ContextMenu.Items[0] as MenuItem).IsChecked = conLvViewModel.ControllerCol[idx].UseCustomColor;
            //(button.ContextMenu.Items[1] as MenuItem).IsChecked = !conLvViewModel.ControllerCol[idx].UseCustomColor;
            button.ContextMenu = item.LightContext;
            button.ContextMenu.IsOpen = true;
        }

        private void MainDS4Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            FlushOverviewQuickSettings(reloadProfile: false);

            if (editor != null)
            {
                editor.Close();
                if (!contextclose)
                {
                    e.Cancel = true;
                    return;
                }
            }

            if (contextclose)
            {
                return;
            }

            if (Global.CloseMini)
            {
                WindowState = WindowState.Minimized;
                e.Cancel = true;
                return;
            }

            // If this method was called directly without sender object then skip the confirmation dialogbox
            if (sender != null && conLvViewModel.ControllerCol.Count > 0)
            {
                MessageBoxResult result = MessageBox.Show(Properties.Resources.CloseConfirm, Properties.Resources.Confirm,
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        private void MainDS4Window_Closed(object sender, EventArgs e)
        {
            DisposePowerLifecycle();
            CancelBoundedHotplugRecovery();
            overviewProfileSaveTimer.Stop();
            overviewStatusRefreshTimer.Stop();
            hotkeysTimer.Stop();
            autoProfilesTimer.Stop();
            //autoProfileHolder.Save();
            Util.UnregisterNotify(regHandle);

            // Attempt to dispose of notify icon early
            if (notifyIcon != null)
            {
                trayIconVM?.Dispose();
                notifyIcon.Dispose();
                notifyIcon = null;
            }

            Application.Current.Shutdown();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            if (!Global.firstRun)
            {
                WindowPlacementHelper.ApplyPlacement(this, startMinimized);
            }

            HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
            HookWindowMessages(source);
            source.AddHook(WndProc);
        }

        private bool inHotPlug = false;
        private int hotplugCounter = 0;
        private readonly object hotplugCounterLock = new object();
        private readonly object hotplugRecoveryLock = new object();
        private CancellationTokenSource hotplugRecoveryCancellation;
        public const int WM_COPYDATA = 0x004A;
        private const int HOTPLUG_CHECK_DELAY = 2000;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam,
            IntPtr lParam, ref bool handled)
        {
            // Handle messages...
            switch (msg)
            {
                case Util.WM_DEVICECHANGE:
                {
                    if (Global.runHotPlug)
                    {
                        Int32 Type = wParam.ToInt32();
                        bool hasManagedController = HasManagedInputController();
                        if (HotplugRecoveryPolicy.ShouldQueueForDeviceChange(Type,
                            hasManagedController))
                        {
                            QueueHotplugScan(queueBehindActiveScan: true);
                            if (!hasManagedController)
                            {
                                StartBoundedHotplugRecovery();
                            }
                        }
                    }
                    break;
                }
                case WM_COPYDATA:
                {
                    // Received InterProcessCommunication (IPC) message. DS4Win command is embedded as a string value in lpData buffer
                    try
                    {
                        App.COPYDATASTRUCT cds = (App.COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(App.COPYDATASTRUCT));
                        if (cds.cbData >= 4 && cds.cbData <= 256)
                        {
                            int tdevice = -1;

                            byte[] buffer = new byte[cds.cbData];
                            Marshal.Copy(cds.lpData, buffer, 0, cds.cbData);
                            string[] strData = Encoding.ASCII.GetString(buffer).Split('.');

                            if (strData.Length >= 1)
                            {
                                strData[0] = strData[0].ToLower();

                                if (strData[0] == "start")
                                { 
                                    if(!Program.rootHub.running) 
                                        ChangeService();
                                }
                                else if (strData[0] == "stop")
                                {    
                                    if (Program.rootHub.running)
                                        ChangeService();
                                }
                                else if (strData[0] == "cycle")
                                {
                                    ChangeService();
                                }
                                else if (strData[0] == "shutdown")
                                {
                                    RequestApplicationShutdown();
                                }
                                else if (strData[0] == "disconnect")
                                {
                                    // Command syntax: Disconnect[.device#] (fex Disconnect.1)
                                    // Disconnect all wireless controllers. ex. (Disconnect)
                                    if (strData.Length == 1)
                                    {
                                        // Attempt to disconnect all wireless controllers
                                        // Opt to make copy of Dictionary before iterating over contents
                                        var dictCopy = new Dictionary<int, CompositeDeviceModel>(conLvViewModel.ControllerDict);
                                        foreach(KeyValuePair<int, CompositeDeviceModel> pair in dictCopy)
                                        {
                                            pair.Value.RequestDisconnect();
                                        }
                                    }
                                    else
                                    {
                                        // Attempt to disconnect one wireless controller
                                        if (int.TryParse(strData[1], out tdevice)) tdevice--;

                                        if (conLvViewModel.ControllerDict.TryGetValue(tdevice, out CompositeDeviceModel model))
                                        {
                                            model.RequestDisconnect();
                                        }
                                    }
                                }
                                else if ((strData[0] == "changeledcolor") && strData.Length >= 5)
                                {
                                        // Command syntax: changeledcolor.device#.red.gree.blue (ex changeledcolor.1.255.0.0)
                                   if (int.TryParse(strData[1], out tdevice))
                                        tdevice--;
                                    if (tdevice >= 0 && tdevice < ControlService.MAX_DS4_CONTROLLER_COUNT)
                                    {
                                        byte.TryParse(strData[2], out byte red);
                                        byte.TryParse(strData[3], out byte green);
                                        byte.TryParse(strData[4], out byte blue);

                                        conLvViewModel.ControllerCol[tdevice].UpdateCustomLightColor(Color.FromRgb(red, green, blue));
                                    }

                                }
                                else if ((strData[0] == "loadprofile" || strData[0] == "loadtempprofile") && strData.Length >= 3)
                                {
                                    // Command syntax: LoadProfile.device#.profileName (fex LoadProfile.1.GameSnake or LoadTempProfile.1.WebBrowserSet)
                                    if (int.TryParse(strData[1], out tdevice)) tdevice--;

                                    if (tdevice >= 0 && tdevice < ControlService.MAX_DS4_CONTROLLER_COUNT &&
                                            File.Exists(Global.appdatapath + "\\Profiles\\" + strData[2] + ".xml"))
                                    {
                                        if (strData[0] == "loadprofile")
                                        {
                                            int idx = profileListHolder.ProfileListCol.Select((item, index) => new { item, index }).
                                                    Where(x => x.item.Name == strData[2]).Select(x => x.index).DefaultIfEmpty(-1).First();

                                            if (idx >= 0 && tdevice < conLvViewModel.ControllerCol.Count)
                                            {
                                                conLvViewModel.ControllerCol[tdevice].ChangeSelectedProfile(strData[2]);
                                            }
                                            else
                                            {
                                                // Preset profile name for later loading
                                                Global.ProfilePath[tdevice] = strData[2];
                                                //Global.LoadProfile(tdevice, true, Program.rootHub);
                                            }
                                        }
                                        else
                                        {
                                            Mapping.RequestTemporaryProfileLoad(
                                                tdevice, strData[2], true,
                                                Program.rootHub);
                                        }

                                        DS4Device device = conLvViewModel.ControllerCol[tdevice].Device;
                                        if (device != null)
                                        {
                                            string prolog = string.Format(Properties.Resources.UsingProfile, (tdevice + 1).ToString(), strData[2], $"{device.Battery}");
                                            Program.rootHub.LogDebug(prolog);
                                        }
                                    }
                                }
                                else if (strData[0] == "outputslot" && strData.Length >= 3)
                                {
                                    // Command syntax: 
                                    //    OutputSlot.slot#.Unplug
                                    //    OutputSlot.slot#.PlugDS4
                                    //    OutputSlot.slot#.PlugX360
                                    if (int.TryParse(strData[1], out tdevice))
                                        tdevice--;

                                    if (tdevice >= 0 && tdevice < ControlService.MAX_DS4_CONTROLLER_COUNT)
                                    {
                                        strData[2] = strData[2].ToLower();
                                        DS4Control.OutSlotDevice slotDevice = Program.rootHub.OutputslotMan.OutputSlots[tdevice];
                                        if (strData[2] == "unplug")
                                            Program.rootHub.DetachUnboundOutDev(slotDevice);
                                        else if (strData[2] == "plugds4")
                                            Program.rootHub.AttachUnboundOutDev(slotDevice, OutContType.ViiperDS4);
                                        else if (strData[2] == "plugx360")
                                            Program.rootHub.AttachUnboundOutDev(slotDevice, OutContType.ViiperX360);
                                        else if (strData[2] == "plugviiperx360")
                                            Program.rootHub.AttachUnboundOutDev(slotDevice, OutContType.ViiperX360);
                                        else if (strData[2] == "plugviiperds4")
                                            Program.rootHub.AttachUnboundOutDev(slotDevice, OutContType.ViiperDS4);
                                        else if (strData[2] == "plugviiperdualsense")
                                            Program.rootHub.AttachUnboundOutDev(slotDevice, OutContType.ViiperDualSense);
                                        else if (strData[2] == "plugviiperdualsenseedge")
                                            Program.rootHub.AttachUnboundOutDev(slotDevice, OutContType.ViiperDualSenseEdge);
                                        else if (strData[2] == "plugviiperswitch2pro")
                                            Program.rootHub.AttachUnboundOutDev(slotDevice, OutContType.ViiperSwitch2Pro);
                                    }
                                }
                                else if (strData[0] == "query" && strData.Length >= 3)
                                {
                                    string propName;
                                    string propValue = String.Empty;

                                    // Command syntax: QueryProfile.device#.Name (fex "Query.1.ProfileName" would print out the name of the active profile in controller 1)
                                    if (int.TryParse(strData[1], out tdevice))
                                        tdevice--;

                                    if (tdevice >= 0 && tdevice < ControlService.MAX_DS4_CONTROLLER_COUNT)
                                    {
                                        // Name of the property to query from a profile or DS4Windows app engine
                                        propName = strData[2].ToLower();

                                            if (propName == "profilename")
                                            {
                                                if (Global.useTempProfile[tdevice])
                                                    propValue = Global.tempprofilename[tdevice];
                                                else
                                                    propValue = Global.ProfilePath[tdevice];
                                            }
                                            else if (propName == "outconttype")
                                                propValue = Global.OutContType[tdevice].ToString();
                                            else if (propName == "activeoutdevtype")
                                                propValue = Global.activeOutDevType[tdevice].ToString();
                                            else if (propName == "usedinputonly")
                                                propValue = Global.useDInputOnly[tdevice].ToString();

                                            else if (propName == "devicevidpid" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = $"VID={App.rootHub.DS4Controllers[tdevice].HidDevice.Attributes.VendorHexId}, PID={App.rootHub.DS4Controllers[tdevice].HidDevice.Attributes.ProductHexId}";
                                            else if (propName == "devicepath" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = App.rootHub.DS4Controllers[tdevice].HidDevice.DevicePath;
                                            else if (propName == "macaddress" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = App.rootHub.DS4Controllers[tdevice].MacAddress;
                                            else if (propName == "displayname" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = App.rootHub.DS4Controllers[tdevice].DisplayName;
                                            else if (propName == "conntype" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = App.rootHub.DS4Controllers[tdevice].ConnectionType.ToString();
                                            else if (propName == "exclusivestatus" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = App.rootHub.DS4Controllers[tdevice].CurrentExclusiveStatus.ToString();
                                            else if (propName == "battery" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = App.rootHub.DS4Controllers[tdevice].Battery.ToString();
                                            else if (propName == "charging" && App.rootHub.DS4Controllers[tdevice] != null)
                                                propValue = App.rootHub.DS4Controllers[tdevice].Charging.ToString();
                                            else if (propName == "outputslottype")
                                                propValue = App.rootHub.OutputslotMan.OutputSlots[tdevice].CurrentType.ToString();
                                            else if (propName == "outputslotpermanenttype")
                                                propValue = App.rootHub.OutputslotMan.OutputSlots[tdevice].PermanentType.ToString();
                                            else if (propName == "outputslotattachedstatus")
                                                propValue = App.rootHub.OutputslotMan.OutputSlots[tdevice].CurrentAttachedStatus.ToString();
                                            else if (propName == "outputslotinputbound")
                                                propValue = App.rootHub.OutputslotMan.OutputSlots[tdevice].CurrentInputBound.ToString();

                                            else if (propName == "apprunning")
                                                propValue = App.rootHub.running.ToString(); // Controller idx value is ignored, but it still needs to be in 1..4 range in a cmdline call
                                    }

                                    // Write out the property value to MMF result data file and notify a client process that the data is available
                                    ((Application.Current) as App).WriteIPCResultDataMMF(propValue);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Eat all exceptions in WM_COPYDATA because exceptions here are not fatal for DS4Windows background app
                    }
                    break;
                }
                default: break;
            }

            return IntPtr.Zero;
        }

        private void InnerHotplug2()
        {
            bool finishedNormally = false;
            try
            {
                Program.rootHub.UpdateHidHiddenAttributes();
                while (true)
                {
                    lock (hotplugCounterLock)
                    {
                        if (hotplugCounter == 0)
                        {
                            inHotPlug = false;
                            finishedNormally = true;
                            return;
                        }

                        // Multiple Windows notifications collapse into one scan.
                        hotplugCounter = 0;
                    }

                    Thread.Sleep(HOTPLUG_CHECK_DELAY);
                    if (!Global.runHotPlug || !Program.rootHub.running)
                    {
                        lock (hotplugCounterLock)
                        {
                            hotplugCounter = 0;
                            inHotPlug = false;
                        }

                        finishedNormally = true;
                        return;
                    }

                    Program.rootHub.HotPlug();
                }
            }
            finally
            {
                // Never strand future device notifications if enumeration throws.
                if (!finishedNormally)
                {
                    lock (hotplugCounterLock)
                    {
                        hotplugCounter = 0;
                        inHotPlug = false;
                    }
                }
            }
        }

        private bool QueueHotplugScan(bool queueBehindActiveScan)
        {
            bool startWorker = false;
            lock (hotplugCounterLock)
            {
                if (!Global.runHotPlug)
                {
                    return false;
                }

                if (!queueBehindActiveScan && (inHotPlug || hotplugCounter > 0))
                {
                    return false;
                }

                hotplugCounter++;
                if (!inHotPlug)
                {
                    inHotPlug = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                Task hotplugTask = Task.Run(InnerHotplug2);
                // Log exceptions that might occur.
                Util.LogAssistBackgroundTask(hotplugTask);
            }

            return true;
        }

        private static bool HasManagedInputController()
        {
            ControlService service = Program.rootHub;
            if (service?.DS4Controllers == null)
            {
                return false;
            }

            DS4Device[] controllers = service.DS4Controllers;
            for (int i = 0; i < controllers.Length; i++)
            {
                DS4Device controller = Volatile.Read(ref controllers[i]);
                if (controller != null && !controller.IsRemoving)
                {
                    return true;
                }
            }

            return false;
        }

        private void StartBoundedHotplugRecovery()
        {
            if (!Program.rootHub.running || !Global.runHotPlug ||
                HasManagedInputController())
            {
                return;
            }

            CancellationTokenSource recoveryCancellation;
            lock (hotplugRecoveryLock)
            {
                // Repeated Windows notifications must not extend the bounded window.
                if (hotplugRecoveryCancellation != null)
                {
                    return;
                }

                recoveryCancellation = new CancellationTokenSource();
                hotplugRecoveryCancellation = recoveryCancellation;
            }

            Task recoveryTask = RunBoundedHotplugRecovery(recoveryCancellation);
            Util.LogAssistBackgroundTask(recoveryTask);
        }

        private async Task RunBoundedHotplugRecovery(
            CancellationTokenSource recoveryCancellation)
        {
            CancellationToken cancellationToken = recoveryCancellation.Token;
            try
            {
                for (int completedAttempts = 0;
                    HotplugRecoveryPolicy.ShouldContinueRecovery(
                        Program.rootHub.running && Global.runHotPlug,
                        HasManagedInputController(), completedAttempts);
                    completedAttempts++)
                {
                    await Task.Delay(
                        HotplugRecoveryPolicy.RecoveryIntervalMilliseconds,
                        cancellationToken).ConfigureAwait(false);

                    if (!HotplugRecoveryPolicy.ShouldContinueRecovery(
                        Program.rootHub.running && Global.runHotPlug,
                        HasManagedInputController(), completedAttempts))
                    {
                        break;
                    }

                    // A fallback tick never queues work behind an active native scan.
                    QueueHotplugScan(queueBehindActiveScan: false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                lock (hotplugRecoveryLock)
                {
                    if (ReferenceEquals(hotplugRecoveryCancellation,
                        recoveryCancellation))
                    {
                        hotplugRecoveryCancellation = null;
                    }
                }

                recoveryCancellation.Dispose();
            }
        }

        private void CancelBoundedHotplugRecovery()
        {
            CancellationTokenSource recoveryCancellation;
            lock (hotplugRecoveryLock)
            {
                recoveryCancellation = hotplugRecoveryCancellation;
                hotplugRecoveryCancellation = null;
            }

            try
            {
                recoveryCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The bounded task completed between the field swap and cancellation.
            }
        }

        private void HookWindowMessages(HwndSource source)
        {
            Guid hidGuid = new Guid();
            NativeMethods.HidD_GetHidGuid(ref hidGuid);
            bool result = Util.RegisterNotify(source.Handle, hidGuid, ref regHandle);
            if (!result)
            {
                App.Current.Shutdown();
            }
        }

        private void ProfEditSBtn_Click(object sender, RoutedEventArgs e)
        {
            Control temp = sender as Control;
            int idx = Convert.ToInt32(temp.Tag);
            controllerLV.SelectedIndex = idx;
            CompositeDeviceModel item = conLvViewModel.CurrentItem;

            if (item != null && item.SelectedIndex != -1)
            {
                ProfileEntity entity = profileListHolder.ProfileListCol[item.SelectedIndex];
                ShowProfileEditor(idx, entity);
            }
        }

        private void NewProfBtn_Click(object sender, RoutedEventArgs e)
        {
            Control temp = sender as Control;
            int idx = Convert.ToInt32(temp.Tag);
            controllerLV.SelectedIndex = idx;
            ShowProfileEditor(idx, null);
            //controllerLV.Focus();
        }

        // Ex Mode Re-Enable
        private async void HideDS4ContCk_Click(object sender, RoutedEventArgs e)
        {
            StartStopBtn.IsEnabled = false;
            //bool checkStatus = hideDS4ContCk.IsChecked == true;
            hideDS4ContCk.IsEnabled = false;
            Task serviceTask = Task.Run(() =>
            {
                App.rootHub.Stop();
                App.rootHub.Start();
            });

            // Log exceptions that might occur
            Util.LogAssistBackgroundTask(serviceTask);
            await serviceTask;

            hideDS4ContCk.IsEnabled = true;
            StartStopBtn.IsEnabled = true;
        }

        private void ReclaimSteamInputCk_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = reclaimSteamInputCk.IsChecked == true;
            Global.ReclaimSteamInput = enabled;
            if (enabled && hideDS4ContCk.IsChecked != true)
            {
                hideDS4ContCk.IsChecked = true;
                Global.UseExclusiveMode = true;
            }

            if (enabled)
            {
                HideDS4ContCk_Click(hideDS4ContCk, e);
            }
        }

        private void UseOscServerCk_Click(object sender, RoutedEventArgs e)
        {
            bool status = useOscServerCk.IsChecked == true;
            App.rootHub.ChangeOSCListenerStatus(status);
        }

        private void UseOscSenderCk_Click(object sender, RoutedEventArgs e)
        {
            bool status = useOscSenderCk.IsChecked == true;
            App.rootHub.ChangeOSCSenderStatus(status);
        }

        private async void UseUdpServerCk_Click(object sender, RoutedEventArgs e)
        {
            bool status = useUdpServerCk.IsChecked == true;
            if (!status)
            {
                App.rootHub.ChangeMotionEventStatus(status);
                await Task.Delay(200).ContinueWith((t) =>
                {
                    App.rootHub.ChangeUDPStatus(status);
                });
            }
            else
            {
                Program.rootHub.ChangeUDPStatus(status);
                await Task.Delay(200).ContinueWith((t) =>
                {
                    App.rootHub.ChangeMotionEventStatus(status);
                });
            }
        }

        private void ProfFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(Global.appdatapath + "\\Profiles");
            startInfo.UseShellExecute = true;
            try
            {
                using (Process temp = Process.Start(startInfo))
                {
                }
            }
            catch { }
        }

        private void ControlPanelBtn_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("control", "joy.cpl");
        }

        private async void DriverSetupBtn_Click(object sender, RoutedEventArgs e)
        {
            StartStopBtn.IsEnabled = false;
            await Task.Run(() =>
            {
                if (App.rootHub.running)
                    App.rootHub.Stop();
            });

            StartStopBtn.IsEnabled = true;
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Global.exelocation;
            startInfo.Arguments = "-driverinstall";
            startInfo.Verb = "runas";
            startInfo.UseShellExecute = true;
            try
            {
                using (Process temp = Process.Start(startInfo))
                {
                    temp.WaitForExit();
                    Global.RefreshHidHideInfo();
                    Global.RefreshFakerInputInfo();

                    settingsWrapVM.DriverCheckRefresh();
                }
            }
            catch { }
        }

        private void ViiperSetupBtn_Click(object sender, RoutedEventArgs e)
        {
            ViiperSetupManager.EnsureReadyWithPrompt(this,
                forcePrompt: true);
        }

        private void ViiperRefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshViiperStatusText();
        }

        private void RefreshViiperStatusText()
        {
            if (viiperStatusText == null)
            {
                return;
            }

            ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: false);
            viiperStatusText.Text = $"{status.DisplayText}. " +
                $"VIIPER helper: {(status.ViiperInstalled ? "installed" : "missing")}; " +
                $"usbip-win2: {(status.UsbipInstalled ? "installed" : "missing")}; " +
                $"server: {(status.ServerRunning ? "running" : "not running")}.";
        }

        private void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    if (Changelog.CheckNewerReleaseExists(out string releaseTag, false))
                        DisplayUpdaterWindow(releaseTag);
                    else
                        Dispatcher.Invoke(() => MessageBox.Show(Properties.Resources.UpToDate, "DS4Windows Updater"));
                }
                catch
                {
                    Dispatcher.Invoke(() => MessageBox.Show(Strings.FailedToRetrieveLatestVersion, "DS4Windows Updater"));
                    // bubble the exception up to allow to see what's wrong in the log
                    throw;
                }
            });
        }

        private void ImportProfBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.AddExtension = true;
            dialog.DefaultExt = ".xml";
            dialog.Filter = "DS4Windows Profile (*.xml)|*.xml";
            dialog.Title = "Select Profile to Import File";
            if (Global.appdatapath != Global.exedirpath)
                dialog.InitialDirectory = Path.Combine(Global.appDataPpath, "Profiles");
            else
                dialog.InitialDirectory = Global.exedirpath + @"\Profiles\";

            if (dialog.ShowDialog() == true)
            {
                string[] files = dialog.FileNames;
                for (int i = 0, arlen = files.Length; i < arlen; i++)
                {
                    string profilename = System.IO.Path.GetFileName(files[i]);
                    string basename = System.IO.Path.GetFileNameWithoutExtension(files[i]);
                    File.Copy(dialog.FileNames[i], Global.appdatapath + "\\Profiles\\" + profilename, true);
                    profileListHolder.AddProfileSort(basename);
                }
            }
        }

        private void ExportProfBtn_Click(object sender, RoutedEventArgs e)
        {
            if (profilesListBox.SelectedItem is ProfileEntity entity)
            {
                SaveFileDialog dialog = new SaveFileDialog();
                dialog.AddExtension = true;
                dialog.DefaultExt = ".xml";
                dialog.Filter = "DS4Windows Profile (*.xml)|*.xml";
                dialog.Title = "Select Profile to Export File";
                Stream stream;
                Stream profile = new StreamReader(Global.appdatapath + "\\Profiles\\" + entity.Name + ".xml").BaseStream;
                if (dialog.ShowDialog() == true)
                {
                    if ((stream = dialog.OpenFile()) != null)
                    {
                        profile.CopyTo(stream);
                        profile.Close();
                        stream.Close();
                    }
                }
            }
        }

        private void DupProfBtn_Click(object sender, RoutedEventArgs e)
        {
            string filename = "";
            if (profilesListBox.SelectedItem is ProfileEntity entity)
            {
                filename = entity.Name;
                dupBox.OldFilename = filename;
                dupBoxBar.Visibility = Visibility.Visible;
                dupBox.Save -= DupBox_Save;
                dupBox.Cancel -= DupBox_Cancel;
                dupBox.Save += DupBox_Save;
                dupBox.Cancel += DupBox_Cancel;
            }
        }

        private void DupBox_Cancel(object sender, EventArgs e)
        {
            dupBoxBar.Visibility = Visibility.Collapsed;
        }

        private void DupBox_Save(DupBox sender, string profilename)
        {
            profileListHolder.AddProfileSort(profilename);
            dupBoxBar.Visibility = Visibility.Collapsed;
        }

        private void DeleteProfBtn_Click(object sender, RoutedEventArgs e)
        {
            if (profilesListBox.SelectedItem is ProfileEntity entity)
            {
                string filename = entity.Name;
                if (MessageBox.Show(Properties.Resources.ProfileCannotRestore.Replace("*Profile name*", "\"" + filename + "\""),
                    Properties.Resources.DeleteProfile,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    entity.DeleteFile();
                    profileListHolder.ProfileListCol.Remove(entity);
                }
            }
        }

        private void SelectProfCombo_KeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
        }

        private void MainDS4Window_StateChanged(object _sender, EventArgs _e)
        {
            CheckMinStatus();
        }

        public void CheckMinStatus()
        {
            bool minToTask = Global.MinToTaskbar;
            if (WindowState == WindowState.Minimized && !minToTask)
            {
                Hide();
                showAppInTaskbar = false;
            }
            else if (WindowState == WindowState.Normal && !minToTask)
            {
                Show();
                showAppInTaskbar = true;
            }
        }

        private void MainDS4Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WindowState != WindowState.Minimized && preserveSize && !IsInitialShow)
            {
                var result = WindowPlacementHelper.GetPlacement(this);
                Global.FormWidth = result.Right - result.Left;
                Global.FormHeight = result.Bottom - result.Top;
            }
        }

        private void MainDS4Window_LocationChanged(object sender, EventArgs e)
        {
            var result = WindowPlacementHelper.GetPlacement(this);
            Global.FormLocationX = result.Left;
            Global.FormLocationY = result.Top;
        }

        private void NotifyIcon_TrayMiddleMouseDown(object sender, RoutedEventArgs e)
        {
            RequestApplicationShutdown();
        }

        private void SwipeTouchCk_Click(object sender, RoutedEventArgs e)
        {
            bool status = swipeTouchCk.IsChecked == true;
            ChangeHotkeysStatus(status);
        }

        private void EditProfBtn_Click(object sender, RoutedEventArgs e)
        {
            if (profilesListBox.SelectedItem is ProfileEntity entity)
            {
                ShowProfileEditor(Global.TEST_PROFILE_INDEX, entity);
            }
        }

        private void ProfileEditor_Closed(object sender, EventArgs e)
        {
            ProfileEditor closingEditor = sender as ProfileEditor ?? editor;
            profDockPanel.Children.Remove(closingEditor);
            profilesBrowserPanel.Visibility = Visibility.Visible;
            profOptsToolbar.Visibility = Visibility.Visible;
            profilesListBox.Visibility = Visibility.Visible;
            preserveSize = true;
            if (closingEditor != null && !closingEditor.Keepsize)
            {
                this.Width = oldSize.Width;
                this.Height = oldSize.Height;
            }
            else
            {
                oldSize = new Size(Width, Height);
            }

            editor = null;
            mainWinVM.ProfileEditorMode = false;
            mainWinVM.EditingProfileName = "Profile";
            mainWinVM.SetEditingControllerContext(null);
            mainWinVM.FullTabsEnabled = true;
            mainTabCon.SelectedIndex = profileEditorReturnTabIndex;
            //Task.Run(() => GC.Collect(0, GCCollectionMode.Forced, false));
        }

        private void NewProfListBtn_Click(object sender, RoutedEventArgs e)
        {
            ShowProfileEditor(Global.TEST_PROFILE_INDEX, null);
        }

        private async void ShowProfileEditor(int device, ProfileEntity entity = null)
        {
            if (editor != null || profileEditorLoading)
            {
                return;
            }

            Stopwatch openTimer = Stopwatch.StartNew();
            long previousPhaseMs = 0;
            void LogOpenPhase(string phase)
            {
                long totalMs = openTimer.ElapsedMilliseconds;
                AppLogger.LogToGui($"[ProfileEditorTiming] {phase}: " +
                    $"phase={totalMs - previousPhaseMs}ms total={totalMs}ms", false);
                previousPhaseMs = totalMs;
            }

            int controllerContextDevice =
                device >= 0 && device < ControlService.CURRENT_DS4_CONTROLLER_LIMIT
                    ? device
                    : mainWinVM.SelectedController?.DevIndex ?? -1;
            CompositeDeviceModel editingController = controllerContextDevice >= 0
                ? conLvViewModel.ControllerCol.FirstOrDefault(controller =>
                    controller.DevIndex == controllerContextDevice)
                : null;

            profileEditorLoading = true;
            profileEditorReturnTabIndex = mainTabCon.SelectedIndex;
            profilesBrowserPanel.Visibility = Visibility.Collapsed;
            profOptsToolbar.Visibility = Visibility.Collapsed;
            profilesListBox.Visibility = Visibility.Collapsed;
            profileEditorLoadingPanel.Visibility = Visibility.Visible;
            mainWinVM.FullTabsEnabled = false;

            preserveSize = false;
            oldSize.Width = Width;
            oldSize.Height = Height;
            if (this.Width < DEFAULT_PROFILE_EDITOR_WIDTH)
            {
                this.Width = DEFAULT_PROFILE_EDITOR_WIDTH;
            }

            if (this.Height < DEFAULT_PROFILE_EDITOR_HEIGHT)
            {
                this.Height = DEFAULT_PROFILE_EDITOR_HEIGHT;
            }

            mainWinVM.EditingProfileName = entity?.Name ?? "New profile";
            mainWinVM.SetEditingControllerContext(editingController);
            mainWinVM.ProfileEditorMode = true;
            mainTabCon.SelectedItem = profilesTab;
            profileEditorNavigationChanging = true;
            mainWinVM.ProfileEditorNavigationIndex = 1;
            profileEditorNavigationChanging = false;
            LogOpenPhase("workspace prepared");

            try
            {
                // Let the editor workspace and loading state render before profile
                // parsing begins. Profile loading is file and XML heavy, and does
                // not need to block WPF's dispatcher.
                await Dispatcher.Yield(DispatcherPriority.Render);
                LogOpenPhase("loading view rendered");

                bool profileAlreadyLoaded = false;
                if (entity != null)
                {
                    if (device == Global.TEST_PROFILE_INDEX)
                    {
                        Global.ProfilePath[Global.TEST_PROFILE_INDEX] = entity.Name;
                    }

                    await Task.Run(() => Global.LoadProfile(device, false, App.rootHub, false));
                    profileAlreadyLoaded = true;
                }
                LogOpenPhase("profile loaded");

                editor = new ProfileEditor(device, controllerContextDevice);
                LogOpenPhase("editor shell constructed");
                editor.CreatedProfile += Editor_CreatedProfile;
                editor.Closed += ProfileEditor_Closed;
                editor.ProfileNameChanged += Editor_ProfileNameChanged;
                editor.Reload(device, entity, profileAlreadyLoaded);
                LogOpenPhase("editor bindings refreshed");

                profileEditorLoadingPanel.Visibility = Visibility.Collapsed;
                profDockPanel.Children.Add(editor);
                mainWinVM.EditingProfileName = editor.ProfileName;
                NavigateProfileEditor(1);
                await Dispatcher.Yield(DispatcherPriority.Render);
                LogOpenPhase("first frame rendered");
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"Failed to open profile editor after " +
                    $"{openTimer.ElapsedMilliseconds}ms: {ex.Message}", true);
                profileEditorLoadingPanel.Visibility = Visibility.Collapsed;
                profilesBrowserPanel.Visibility = Visibility.Visible;
                profOptsToolbar.Visibility = Visibility.Visible;
                profilesListBox.Visibility = Visibility.Visible;
                mainWinVM.ProfileEditorMode = false;
                mainWinVM.EditingProfileName = "Profile";
                mainWinVM.SetEditingControllerContext(null);
                mainWinVM.FullTabsEnabled = true;
                mainTabCon.SelectedIndex = profileEditorReturnTabIndex;

                if (!preserveSize)
                {
                    Width = oldSize.Width;
                    Height = oldSize.Height;
                }

                editor = null;
            }
            finally
            {
                profileEditorLoading = false;
            }
        }

        private void Editor_ProfileNameChanged(object sender, EventArgs e)
        {
            if (sender is ProfileEditor activeEditor && mainWinVM.ProfileEditorMode)
            {
                mainWinVM.EditingProfileName = activeEditor.ProfileName;
            }
        }

        private void Editor_CreatedProfile(ProfileEditor sender, string profile)
        {
            profileListHolder.AddProfileSort(profile);
            int devnum = sender.DeviceNum;
            if (devnum >= 0 && devnum+1 <= conLvViewModel.ControllerCol.Count)
            {
                conLvViewModel.ControllerCol[devnum].ChangeSelectedProfile(profile);
            }
        }

        private void NotifyIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            if (!showAppInTaskbar)
            {
                Show();
            }

            WindowState = WindowState.Normal;
        }

        private void ProfilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (profilesListBox.SelectedItem is ProfileEntity entity)
            {
                ShowProfileEditor(Global.TEST_PROFILE_INDEX, entity);
            }
        }

        private void Html5GameBtn_Click(object sender, RoutedEventArgs e)
        {
            Util.StartProcessHelper("https://gamepad-tester.com/");
        }

        private void HidHideBtn_Click(object sender, RoutedEventArgs e)
        {
            string path = Util.GetHidHideClientPath();
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo(path);
                    startInfo.UseShellExecute = true;
                    using (Process proc = Process.Start(startInfo)) { }
                }
                catch { }
            }
        }

        private void FakeExeNameExplainBtn_Click(object sender, RoutedEventArgs e)
        {
            string message = Translations.Strings.CustomExeNameInfo;
            MessageBox.Show(message, "Custom Exe Name Info", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void XinputCheckerBtn_Click(object sender, RoutedEventArgs e)
        {
            string path = System.IO.Path.Combine(Global.exedirpath, "Tools",
                "XInputChecker", "XInputChecker.exe");

            if (File.Exists(path))
            {
                try
                {
                    using (Process proc = Process.Start(path)) { }
                }
                catch { }
            }
        }

        private void ChecklogViewBtn_Click(object sender, RoutedEventArgs e)
        {
            ChangelogWindow changelogWin = new ChangelogWindow();
            changelogWin.ShowDialog();
        }

        private void DeviceOptionSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            ControllerRegisterOptionsWindow optsWindow =
                new ControllerRegisterOptionsWindow(Program.rootHub.DeviceOptions, Program.rootHub);

            optsWindow.Owner = this;
            optsWindow.Show();
        }

        private void RenameProfBtn_Click(object sender, RoutedEventArgs e)
        {
            if (profilesListBox.SelectedItem is ProfileEntity entity)
            {
                string filename = Path.Combine(Global.appdatapath,
                    "Profiles", $"{entity.Name}.xml");

                // Disallow renaming Default profile
                if (entity.Name != "Default" &&
                    File.Exists(filename))
                {
                    RenameProfileWindow renameWin = new RenameProfileWindow();
                    renameWin.ChangeProfileName(entity.Name);
                    bool? result = renameWin.ShowDialog();
                    if (result.HasValue && result.Value)
                    {
                        entity.RenameProfile(renameWin.RenameProfileVM.ProfileName);
                        profilesCollectionView?.Refresh();
                        trayIconVM.PopulateContextMenu();
                    }
                }
            }
        }

        private bool ProfileMatchesSearch(object value)
        {
            if (value is not ProfileEntity profile)
            {
                return false;
            }

            string query = profilesSearchTextBox?.Text?.Trim();
            return string.IsNullOrEmpty(query) ||
                profile.Name?.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void ProfilesSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool hasQuery = !string.IsNullOrWhiteSpace(profilesSearchTextBox.Text);
            clearProfilesSearchBtn.Visibility = hasQuery
                ? Visibility.Visible
                : Visibility.Collapsed;
            profilesEmptyTitle.Text = hasQuery ? "No matching profiles" : "No profiles yet";
            profilesEmptyDescription.Text = hasQuery
                ? "Try a different profile name or clear the search."
                : "Create or import a profile to get started.";
            profilesCollectionView?.Refresh();
        }

        private void ClearProfilesSearchBtn_Click(object sender, RoutedEventArgs e)
        {
            profilesSearchTextBox.Clear();
            profilesSearchTextBox.Focus();
        }

        private void ProcessPriorityComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var s = (ComboBox)sender;
            var selectedPriority = (ProcessPriorityClass)s.SelectedItem;
            if (!Global.IsAdministrator() && selectedPriority == ProcessPriorityClass.RealTime)
            {
                MessageBox.Show(Strings.RealTimeNoAdmin);
                selectedPriority = ProcessPriorityClass.High;
                settingsWrapVM.ProcessPriorityIndex = ProcessPriorityClasses.IndexOf(ProcessPriorityClass.High);
            }
            ManagedAudioLatencyLease.ApplyRequestedProcessPriority(
                selectedPriority);
        }
    }

    public class ImageLocationPaths
    {
        public string NewProfile { get => $"{Global.RESOURCES_PREFIX}/{App.Current.FindResource("NewProfileImg")}"; }
        public event EventHandler NewProfileChanged;

        public string EditProfile { get => $"{Global.RESOURCES_PREFIX}/{App.Current.FindResource("EditImg")}"; }
        public event EventHandler EditProfileChanged;

        public string DeleteProfile { get => $"{Global.RESOURCES_PREFIX}/{App.Current.FindResource("DeleteImg")}"; }
        public event EventHandler DeleteProfileChanged;

        public string DuplicateProfile { get => $"{Global.RESOURCES_PREFIX}/{App.Current.FindResource("CopyImg")}"; }
        public event EventHandler DuplicateProfileChanged;

        public string ExportProfile { get => $"{Global.RESOURCES_PREFIX}/{App.Current.FindResource("ExportImg")}"; }
        public event EventHandler ExportProfileChanged;

        public string ImportProfile { get => $"{Global.RESOURCES_PREFIX}/{App.Current.FindResource("ImportImg")}"; }
        public event EventHandler ImportProfileChanged;

        public ImageLocationPaths()
        {
            App current = App.Current as App;
            if (current != null)
            {
                current.ThemeChanged += Current_ThemeChanged;
            }
        }

        private void Current_ThemeChanged(object sender, EventArgs e)
        {
            NewProfileChanged?.Invoke(this, EventArgs.Empty);
            EditProfileChanged?.Invoke(this, EventArgs.Empty);
            DeleteProfileChanged?.Invoke(this, EventArgs.Empty);
            DuplicateProfileChanged?.Invoke(this, EventArgs.Empty);
            ExportProfileChanged?.Invoke(this, EventArgs.Empty);
            ImportProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
