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
        private const string ManagedBundleTag = "DS4WindowsManagedV2";
        private readonly Dictionary<string, PackageState> packageStates = new Dictionary<string, PackageState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> installerBusyRetries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> managedRelatedBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        private bool relatedUpgradeDetected;
        private string newerRelatedBundleVersion;
        private bool deferInfrastructureUntilUpgradeCompletes;
        private bool infrastructureRecoveryPass;
        private Mutex bundleMutex;
        private bool bundleMutexOwned;
        private int planStarted;

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
                        engine.SetVariableString("SetupCorrelationId",
                            Guid.NewGuid().ToString("N"), true);
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
                catch (Exception ex)
                {
                    result = 1;
                    try
                    {
                        engine.Log(LogLevel.Error,
                            "Unhandled bootstrapper failure: " + ex);
                    }
                    catch { }
                    try { engine.CloseSplashScreen(); } catch { }
                }
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
            // The incoming Burn engine owns the transaction mutex for its
            // complete package chain, including related-bundle removal. An
            // outgoing bundle launched by that engine must not compete with
            // its parent for the same lock; all top-level invocations still
            // have to acquire it before planning anything.
            var parentOwnedRelatedUninstall =
                IsParentOwnedRelatedUninstall(action);
            if (!parentOwnedRelatedUninstall && !EnsureBundleMutex()) return;
            StartPlan(action, () =>
            {
                deferInfrastructureUntilUpgradeCompletes =
                    !infrastructureRecoveryPass &&
                    (action == LaunchAction.Install ||
                     action == LaunchAction.Repair) &&
                    relatedUpgradeDetected;
                if (deferInfrastructureUntilUpgradeCompletes)
                {
                    engine.Log(LogLevel.Standard,
                        "Deferring shared infrastructure until older related bundles are removed.");
                }
                engine.SetVariableNumeric("CreateDesktopShortcut", desktopShortcut ? 1 : 0);
                engine.SetVariableNumeric("InstallHidHide", hidHide ? 1 : 0);
                engine.SetVariableNumeric("InstallFakerInput", fakerInput ? 1 : 0);
            });
        }

        private bool StartPlan(LaunchAction action, Action configure = null)
        {
            if (Interlocked.CompareExchange(ref planStarted, 1, 0) != 0)
            {
                engine.Log(LogLevel.Standard,
                    "Ignoring a duplicate installer plan request while the current transaction is active.");
                return false;
            }

            try
            {
                plannedAction = action;
                configure?.Invoke();
                if (command.Display == Display.Full)
                {
                    Ui(() => window.ShowPlanning());
                }
                engine.Plan(action);
                return true;
            }
            catch
            {
                Interlocked.Exchange(ref planStarted, 0);
                throw;
            }
        }

        private bool TryAcquireBundleMutex(int waitMilliseconds)
        {
            if (bundleMutexOwned) return true;
            try
            {
                bundleMutex = new Mutex(false,
                    @"Global\DS4Windows-Installer-Transaction");
                try
                {
                    bundleMutexOwned = bundleMutex.WaitOne(waitMilliseconds);
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

            if (!bundleMutexOwned)
            {
                bundleMutex?.Dispose();
                bundleMutex = null;
            }
            return bundleMutexOwned;
        }

        private bool EnsureBundleMutex(int waitMilliseconds = 0)
        {
            if (TryAcquireBundleMutex(waitMilliseconds)) return true;
            ShowFailure(1618,
                "Another DS4Windows installation or repair is already running. Close it, then choose Retry.");
            return false;
        }

        private bool IsParentOwnedRelatedUninstall(LaunchAction action)
        {
            return command.Relation == RelationType.Upgrade &&
                   action == LaunchAction.Uninstall;
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
            infrastructureRecoveryPass = false;
            deferInfrastructureUntilUpgradeCompletes = false;
            applyCompleted = false;
            failureShown = false;
            lock (installerBusyRetries)
            {
                installerBusyRetries.Clear();
            }
            Interlocked.Exchange(ref planStarted, 0);
            engine.Detect();
        }

        internal void CloseWithCurrentResult() => Close(result);

        internal void LaunchDs4Windows()
        {
            var task = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                "/Run /TN \"RunDS4Windows\"")
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
                managedRelatedBundles.Clear();
                relatedUpgradeDetected = false;
                newerRelatedBundleVersion = null;
            };
            DetectRelatedBundle += (_, e) =>
            {
                if (e.RelationType == RelationType.Upgrade)
                {
                    var managedGeneration = string.Equals(e.BundleTag,
                        ManagedBundleTag, StringComparison.Ordinal);
                    if (managedGeneration)
                    {
                        managedRelatedBundles.Add(e.ProductCode);
                    }
                    var currentVersion = engine.GetVariableVersion(
                        "WixBundleVersion");
                    if (IsRelatedBundleNewer(e.Version, currentVersion))
                    {
                        newerRelatedBundleVersion = e.Version;
                    }
                    else if (managedGeneration)
                    {
                        relatedUpgradeDetected = true;
                    }
                }
            };
            DetectPackageComplete += (_, e) => packageStates[e.PackageId] = e.State;
            DetectComplete += OnDetectComplete;
            PlanPackageBegin += OnPlanPackageBegin;
            PlanRelatedBundle += (_, e) =>
            {
                if (infrastructureRecoveryPass ||
                    (command.Relation == RelationType.Upgrade &&
                     plannedAction == LaunchAction.Uninstall) ||
                    !managedRelatedBundles.Contains(e.BundleId))
                {
                    // The incoming primary engine owns related-bundle
                    // ordering. An outgoing bundle only removes itself, and
                    // the isolated recovery pass only repairs infrastructure;
                    // neither may recursively launch sibling bundle engines.
                    e.State = RequestState.None;
                }
            };
            PlanComplete += OnPlanComplete;
            ApplyBegin += (_, __) => Ui(() => window.ShowApplying());
            ExecutePackageBegin += (_, e) =>
            {
                Ui(() => window.SetCurrentPackage(e.PackageId));
            };
            ExecutePackageComplete += OnExecutePackageComplete;
            ExecuteProgress += (_, e) => Ui(() => window.SetProgress(e.OverallPercentage));
            Error += (_, e) =>
            {
                lastError = string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Setup error " + e.ErrorCode : e.ErrorMessage;
                engine.Log(LogLevel.Error, lastError);
            };
            ApplyComplete += OnApplyComplete;
        }

        private void OnExecutePackageComplete(object sender,
            ExecutePackageCompleteEventArgs e)
        {
            if (IsInstallerBusyStatus(e.Status))
            {
                // Related bundles are non-vital cleanup owned by Burn, not
                // packages in this bundle's chain. Legacy DS4Windows bundles
                // can return ERROR_INSTALL_ALREADY_RUNNING while their child
                // elevated engine is still winding down. Retrying the same
                // stale bundle only repeats the collision and presents a
                // misleading "another installation" status to the user.
                // Keep retry protection for our own vital MSI/setup packages;
                // let Burn continue after one failed legacy cleanup attempt.
                if (!packageStates.ContainsKey(e.PackageId ?? string.Empty))
                {
                    engine.Log(LogLevel.Standard,
                        "Not retrying installer-busy result from non-vital " +
                        "related bundle '" + e.PackageId + "'.");
                    return;
                }

                const int maximumRetries = 3;
                int attempt;
                string packageId = e.PackageId ?? string.Empty;
                lock (installerBusyRetries)
                {
                    installerBusyRetries.TryGetValue(packageId, out attempt);
                    attempt++;
                    installerBusyRetries[packageId] = attempt;
                }

                if (attempt <= maximumRetries)
                {
                    engine.Log(LogLevel.Standard,
                        "Windows Installer is busy; retrying package '" +
                        packageId + "' (" + attempt + "/" +
                        maximumRetries + ").");
                    Ui(() => window.ShowInstallerBusyRetry(attempt,
                        maximumRetries));
                    Thread.Sleep(750 * attempt);
                    e.Action =
                        BOOTSTRAPPER_EXECUTEPACKAGECOMPLETE_ACTION.Retry;
                    return;
                }

                lastError = "Another Windows installation is still active. " +
                    "Let it finish, close any stale installer windows, then " +
                    "choose Retry. DS4Windows did not wait indefinitely or " +
                    "change the existing installation.";
            }
            else if (e.Status >= 0)
            {
                lock (installerBusyRetries)
                {
                    installerBusyRetries.Remove(e.PackageId ?? string.Empty);
                }
                return;
            }

            if (string.Equals(e.PackageId, "ViiperUsbipSetup",
                    StringComparison.OrdinalIgnoreCase))
            {
                infrastructureFailed = true;
            }
        }

        internal static bool IsInstallerBusyStatus(int status)
        {
            return status == 1618 ||
                unchecked((uint)status) == 0x80070652u;
        }

        private void OnDetectComplete(object sender, DetectCompleteEventArgs e)
        {
            if (e.Status < 0)
            {
                ShowFailure(e.Status, "Setup could not inspect the current installation.");
                return;
            }

            // During a bundle upgrade Burn launches the outgoing bundle with
            // an Upgrade relation and an Uninstall action. It necessarily
            // detects the incoming bundle as newer. Blocking that child here
            // returns ERROR_PRODUCT_VERSION (1638) to the parent, prevents
            // Burn from unregistering the old bundle, and leaves a stale ARP
            // and package-cache owner behind. Downgrade protection applies
            // only to a top-level install/repair; the parent engine already
            // owns and orders a related-bundle uninstall.
            if (!IsParentOwnedRelatedUninstall(command.Action) &&
                !string.IsNullOrWhiteSpace(newerRelatedBundleVersion))
            {
                ShowFailure(1638,
                    "A newer DS4Windows installer (" +
                    newerRelatedBundleVersion +
                    ") is already installed. This older package will not " +
                    "remove or replace it.");
                return;
            }

            infrastructureHealthy = InfrastructureProbe.IsHealthy();

            // Burn removes related bundles after it executes this bundle's
            // package chain. Older DS4Windows bundles own older infrastructure
            // helpers, so installing VIIPER before those bundles are removed
            // lets their uninstall overwrite or delete the new helper. Finish
            // the app upgrade first, then run one isolated infrastructure
            // recovery pass against the final machine state.
            if (infrastructureRecoveryPass)
            {
                if (!EnsureBundleMutex()) return;
                StartPlan(LaunchAction.Repair);
                return;
            }

            var mode = registrationType == RegistrationType.Full ? InstallerMode.Repair : InstallerMode.Install;
            if (command.Action == LaunchAction.Uninstall)
            {
                mode = InstallerMode.Uninstall;
            }
            else if (registrationType == RegistrationType.None && packageStates.TryGetValue("DS4WindowsMsi", out var msiState) && msiState == PackageState.Present)
            {
                mode = InstallerMode.Update;
            }
            else if (registrationType == RegistrationType.None &&
                     relatedUpgradeDetected)
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
                StartPlan(resumeAction);
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
            if (string.Equals(e.PackageId, "CloseRunningApplications",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Quiesce managed processes before forward install/repair
                // execution. Uninstall uses the dedicated tail preflight
                // below because Burn unwinds its package chain in reverse.
                e.State = !infrastructureRecoveryPass &&
                    (plannedAction == LaunchAction.Install ||
                     plannedAction == LaunchAction.Repair)
                    ? RequestState.Present
                    : RequestState.None;
                return;
            }

            if (string.Equals(e.PackageId,
                    "CloseRunningApplicationsForUninstall",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Burn uninstalls in reverse chain order. This tail package
                // is therefore the first executable action during direct or
                // related-bundle uninstall, before infrastructure or MSI
                // ownership is removed.
                e.State = !infrastructureRecoveryPass &&
                    plannedAction == LaunchAction.Uninstall
                    ? RequestState.Present
                    : RequestState.None;
                return;
            }

            if (string.Equals(e.PackageId,
                    "ViiperUsbipUninstall",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Shared infrastructure is permanent in the normal package
                // chain so an outgoing related bundle can unregister without
                // deleting the incoming backend. Only a direct Add/Remove
                // Programs uninstall explicitly runs this dedicated action.
                e.State = !infrastructureRecoveryPass &&
                    plannedAction == LaunchAction.Uninstall &&
                    command.Relation != RelationType.Upgrade
                    ? RequestState.Present
                    : RequestState.None;
                return;
            }

            if (command.Relation == RelationType.Upgrade &&
                plannedAction == LaunchAction.Uninstall &&
                string.Equals(e.PackageId, "ViiperUsbipSetup",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Burn is removing this bundle as part of an upgrade. Shared
                // VIIPER/USB-IP belongs to the incoming bundle, so an outgoing
                // bundle must never tear it down or replace it with its older
                // payload. A direct Add/Remove Programs uninstall still runs
                // the infrastructure uninstaller normally.
                e.State = RequestState.None;
                return;
            }

            if (infrastructureRecoveryPass)
            {
                if (string.Equals(e.PackageId, "ViiperUsbipSetup",
                        StringComparison.OrdinalIgnoreCase))
                {
                    e.State = e.CurrentState == PackageState.Present
                        ? RequestState.Repair
                        : RequestState.Present;
                }
                else
                {
                    // The MSI and optional drivers already completed in the
                    // primary transaction. The recovery pass is deliberately
                    // limited to the shared VIIPER/USB-IP contract.
                    e.State = RequestState.None;
                }
                return;
            }

            if (string.Equals(e.PackageId, "ViiperUsbipSetup",
                    StringComparison.OrdinalIgnoreCase) &&
                deferInfrastructureUntilUpgradeCompletes)
            {
                e.State = RequestState.None;
                return;
            }

            if (string.Equals(e.PackageId, "ViiperUsbipSetup",
                    StringComparison.OrdinalIgnoreCase) &&
                (plannedAction == LaunchAction.Install ||
                 plannedAction == LaunchAction.Repair))
            {
                // Preflight intentionally stops both output owners before MSI
                // mutation. Always run the lightweight infrastructure helper
                // afterwards so it can verify exact hashes/ABI, restore the
                // startup contract, and restart the API in this transaction.
                // The helper itself skips replacement when VIIPER and USB-IP
                // are already the exact healthy versions.
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

            var restartRequired =
                e.Restart == ApplyRestart.RestartRequired ||
                e.Restart == ApplyRestart.RestartInitiated;
            if (restartRequired)
            {
                // Quiet/passive callers must receive the same reboot contract
                // as the full UI rather than a misleading zero exit code.
                result = 3010;
            }

            if (!restartRequired &&
                (plannedAction == LaunchAction.Install ||
                 plannedAction == LaunchAction.Repair) &&
                !InfrastructureProbe.IsHealthy())
            {
                if (infrastructureRecoveryPass)
                {
                    ShowFailure(1,
                        "DS4Windows installed, but VIIPER/USB-IP did not pass " +
                        "the final post-upgrade health check.");
                    return;
                }

                infrastructureRecoveryPass = true;
                applyCompleted = false;
                infrastructureFailed = false;
                engine.Log(LogLevel.Standard,
                    "Starting isolated post-upgrade infrastructure recovery pass.");
                Ui(() => window.ShowPlanning());
                Interlocked.Exchange(ref planStarted, 0);
                engine.Detect();
                return;
            }

            if (command.Display != Display.Full)
            {
                Close(result);
                return;
            }

            Ui(() =>
            {
                if (restartRequired)
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

        internal static bool IsRelatedBundleNewer(string relatedVersion,
            string currentVersion)
        {
            Version related;
            Version current;
            return Version.TryParse(relatedVersion, out related) &&
                   Version.TryParse(currentVersion, out current) &&
                   related > current;
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
