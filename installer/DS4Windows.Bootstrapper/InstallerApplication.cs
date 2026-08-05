using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace DS4Windows.Bootstrapper
{
    internal sealed class InstallerApplication : BootstrapperApplication
    {
        private readonly Dictionary<string, PackageState> packageStates = new Dictionary<string, PackageState>(StringComparer.OrdinalIgnoreCase);
        private InstallerWindow window;
        private IBootstrapperCommand command;
        private RegistrationType registrationType;
        private LaunchAction plannedAction = LaunchAction.Install;
        private int result;
        private string lastError;
        private bool infrastructureHealthy;
        private bool infrastructureFailed;
        private bool closingProgrammatically;
        private bool applyCompleted;
        private bool failureShown;
        private Mutex bundleMutex;
        private bool bundleMutexOwned;

        internal IEngine Engine => engine;

        protected override void OnCreate(CreateEventArgs args)
        {
            base.OnCreate(args);
            command = args.Command;
        }

        protected override void Run()
        {
            try
            {
                HookEvents();
                // Burn restores persisted variables when it resumes after a
                // package-requested reboot. Preserve the user who started the
                // transaction instead of replacing that identity with whichever
                // account happens to sign in first after Windows restarts.
                if (command.Resume != ResumeType.Reboot)
                {
                    SetInteractiveUserVariables();
                }
                window = new InstallerWindow(this);
                if (command.Display == Display.Full || command.Display == Display.Passive)
                {
                    window.Show();
                }

                engine.CloseSplashScreen();
                engine.Detect();
                Dispatcher.Run();
            }
            finally
            {
                ReleaseBundleMutex();
            }
            engine.Quit(NormalizeExitCode(result));
        }

        private void SetInteractiveUserVariables()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                engine.SetVariableString("TargetUserSid",
                    identity.User?.Value ?? string.Empty, true);
                engine.SetVariableString("TargetUserName",
                    identity.Name ?? Environment.UserName, true);
            }
            engine.SetVariableString("TargetLocalAppData",
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), true);
            engine.SetVariableString("TargetRoamingAppData",
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData), true);
        }

        internal void Begin(LaunchAction action, bool desktopShortcut, bool hidHide, bool fakerInput)
        {
            if (!EnsureBundleMutex()) return;
            plannedAction = action;
            engine.SetVariableNumeric("CreateDesktopShortcut", desktopShortcut ? 1 : 0);
            engine.SetVariableNumeric("InstallHidHide", hidHide ? 1 : 0);
            engine.SetVariableNumeric("InstallFakerInput", fakerInput ? 1 : 0);
            if (command.Display == Display.Full)
            {
                Ui(() => window.ShowPlanning());
            }
            engine.Plan(action);
        }

        private bool EnsureBundleMutex()
        {
            if (bundleMutexOwned) return true;
            try
            {
                bundleMutex = new Mutex(false,
                    @"Global\DS4Windows-Installer-Transaction");
                try
                {
                    bundleMutexOwned = bundleMutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    bundleMutexOwned = true;
                }
            }
            catch (Exception ex)
            {
                engine.Log(LogLevel.Error,
                    "Could not inspect the installer transaction mutex: " +
                    ex.Message);
                bundleMutexOwned = false;
            }

            if (bundleMutexOwned) return true;
            bundleMutex?.Dispose();
            bundleMutex = null;
            ShowFailure(1618,
                "Another DS4Windows installation or repair is already running. Close it, then choose Retry.");
            return false;
        }

        private void ReleaseBundleMutex()
        {
            if (bundleMutexOwned)
            {
                try { bundleMutex.ReleaseMutex(); } catch { }
            }
            bundleMutexOwned = false;
            bundleMutex?.Dispose();
            bundleMutex = null;
        }

        internal void Retry()
        {
            result = 0;
            lastError = null;
            infrastructureFailed = false;
            applyCompleted = false;
            failureShown = false;
            engine.Detect();
        }

        internal void CloseWithCurrentResult() => Close(result);

        internal void LaunchDs4Windows()
        {
            var task = new ProcessStartInfo("schtasks.exe", "/Run /TN \"RunDS4Windows\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var process = Process.Start(task))
                {
                    if (process != null && process.WaitForExit(10000) &&
                        process.ExitCode == 0)
                    {
                        for (var attempt = 0; attempt < 20; attempt++)
                        {
                            using (var running = FirstDs4WindowsProcess())
                            {
                                if (running != null) return;
                            }
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                }
            }
            catch { }

            // A standard user elevated the installer with alternate admin
            // credentials, or task registration was deliberately deferred.
            // The installed application must still launch successfully.
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "DS4Windows", "DS4Windows.exe");
            if (File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path)
                        { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex)
                {
                    engine.Log(LogLevel.Error,
                        "Could not launch DS4Windows: " + ex.Message);
                }
            }
        }

        private static Process FirstDs4WindowsProcess()
        {
            var processes = Process.GetProcessesByName("DS4Windows");
            if (processes.Length == 0) return null;
            for (var index = 1; index < processes.Length; index++)
            {
                processes[index].Dispose();
            }
            return processes[0];
        }

        internal void OpenLog()
        {
            try
            {
                // setup-actions.log is appended before target-user validation
                // and before the child PowerShell script starts. It therefore
                // cannot be a stale log from a previous child transaction when
                // an early helper failure occurs.
                var path = infrastructureFailed ? SetupActionsLogPath : null;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    path = engine.GetVariableString("WixBundleLog");
                }
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch { }
        }

        internal string Diagnostics()
        {
            string log = null;
            try { log = engine.GetVariableString("WixBundleLog"); } catch { }
            var actionLog = InstallerActionLogPath;
            var helperLog = SetupActionsLogPath;
            return "DS4Windows Setup\r\n" +
                   "Action: " + plannedAction + "\r\n" +
                   "Registered: " + registrationType + "\r\n" +
                   "Infrastructure healthy: " + infrastructureHealthy + "\r\n" +
                   "Error: " + (lastError ?? "none") + "\r\n" +
                   "Bundle log: " + (log ?? "unavailable") + "\r\n" +
                   "Setup helper log: " + helperLog + "\r\n" +
                   "Infrastructure log: " + actionLog + "\r\n\r\n" +
                   "--- Setup helper tail ---\r\n" +
                   ReadLogTail(helperLog, 6000) + "\r\n\r\n" +
                   "--- Infrastructure tail ---\r\n" +
                   ReadLogTail(actionLog, 6000);
        }

        internal void Close(int exitCode = 0)
        {
            if (window != null && !window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(() => Close(exitCode)));
                return;
            }
            result = exitCode;
            closingProgrammatically = true;
            window?.Close();
            Dispatcher.ExitAllFrames();
        }

        internal void OnWindowClosed()
        {
            if (!closingProgrammatically && !applyCompleted && !failureShown)
            {
                result = 1223;
            }
            Dispatcher.ExitAllFrames();
        }

        internal bool RestartWindows()
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                engine.Log(LogLevel.Error,
                    "Could not restart Windows: " + ex.Message);
                return false;
            }
        }

        private void HookEvents()
        {
            DetectBegin += (_, e) =>
            {
                registrationType = e.RegistrationType;
                packageStates.Clear();
            };
            DetectPackageComplete += (_, e) => packageStates[e.PackageId] = e.State;
            DetectComplete += OnDetectComplete;
            PlanPackageBegin += OnPlanPackageBegin;
            PlanComplete += OnPlanComplete;
            ApplyBegin += (_, __) => Ui(() => window.ShowApplying());
            ExecutePackageBegin += (_, e) => Ui(() => window.SetCurrentPackage(e.PackageId));
            ExecutePackageComplete += (_, e) =>
            {
                if (string.Equals(e.PackageId, "ViiperUsbipSetup",
                        StringComparison.OrdinalIgnoreCase) && e.Status < 0)
                {
                    infrastructureFailed = true;
                }
            };
            ExecuteProgress += (_, e) => Ui(() => window.SetProgress(e.OverallPercentage));
            Error += (_, e) =>
            {
                lastError = string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Setup error " + e.ErrorCode : e.ErrorMessage;
                engine.Log(LogLevel.Error, lastError);
            };
            ApplyComplete += OnApplyComplete;
        }

        private void OnDetectComplete(object sender, DetectCompleteEventArgs e)
        {
            if (e.Status < 0)
            {
                ShowFailure(e.Status, "Setup could not inspect the current installation.");
                return;
            }

            infrastructureHealthy = InfrastructureProbe.IsHealthy();
            var mode = registrationType == RegistrationType.Full ? InstallerMode.Repair : InstallerMode.Install;
            if (command.Action == LaunchAction.Uninstall)
            {
                mode = InstallerMode.Uninstall;
            }
            else if (registrationType == RegistrationType.None && packageStates.TryGetValue("DS4WindowsMsi", out var msiState) && msiState == PackageState.Present)
            {
                mode = InstallerMode.Update;
            }

            // Burn persists the chain state and re-launches the cached bundle
            // after a package-requested reboot. Resume that saved action
            // immediately, just as WixStdBA does, instead of presenting a new
            // confirmation page and leaving the chain half-finished.
            if (command.Resume == ResumeType.Reboot)
            {
                if (!EnsureBundleMutex()) return;
                var resumeAction = command.Action == LaunchAction.Unknown
                    ? LaunchAction.Install
                    : command.Action;
                plannedAction = resumeAction;
                Ui(() => window.ShowPlanning());
                engine.Plan(resumeAction);
                return;
            }

            Ui(() => window.ShowConfirmation(mode, packageStates, infrastructureHealthy));

            if (command.Display != Display.Full)
            {
                var action = command.Action == LaunchAction.Unknown ? LaunchAction.Install : command.Action;
                if (action == LaunchAction.Layout)
                {
                    var layoutDirectory = string.IsNullOrWhiteSpace(command.LayoutDirectory)
                        ? Environment.CurrentDirectory
                        : command.LayoutDirectory;
                    engine.SetVariableString("WixBundleLayoutDirectory", layoutDirectory, false);
                }
                Begin(action, true, false, false);
            }
        }

        private void OnPlanPackageBegin(object sender, PlanPackageBeginEventArgs e)
        {
            if (string.Equals(e.PackageId, "ViiperUsbipSetup", StringComparison.OrdinalIgnoreCase) &&
                plannedAction != LaunchAction.Uninstall && plannedAction != LaunchAction.Layout &&
                !infrastructureHealthy)
            {
                e.State = e.CurrentState == PackageState.Present ? RequestState.Repair : RequestState.Present;
            }
        }

        private void OnPlanComplete(object sender, PlanCompleteEventArgs e)
        {
            if (e.Status < 0)
            {
                ShowFailure(e.Status, "Setup could not create a safe installation plan.");
                return;
            }
            Ui(() => engine.Apply(new WindowInteropHelper(window).EnsureHandle()));
        }

        private void OnApplyComplete(object sender, ApplyCompleteEventArgs e)
        {
            result = e.Status;
            applyCompleted = true;
            if (e.Status < 0)
            {
                var detail = infrastructureFailed ?
                    InfrastructureFailureSummary() : null;
                var message = lastError ?? "Setup did not complete.";
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    message += "\r\n\r\n" + detail;
                }
                ShowFailure(e.Status, message);
                return;
            }

            if (command.Display != Display.Full)
            {
                Close(e.Status);
                return;
            }

            Ui(() =>
            {
                if (e.Restart == ApplyRestart.RestartRequired || e.Restart == ApplyRestart.RestartInitiated)
                {
                    result = 3010;
                    window.ShowRestart();
                }
                else
                {
                    window.ShowComplete(plannedAction);
                }
            });
        }

        private void ShowFailure(int status, string message)
        {
            result = status;
            failureShown = true;
            lastError = message + " (0x" + status.ToString("X8") + ")";
            if (command.Display != Display.Full)
            {
                Close(status);
                return;
            }
            Ui(() => window.ShowFailure(lastError));
        }

        private void Ui(Action action)
        {
            if (window == null) return;
            if (window.Dispatcher.CheckAccess()) action();
            else window.Dispatcher.BeginInvoke(action);
        }

        private static int NormalizeExitCode(int exitCode)
        {
            return (exitCode & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? exitCode & 0xFFFF : exitCode;
        }

        private static string InstallerActionLogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DS4Windows", "Installer", "infrastructure-actions.log");

        private static string SetupActionsLogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DS4Windows", "Installer", "setup-actions.log");

        private static string InfrastructureFailureSummary()
        {
            var tail = ReadLogTail(SetupActionsLogPath, 12000);
            if (string.IsNullOrWhiteSpace(tail))
            {
                tail = ReadLogTail(InstallerActionLogPath, 10000);
            }
            if (string.IsNullOrWhiteSpace(tail)) return null;
            var invocationMarker = "=== DS4Windows setup invocation ";
            var invocationStart = tail.LastIndexOf(invocationMarker,
                StringComparison.Ordinal);
            if (invocationStart >= 0)
            {
                tail = tail.Substring(invocationStart);
            }
            var marker = "Setup could not finish:";
            var index = tail.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var end = tail.IndexOfAny(new[] { '\r', '\n' }, index);
                var summary = end < 0 ? tail.Substring(index) :
                    tail.Substring(index, end - index);
                return summary + "\r\nOpen Log includes the complete diagnostic record.";
            }
            return "VIIPER/USB-IP setup failed. Open Log includes the detailed child-process diagnostics.";
        }

        private static string ReadLogTail(string path, int maximumBytes)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                using (var stream = new FileStream(path, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var length = (int)Math.Min(stream.Length, maximumBytes);
                    stream.Seek(-length, SeekOrigin.End);
                    var buffer = new byte[length];
                    var read = stream.Read(buffer, 0, length);
                    return Encoding.UTF8.GetString(buffer, 0, read).Trim();
                }
            }
            catch { return string.Empty; }
        }
    }

    internal enum InstallerMode
    {
        Install,
        Update,
        Repair,
        Uninstall,
    }
}
