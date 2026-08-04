using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

        internal IEngine Engine => engine;

        protected override void OnCreate(CreateEventArgs args)
        {
            base.OnCreate(args);
            command = args.Command;
        }

        protected override void Run()
        {
            HookEvents();
            window = new InstallerWindow(this);
            if (command.Display == Display.Full || command.Display == Display.Passive)
            {
                window.Show();
            }

            engine.CloseSplashScreen();
            engine.Detect();
            Dispatcher.Run();
            engine.Quit(NormalizeExitCode(result));
        }

        internal void Begin(LaunchAction action, bool desktopShortcut, bool hidHide, bool fakerInput)
        {
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

        internal void Retry()
        {
            lastError = null;
            engine.Detect();
        }

        internal void LaunchDs4Windows()
        {
            var task = new ProcessStartInfo("schtasks.exe", "/Run /TN \"RunDS4Windows\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try { Process.Start(task)?.Dispose(); }
            catch
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DS4Windows", "DS4Windows.exe");
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
        }

        internal void OpenLog()
        {
            try
            {
                var path = engine.GetVariableString("WixBundleLog");
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
            return "DS4Windows Setup\r\n" +
                   "Action: " + plannedAction + "\r\n" +
                   "Registered: " + registrationType + "\r\n" +
                   "Infrastructure healthy: " + infrastructureHealthy + "\r\n" +
                   "Error: " + (lastError ?? "none") + "\r\n" +
                   "Log: " + (log ?? "unavailable");
        }

        internal void Close(int exitCode = 0)
        {
            if (window != null && !window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(() => Close(exitCode)));
                return;
            }
            result = exitCode;
            window?.Close();
            Dispatcher.ExitAllFrames();
        }

        internal void RestartWindows()
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }

        private void HookEvents()
        {
            DetectBegin += (_, e) => registrationType = e.RegistrationType;
            DetectPackageComplete += (_, e) => packageStates[e.PackageId] = e.State;
            DetectComplete += OnDetectComplete;
            PlanPackageBegin += OnPlanPackageBegin;
            PlanComplete += OnPlanComplete;
            ApplyBegin += (_, __) => Ui(() => window.ShowApplying());
            ExecutePackageBegin += (_, e) => Ui(() => window.SetCurrentPackage(e.PackageId));
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
            if (e.Status < 0)
            {
                ShowFailure(e.Status, lastError ?? "Setup did not complete.");
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
    }

    internal enum InstallerMode
    {
        Install,
        Update,
        Repair,
        Uninstall,
    }
}
