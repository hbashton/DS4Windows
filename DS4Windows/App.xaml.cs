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

using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WPFLocalizeExtension.Engine;

namespace DS4WinWPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    [System.Security.SuppressUnmanagedCodeSecurity]
    public partial class App : Application
    {
        static App()
        {
            EventManager.RegisterClassHandler(typeof(ComboBox),
                UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(ComboBox_PreviewMouseWheel),
                handledEventsToo: true);
        }

        private static void ComboBox_PreviewMouseWheel(object sender,
            MouseWheelEventArgs eventArgs)
        {
            if (sender is not ComboBox comboBox || comboBox.IsDropDownOpen)
            {
                return;
            }

            // A closed dropdown is a setting, not a scroll target. Preserve
            // normal page scrolling without changing its selected value.
            eventArgs.Handled = true;
            if (comboBox.Parent is UIElement parent)
            {
                parent.RaiseEvent(new MouseWheelEventArgs(
                    eventArgs.MouseDevice, eventArgs.Timestamp,
                    eventArgs.Delta)
                {
                    RoutedEvent = Mouse.MouseWheelEvent,
                    Source = comboBox,
                });
            }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "FindWindow")]
        private static extern IntPtr FindWindow(string sClass, string sWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        private Thread controlThread;
        public static DS4Windows.ControlService rootHub;
        public static HttpClient requestClient;
        private bool skipSave;
        private bool runShutdown;
        private int shutdownStarted;
        private int startupFailureShown;
        private bool exitApp;
        private Thread testThread;
        private bool exitComThread = false;
        private const string SingleAppComEventName = "{a52b5b20-d9ee-4f32-8518-307fa14aa0c6}";
        private EventWaitHandle threadComEvent = null;
        private static LoggerHolder logHolder;

        private MemoryMappedFile ipcClassNameMMF = null; // MemoryMappedFile for inter-process communication used to hold className of DS4Form window
        private MemoryMappedFile ipcResultDataMMF = null; // MemoryMappedFile for inter-process communication used to exchange string result data between cmdline client process and the background running DS4Windows app

        private static Dictionary<DS4Windows.AppThemeChoice, string> themeLocs = new
            Dictionary<DS4Windows.AppThemeChoice, string>()
        {
            [DS4Windows.AppThemeChoice.Default] = "DS4Forms/Themes/DefaultTheme.xaml",
            [DS4Windows.AppThemeChoice.Light] = "DS4Forms/Themes/DefaultTheme.xaml",
            [DS4Windows.AppThemeChoice.Dark] = "DS4Forms/Themes/DarkTheme.xaml",
        };

        public event EventHandler ThemeChanged;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Install the safety net before configuration discovery, service
            // construction, or logger setup. Failures in those phases used to
            // terminate the WinExe without a console, dialog, or diagnostic file.
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException +=
                CurrentDomain_UnhandledException;

            try
            {
                ApplicationStartupCore(sender, e);
            }
            catch (Exception ex)
            {
                ReportStartupFailure(ex, "application startup");
                Current.Shutdown(1);
            }
        }

        private void ApplicationStartupCore(object sender, StartupEventArgs e)
        {
            runShutdown = true;
            skipSave = true;

            if (StartupMethods.TryRunTaskRefreshHelper(e.Args,
                    out int startupTaskExitCode))
            {
                runShutdown = false;
                Current.Shutdown(startupTaskExitCode);
                return;
            }

            if (DS4Windows.ViiperSetupManager.
                TryRunStartupTaskRegistrationHelper(e.Args,
                    out int viiperTaskExitCode))
            {
                runShutdown = false;
                Current.Shutdown(viiperTaskExitCode);
                return;
            }

            if (DS4Windows.ViiperSetupManager.
                TryRunElevatedInstallerHost(e.Args,
                    out int viiperInstallerExitCode))
            {
                runShutdown = false;
                Current.Shutdown(viiperInstallerExitCode);
                return;
            }

            if (DS4Windows.ViiperSetupManager.
                TryRunForeignViiperTerminationHelper(e.Args,
                    out int viiperHelperExitCode))
            {
                runShutdown = false;
                Current.Shutdown(viiperHelperExitCode);
                return;
            }

            if (DS4Windows.InputDevices.DualSenseBluetoothAudioPacer.
                TryRunHelper(e.Args))
            {
                runShutdown = false;
                Current.Shutdown();
                return;
            }

            if (DS4Windows.GameBarIntegration.TryRunProbeCommand(e.Args))
            {
                runShutdown = false;
                Current.Shutdown();
                return;
            }

            ArgumentParser parser = new ArgumentParser();
            parser.Parse(e.Args);
            CheckOptions(parser);

            try
            {
                string exeDir = Path.GetDirectoryName(DS4Windows.Global.exelocation);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    Environment.CurrentDirectory = exeDir;
                }
            }
            catch
            {
                // Keep startup going. A bad working directory should not block DS4Windows.
            }

            if (exitApp)
            {
                return;
            }

            // Do this before the single-instance probe. Opening a portable
            // copy while another instance is active must still make the
            // portable executable the user's explicit startup choice.
            StartupMethods.RetargetExistingTaskToCurrentExecutable();

            try
            {
                Process.GetCurrentProcess().PriorityClass =
                    ProcessPriorityClass.High;
            }
            catch { } // Ignore problems raising the priority.

            // Force Normal IO Priority
            IntPtr ioPrio = new IntPtr(2);
            DS4Windows.Util.NtSetInformationProcess(Process.GetCurrentProcess().Handle,
                DS4Windows.Util.PROCESS_INFORMATION_CLASS.ProcessIoPriority, ref ioPrio, 4);

            // Force Normal Page Priority
            IntPtr pagePrio = new IntPtr(5);
            DS4Windows.Util.NtSetInformationProcess(Process.GetCurrentProcess().Handle,
                DS4Windows.Util.PROCESS_INFORMATION_CLASS.ProcessPagePriority, ref pagePrio, 4);

            // another instance is already running if TryOpenExisting returns true.
            try
            {
                if (EventWaitHandleAcl.TryOpenExisting(SingleAppComEventName,
                EventWaitHandleRights.Synchronize |
                EventWaitHandleRights.Modify,
                out EventWaitHandle tempComEvent))
                {
                    tempComEvent.Set();  // signal the other instance.
                    tempComEvent.Close();

                    runShutdown = false;
                    Current.Shutdown();    // Quit temp instance
                    return;
                }
            }
            catch (System.UnauthorizedAccessException)
            {
                // An existing elevated instance can deny this process access if it was
                // started by an older build. Do not continue into a second mapper,
                // but never make the new process appear to do nothing.
                ShowSingleInstanceAccessError();
                runShutdown = false;
                Current.Shutdown();
                return;
            }

            // Allow sleep time durations less than 16 ms
            DS4Windows.Util.timeBeginPeriod(1);

            // Create the Event handle
            try
            {
                threadComEvent = CreateSingleAppComEvent();
            }
            catch (UnauthorizedAccessException)
            {
                // Another elevated instance can win the race with older event security.
                ShowSingleInstanceAccessError();
                runShutdown = false;
                Current.Shutdown();
                return;
            }

            CreateTempWorkerThread();

            CreateControlService(parser);
            // Let WPF use the best renderer available for the current session. The
            // previous SoftwareOnly override made the card-based UI noticeably laggy
            // during resize, scrolling, and page changes. WPF still falls back to
            // software automatically when hardware acceleration is unavailable.
            RenderOptions.ProcessRenderMode = RenderMode.Default;

            DS4Windows.Global.FindConfigLocation();
            bool firstRun = DS4Windows.Global.firstRun;

            // Could not find unique profile location; does not exist or multiple places.
            // Advise user to specify where DS4Windows should save its configuation files
            // and profiles
            if (firstRun)
            {
                DS4Forms.SaveWhere savewh =
                    new DS4Forms.SaveWhere(DS4Windows.Global.multisavespots);
                ShowStartupDialog(savewh);
                if (!savewh.ChoiceMade)
                {
                    runShutdown = false;
                    Current.Shutdown();
                    return;
                }
            }

            // Exit if base configuration could not be generated
            if (firstRun && !CreateConfDirSkeleton())
            {
                MessageBox.Show($"Cannot create config folder structure in {DS4Windows.Global.appdatapath}. Exiting",
                    "DS4Windows", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown(1);
                return;
            }

            logHolder = new LoggerHolder(rootHub);
            Logger logger = logHolder.Logger;
            string version = DS4Windows.Global.exeDisplayVersion;
            logger.Info($"DS4Windows version {version}");
            logger.Info($"DS4Windows exe file: {DS4Windows.Global.exeFileName}");
            logger.Info($"DS4Windows Assembly Architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");
            logger.Info($"OS Version: {Environment.OSVersion}");
            logger.Info($"OS Product Name: {DS4Windows.Util.GetOSProductName()}");
            logger.Info($"OS Release ID: {DS4Windows.Util.GetOSReleaseId()}");
            logger.Info($"System Architecture: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
            logger.Info("Logger created");
            StartupDiag(logger, $"App bootstrap pid={Environment.ProcessId} admin={DS4Windows.Global.IsAdministrator()} cwd=\"{Environment.CurrentDirectory}\" cmd=\"{Environment.CommandLine}\"");
            StartupDiag(logger, $"Exe location=\"{DS4Windows.Global.exelocation}\" configPath=\"{DS4Windows.Global.appdatapath}\" firstRun={firstRun}");

            StartupDiag(logger, "Global.Load begin");
            bool readAppConfig = DS4Windows.Global.Load();
            StartupDiag(logger, $"Global.Load end readAppConfig={readAppConfig}");
            if (!firstRun && !readAppConfig)
            {
                logger.Info($@"Profiles.xml not read at location ${DS4Windows.Global.appdatapath}\Profiles.xml. Using default app settings");
            }

            // Ask user which devices the mapper should attempt to open when detected.
            // Currently only support DS4 by default to avoid extra complications from
            // Steam Input
            if (firstRun)
            {
                DS4Forms.FirstLaunchUtilWindow firstLaunchUtilWin =
                    new DS4Forms.FirstLaunchUtilWindow(DS4Windows.Global.DeviceOptions);
                ShowStartupDialog(firstLaunchUtilWin);
                DS4Windows.Global.Save();
            }

            if (firstRun)
            {
                logger.Info("No config found. Creating default config");
                AttemptSave();

                DS4Windows.Global.SaveAsNewProfile(0, "Default");
                for (int i = 0; i < DS4Windows.ControlService.MAX_DS4_CONTROLLER_COUNT; i++)
                {
                    DS4Windows.Global.ProfilePath[i] = DS4Windows.Global.OlderProfilePath[i] = "Default";
                }

                logger.Info("Default config created");
            }

            // Apply the saved theme before showing any startup UI. This keeps
            // prerequisite prompts consistent with the main application in
            // both explicit and Windows-following theme modes.
            SetUICulture(DS4Windows.Global.UseLang);
            ChangeTheme(DS4Windows.Global.UseCurrentTheme, false);

            // VIIPER is the only virtual-controller backend. Make a missing
            // backend actionable at startup instead of letting profile output
            // fail later with an opaque device error. The installer requests
            // elevation itself, so DS4Windows does not need to stay elevated.
            if (Environment.Is64BitProcess)
            {
                DS4Windows.ViiperSetupManager.
                    RefreshSelectedStartupTaskOnLaunch();
                bool viiperReady = DS4Windows.ViiperSetupManager.
                    EnsureReadyWithPrompt(null);
                if (!viiperReady || Dispatcher.HasShutdownStarted ||
                    Dispatcher.HasShutdownFinished)
                {
                    if (!Dispatcher.HasShutdownStarted &&
                        !Dispatcher.HasShutdownFinished)
                    {
                        Shutdown();
                    }
                    return;
                }
            }
            else
            {
                MessageBox.Show(
                    "This build cannot create VIIPER virtual controllers. Install the x64 DS4Windows build on 64-bit Windows.",
                    "DS4Windows virtual controller setup",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            skipSave = false;

            StartupDiag(logger, "Global.LoadActions begin");
            if (!DS4Windows.Global.LoadActions())
            {
                StartupDiag(logger, "Global.LoadActions failed; CreateStdActions begin");
                DS4Windows.Global.CreateStdActions();
                StartupDiag(logger, "CreateStdActions end");
            }
            else
            {
                StartupDiag(logger, "Global.LoadActions end success");
            }

            StartupDiag(logger, "LoadLinkedProfiles begin");
            DS4Windows.Global.LoadLinkedProfiles();
            StartupDiag(logger, "LoadLinkedProfiles end");
            StartupDiag(logger, "MainWindow ctor begin");
            DS4Forms.MainWindow window = new DS4Forms.MainWindow(parser);
            StartupDiag(logger, "MainWindow ctor end");
            MainWindow = window;
            window.IsInitialShow = true;
            StartupDiag(logger, "MainWindow.Show begin");
            window.Show();
            StartupDiag(logger, "MainWindow.Show end");
            window.IsInitialShow = false;

            // Set up hooks for IPC command calls
            HwndSource source = PresentationSource.FromVisual(window) as HwndSource;
            StartupDiag(logger, "CreateIPCClassNameMMF begin");
            CreateIPCClassNameMMF(source.Handle);
            StartupDiag(logger, "CreateIPCClassNameMMF end");

            window.CheckMinStatus();

            bool runningAsAdmin = DS4Windows.Global.IsAdministrator();
            rootHub.LogDebug($"Running as {(runningAsAdmin ? "Admin" : "User")}");

            if (DS4Windows.Global.hidHideInstalled)
            {
                StartupDiag(logger, "CheckHidHidePresence begin");
                rootHub.CheckHidHidePresence();
                StartupDiag(logger, "CheckHidHidePresence end");
            }

            StartupDiag(logger, "LoadPermanentSlotsConfig begin");
            rootHub.LoadPermanentSlotsConfig();
            StartupDiag(logger, "LoadPermanentSlotsConfig end");
            StartupDiag(logger, "MainWindow.LateChecks begin");
            window.LateChecks(parser);
            StartupDiag(logger, "MainWindow.LateChecks returned");
        }

        private static void ShowStartupDialog(Window dialog)
        {
            dialog.ShowInTaskbar = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ContentRendered += (_, _) =>
            {
                dialog.Topmost = true;
                dialog.Activate();
                dialog.Topmost = false;
                dialog.Focus();
            };
            dialog.ShowDialog();
        }

        private static void ShowSingleInstanceAccessError()
        {
            try
            {
                MessageBox.Show(
                    "Another DS4Windows instance is already running under a different permission level. " +
                    "Open it from the notification area, or close the existing DS4Windows.exe in Task Manager and try again.",
                    "DS4Windows is already running", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch { }
        }

        private static void StartupDiag(Logger logger, string message)
        {
            if (!DS4Windows.Global.VerboseStartupLogging)
            {
                return;
            }

            logger.Info($"[StartupDiag][T{Thread.CurrentThread.ManagedThreadId}] {message}");
            LogManager.Flush(TimeSpan.FromSeconds(1));
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception ??
                new InvalidOperationException(
                    $"Unhandled non-Exception object: {e.ExceptionObject}");
            Logger logger = logHolder?.Logger;
            if (MainWindow == null)
            {
                // Do not marshal synchronously to the dispatcher here. During
                // ControlService construction the UI thread is intentionally
                // waiting for this worker to finish, so Dispatcher.Invoke would
                // deadlock the exact startup failure we are trying to report.
                ReportStartupFailure(exception,
                    "background startup thread");
            }
            else if (logger != null)
            {
                logger.Error(exception,
                    $"Unhandled application exception: {exception.Message}");
                LogManager.Flush(TimeSpan.FromSeconds(1));
            }
            else
            {
                StartupFailureReporter.Write(exception,
                    "background startup thread",
                    DS4Windows.Global.appdatapath);
            }

            if (e.IsTerminating)
            {
                try { rootHub?.PrepareAbort(); }
                catch { }
            }
        }

        private static EventWaitHandle CreateSingleAppComEvent()
        {
            EventWaitHandleSecurity security = new EventWaitHandleSecurity();
            EventWaitHandleRights appRights = EventWaitHandleRights.Synchronize |
                EventWaitHandleRights.Modify |
                EventWaitHandleRights.ReadPermissions;

            SecurityIdentifier authenticatedUsersSid = new SecurityIdentifier(
                WellKnownSidType.AuthenticatedUserSid, null);
            security.AddAccessRule(new EventWaitHandleAccessRule(authenticatedUsersSid,
                appRights, AccessControlType.Allow));

            SecurityIdentifier currentUserSid = WindowsIdentity.GetCurrent().User;
            if (currentUserSid != null)
            {
                security.AddAccessRule(new EventWaitHandleAccessRule(currentUserSid,
                    EventWaitHandleRights.FullControl, AccessControlType.Allow));
            }

            return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset,
                SingleAppComEventName, out _, security);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            ReportStartupFailure(e.Exception, "WPF dispatcher");
            e.Handled = true;
            Current.Shutdown(1);
        }

        private void ReportStartupFailure(Exception exception, string phase)
        {
            try
            {
                logHolder?.Logger?.Fatal(exception,
                    $"DS4Windows failed during {phase}");
                LogManager.Flush(TimeSpan.FromSeconds(1));
            }
            catch { }

            string logPath = StartupFailureReporter.Write(exception, phase,
                DS4Windows.Global.appdatapath);
            if (Interlocked.Exchange(ref startupFailureShown, 1) != 0)
            {
                return;
            }

            try
            {
                MessageBox.Show(
                    StartupFailureReporter.BuildUserMessage(logPath),
                    "DS4Windows startup failed", MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // The synchronous fallback log remains available even if WPF
                // is too damaged to display a final error window.
            }
        }

        private bool CreateConfDirSkeleton()
        {
            bool result = true;
            try
            {
                Directory.CreateDirectory(DS4Windows.Global.appdatapath);
                Directory.CreateDirectory(DS4Windows.Global.appdatapath + @"\Profiles\");
                Directory.CreateDirectory(DS4Windows.Global.appdatapath + @"\Logs\");
                //Directory.CreateDirectory(DS4Windows.Global.appdatapath + @"\Macros\");
            }
            catch (UnauthorizedAccessException)
            {
                result = false;
            }


            return result;
        }

        private void AttemptSave()
        {
            if (!DS4Windows.Global.Save()) //if can't write to file
            {
                if (MessageBox.Show("Cannot write at current location\nCopy Settings to appdata?", "DS4Windows",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try
                    {
                        Directory.CreateDirectory(DS4Windows.Global.appDataPpath);
                        File.Copy(DS4Windows.Global.exedirpath + "\\Profiles.xml",
                            DS4Windows.Global.appDataPpath + "\\Profiles.xml");
                        File.Copy(DS4Windows.Global.exedirpath + "\\Auto Profiles.xml",
                            DS4Windows.Global.appDataPpath + "\\Auto Profiles.xml");
                        Directory.CreateDirectory(DS4Windows.Global.appDataPpath + "\\Profiles");
                        foreach (string s in Directory.GetFiles(DS4Windows.Global.exedirpath + "\\Profiles"))
                        {
                            File.Copy(s, DS4Windows.Global.appDataPpath + "\\Profiles\\" + Path.GetFileName(s));
                        }
                    }
                    catch { }
                    MessageBox.Show("Copy complete, please relaunch DS4Windows and remove settings from Program Directory",
                        "DS4Windows");
                }
                else
                {
                    MessageBox.Show("DS4Windows cannot edit settings here, This will now close",
                        "DS4Windows");
                }

                DS4Windows.Global.appdatapath = null;
                skipSave = true;
                Current.Shutdown();
                return;
            }
        }

        private void CheckOptions(ArgumentParser parser)
        {
            if (parser.HasErrors)
            {
                runShutdown = false;
                exitApp = true;
                Current.Shutdown(1);
            }
            else if (parser.Driverinstall)
            {
                // Load DS4Windows config if it exists
                DS4Windows.Global.FindConfigLocation();
                bool readAppConfig = DS4Windows.Global.Load();
                if (readAppConfig)
                {
                    // Have app use selected culture
                    SetUICulture(DS4Windows.Global.UseLang);
                    DS4Windows.AppThemeChoice themeChoice = DS4Windows.Global.UseCurrentTheme;
                    ChangeTheme(DS4Windows.Global.UseCurrentTheme, false);
                }

                CreateBaseThread();
                DS4Forms.WelcomeDialog dialog = new DS4Forms.WelcomeDialog(true);
                dialog.ShowDialog();
                runShutdown = false;
                exitApp = true;
                Current.Shutdown();
            }
            else if (parser.ReenableDevice)
            {
                DS4Windows.DS4Devices.reEnableDevice(parser.DeviceInstanceId);
                runShutdown = false;
                exitApp = true;
                Current.Shutdown();
            }
            else if (parser.Runtask)
            {
                StartupMethods.LaunchOldTask();
                runShutdown = false;
                exitApp = true;
                Current.Shutdown();
            }
            else if (parser.Command)
            {
                IntPtr hWndDS4WindowsForm = IntPtr.Zero;
                hWndDS4WindowsForm = FindWindow(ReadIPCClassNameMMF(), "DS4Windows");
                if (hWndDS4WindowsForm != IntPtr.Zero)
                {
                    bool bDoSendMsg = true;
                    bool bWaitResultData = false;
                    bool bOwnsMutex = false;
                    Mutex ipcSingleTaskMutex = null;
                    EventWaitHandle ipcNotifyEvent = null;

                    COPYDATASTRUCT cds;
                    cds.lpData = IntPtr.Zero;

                    try
                    {
                        if (parser.CommandArgs.ToLower().StartsWith("query."))
                        {
                            // Query.device# (1..4) command returns a string result via memory mapped file. The cmd is sent to the background DS4Windows 
                            // process (via WM_COPYDATA wnd msg), then this client process waits for the availability of the result and prints it to console output pipe.
                            // Use mutex obj to make sure that concurrent client calls won't try to write and read the same MMF result file at the same time.
                            ipcSingleTaskMutex = new Mutex(false, "DS4Windows_IPCResultData_SingleTaskMtx");
                            try
                            {
                                bOwnsMutex = ipcSingleTaskMutex.WaitOne(10000);
                            }
                            catch (AbandonedMutexException)
                            {
                                bOwnsMutex = true;
                            }

                            if (bOwnsMutex)
                            {
                                // This process owns the inter-process sync mutex obj. Let's proceed with creating the output MMF file and waiting for a result.
                                bWaitResultData = true;
                                CreateIPCResultDataMMF();
                                ipcNotifyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DS4Windows_IPCResultData_ReadyEvent");
                            }
                            else
                                // If the mtx failed then something must be seriously wrong. Cannot do anything in that case because MMF file may be modified by concurrent processes.
                                bDoSendMsg = false;
                        }

                        if (bDoSendMsg)
                        {
                            cds.dwData = IntPtr.Zero;
                            cds.cbData = parser.CommandArgs.Length;
                            cds.lpData = Marshal.StringToHGlobalAnsi(parser.CommandArgs);
                            SendMessage(hWndDS4WindowsForm, DS4Forms.MainWindow.WM_COPYDATA, IntPtr.Zero, ref cds);

                            if (bWaitResultData)
                                Console.WriteLine(WaitAndReadIPCResultDataMMF(ipcNotifyEvent));
                        }
                    }
                    finally
                    {
                        // Release the result MMF file in the client process before releasing the mtx and letting other client process to proceed with the same MMF file
                        if (ipcResultDataMMF != null) ipcResultDataMMF.Dispose();
                        ipcResultDataMMF = null;

                        // If this was "Query.xxx" cmdline client call then release the inter-process mutex and let other concurrent clients to proceed (if there are anyone waiting for the MMF result file)
                        if (bOwnsMutex && ipcSingleTaskMutex != null)
                            ipcSingleTaskMutex.ReleaseMutex();

                        if (cds.lpData != IntPtr.Zero)
                            Marshal.FreeHGlobal(cds.lpData);
                    }
                }

                runShutdown = false;
                exitApp = true;
                Current.Shutdown();
            }
        }

        private void CreateControlService(ArgumentParser parser)
        {
            controlThread = new Thread(() =>
            {
                rootHub = new DS4Windows.ControlService(parser);

                DS4Windows.Program.rootHub = rootHub;
                requestClient = new HttpClient();
                requestClient.DefaultRequestHeaders.Add("User-Agent", "DS4Windows");
            });
            controlThread.Priority = ThreadPriority.Normal;
            controlThread.IsBackground = true;
            controlThread.Start();
            while (controlThread.IsAlive)
                Thread.SpinWait(500);
        }

        private void CreateBaseThread()
        {
            controlThread = new Thread(() =>
            {
                DS4Windows.Program.rootHub = rootHub;
                requestClient = new HttpClient();
                requestClient.DefaultRequestHeaders.Add("User-Agent", "DS4Windows");
            });
            controlThread.Priority = ThreadPriority.Normal;
            controlThread.IsBackground = true;
            controlThread.Start();
            while (controlThread.IsAlive)
                Thread.SpinWait(500);
        }

        private void CreateTempWorkerThread()
        {
            testThread = new Thread(SingleAppComThread_DoWork);
            testThread.Priority = ThreadPriority.Lowest;
            testThread.IsBackground = true;
            testThread.Start();
        }

        private void SingleAppComThread_DoWork()
        {
            while (!exitComThread)
            {
                EventWaitHandle comEvent = Volatile.Read(ref threadComEvent);
                if (comEvent == null)
                {
                    return;
                }

                // check for a signal.
                try
                {
                    if (!comEvent.WaitOne())
                    {
                        continue;
                    }

                    comEvent.Reset();
                    // The user tried to start another instance. We can't allow that,
                    // so bring the other instance back into view and enable that one.
                    // That form is created in another thread, so we need some thread sync magic.
                    if (!exitComThread)
                    {
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            ActivateBestApplicationWindow();
                        }));
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private void ActivateBestApplicationWindow()
        {
            Window target = MainWindow;
            if (target == null || !target.IsLoaded)
            {
                foreach (Window candidate in Windows)
                {
                    if (candidate != null && candidate.IsVisible)
                    {
                        target = candidate;
                        if (candidate.IsActive)
                        {
                            break;
                        }
                    }
                }
            }

            if (target == null)
            {
                // Startup has the single-instance handle but has not produced
                // any UI yet. Record this rare state instead of throwing from
                // a null MainWindow dereference and killing the hidden process.
                StartupFailureReporter.Write(
                    new InvalidOperationException(
                        "A second launch signaled DS4Windows before any startup window existed."),
                    "single-instance activation",
                    DS4Windows.Global.appdatapath);
                return;
            }

            if (!target.IsVisible)
            {
                target.Show();
            }

            if (target.WindowState == WindowState.Minimized)
            {
                target.WindowState = WindowState.Normal;
            }

            target.Activate();
            target.Topmost = true;
            target.Topmost = false;
            target.Focus();
        }

        public void CreateIPCClassNameMMF(IntPtr hWnd)
        {
            if (ipcClassNameMMF != null) return; // Already holding a handle to MMF file. No need to re-write the data

            try
            {
                StringBuilder wndClassNameStr = new StringBuilder(128);
                if (GetClassName(hWnd, wndClassNameStr, wndClassNameStr.Capacity) != 0 && wndClassNameStr.Length > 0)
                {
                    byte[] buffer = ASCIIEncoding.ASCII.GetBytes(wndClassNameStr.ToString());

                    ipcClassNameMMF = MemoryMappedFile.CreateNew("DS4Windows_IPCClassName.dat", 128);
                    MemoryMappedViewAccessor ipcClassNameMMA_Now = ipcClassNameMMF.CreateViewAccessor(0, buffer.Length);
                    ipcClassNameMMA_Now.WriteArray(0, buffer, 0, buffer.Length);
                    ipcClassNameMMA_Now?.Dispose();
                    // The MMF file is alive as long this process holds the file handle open
                }
            }
            catch (Exception)
            {
                /* Eat all exceptions because errors here are not fatal for DS4Win */
            }
        }

        private string ReadIPCClassNameMMF()
        {
            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor mma = null;

            try
            {
                byte[] buffer = new byte[128];
                mmf = MemoryMappedFile.OpenExisting("DS4Windows_IPCClassName.dat");
                mma = mmf.CreateViewAccessor(0, 128);
                mma.ReadArray(0, buffer, 0, buffer.Length);
                return ASCIIEncoding.ASCII.GetString(buffer);
            }
            catch (Exception)
            {
                // Eat all exceptions
            }
            finally
            {
                if (mma != null) mma.Dispose();
                if (mmf != null) mmf.Dispose();
            }

            return null;
        }

        private void CreateIPCResultDataMMF()
        {
            // Cmdline client process calls this to create the MMF file used in inter-process-communications. The background DS4Windows process 
            // uses WriteIPCResultDataMMF method to write a command result and the client process reads the result from the same MMF file.
            if (ipcResultDataMMF != null) return; // Already holding a handle to MMF file. No need to re-write the data

            try
            {
                ipcResultDataMMF = MemoryMappedFile.CreateNew("DS4Windows_IPCResultData.dat", 256);
                // The MMF file is alive as long this process holds the file handle open
            }
            catch (Exception)
            {
                /* Eat all exceptions because errors here are not fatal for DS4Win */
            }
        }

        private string WaitAndReadIPCResultDataMMF(EventWaitHandle ipcNotifyEvent)
        {
            if (ipcResultDataMMF != null)
            {
                // Wait until the inter-process-communication (IPC) result data is available and read the result
                try
                {
                    // Wait max 10 secs and if the result is still not available then timeout and return "empty" result
                    if (ipcNotifyEvent == null || ipcNotifyEvent.WaitOne(10000))
                    {
                        int strNullCharIdx;
                        byte[] buffer = new byte[256];
                        MemoryMappedViewAccessor ipcResultDataMMA = ipcClassNameMMF.CreateViewAccessor(0, buffer.Length);
                        ipcResultDataMMA.ReadArray(0, buffer, 0, buffer.Length);
                        strNullCharIdx = Array.FindIndex(buffer, byteVal => byteVal == 0);
                        return ASCIIEncoding.ASCII.GetString(buffer, 0, (strNullCharIdx <= 1 ? 1 : strNullCharIdx));
                    }
                }
                catch (Exception)
                {
                    /* Eat all exceptions because errors here are not fatal for DS4Win */
                }
            }

            return String.Empty;
        }

        public void WriteIPCResultDataMMF(string dataStr)
        {
            // The background DS4Windows process calls this method to write out the result of "-command QueryProfile.device#" command.
            // The cmdline client process reads the result from the DS4Windows_IPCResultData.dat MMF file and sends the result to console output pipe.
            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor mma = null;
            EventWaitHandle ipcNotifyEvent = null;

            try
            {
                ipcNotifyEvent = EventWaitHandle.OpenExisting("DS4Windows_IPCResultData_ReadyEvent");

                byte[] buffer = ASCIIEncoding.ASCII.GetBytes(dataStr);
                mmf = MemoryMappedFile.OpenExisting("DS4Windows_IPCResultData.dat");
                mma = mmf.CreateViewAccessor(0, 256);
                mma.WriteArray(0, buffer, 0, (buffer.Length >= 256 ? 256 : buffer.Length));
            }
            catch (Exception)
            {
                // Eat all exceptions
            }
            finally
            {
                if (mma != null) mma.Dispose();
                if (mmf != null) mmf.Dispose();

                if (ipcNotifyEvent != null) ipcNotifyEvent.Set();
            }
        }

        private void SetUICulture(string culture)
        {
            try
            {
                //CultureInfo ci = new CultureInfo("ja");
                CultureInfo ci = CultureInfo.GetCultureInfo(culture);
                LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
                LocalizeDictionary.Instance.Culture = ci;
                // fixes the culture in threads
                CultureInfo.DefaultThreadCurrentCulture = ci;
                CultureInfo.DefaultThreadCurrentUICulture = ci;
                //DS4WinWPF.Properties.Resources.Culture = ci;
                Thread.CurrentThread.CurrentCulture = ci;
                Thread.CurrentThread.CurrentUICulture = ci;
            }
            catch (CultureNotFoundException) { /* Skip setting culture that we cannot set */ }
        }

        public void ChangeTheme(DS4Windows.AppThemeChoice themeChoice,
            bool fireChanged = true)
        {
            if (themeChoice == DS4Windows.AppThemeChoice.Default)
            {
                Application.Current.Resources.MergedDictionaries.Clear();

                // Attempt to switch theme based on currently selected Windows apps theme mode
                DS4Windows.AppThemeChoice implicitTheme = DS4Windows.Util.SystemAppsUsingDarkTheme() ?
                    DS4Windows.AppThemeChoice.Dark : DS4Windows.AppThemeChoice.Light;
                themeLocs.TryGetValue(implicitTheme, out string loc);
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary() { Source = new Uri(loc, uriKind: UriKind.Relative) });
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary()
                {
                    Source = new Uri("DS4Forms/Themes/BridgeShellStyles.xaml", UriKind.Relative)
                });

                if (fireChanged)
                {
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (themeLocs.TryGetValue(themeChoice, out string loc))
            {
                Application.Current.Resources.MergedDictionaries.Clear();
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary() { Source = new Uri(loc, uriKind: UriKind.Relative) });
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary()
                {
                    Source = new Uri("DS4Forms/Themes/BridgeShellStyles.xaml", UriKind.Relative)
                });

                if (fireChanged)
                {
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            if (runShutdown)
            {
                logHolder?.Logger?.Info("Request App Shutdown");
                CleanShutdown();
            }
        }

        private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            // WM_QUERYENDSESSION can arrive while startup is still constructing the
            // logger and controller service (and WPF can subsequently raise Exit as
            // well).  Never dereference partially initialized application state from
            // the window-message callback and never run teardown twice.
            logHolder?.Logger?.Info("User Session Ending");
            try
            {
                CleanShutdown();
            }
            catch (Exception ex)
            {
                // An exception escaping this callback is promoted by WPF to
                // 0xc000041d and terminates the process. Session teardown is
                // necessarily best-effort, but preserve the failure when logging is
                // already available.
                logHolder?.Logger?.Error(ex,
                    "Unexpected failure while ending the Windows session");
            }
        }

        private void CleanShutdown()
        {
            if (!runShutdown ||
                Interlocked.CompareExchange(ref shutdownStarted, 1, 0) != 0)
            {
                return;
            }

            try
            {
                (MainWindow as DS4Forms.MainWindow)?
                    .PrepareForApplicationShutdown();
            }
            catch (Exception ex)
            {
                logHolder?.Logger?.Warn(ex,
                    "Could not stop power notifications during application shutdown");
            }

            bool shutdownTimedOut = false;
            DS4Windows.ControlService shutdownHub = rootHub;
            if (shutdownHub != null)
            {
                Task shutdownTask = Task.Run(() =>
                {
                    try
                    {
                        shutdownHub.StopAndShutDown(immediateUnplug: true);
                    }
                    catch (Exception ex)
                    {
                        logHolder?.Logger?.Error(ex,
                            "Controller service teardown failed during application shutdown");
                        try { shutdownHub.PrepareAbort(); }
                        catch { }
                    }
                });

                if (!shutdownTask.Wait(TimeSpan.FromSeconds(8)))
                {
                    shutdownTimedOut = true;
                    try
                    {
                        logHolder?.Logger?.Warn("Timed out while stopping controller service during shutdown. Forcing process exit to avoid a stale single-instance lock.");
                        shutdownHub.PrepareAbort();
                    }
                    catch { }
                }
            }

            if (!skipSave)
            {
                try
                {
                    DS4Windows.Global.Save();
                }
                catch (Exception ex)
                {
                    logHolder?.Logger?.Error(ex,
                        "Could not save settings during application shutdown");
                }
            }

            // Reset timer
            try
            {
                DS4Windows.Util.timeEndPeriod(1);
            }
            catch { }

            exitComThread = true;
            EventWaitHandle comEvent = Interlocked.Exchange(
                ref threadComEvent, null);
            if (comEvent != null)
            {
                try
                {
                    comEvent.Set();  // signal the other instance.
                    if (testThread != null && !testThread.Join(2000))
                    {
                        shutdownTimedOut = true;
                        logHolder?.Logger?.Warn("Timed out waiting for single-instance worker thread to exit.");
                    }
                }
                catch (ObjectDisposedException) { }
                finally
                {
                    comEvent.Dispose();
                }
            }

            MemoryMappedFile classNameMmf = Interlocked.Exchange(
                ref ipcClassNameMMF, null);
            try { classNameMmf?.Dispose(); }
            catch (ObjectDisposedException) { }

            try
            {
                LogManager.Flush();
                LogManager.Shutdown();
            }
            catch { }

            if (shutdownTimedOut)
            {
                Environment.Exit(0);
            }
        }
    }
}
