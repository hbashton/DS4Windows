/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using ExecAction = Microsoft.Win32.TaskScheduler.ExecAction;
using LogonTrigger = Microsoft.Win32.TaskScheduler.LogonTrigger;
using TaskDefinition = Microsoft.Win32.TaskScheduler.TaskDefinition;
using TaskLogonType = Microsoft.Win32.TaskScheduler.TaskLogonType;
using TaskRunLevel = Microsoft.Win32.TaskScheduler.TaskRunLevel;
using TaskService = Microsoft.Win32.TaskScheduler.TaskService;

namespace DS4Windows
{
    public sealed class ViiperPrerequisiteStatus
    {
        public bool ViiperInstalled { get; set; }
        public bool ViiperPackageCurrent { get; set; }
        public bool ServerRunning { get; set; }
        public bool UsbipInstalled { get; set; }
        public bool UsbipDriverFilesSafe { get; set; }
        public string UsbipDriverIntegrityMessage { get; set; }
        public bool UsbipRuntimeReady { get; set; }
        public bool UsbipRebootOrRepairRequired { get; set; }
        public bool ViiperProcessConflict { get; set; }
        public bool ViiperStartupTaskReady { get; set; }
        public bool SetupScriptFound { get; set; }
        public string ViiperPath { get; set; }
        public string SetupScriptPath { get; set; }
        public string UsbipPath { get; set; }
        public string UsbipVersion { get; set; }
        public string UsbipProbeMessage { get; set; }
        public string ViiperProcessConflictMessage { get; set; }
        public bool UsingExternalViiper { get; set; }
        public bool CitrixUsbMonitorConflict { get; set; }
        public string CitrixUsbMonitorConflictMessage { get; set; }

        // Runtime readiness is deliberately independent of startup-task
        // maintenance. A healthy compatible VIIPER server must remain usable
        // when DS4Windows or VIIPER is run portably; a stale/missing task can
        // be repaired without preventing virtual devices from being created.
        public bool Ready => ViiperInstalled && ViiperPackageCurrent &&
            !ViiperProcessConflict && ServerRunning && UsbipInstalled &&
            UsbipDriverFilesSafe && UsbipRuntimeReady &&
            !CitrixUsbMonitorConflict;

        public string DisplayText
        {
            get
            {
                if (Ready)
                {
                    return ViiperStartupTaskReady
                        ? "VIIPER ready"
                        : "VIIPER ready; startup task needs repair";
                }

                if (CitrixUsbMonitorConflict)
                {
                    return string.IsNullOrWhiteSpace(
                        CitrixUsbMonitorConflictMessage)
                        ? "Citrix USB Monitor must be disabled before VIIPER can start safely"
                        : CitrixUsbMonitorConflictMessage;
                }

                if (ViiperProcessConflict)
                {
                    return string.IsNullOrWhiteSpace(
                        ViiperProcessConflictMessage)
                        ? "VIIPER startup blocked by another viiper.exe"
                        : ViiperProcessConflictMessage;
                }

                if (!UsbipInstalled && !ViiperInstalled)
                {
                    return "VIIPER and usbip-win2 need setup";
                }

                if (!UsbipInstalled)
                {
                    return string.IsNullOrWhiteSpace(UsbipVersion)
                        ? "usbip-win2 0.9.7.7 is missing"
                        : $"usbip-win2 {UsbipVersion} must be replaced with supported 0.9.7.7";
                }

                if (!UsbipDriverFilesSafe)
                {
                    return string.IsNullOrWhiteSpace(
                        UsbipDriverIntegrityMessage)
                        ? "usbip-win2 driver repair required"
                        : UsbipDriverIntegrityMessage;
                }

                if (!UsbipRuntimeReady || UsbipRebootOrRepairRequired)
                {
                    return "usbip-win2 reboot or repair required";
                }

                if (!ViiperInstalled)
                {
                    return "VIIPER helper missing";
                }

                if (!ViiperPackageCurrent)
                {
                    return "packaged VIIPER update required";
                }

                if (!ViiperStartupTaskReady)
                {
                    return "VIIPER elevated startup task needs repair";
                }

                return ServerRunning ? "VIIPER status unknown" : "VIIPER server not running";
            }
        }
    }

    public static class ViiperSetupManager
    {
        public const string ApiHost = "127.0.0.1";
        public const int ApiPort = 3242;
        internal static readonly Version RequiredUsbipVersion = new Version(0, 9, 7, 7);

        private const string InstallerScriptName = "install-viiper-backend.ps1";
        private const string InstallerHostArgument =
            "--run-embedded-viiper-installer";
        private const string InstallerResourceName =
            "DS4Windows.install-viiper-backend.ps1";
        private const string BundledViiperName = "VIIPER-0.0.6-x64.exe";
        private const string BundledViiperHashName =
            BundledViiperName + ".sha256";
        private const string BundledUsbipName = "USBip-0.9.7.7-x64.exe";
        private const string BundledHidHideName =
            "HidHide_1.5.230_x64.exe";
        private const string BundledFakerInputName =
            "FakerInput_0.1.0_x64.msi";
        private const string TerminateForeignViiperArgument =
            "--terminate-foreign-viiper";
        private const string RegisterViiperTaskArgument =
            "--register-viiper-startup-task";
        private const string ViiperStartupTaskName = "RunVIIPER";
        private static readonly Version RequiredViiperVersion =
            new Version(0, 0, 6, 0);
        private const int ForeignViiperHelperTimeoutMilliseconds = 15000;
        private const string UsbipRelativePath = @"USBip\usbip.exe";
        private const int UsbipProbeTimeoutMilliseconds = 3000;
        private const string CitrixUsbMonitorServiceName = "ctxusbm";
        private const string CitrixUsbMonitorImageName = "ctxusbmon.sys";
        private const string UsbipUdeServiceName = "usbip2_ude";
        private const string UsbipFilterServiceName = "usbip2_filter";
        internal const string SupportedUsbipUdeSha256 =
            "51DB440065393E588A6B2585508C50EB3E1510B7B06D9AFA6C5BDE583751EA7D";
        internal const string SupportedUsbipFilterSha256 =
            "C290299FF4D0F6A597DB5CE03E15B29A5349CDCE7C587EBFBD9ECAECA04F73ED";
        private static readonly object serverStartLock = new object();
        private static readonly object foreignViiperProcessLock = new object();
        private static readonly Lazy<(bool Conflict, string Message)>
            citrixUsbMonitorStatus = new(EvaluateCitrixUsbMonitorConflict,
                LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly Lazy<(bool Safe, string Message)>
            usbipDriverIntegrityStatus = new(EvaluateUsbipDriverIntegrity,
                LazyThreadSafetyMode.ExecutionAndPublication);
        private static DateTime lastServerStartAttemptUtc = DateTime.MinValue;
        private static DateTime lastForeignViiperTerminationAttemptUtc =
            DateTime.MinValue;
        private static string lastForeignViiperTerminationFailure;
        private static int promptShownThisSession;
        private static int installerRunning;

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess,
            bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process,
            int flags, StringBuilder executablePath, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private sealed class ViiperProcessIdentity
        {
            public int ProcessId { get; init; }
            public string ExecutablePath { get; init; }
        }

        public static bool IsViiperOutputType(OutContType type) => ViiperOutDevice.IsViiperType(type);

        public static ViiperPrerequisiteStatus GetStatus(bool tryStartServer = false)
        {
            string canonicalViiperPath = GetCanonicalViiperExePath();
            string viiperPath = ResolveRuntimeViiperPath(
                canonicalViiperPath, Global.PreferredViiperPath);
            bool usingExternalViiper = !IsExactViiperExecutablePath(
                viiperPath, canonicalViiperPath);
            string bundledViiperPath = GetBundledViiperPath();
            string setupScriptPath = GetSetupScriptPath();
            string usbipPath = GetCanonicalUsbipPath();
            Version usbipVersion = TryGetUsbipVersion(usbipPath);
            bool usbipInstalled = IsSupportedUsbipVersion(usbipVersion);
            bool usbipRuntimeReady = false;
            string usbipProbeMessage;
            (bool usbipDriverFilesSafe,
                string usbipDriverIntegrityMessage) =
                usbipDriverIntegrityStatus.Value;
            bool citrixUsbMonitorConflict =
                TryGetCitrixUsbMonitorConflict(
                    out string citrixUsbMonitorConflictMessage);

            if (usbipInstalled && usbipDriverFilesSafe)
            {
                usbipRuntimeReady = TryProbeUsbipRuntime(usbipPath,
                    out usbipProbeMessage);
            }
            else if (!File.Exists(usbipPath))
            {
                usbipProbeMessage =
                    $"usbip.exe was not found at {usbipPath}.";
            }
            else
            {
                usbipProbeMessage = !usbipDriverFilesSafe &&
                    usbipInstalled
                    ? usbipDriverIntegrityMessage
                    : usbipVersion == null
                        ? "The canonical usbip.exe version could not be read."
                        : $"usbip.exe {usbipVersion} does not match the supported {RequiredUsbipVersion}.";
            }

            bool viiperPackageCurrent = usingExternalViiper
                ? IsCompatibleExternalViiper(viiperPath)
                : FilesHaveSameSha256(viiperPath, bundledViiperPath);
            bool viiperStartupTaskReady = IsViiperStartupTaskValid(
                viiperPath, out _);
            bool canonicalViiperRunning;
            string viiperProcessConflictMessage;
            bool viiperProcessOwnershipReady = InspectViiperProcessOwnership(
                viiperPath, out canonicalViiperRunning,
                out viiperProcessConflictMessage);

            if (tryStartServer && File.Exists(viiperPath) &&
                viiperPackageCurrent && usbipRuntimeReady &&
                !citrixUsbMonitorConflict)
            {
                if (viiperProcessOwnershipReady &&
                    !canonicalViiperRunning)
                {
                    TryStartServerOnce(viiperPath);
                    viiperProcessOwnershipReady =
                        InspectViiperProcessOwnership(viiperPath,
                            out canonicalViiperRunning,
                            out viiperProcessConflictMessage);
                }
            }

            bool viiperServerRunning = viiperProcessOwnershipReady &&
                canonicalViiperRunning && CanPingServer();

            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperPath = viiperPath,
                SetupScriptPath = setupScriptPath,
                ViiperInstalled = File.Exists(viiperPath),
                ViiperPackageCurrent = viiperPackageCurrent,
                ViiperProcessConflict = !viiperProcessOwnershipReady,
                ViiperProcessConflictMessage = viiperProcessConflictMessage,
                UsingExternalViiper = usingExternalViiper,
                ViiperStartupTaskReady = viiperStartupTaskReady,
                SetupScriptFound = File.Exists(setupScriptPath),
                UsbipInstalled = usbipInstalled,
                UsbipDriverFilesSafe = usbipDriverFilesSafe,
                UsbipDriverIntegrityMessage =
                    usbipDriverIntegrityMessage,
                UsbipRuntimeReady = usbipRuntimeReady,
                UsbipRebootOrRepairRequired = usbipInstalled &&
                    (!usbipDriverFilesSafe || !usbipRuntimeReady),
                UsbipPath = usbipPath,
                UsbipVersion = usbipVersion?.ToString(),
                UsbipProbeMessage = usbipProbeMessage,
                ServerRunning = viiperServerRunning,
                CitrixUsbMonitorConflict = citrixUsbMonitorConflict,
                CitrixUsbMonitorConflictMessage =
                    citrixUsbMonitorConflictMessage,
            };

            return status;
        }

        public static bool EnsureReadyWithPrompt(Window owner, bool forcePrompt = false)
        {
            ViiperPrerequisiteStatus status = GetStatus(tryStartServer: true);
            bool readyPortableViiper = IsReadyPortableRuntime(status);
            if (status.Ready && !readyPortableViiper)
            {
                return true;
            }

            if (Global.SuppressViiperSetupPrompt && !forcePrompt)
            {
                return readyPortableViiper;
            }

            if (Volatile.Read(ref promptShownThisSession) == 1 && !forcePrompt)
            {
                return readyPortableViiper;
            }

            Interlocked.Exchange(ref promptShownThisSession, 1);
            string alternativeViiperPath = readyPortableViiper
                ? status.ViiperPath
                : FindAlternativeViiperPath(GetCanonicalViiperExePath());
            DS4WinWPF.DS4Forms.ViiperSetupPrompt prompt = new(
                status.DisplayText, alternativeViiperPath,
                status.CitrixUsbMonitorConflict, readyPortableViiper);
            if (owner != null && owner.IsLoaded)
            {
                prompt.Owner = owner;
                prompt.ShowInTaskbar = false;
                prompt.WindowStartupLocation =
                    WindowStartupLocation.CenterOwner;
            }
            else
            {
                // Startup prerequisite checks run before MainWindow exists.
                // In particular, a scheduled "-m" launch has no visible
                // owner that can surface this modal. Give an unowned prompt
                // its own taskbar presence and explicitly activate it so the
                // application cannot appear to have vanished while ShowDialog
                // waits for input.
                prompt.ShowInTaskbar = true;
                prompt.WindowStartupLocation =
                    WindowStartupLocation.CenterScreen;
                prompt.ContentRendered += (_, _) =>
                {
                    prompt.Topmost = true;
                    prompt.Activate();
                    prompt.Topmost = false;
                    prompt.Focus();
                };
            }

            prompt.ShowDialog();
            if (prompt.SuppressFuturePrompts)
            {
                Global.SuppressViiperSetupPrompt = true;
                Global.Save();
            }

            switch (prompt.Decision)
            {
                case DS4WinWPF.DS4Forms.ViiperSetupPromptDecision.
                    InstallStandard:
                    DS4WinWPF.StartupMethods.
                        RetargetExistingTaskToCurrentExecutable();
                    return LaunchInstaller(status, owner);

                case DS4WinWPF.DS4Forms.ViiperSetupPromptDecision.
                    UseExisting:
                    return readyPortableViiper
                        ? KeepReadyPortableViiper(alternativeViiperPath,
                            owner)
                        : TryAdoptViiperExecutable(alternativeViiperPath,
                            owner);

                default:
                    return readyPortableViiper;
            }
        }

        internal static bool IsReadyPortableRuntime(
            ViiperPrerequisiteStatus status)
        {
            return status != null && status.Ready &&
                status.UsingExternalViiper;
        }

        public static void RefreshSelectedStartupTaskOnLaunch()
        {
            string canonicalPath = GetCanonicalViiperExePath();
            string selectedPath = ResolveRuntimeViiperPath(canonicalPath,
                Global.PreferredViiperPath);
            if (!File.Exists(selectedPath))
            {
                return;
            }

            if (!EnsureViiperStartupTask(selectedPath,
                    requestElevation: true))
            {
                return;
            }

            string persistedPath = IsExactViiperExecutablePath(selectedPath,
                    canonicalPath)
                ? string.Empty
                : Path.GetFullPath(selectedPath);
            if (!string.Equals(Global.PreferredViiperPath ?? string.Empty,
                    persistedPath, StringComparison.OrdinalIgnoreCase))
            {
                Global.PreferredViiperPath = persistedPath;
                Global.Save();
            }
        }

        public static void RefreshSelectedStartupTaskAfterRunAtStartupChange()
        {
            RefreshSelectedStartupTaskOnLaunch();
        }

        public static bool LaunchInstaller(ViiperPrerequisiteStatus status = null, Window owner = null)
        {
            status ??= GetStatus();
            if (!status.SetupScriptFound)
            {
                string message =
                    "This DS4Windows package is incomplete: the bundled " +
                    "VIIPER setup script is missing.\n\nDownload or extract " +
                    "the complete DS4Windows package, then try again. " +
                    "Setup does not download missing components.";
                if (owner != null)
                {
                    MessageBox.Show(owner, message, "VIIPER setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(message, "VIIPER setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }

            if (Interlocked.CompareExchange(ref installerRunning, 1, 0) != 0)
            {
                ShowInstallerMessage(owner,
                    "VIIPER setup is already running. Finish the open setup window, then use Refresh to verify it.",
                    "VIIPER setup", MessageBoxImage.Information);
                return true;
            }

            try
            {
                string targetLocalAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                string targetUserSid = identity.User?.Value ?? string.Empty;
                string targetUserName = identity.Name ?? string.Empty;

                if (string.IsNullOrWhiteSpace(targetLocalAppData) ||
                    string.IsNullOrWhiteSpace(targetUserSid) ||
                    string.IsNullOrWhiteSpace(targetUserName) ||
                    string.IsNullOrWhiteSpace(Global.exelocation))
                {
                    throw new InvalidOperationException(
                        "DS4Windows could not determine the current Windows " +
                        "account required for elevated startup-task setup.");
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Global.exelocation,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                startInfo.ArgumentList.Add(InstallerHostArgument);
                startInfo.ArgumentList.Add("--target-local-appdata");
                startInfo.ArgumentList.Add(targetLocalAppData);
                startInfo.ArgumentList.Add("--target-user-sid");
                startInfo.ArgumentList.Add(targetUserSid);
                startInfo.ArgumentList.Add("--target-user-name");
                startInfo.ArgumentList.Add(targetUserName);
                startInfo.ArgumentList.Add("--target-ds4windows-path");
                startInfo.ArgumentList.Add(Global.exelocation);
                startInfo.ArgumentList.Add("--package-extras");
                startInfo.ArgumentList.Add(Path.Combine(Global.exedirpath,
                    "extras"));
                Process process = Process.Start(startInfo);
                if (process == null)
                {
                    throw new InvalidOperationException(
                        "Windows did not start the setup process.");
                }

                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => InstallerProcess_Exited(process,
                    owner);
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Interlocked.Exchange(ref installerRunning, 0);
                ShowInstallerMessage(owner,
                    "VIIPER setup was canceled at the Windows administrator prompt. No changes were made.",
                    "VIIPER setup canceled", MessageBoxImage.Information);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref installerRunning, 0);
                string message = $"Could not launch VIIPER setup: {ex.Message}";
                ShowInstallerMessage(owner, message, "VIIPER setup",
                    MessageBoxImage.Error);

                return false;
            }
        }

        private static void InstallerProcess_Exited(Process process,
            Window owner)
        {
            int exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }
            try { process.Dispose(); } catch { }
            Interlocked.Exchange(ref installerRunning, 0);

            Application application = Application.Current;
            if (application?.Dispatcher == null ||
                application.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            application.Dispatcher.BeginInvoke(new Action(() =>
            {
                // Change ownership only after the bundled installer succeeds;
                // canceling UAC must not disable a working portable backend.
                if (exitCode == 0)
                {
                    Global.PreferredViiperPath = string.Empty;
                    Global.Save();
                }

                ViiperPrerequisiteStatus refreshed = GetStatus(
                    tryStartServer: true);
                if (exitCode == 0 && refreshed.Ready)
                {
                    Interlocked.Exchange(ref promptShownThisSession, 0);
                    AppLogger.LogToGui(
                        "SUCCESSFUL: VIIPER setup finished successfully. The managed startup task owns the DS4Windows restart.",
                        false, false);
                    return;
                }

                if (exitCode == 3010)
                {
                    ShowInstallerMessage(owner,
                        "VIIPER setup reached a required kernel-driver " +
                        "safety boundary. Restart Windows, then run Install " +
                        "/ Repair again to finish setup. No replacement " +
                        "driver was overlaid in the current Windows session.",
                        "VIIPER setup requires a restart",
                        MessageBoxImage.Warning);
                    return;
                }

                string logPath = Path.Combine(
                    GetNativeProgramFilesPath(),
                    "DS4Windows", "VIIPER", "install.log");
                string message = exitCode == 0
                    ? "VIIPER was installed, but Windows is not reporting every component as ready yet. Restart Windows once, then click Refresh."
                    : exitCode == 1223
                    ? "VIIPER setup was canceled. No USBIP driver or foreign executable was changed."
                    : BuildInstallerFailureMessage(exitCode, logPath);
                ShowInstallerMessage(owner, message, "VIIPER setup",
                    exitCode == 1223 ? MessageBoxImage.Information :
                    exitCode == 0 ? MessageBoxImage.Warning :
                        MessageBoxImage.Error);
            }));
        }

        public static bool TryRunStartupTaskRegistrationHelper(string[] args,
            out int exitCode)
        {
            exitCode = 1;
            if (args == null || args.Length != 3 ||
                !string.Equals(args[0], RegisterViiperTaskArgument,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                string viiperPath = Encoding.UTF8.GetString(
                    Convert.FromBase64String(args[1]));
                string targetUserSid = Encoding.UTF8.GetString(
                    Convert.FromBase64String(args[2]));
                string currentUserSid = WindowsIdentity.GetCurrent().User?.
                    Value;
                if (!Global.IsAdministrator() ||
                    !string.Equals(currentUserSid, targetUserSid,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsSelectableViiperExecutable(viiperPath))
                {
                    exitCode = 5;
                    return true;
                }

                RegisterViiperStartupTask(viiperPath);
                exitCode = IsViiperStartupTaskValid(viiperPath, out _)
                    ? 0
                    : 1;
            }
            catch (FormatException)
            {
                exitCode = 87;
            }
            catch
            {
                exitCode = 1;
            }

            return true;
        }

        public static bool TryRunElevatedInstallerHost(string[] args,
            out int exitCode)
        {
            exitCode = 1;
            if (args == null || args.Length == 0 ||
                !string.Equals(args[0], InstallerHostArgument,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                WindowsPrincipal principal = new WindowsPrincipal(
                    WindowsIdentity.GetCurrent());
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    throw new InvalidOperationException(
                        "The embedded VIIPER installer host is not elevated.");
                }

                string targetLocalAppData = GetRequiredInstallerArgument(args,
                    "--target-local-appdata");
                string targetUserSid = GetRequiredInstallerArgument(args,
                    "--target-user-sid");
                string targetUserName = GetRequiredInstallerArgument(args,
                    "--target-user-name");
                string targetDs4WindowsPath =
                    GetRequiredInstallerArgument(args,
                        "--target-ds4windows-path");
                string packageExtras = GetRequiredInstallerArgument(args,
                    "--package-extras");

                string setupRoot = Path.Combine(
                    GetNativeProgramFilesPath(),
                    "DS4Windows.Setup");
                string setupDirectory = Path.Combine(setupRoot,
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(setupDirectory);
                string scriptPath = Path.Combine(setupDirectory,
                    InstallerScriptName);

                try
                {
                    using Stream resource = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream(InstallerResourceName);
                    if (resource == null)
                    {
                        throw new InvalidOperationException(
                            "The embedded VIIPER installer resource is missing.");
                    }
                    using (FileStream file = new FileStream(scriptPath,
                               FileMode.CreateNew, FileAccess.Write,
                               FileShare.None))
                    {
                        resource.CopyTo(file);
                    }

                    // Snapshot the complete release package after elevation
                    // into a Program Files staging directory. Every source
                    // handle denies write/delete sharing while it is copied,
                    // so another unelevated process cannot swap a DLL or
                    // rewrite the package manifest across the UAC boundary.
                    string stagedPackageRoot = StageInstallerPackage(
                        packageExtras, setupDirectory);
                    string stagedExtras = Path.Combine(stagedPackageRoot,
                        "extras");

                    string powershellPath = Path.Combine(
                        Environment.SystemDirectory, "WindowsPowerShell",
                        "v1.0", "powershell.exe");
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = powershellPath,
                        UseShellExecute = true,
                        WorkingDirectory = stagedExtras,
                    };
                    startInfo.ArgumentList.Add("-NoProfile");
                    startInfo.ArgumentList.Add("-ExecutionPolicy");
                    startInfo.ArgumentList.Add("Bypass");
                    startInfo.ArgumentList.Add("-File");
                    startInfo.ArgumentList.Add(scriptPath);
                    startInfo.ArgumentList.Add("-NoPause");
                    startInfo.ArgumentList.Add("-TargetLocalAppData");
                    startInfo.ArgumentList.Add(targetLocalAppData);
                    startInfo.ArgumentList.Add("-TargetUserSid");
                    startInfo.ArgumentList.Add(targetUserSid);
                    startInfo.ArgumentList.Add("-TargetUserName");
                    startInfo.ArgumentList.Add(targetUserName);
                    startInfo.ArgumentList.Add("-TargetDs4WindowsPath");
                    startInfo.ArgumentList.Add(targetDs4WindowsPath);
                    startInfo.ArgumentList.Add("-PackageExtrasRoot");
                    startInfo.ArgumentList.Add(stagedExtras);
                    startInfo.ArgumentList.Add("-InstallerHostPid");
                    startInfo.ArgumentList.Add(
                        Environment.ProcessId.ToString());

                    using Process process = Process.Start(startInfo);
                    if (process == null)
                    {
                        throw new InvalidOperationException(
                            "Windows did not start the embedded VIIPER setup.");
                    }
                    process.WaitForExit();
                    exitCode = process.ExitCode;

                    if (exitCode == 3010)
                    {
                        MessageBox.Show(
                            "VIIPER setup reached a required kernel-driver " +
                            "safety boundary. Restart Windows, then run " +
                            "Install / Repair again to finish setup. No " +
                            "replacement driver was overlaid in this session.",
                            "Restart required", MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    else if (exitCode != 0 && exitCode != 1223)
                    {
                        MessageBox.Show(
                            $"VIIPER setup did not finish (exit code " +
                            $"{exitCode}). Review " +
                            @"%ProgramFiles%\DS4Windows\VIIPER\install.log " +
                            "and run Install / Repair again.",
                            "VIIPER setup failed", MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(setupDirectory, recursive: true);
                    }
                    catch { }
                }

                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(
                        $"VIIPER setup host failed: {ex.Message}",
                        "VIIPER setup", MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch { }
                return true;
            }
        }

        private static string StageInstallerPackage(string packageExtras,
            string setupDirectory)
        {
            string sourceRoot = Directory.GetParent(packageExtras)?.FullName;
            if (string.IsNullOrWhiteSpace(sourceRoot) ||
                !Directory.Exists(sourceRoot))
            {
                throw new InvalidOperationException(
                    "The DS4Windows release package root is missing.");
            }

            string manifestPath = Path.Combine(sourceRoot,
                ".ds4windows-managed-files.txt");
            string stagedRoot = Path.Combine(setupDirectory, "package");
            Directory.CreateDirectory(stagedRoot);

            using FileStream manifestStream = new FileStream(manifestPath,
                FileMode.Open, FileAccess.Read, FileShare.Read);
            List<string> relativePaths = new List<string>();
            using (StreamReader reader = new StreamReader(manifestStream,
                       Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                       bufferSize: 4096, leaveOpen: true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string relativePath = line.Trim().Replace('/',
                        Path.DirectorySeparatorChar);
                    if (!string.IsNullOrWhiteSpace(relativePath))
                    {
                        relativePaths.Add(relativePath);
                    }
                }
            }

            if (relativePaths.Count == 0)
            {
                throw new InvalidOperationException(
                    "The DS4Windows managed-file manifest is empty.");
            }

            string sourcePrefix = Path.GetFullPath(sourceRoot)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string stagedPrefix = Path.GetFullPath(stagedRoot)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            HashSet<string> seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (string relativePath in relativePaths)
            {
                if (Path.IsPathFullyQualified(relativePath) ||
                    !seen.Add(relativePath))
                {
                    throw new InvalidOperationException(
                        $"Unsafe or duplicate package path: {relativePath}");
                }

                string sourcePath = Path.GetFullPath(Path.Combine(sourceRoot,
                    relativePath));
                string targetPath = Path.GetFullPath(Path.Combine(stagedRoot,
                    relativePath));
                if (!sourcePath.StartsWith(sourcePrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    !targetPath.StartsWith(stagedPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(sourcePath) ||
                    (File.GetAttributes(sourcePath) &
                        FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid package file: {relativePath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using FileStream source = new FileStream(sourcePath,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                using FileStream target = new FileStream(targetPath,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
                target.Flush(flushToDisk: true);
            }

            manifestStream.Position = 0;
            string stagedManifest = Path.Combine(stagedRoot,
                ".ds4windows-managed-files.txt");
            using (FileStream target = new FileStream(stagedManifest,
                       FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                manifestStream.CopyTo(target);
                target.Flush(flushToDisk: true);
            }

            string stagedExtras = Path.Combine(stagedRoot, "extras");
            string[] requiredOfflineFiles =
            {
                Path.Combine(stagedRoot, "DS4Windows.exe"),
                Path.Combine(stagedExtras, InstallerScriptName),
                Path.Combine(stagedExtras, BundledViiperName),
                Path.Combine(stagedExtras, BundledViiperHashName),
                Path.Combine(stagedExtras, BundledUsbipName),
                Path.Combine(stagedExtras, BundledHidHideName),
                Path.Combine(stagedExtras, BundledFakerInputName),
            };
            string missingOfflineFile = Array.Find(requiredOfflineFiles,
                path => !File.Exists(path));
            if (missingOfflineFile != null)
            {
                throw new InvalidOperationException(
                    "The staged offline DS4Windows package is incomplete: " +
                    Path.GetFileName(missingOfflineFile) + " is missing.");
            }

            return stagedRoot;
        }

        private static string GetRequiredInstallerArgument(string[] args,
            string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name,
                        StringComparison.Ordinal))
                {
                    string value = args[i + 1];
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            throw new InvalidOperationException(
                $"The required installer argument {name} is missing.");
        }

        private static void ShowInstallerMessage(Window owner, string message,
            string caption, MessageBoxImage image)
        {
            if (owner != null && owner.IsLoaded)
            {
                MessageBox.Show(owner, message, caption, MessageBoxButton.OK,
                    image);
            }
            else
            {
                MessageBox.Show(message, caption, MessageBoxButton.OK, image);
            }
        }

        private static string GetCanonicalViiperExePath()
        {
            return Path.Combine(GetNativeProgramFilesPath(), "DS4Windows", "VIIPER",
                "viiper.exe");
        }

        internal static string BuildInstallerFailureMessage(int exitCode,
            string logPath)
        {
            return $"VIIPER setup could not finish (exit code {exitCode}).\n\n" +
                "If a viiper.exe process was still running, it may have " +
                "blocked the VIIPER registration step. Close viiper.exe " +
                "manually and run Repair again.\n\nReview the setup log " +
                $"for details:\n{logPath}";
        }

        internal static string ResolveConfiguredViiperPath(
            string canonicalPath, string preferredPath)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath))
            {
                try
                {
                    string normalized = Path.GetFullPath(preferredPath);
                    if (File.Exists(normalized))
                    {
                        return normalized;
                    }
                }
                catch { }
            }

            return canonicalPath;
        }

        internal static string ResolveRuntimeViiperPath(
            string canonicalPath, string preferredPath)
        {
            string selectedPath = ResolveConfiguredViiperPath(canonicalPath,
                preferredPath);
            // An explicit, existing portable choice is authoritative. A stale
            // running process or startup-task action describes yesterday's
            // runtime; it must not silently replace the path the user saved.
            if (!string.IsNullOrWhiteSpace(preferredPath))
            {
                try
                {
                    if (IsExactViiperExecutablePath(selectedPath,
                            Path.GetFullPath(preferredPath)))
                    {
                        return selectedPath;
                    }
                }
                catch { }
            }

            List<ViiperProcessIdentity> processes =
                GetRunningViiperProcesses();
            if (processes != null)
            {
                foreach (ViiperProcessIdentity process in processes)
                {
                    if (IsExactViiperExecutablePath(process.ExecutablePath,
                            selectedPath))
                    {
                        return selectedPath;
                    }
                }

                string runningAlternative = SelectAlternativeViiperPath(
                    selectedPath,
                    processes.ConvertAll(process => process.ExecutablePath),
                    null);
                if (!string.IsNullOrWhiteSpace(runningAlternative))
                {
                    return runningAlternative;
                }
            }

            string taskAlternative = SelectAlternativeViiperPath(
                selectedPath, null, GetRunViiperTaskExecutablePath());
            return string.IsNullOrWhiteSpace(taskAlternative)
                ? selectedPath
                : taskAlternative;
        }

        internal static bool IsCompatibleViiperVersion(string versionText)
        {
            return Version.TryParse(versionText, out Version version) &&
                version == RequiredViiperVersion;
        }

        private static bool IsCompatibleExternalViiper(string viiperPath)
        {
            try
            {
                if (!File.Exists(viiperPath) ||
                    !string.Equals(Path.GetFileName(viiperPath),
                        "viiper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(
                    viiperPath);
                return IsCompatibleViiperVersion(versionInfo.FileVersion);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSelectableViiperExecutable(string viiperPath)
        {
            try
            {
                string normalized = Path.GetFullPath(viiperPath);
                if (!File.Exists(normalized) ||
                    (File.GetAttributes(normalized) &
                        FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                string canonicalPath = GetCanonicalViiperExePath();
                return IsExactViiperExecutablePath(normalized, canonicalPath)
                    ? FilesHaveSameSha256(normalized,
                        GetBundledViiperPath())
                    : IsCompatibleExternalViiper(normalized);
            }
            catch
            {
                return false;
            }
        }

        internal static string SelectAlternativeViiperPath(
            string canonicalPath, IEnumerable<string> runningPaths,
            string taskPath)
        {
            HashSet<string> candidates = new(
                StringComparer.OrdinalIgnoreCase);
            if (runningPaths != null)
            {
                foreach (string path in runningPaths)
                {
                    AddAlternativeCandidate(candidates, canonicalPath, path);
                }
            }

            AddAlternativeCandidate(candidates, canonicalPath, taskPath);
            return candidates.Count == 1
                ? new List<string>(candidates)[0]
                : null;
        }

        private static void AddAlternativeCandidate(
            HashSet<string> candidates, string canonicalPath,
            string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return;
            }

            try
            {
                string normalized = Path.GetFullPath(candidatePath);
                if (!IsExactViiperExecutablePath(normalized, canonicalPath) &&
                    IsSelectableViiperExecutable(normalized))
                {
                    candidates.Add(normalized);
                }
            }
            catch { }
        }

        private static string FindAlternativeViiperPath(
            string canonicalPath)
        {
            List<ViiperProcessIdentity> processes =
                GetRunningViiperProcesses();
            IEnumerable<string> runningPaths = processes?.ConvertAll(
                process => process.ExecutablePath);
            return SelectAlternativeViiperPath(canonicalPath, runningPaths,
                GetRunViiperTaskExecutablePath());
        }

        private static string GetRunViiperTaskExecutablePath()
        {
            try
            {
                using TaskService service = new TaskService();
                using Microsoft.Win32.TaskScheduler.Task task =
                    service.GetTask(@"\" + ViiperStartupTaskName);
                return task?.Definition.Actions.Count == 1 &&
                    task.Definition.Actions[0] is ExecAction action
                    ? action.Path
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetNativeProgramFilesPath()
        {
            // ProgramW6432 remains the native 64-bit Program Files directory
            // even if an x86 process is inspecting the x64-only VIIPER setup.
            string programFiles = Environment.GetEnvironmentVariable(
                "ProgramW6432");
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                programFiles = Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);
            }

            return Path.GetFullPath(programFiles);
        }

        private static string TryResolveAccountSid(string account)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                return null;
            }

            try
            {
                return new SecurityIdentifier(account).Value;
            }
            catch { }

            try
            {
                return ((SecurityIdentifier)new NTAccount(account).Translate(
                    typeof(SecurityIdentifier))).Value;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsViiperStartupTaskValid(string viiperPath,
            out string failureMessage)
        {
            failureMessage = null;
            try
            {
                using TaskService service = new TaskService();
                Microsoft.Win32.TaskScheduler.Task task =
                    service.GetTask(@"\" + ViiperStartupTaskName);
                if (task == null)
                {
                    failureMessage = "RunVIIPER is not registered.";
                    return false;
                }

                using (task)
                {
                    TaskDefinition definition = task.Definition;
                    if (!task.Enabled || definition.Actions.Count != 1 ||
                        definition.Triggers.Count != 1 ||
                        definition.Principal.RunLevel != TaskRunLevel.Highest ||
                        definition.Principal.LogonType !=
                            TaskLogonType.InteractiveToken ||
                        definition.Settings.Priority !=
                            ProcessPriorityClass.High ||
                        definition.Actions[0] is not ExecAction action ||
                        definition.Triggers[0] is not LogonTrigger trigger)
                    {
                        failureMessage = "RunVIIPER does not have the exact " +
                            "enabled, elevated logon-task shape.";
                        return false;
                    }

                    string expectedDirectory = Path.GetDirectoryName(
                        viiperPath);
                    string currentSid = WindowsIdentity.GetCurrent().User?.Value;
                    bool valid = IsExactViiperExecutablePath(action.Path,
                            viiperPath) &&
                        string.Equals(action.Arguments?.Trim(), "server",
                            StringComparison.Ordinal) &&
                        IsExactViiperExecutablePath(
                            Path.Combine(action.WorkingDirectory ?? string.Empty,
                                "viiper.exe"), viiperPath) &&
                        string.Equals(TryResolveAccountSid(
                                definition.Principal.UserId), currentSid,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(TryResolveAccountSid(trigger.UserId),
                            currentSid, StringComparison.OrdinalIgnoreCase);
                    if (!valid)
                    {
                        failureMessage = "RunVIIPER does not target the " +
                            "selected backend for the current Windows account.";
                    }

                    return valid;
                }
            }
            catch (Exception ex)
            {
                failureMessage = "RunVIIPER could not be verified: " +
                    ex.Message;
                return false;
            }
        }

        private static bool TryAdoptViiperExecutable(string viiperPath,
            Window owner)
        {
            if (!IsSelectableViiperExecutable(viiperPath))
            {
                ShowInstallerMessage(owner,
                    "The selected VIIPER executable is missing or does not " +
                    "match the supported VIIPER 0.0.6 contract.",
                    "VIIPER could not be selected", MessageBoxImage.Warning);
                return false;
            }

            if (!TryTerminateForeignViiperProcesses(viiperPath,
                    out string ownershipFailure))
            {
                ShowInstallerMessage(owner, ownershipFailure,
                    "VIIPER ownership could not be changed",
                    MessageBoxImage.Warning);
                return false;
            }

            if (!EnsureViiperStartupTask(viiperPath,
                    requestElevation: true))
            {
                ShowInstallerMessage(owner,
                    "Windows could not retarget the elevated RunVIIPER " +
                    "startup task. The executable was not adopted.",
                    "VIIPER startup task", MessageBoxImage.Warning);
                return false;
            }

            Global.PreferredViiperPath = Path.GetFullPath(viiperPath);
            if (!Global.Save())
            {
                ShowInstallerMessage(owner,
                    "DS4Windows could not save the selected VIIPER path.",
                    "VIIPER preference", MessageBoxImage.Warning);
                return false;
            }

            DS4WinWPF.StartupMethods.
                RetargetExistingTaskToCurrentExecutable();
            TryStartServerOnce(Global.PreferredViiperPath);
            return GetStatus(tryStartServer: false).Ready;
        }

        private static bool KeepReadyPortableViiper(string viiperPath,
            Window owner)
        {
            // GetStatus already proved that this exact executable is running,
            // compatible, and backed by a healthy USB/IP runtime. Keeping it
            // must never tear down that working session merely because task
            // registration is canceled or unavailable.
            if (!IsSelectableViiperExecutable(viiperPath))
            {
                return false;
            }

            Global.PreferredViiperPath = Path.GetFullPath(viiperPath);
            if (!Global.Save())
            {
                AppLogger.LogToGui(
                    "Portable VIIPER is still active, but its preferred path could not be saved.",
                    true);
            }

            bool taskReady = EnsureViiperStartupTask(viiperPath,
                requestElevation: true);
            if (!taskReady)
            {
                ShowInstallerMessage(owner,
                    "Portable VIIPER remains active for this session. " +
                    "Windows did not update its startup task, so use " +
                    "Settings > VIIPER Virtual Controller Support to retry " +
                    "before the next sign-in.",
                    "Portable VIIPER kept", MessageBoxImage.Information);
            }

            DS4WinWPF.StartupMethods.
                RetargetExistingTaskToCurrentExecutable();
            return true;
        }

        private static bool EnsureViiperStartupTask(string viiperPath,
            bool requestElevation)
        {
            if (!IsSelectableViiperExecutable(viiperPath))
            {
                return false;
            }

            if (IsViiperStartupTaskValid(viiperPath, out _))
            {
                return true;
            }

            try
            {
                if (Global.IsAdministrator())
                {
                    RegisterViiperStartupTask(viiperPath);
                    return IsViiperStartupTaskValid(viiperPath, out _);
                }

                if (!requestElevation ||
                    string.IsNullOrWhiteSpace(Global.exelocation))
                {
                    return false;
                }

                string currentUserSid = WindowsIdentity.GetCurrent().User?.
                    Value;
                if (string.IsNullOrWhiteSpace(currentUserSid))
                {
                    return false;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Global.exelocation,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                startInfo.ArgumentList.Add(RegisterViiperTaskArgument);
                startInfo.ArgumentList.Add(Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(viiperPath))));
                startInfo.ArgumentList.Add(Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(currentUserSid)));
                using Process process = Process.Start(startInfo);
                if (process == null || !process.WaitForExit(15000) ||
                    process.ExitCode != 0)
                {
                    return false;
                }

                return IsViiperStartupTaskValid(viiperPath, out _);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void RegisterViiperStartupTask(string viiperPath)
        {
            string fullPath = Path.GetFullPath(viiperPath);
            string workingDirectory = Path.GetDirectoryName(fullPath);
            string currentUser = WindowsIdentity.GetCurrent().Name;
            using TaskService service = new TaskService();
            Microsoft.Win32.TaskScheduler.Task existing =
                service.GetTask(@"\" + ViiperStartupTaskName);
            if (existing != null)
            {
                existing.Dispose();
                service.RootFolder.DeleteTask(ViiperStartupTaskName);
            }

            TaskDefinition definition = service.NewTask();
            definition.Triggers.Add(new LogonTrigger
            {
                UserId = currentUser,
            });
            definition.Actions.Add(new ExecAction(fullPath, "server",
                workingDirectory));
            definition.Principal.UserId = currentUser;
            definition.Principal.LogonType =
                TaskLogonType.InteractiveToken;
            definition.Principal.RunLevel = TaskRunLevel.Highest;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            definition.Settings.MultipleInstances =
                Microsoft.Win32.TaskScheduler.TaskInstancesPolicy.IgnoreNew;
            definition.Settings.AllowDemandStart = true;
            // Priority 7 is Task Scheduler's default and maps to below-normal
            // CPU priority plus low I/O and memory priority.  The virtual USB
            // audio producer must not be starved by unrelated foreground CPU
            // work, so give the backend a high (never realtime) task priority.
            definition.Settings.Priority = ProcessPriorityClass.High;
            service.RootFolder.RegisterTaskDefinition(
                ViiperStartupTaskName, definition);
        }

        internal static bool IsExactViiperExecutablePath(string candidatePath,
            string canonicalPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) ||
                string.IsNullOrWhiteSpace(canonicalPath))
            {
                return false;
            }

            try
            {
                string candidate = Path.GetFullPath(candidatePath)
                    .TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                string canonical = Path.GetFullPath(canonicalPath)
                    .TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                return string.Equals(candidate, canonical,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static List<ViiperProcessIdentity> GetRunningViiperProcesses()
        {
            List<ViiperProcessIdentity> result = new();
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("viiper");
            }
            catch
            {
                return null;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        result.Add(new ViiperProcessIdentity
                        {
                            ProcessId = process.Id,
                            ExecutablePath = TryGetProcessExecutablePath(
                                process.Id),
                        });
                    }
                    catch
                    {
                        // A process can exit between enumeration and identity
                        // inspection. A later convergence pass handles it.
                    }
                }
            }

            return result;
        }

        private static string TryGetProcessExecutablePath(int processId)
        {
            IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation,
                false, processId);
            if (processHandle != IntPtr.Zero)
            {
                try
                {
                    StringBuilder path = new(32768);
                    int length = path.Capacity;
                    if (QueryFullProcessImageName(processHandle, 0, path,
                            ref length) && length > 0)
                    {
                        return path.ToString(0, length);
                    }
                }
                finally
                {
                    CloseHandle(processHandle);
                }
            }

            try
            {
                using Process process = Process.GetProcessById(processId);
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        private static bool InspectViiperProcessOwnership(
            string canonicalPath, out bool canonicalProcessRunning,
            out string conflictMessage)
        {
            canonicalProcessRunning = false;
            List<string> conflicts = new();
            List<ViiperProcessIdentity> processes =
                GetRunningViiperProcesses();
            if (processes == null)
            {
                conflictMessage = "VIIPER startup blocked: Windows could " +
                    "not enumerate running viiper.exe processes, so process " +
                    "ownership could not be verified.";
                return false;
            }

            foreach (ViiperProcessIdentity process in processes)
            {
                if (IsExactViiperExecutablePath(process.ExecutablePath,
                        canonicalPath))
                {
                    canonicalProcessRunning = true;
                    continue;
                }

                string path = string.IsNullOrWhiteSpace(process.ExecutablePath)
                    ? "an unreadable executable path"
                    : process.ExecutablePath;
                conflicts.Add($"PID {process.ProcessId} from {path}");
            }

            if (conflicts.Count == 0)
            {
                conflictMessage = null;
                return true;
            }

            conflictMessage = "VIIPER startup blocked: another viiper.exe " +
                $"is not the selected copy at {canonicalPath} " +
                $"({string.Join("; ", conflicts)}). Close it or approve the " +
                "administrator prompt so DS4Windows can stop it safely.";
            return false;
        }

        private static bool TryTerminateForeignViiperProcesses(
            string canonicalPath, out string failureMessage,
            bool allowElevation = true)
        {
            lock (foreignViiperProcessLock)
            {
                bool canonicalRunning;
                if (InspectViiperProcessOwnership(canonicalPath,
                        out canonicalRunning, out failureMessage))
                {
                    lastForeignViiperTerminationFailure = null;
                    return true;
                }

                DateTime now = DateTime.UtcNow;
                if (allowElevation &&
                    (now - lastForeignViiperTerminationAttemptUtc).
                        TotalSeconds < 5)
                {
                    failureMessage = lastForeignViiperTerminationFailure ??
                        failureMessage;
                    return false;
                }

                lastForeignViiperTerminationAttemptUtc = now;
                bool success = TryTerminateForeignViiperProcessesLocked(
                    canonicalPath, out failureMessage, allowElevation);
                lastForeignViiperTerminationFailure = success
                    ? null
                    : failureMessage;
                return success;
            }
        }

        private static bool TryTerminateForeignViiperProcessesLocked(
            string canonicalPath, out string failureMessage,
            bool allowElevation)
        {
            List<string> failures = new();
            List<ViiperProcessIdentity> processes =
                GetRunningViiperProcesses();
            if (processes == null)
            {
                failureMessage = "VIIPER startup blocked: Windows could " +
                    "not enumerate running viiper.exe processes.";
                return false;
            }

            foreach (ViiperProcessIdentity identity in processes)
            {
                if (IsExactViiperExecutablePath(identity.ExecutablePath,
                        canonicalPath))
                {
                    continue;
                }

                if (!TryTerminateForeignViiperProcess(identity.ProcessId,
                        canonicalPath, out string detail))
                {
                    failures.Add(detail);
                }
            }

            bool canonicalRunning;
            if (InspectViiperProcessOwnership(canonicalPath,
                    out canonicalRunning, out failureMessage))
            {
                return true;
            }

            if (!allowElevation)
            {
                if (failures.Count > 0)
                {
                    failureMessage = $"{failureMessage} " +
                        string.Join("; ", failures);
                }

                return false;
            }

            if (!TryRunElevatedForeignViiperTermination(canonicalPath,
                    out string elevatedFailure))
            {
                string directFailure = failures.Count == 0
                    ? string.Empty
                    : $" Direct stop: {string.Join("; ", failures)}.";
                failureMessage = $"{failureMessage}{directFailure} " +
                    elevatedFailure;
                return false;
            }

            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (InspectViiperProcessOwnership(canonicalPath,
                        out canonicalRunning, out failureMessage))
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            failureMessage = $"{failureMessage} The conflicting process " +
                "remained active after elevated termination.";
            return false;
        }

        private static bool TryTerminateForeignViiperProcess(int processId,
            string canonicalPath, out string failureMessage)
        {
            failureMessage = null;
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (!string.Equals(process.ProcessName, "viiper",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string currentPath = TryGetProcessExecutablePath(processId);
                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    failureMessage = $"PID {processId} path could not be " +
                        "verified";
                    return false;
                }

                if (IsExactViiperExecutablePath(currentPath, canonicalPath))
                {
                    return true;
                }

                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    failureMessage = $"PID {processId} did not exit";
                    return false;
                }

                return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Exception ex)
            {
                failureMessage = $"PID {processId}: {ex.Message}";
                return false;
            }
        }

        private static bool TryRunElevatedForeignViiperTermination(
            string canonicalPath, out string failureMessage)
        {
            failureMessage = null;
            string helperPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(helperPath) ||
                !File.Exists(helperPath))
            {
                helperPath = Global.exelocation;
            }

            if (string.IsNullOrWhiteSpace(helperPath) ||
                !File.Exists(helperPath))
            {
                failureMessage = "DS4Windows could not locate its elevated " +
                    "termination helper. No fallback executable was used.";
                return false;
            }

            string encodedCanonicalPath = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(canonicalPath));
            Process helper = null;
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = helperPath,
                    Arguments = $"{TerminateForeignViiperArgument} " +
                        encodedCanonicalPath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                helper = Process.Start(startInfo);
                if (helper == null)
                {
                    failureMessage = "Windows did not start the elevated " +
                        "VIIPER termination helper.";
                    return false;
                }

                if (!helper.WaitForExit(
                        ForeignViiperHelperTimeoutMilliseconds))
                {
                    try { helper.Kill(entireProcessTree: true); } catch { }
                    failureMessage = "The elevated VIIPER termination helper " +
                        "timed out.";
                    return false;
                }

                if (helper.ExitCode != 0)
                {
                    failureMessage = "The elevated VIIPER termination helper " +
                        $"failed with exit code {helper.ExitCode}.";
                    return false;
                }

                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                failureMessage = "Administrator permission was canceled; " +
                    "the conflicting viiper.exe remains active.";
                return false;
            }
            catch (Exception ex)
            {
                failureMessage = "Could not run the elevated VIIPER " +
                    $"termination helper: {ex.Message}";
                return false;
            }
            finally
            {
                helper?.Dispose();
            }
        }

        public static bool TryRunForeignViiperTerminationHelper(string[] args,
            out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0 ||
                !string.Equals(args[0], TerminateForeignViiperArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (args.Length != 2)
            {
                exitCode = 87;
                return true;
            }

            string canonicalPath;
            try
            {
                canonicalPath = Encoding.UTF8.GetString(
                    Convert.FromBase64String(args[1]));
                if (!Path.IsPathFullyQualified(canonicalPath) ||
                    !string.Equals(Path.GetFileName(canonicalPath),
                        "viiper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = 87;
                    return true;
                }
            }
            catch
            {
                exitCode = 87;
                return true;
            }

            exitCode = TryTerminateForeignViiperProcesses(canonicalPath,
                out _, allowElevation: false) ? 0 : 5;
            return true;
        }

        private static string GetSetupScriptPath()
        {
            return Path.Combine(Global.exedirpath, "extras", InstallerScriptName);
        }

        private static string GetBundledViiperPath()
        {
            return Path.Combine(Global.exedirpath, "extras",
                BundledViiperName);
        }

        private static bool FilesHaveSameSha256(string installedPath,
            string bundledPath)
        {
            try
            {
                FileInfo installed = new FileInfo(installedPath);
                FileInfo bundled = new FileInfo(bundledPath);
                if (!installed.Exists || !bundled.Exists ||
                    installed.Length != bundled.Length)
                {
                    return false;
                }

                using FileStream installedStream = installed.OpenRead();
                using FileStream bundledStream = bundled.OpenRead();
                byte[] installedHash = SHA256.HashData(installedStream);
                byte[] bundledHash = SHA256.HashData(bundledStream);
                return CryptographicOperations.FixedTimeEquals(installedHash,
                    bundledHash);
            }
            catch
            {
                return false;
            }
        }

        private static string GetCanonicalUsbipPath()
        {
            return Path.Combine(GetNativeProgramFilesPath(),
                UsbipRelativePath);
        }

        private static Version TryGetUsbipVersion(string usbipPath)
        {
            if (!File.Exists(usbipPath))
            {
                return null;
            }

            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(
                    usbipPath);
                if (info.FileMajorPart < 0 || info.FileMinorPart < 0 ||
                    info.FileBuildPart < 0 || info.FilePrivatePart < 0)
                {
                    return null;
                }

                return new Version(info.FileMajorPart, info.FileMinorPart,
                    info.FileBuildPart, info.FilePrivatePart);
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsSupportedUsbipVersion(Version version)
        {
            return version != null && version == RequiredUsbipVersion;
        }

        internal static bool AreSupportedUsbipDriverHashes(string udeHash,
            string filterHash)
        {
            return string.Equals(udeHash, SupportedUsbipUdeSha256,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(filterHash, SupportedUsbipFilterSha256,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static (bool Safe, string Message)
            EvaluateUsbipDriverIntegrity()
        {
            try
            {
                if (!TryGetSystemDriverSha256(UsbipUdeServiceName,
                        out string udeHash, out string udeError))
                {
                    return (false,
                        $"usbip-win2 driver repair required: {udeError}");
                }

                if (!TryGetSystemDriverSha256(UsbipFilterServiceName,
                        out string filterHash, out string filterError))
                {
                    return (false,
                        $"usbip-win2 driver repair required: {filterError}");
                }

                if (!AreSupportedUsbipDriverHashes(udeHash, filterHash))
                {
                    return (false,
                        "Unsafe or mixed usbip-win2 driver files detected. " +
                        "Install / Repair must replace them before VIIPER " +
                        "can start.");
                }

                return (true,
                    "The loaded usbip-win2 0.9.7.7 driver files match the " +
                    "verified signed package.");
            }
            catch (Exception ex)
            {
                return (false,
                    "usbip-win2 driver integrity could not be verified: " +
                    ex.Message);
            }
        }

        private static bool TryGetSystemDriverSha256(string serviceName,
            out string sha256, out string error)
        {
            sha256 = null;
            error = null;
            string pathName = null;
            int matches = 0;

            using ManagementObjectSearcher searcher = new(
                "SELECT PathName FROM Win32_SystemDriver " +
                $"WHERE Name='{serviceName}'");
            foreach (ManagementObject driver in searcher.Get())
            {
                matches++;
                pathName = driver["PathName"] as string;
            }

            if (matches != 1)
            {
                error = matches == 0
                    ? $"the {serviceName} service is missing"
                    : $"multiple {serviceName} services were returned";
                return false;
            }

            string driverPath = ResolveSystemDriverPath(pathName);
            if (string.IsNullOrWhiteSpace(driverPath) ||
                !File.Exists(driverPath))
            {
                error = $"the active {serviceName} driver file is missing";
                return false;
            }

            using FileStream stream = new(driverPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return true;
        }

        private static string ResolveSystemDriverPath(string pathName)
        {
            if (string.IsNullOrWhiteSpace(pathName))
            {
                return null;
            }

            string path = Environment.ExpandEnvironmentVariables(
                pathName.Trim());
            if (path.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = path.IndexOf('"', 1);
                if (closingQuote <= 1)
                {
                    return null;
                }

                path = path.Substring(1, closingQuote - 1);
            }
            else
            {
                int sysExtension = path.IndexOf(".sys",
                    StringComparison.OrdinalIgnoreCase);
                if (sysExtension >= 0)
                {
                    path = path.Substring(0, sysExtension + 4);
                }
            }

            if (path.StartsWith(@"\??\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                path = path.Substring(4);
            }

            const string systemRootPrefix = @"\SystemRoot\";
            if (path.StartsWith(systemRootPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(Environment.GetEnvironmentVariable(
                        "SystemRoot") ?? @"C:\Windows",
                    path.Substring(systemRootPrefix.Length));
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(Environment.GetEnvironmentVariable(
                        "SystemRoot") ?? @"C:\Windows", path);
            }

            return Path.GetFullPath(path);
        }

        internal static bool IsUnsafeCitrixUsbMonitorState(bool installed,
            string state, int? startValue)
        {
            if (!installed)
            {
                return false;
            }

            if (string.Equals(state, "Running",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // SERVICE_DISABLED is 4. An installed monitor that is Manual or
            // Automatic can load on the next USB enumeration even when it
            // happens to be stopped at the instant we inspect it.
            return !startValue.HasValue || startValue.Value != 4;
        }

        private static bool TryGetCitrixUsbMonitorConflict(
            out string conflictMessage)
        {
            (bool conflict, string message) = citrixUsbMonitorStatus.Value;
            conflictMessage = message;
            return conflict;
        }

        private static (bool Conflict, string Message)
            EvaluateCitrixUsbMonitorConflict()
        {
            bool installed = false;
            string state = null;
            string imagePath = null;
            int? startValue = null;

            try
            {
                using RegistryKey service = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" +
                    CitrixUsbMonitorServiceName);
                if (service != null)
                {
                    installed = true;
                    imagePath = service.GetValue("ImagePath") as string;
                    object rawStart = service.GetValue("Start");
                    if (rawStart != null)
                    {
                        startValue = Convert.ToInt32(rawStart);
                    }
                }
            }
            catch
            {
                // A present service whose configuration cannot be read is
                // intentionally treated as unsafe below.
                installed = true;
            }

            try
            {
                using ManagementObjectSearcher searcher = new(
                    "SELECT State, PathName FROM Win32_SystemDriver " +
                    $"WHERE Name='{CitrixUsbMonitorServiceName}'");
                foreach (ManagementObject driver in searcher.Get())
                {
                    installed = true;
                    state = driver["State"] as string;
                    imagePath ??= driver["PathName"] as string;
                    break;
                }
            }
            catch
            {
                // Registry state is sufficient when WMI is unavailable.
            }

            if (!installed)
            {
                return (false, null);
            }

            if (!string.IsNullOrWhiteSpace(imagePath) &&
                imagePath.IndexOf(CitrixUsbMonitorImageName,
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                return (true,
                    "A ctxusbm kernel service with an unexpected driver " +
                    "image is installed. DS4Windows cannot validate this " +
                    "USB filter safely, so VIIPER remains stopped.");
            }

            if (!IsUnsafeCitrixUsbMonitorState(installed, state,
                    startValue))
            {
                return (false, null);
            }

            string conflictMessage =
                "Citrix USB Monitor (ctxusbmon.sys) is enabled. " +
                "It can crash Windows while USB/IP virtual controllers " +
                "connect or disconnect. DS4Windows has paused VIIPER for " +
                "system safety. Install / Repair can disable only Citrix " +
                "generic USB redirection; restart Windows afterward.";
            return (true, conflictMessage);
        }

        internal static bool IsSuccessfulUsbipPortProbe(int exitCode,
            string output)
        {
            if (exitCode != 0)
            {
                return false;
            }

            string diagnostic = output ?? string.Empty;
            return diagnostic.IndexOf("ABI mismatch",
                       StringComparison.OrdinalIgnoreCase) < 0 &&
                   diagnostic.IndexOf("unexpected size",
                       StringComparison.OrdinalIgnoreCase) < 0 &&
                   diagnostic.IndexOf("specified conversion is not valid",
                       StringComparison.OrdinalIgnoreCase) < 0 &&
                   diagnostic.IndexOf("invalid structure size",
                       StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool TryProbeUsbipRuntime(string usbipPath,
            out string message)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = usbipPath,
                    Arguments = "port",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using Process process = Process.Start(startInfo);
                if (process == null)
                {
                    message = "Windows did not start usbip.exe port.";
                    return false;
                }

                System.Threading.Tasks.Task<string> stdout =
                    process.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> stderr =
                    process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(UsbipProbeTimeoutMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    message = "usbip.exe port timed out; reboot or repair usbip-win2.";
                    return false;
                }

                System.Threading.Tasks.Task.WhenAll(stdout, stderr)
                    .GetAwaiter().GetResult();
                string output = string.Join(Environment.NewLine,
                    stdout.Result, stderr.Result).Trim();
                if (!IsSuccessfulUsbipPortProbe(process.ExitCode, output))
                {
                    string detail = string.IsNullOrWhiteSpace(output)
                        ? "no diagnostic output"
                        : output;
                    message = $"usbip.exe port failed (exit {process.ExitCode}): {detail}";
                    return false;
                }

                message = "usbip.exe port confirmed a compatible userspace/driver ABI.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"usbip.exe port could not run: {ex.Message}";
                return false;
            }
        }

        private static bool TryStartServerOnce(string viiperPath)
        {
            lock (serverStartLock)
            {
                if (!InspectViiperProcessOwnership(viiperPath,
                        out bool canonicalProcessRunning, out _))
                {
                    return false;
                }

                if (canonicalProcessRunning)
                {
                    return CanPingServer();
                }

                DateTime now = DateTime.UtcNow;
                if ((now - lastServerStartAttemptUtc).TotalSeconds < 3)
                {
                    return false;
                }

                lastServerStartAttemptUtc = now;
                return TryStartServer(viiperPath);
            }
        }

        private static bool TryStartServer(string viiperPath)
        {
            try
            {
                // Setup owns one verified, highest-privilege RunVIIPER task.
                // Never fall back to launching an arbitrary or unelevated
                // backend process from DS4Windows.
                if (!IsViiperStartupTaskValid(viiperPath, out _))
                {
                    return false;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory,
                        "schtasks.exe"),
                    Arguments = $"/Run /TN \"\\{ViiperStartupTaskName}\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                };
                using Process process = Process.Start(startInfo);
                if (process == null || !process.WaitForExit(5000) ||
                    process.ExitCode != 0)
                {
                    return false;
                }

                Thread.Sleep(750);
                return InspectViiperProcessOwnership(viiperPath,
                        out bool canonicalProcessRunning, out _) &&
                    canonicalProcessRunning;
            }
            catch
            {
                return false;
            }
        }

        private static bool CanPingServer()
        {
            try
            {
                using TcpClient tcp = new TcpClient
                {
                    NoDelay = true,
                    SendTimeout = 500,
                    ReceiveTimeout = 1000,
                };

                IAsyncResult result = tcp.BeginConnect(ApiHost, ApiPort, null, null);
                if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(750)))
                {
                    return false;
                }

                tcp.EndConnect(result);
                NetworkStream stream = tcp.GetStream();
                byte[] request = Encoding.UTF8.GetBytes("ping\0");
                stream.Write(request, 0, request.Length);

                byte[] buffer = new byte[256];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return false;
                }

                string response = Encoding.UTF8.GetString(buffer, 0, read);
                return response.IndexOf("VIIPER", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

    }
}
