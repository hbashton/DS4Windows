/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DS4Windows
{
    public static partial class ViiperSetupManager
    {
        private const string NativeBundleDirectoryName =
            "viiper-native-udecx";
        private const string NativeBundleLockName =
            "viiper-native-udecx.lock.json";
        private const string NativeReceiptKey =
            @"SOFTWARE\DS4Windows\NativeVIIPER";
        private const uint OwnerSecurityInformation = 0x00000001;
        private const uint GroupSecurityInformation = 0x00000002;
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;
        private const uint NativeInfiniteWait = 0xffffffff;
        private const uint NativeWaitObject0 = 0;

        private static readonly string[] NativeBundleRootEntries =
        {
            "viiper.exe",
            "ViiperUdeCtl.exe",
            "submission-manifest.json",
            "driver",
        };

        private static readonly string[] NativeBundleDriverEntries =
        {
            "ViiperUde.inf",
            "ViiperUde.sys",
            "ViiperUde.cat",
        };

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor, uint stringSDRevision,
            out IntPtr securityDescriptor, out uint securityDescriptorSize);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool SetFileSecurity(string fileName,
            uint securityInformation, IntPtr securityDescriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out NativeFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            SafeProcessHandle handle, uint milliseconds);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        private static bool LaunchNativePackageInstaller(Window owner,
            bool portableInstallation)
        {
            if (!NativeInstallerPins.TryLoad(out _))
            {
                return false;
            }
            var extras = Path.Combine(Global.exedirpath, "extras");
            if (!Directory.Exists(Path.Combine(extras,
                    NativeBundleDirectoryName)) ||
                !File.Exists(Path.Combine(extras, NativeBundleLockName)))
            {
                ShowInstallerMessage(owner,
                    "This DS4Windows package does not contain the reviewed " +
                    "native VIIPER UdeCx bundle. Extract the complete signed " +
                    "release and try again; setup does not download or " +
                    "substitute driver files.", "VIIPER native setup",
                    MessageBoxImage.Warning);
                return false;
            }
            if (Interlocked.CompareExchange(ref installerRunning, 1, 0) != 0)
            {
                ShowInstallerMessage(owner,
                    "VIIPER setup is already running. Finish it before " +
                    "starting another repair.", "VIIPER native setup",
                    MessageBoxImage.Information);
                return true;
            }

            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                string targetSid = identity.User?.Value ?? string.Empty;
                string targetName = identity.Name ?? string.Empty;
                string localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(targetSid) ||
                    string.IsNullOrWhiteSpace(targetName) ||
                    string.IsNullOrWhiteSpace(localAppData) ||
                    string.IsNullOrWhiteSpace(Global.exelocation))
                {
                    throw new InvalidOperationException(
                        "DS4Windows could not identify the interactive account.");
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Global.exelocation,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                startInfo.ArgumentList.Add(InstallerHostArgument);
                startInfo.ArgumentList.Add("--native-package");
                startInfo.ArgumentList.Add("--target-local-appdata");
                startInfo.ArgumentList.Add(localAppData);
                startInfo.ArgumentList.Add("--target-user-sid");
                startInfo.ArgumentList.Add(targetSid);
                startInfo.ArgumentList.Add("--target-user-name");
                startInfo.ArgumentList.Add(targetName);
                startInfo.ArgumentList.Add("--target-ds4windows-path");
                startInfo.ArgumentList.Add(Global.exelocation);
                startInfo.ArgumentList.Add("--package-extras");
                startInfo.ArgumentList.Add(extras);
                if (portableInstallation)
                    startInfo.ArgumentList.Add("--portable-installation");

                Process process = Process.Start(startInfo) ??
                    throw new InvalidOperationException(
                        "Windows did not start the elevated VIIPER host.");
                int completionHandled = 0;
                void Complete(object sender, EventArgs eventArgs)
                {
                    if (Interlocked.Exchange(ref completionHandled, 1) != 0)
                        return;
                    InstallerProcess_Exited(process, owner,
                        portableInstallation, localAppData);
                }
                process.Exited += Complete;
                process.EnableRaisingEvents = true;
                try
                {
                    if (process.HasExited) Complete(process, EventArgs.Empty);
                }
                catch (InvalidOperationException) { }
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Interlocked.Exchange(ref installerRunning, 0);
                ShowInstallerMessage(owner,
                    "VIIPER native setup was canceled at the administrator " +
                    "prompt. No changes were made.",
                    "VIIPER native setup canceled",
                    MessageBoxImage.Information);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref installerRunning, 0);
                ShowInstallerMessage(owner,
                    "Could not launch VIIPER native setup: " + ex.Message,
                    "VIIPER native setup", MessageBoxImage.Error);
                return false;
            }
        }

        private static int RunElevatedNativePackageInstall(
            string packageExtras, string targetUserSid,
            bool portableInstallation)
        {
            if (!NativeInstallerPins.TryLoad(out var pins))
                throw new InvalidOperationException(
                    "This DS4Windows build has no native package pins.");
            var sid = new SecurityIdentifier(targetUserSid);
            if (!string.Equals(sid.Value, targetUserSid,
                    StringComparison.Ordinal) || sid.AccountDomainSid == null)
                throw new InvalidOperationException(
                    "The native package target SID is not canonical.");

            string sourceRoot = Path.Combine(packageExtras,
                NativeBundleDirectoryName);
            string sourceLock = Path.Combine(packageExtras,
                NativeBundleLockName);
            string programFiles = GetNativeProgramFilesPath();
            EnsurePathDoesNotTraverseReparsePoints(programFiles,
                requireExisting: true);
            // Use one unguessable direct child of the OS-protected Program
            // Files root. A fixed intermediate directory could have been
            // pre-seeded with a weak DACL or retained rename handle.
            string setupDirectory = Path.Combine(programFiles,
                "DS4Windows.NativeSetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(setupDirectory);
            EnsurePathDoesNotTraverseReparsePoints(setupDirectory,
                requireExisting: true);
            ProtectNativeSetupDirectory(setupDirectory);

            DS4WinWPF.DS4Forms.ViiperSetupProgress progress = null;
            bool progressFinished = false;
            void FinishProgress(bool success)
            {
                if (progressFinished) return;
                progressFinished = true;
                try { progress?.Finish(success); } catch { }
            }

            try
            {
                progress = new DS4WinWPF.DS4Forms.ViiperSetupProgress(
                    GetInfrastructureActionsLogPath());
                progress.ShowPreparing();
                progress.SetPhase(
                    "Verifying and staging the signed native VIIPER package...");
                using NativeStagedMedia media = NativeStagedMedia.Create(
                    sourceRoot, sourceLock, setupDirectory, pins);

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = media.BrokerPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = setupDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in new[]
                {
                    "native-package-install",
                    "--package-directory", media.DriverDirectory,
                    "--submission-manifest", media.ManifestPath,
                    "--source-revision", pins.SourceRevision,
                    "--driver-helper", media.HelperPath,
                    "--expected-broker-sha256", pins.BrokerSha256,
                    "--expected-helper-sha256", pins.HelperSha256,
                    "--expected-manifest-sha256", pins.ManifestSha256,
                    "--expected-inf-sha256", pins.InfSha256,
                    "--expected-sys-sha256", pins.SysSha256,
                    "--expected-cat-sha256", pins.CatSha256,
                    "--target-user-sid", targetUserSid,
                })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using Process process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    throw new InvalidOperationException(
                        "Windows did not start the staged VIIPER broker.");
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                progress.SetPhase(
                    "Installing and authenticating the native VIIPER bus...");
                // VIIPER owns its absolute mutation and rollback deadlines.
                // Never time-limit or terminate this process after launch.
                int exitCode = WaitForNativePackageProcess(process, progress);
                WriteInstallerHostLog(
                    standardOutput.GetAwaiter().GetResult() +
                    Environment.NewLine +
                    standardError.GetAwaiter().GetResult());
                FinishProgress(exitCode == 0);
                if (exitCode == 0)
                {
                    try { CommitNativeInstallerReceipt(pins, targetUserSid); }
                    catch (Exception ex)
                    {
                        WriteInstallerHostLog(
                            "Native package committed; scheduling receipt " +
                            "write failed: " + ex);
                    }
                }
                else if (exitCode == 3010)
                {
                    MessageBox.Show(
                        "The native VIIPER transaction reached a verified " +
                        "reboot boundary. Restart Windows, then run Install / " +
                        "Repair again to complete activation.",
                        "Restart required", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    WriteInstallerHostLog(
                        "Native VIIPER transaction exited with code " +
                        exitCode + ".");
                }
                return exitCode;
            }
            catch
            {
                FinishProgress(false);
                throw;
            }
            finally
            {
                DeleteNativeSetupDirectory(setupDirectory);
            }
        }

        private static int WaitForNativePackageProcess(Process process,
            DS4WinWPF.DS4Forms.ViiperSetupProgress progress)
        {
            Task exactWait = Task.Run(() =>
            {
                while (WaitForSingleObject(process.SafeHandle,
                           NativeInfiniteWait) != NativeWaitObject0)
                {
                    Thread.Sleep(50);
                }
            });
            if (progress.Dispatcher.CheckAccess() && !exactWait.IsCompleted)
            {
                DispatcherFrame frame = new DispatcherFrame();
                exactWait.GetAwaiter().OnCompleted(() =>
                    progress.Dispatcher.BeginInvoke(new Action(() =>
                        frame.Continue = false)));
                Dispatcher.PushFrame(frame);
            }
            exactWait.GetAwaiter().GetResult();
            return process.ExitCode;
        }

        private static void DeleteNativeSetupDirectory(string path)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt != 5; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return;
                    EnsurePathDoesNotTraverseReparsePoints(path,
                        requireExisting: true);
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt != 4) Thread.Sleep(100);
                }
            }
            WriteInstallerHostLog("Protected native staging cleanup failed " +
                "for '" + path + "': " + lastError);
        }

        private static void ProtectNativeSetupDirectory(string path)
        {
            const string sddl =
                "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1,
                    out IntPtr descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not create the native staging security descriptor.");
            try
            {
                uint information = OwnerSecurityInformation |
                    GroupSecurityInformation | DaclSecurityInformation |
                    ProtectedDaclSecurityInformation;
                if (!SetFileSecurity(path, information, descriptor))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not protect the native staging directory.");
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        private static void ValidateNativeInstallerAccount(string sidValue,
            string accountName, string localAppData)
        {
            SecurityIdentifier sid = new SecurityIdentifier(sidValue);
            if (!string.Equals(sid.Value, sidValue, StringComparison.Ordinal) ||
                sid.AccountDomainSid == null)
                throw new InvalidOperationException(
                    "The native package target SID is not canonical.");

            using RegistryKey machine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey profile = machine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" +
                sid.Value);
            string profilePath = Environment.ExpandEnvironmentVariables(
                profile?.GetValue("ProfileImagePath") as string ?? string.Empty);
            string expectedLocal = string.IsNullOrWhiteSpace(profilePath)
                ? string.Empty
                : Path.Combine(profilePath, "AppData", "Local");
            string registeredName = ((NTAccount)sid.Translate(
                typeof(NTAccount))).Value;
            if (string.IsNullOrWhiteSpace(expectedLocal) ||
                !string.Equals(Path.GetFullPath(expectedLocal).TrimEnd(
                        Path.DirectorySeparatorChar),
                    Path.GetFullPath(localAppData).TrimEnd(
                        Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(registeredName, accountName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The native package target does not match its registered " +
                    "Windows account and profile.");
        }

        private static void CommitNativeInstallerReceipt(
            NativeInstallerPins pins, string targetSid)
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = machine.CreateSubKey(NativeReceiptKey,
                writable: true) ?? throw new InvalidOperationException(
                "Could not create the native VIIPER receipt.");
            key.SetValue("State", "Committing", RegistryValueKind.String);
            key.SetValue("Schema", 1, RegistryValueKind.DWord);
            key.SetValue("PackageIdentity", pins.LockSha256,
                RegistryValueKind.String);
            key.SetValue("SourceRevision", pins.SourceRevision,
                RegistryValueKind.String);
            key.SetValue("DriverPackageVersion", pins.DriverPackageVersion,
                RegistryValueKind.String);
            key.SetValue("BrokerSHA256", pins.BrokerSha256,
                RegistryValueKind.String);
            key.SetValue("HelperSHA256", pins.HelperSha256,
                RegistryValueKind.String);
            key.SetValue("ManifestSHA256", pins.ManifestSha256,
                RegistryValueKind.String);
            key.SetValue("InfSHA256", pins.InfSha256,
                RegistryValueKind.String);
            key.SetValue("SysSHA256", pins.SysSha256,
                RegistryValueKind.String);
            key.SetValue("CatSHA256", pins.CatSha256,
                RegistryValueKind.String);
            key.SetValue("DriverBuildIdentity", pins.DriverBuildIdentity,
                RegistryValueKind.String);
            key.SetValue("TargetUserSid", targetSid,
                RegistryValueKind.String);
            key.SetValue("InstalledUtc", DateTime.UtcNow.ToString("O"),
                RegistryValueKind.String);
            key.Flush();
            key.SetValue("State", "Ready", RegistryValueKind.String);
            key.Flush();
        }

        private sealed class NativeStagedMedia : IDisposable
        {
            private readonly List<FileStream> retainedFiles = new();
            internal string BrokerPath { get; private set; }
            internal string HelperPath { get; private set; }
            internal string ManifestPath { get; private set; }
            internal string DriverDirectory { get; private set; }

            internal static NativeStagedMedia Create(string sourceRoot,
                string sourceLock, string stagingRoot,
                NativeInstallerPins pins)
            {
                var media = new NativeStagedMedia();
                try
                {
                    ValidateNativeDirectory(sourceRoot,
                        NativeBundleRootEntries);
                    string sourceDriver = Path.Combine(sourceRoot, "driver");
                    ValidateNativeDirectory(sourceDriver,
                        NativeBundleDriverEntries);
                    string targetRoot = Path.Combine(stagingRoot,
                        NativeBundleDirectoryName);
                    media.DriverDirectory = Path.Combine(targetRoot, "driver");
                    Directory.CreateDirectory(media.DriverDirectory);

                    var files = new[]
                    {
                        ("viiper.exe", pins.BrokerSha256),
                        ("ViiperUdeCtl.exe", pins.HelperSha256),
                        ("submission-manifest.json", pins.ManifestSha256),
                        ("driver/ViiperUde.inf", pins.InfSha256),
                        ("driver/ViiperUde.sys", pins.SysSha256),
                        ("driver/ViiperUde.cat", pins.CatSha256),
                    };
                    foreach ((string relative, string expectedHash) in files)
                    {
                        CopyVerifiedFile(Path.Combine(sourceRoot,
                                relative.Replace('/', Path.DirectorySeparatorChar)),
                            Path.Combine(targetRoot,
                                relative.Replace('/', Path.DirectorySeparatorChar)),
                            expectedHash);
                    }
                    string targetLock = Path.Combine(stagingRoot,
                        NativeBundleLockName);
                    CopyVerifiedFile(sourceLock, targetLock, pins.LockSha256);

                    media.BrokerPath = Path.Combine(targetRoot, "viiper.exe");
                    media.HelperPath = Path.Combine(targetRoot,
                        "ViiperUdeCtl.exe");
                    media.ManifestPath = Path.Combine(targetRoot,
                        "submission-manifest.json");
                    foreach ((string relative, string expectedHash) in files)
                    {
                        media.RetainVerified(Path.Combine(targetRoot,
                                relative.Replace('/', Path.DirectorySeparatorChar)),
                            expectedHash);
                    }
                    media.RetainVerified(targetLock, pins.LockSha256);
                    ValidateNativeDirectory(targetRoot,
                        NativeBundleRootEntries);
                    ValidateNativeDirectory(media.DriverDirectory,
                        NativeBundleDriverEntries);
                    return media;
                }
                catch
                {
                    media.Dispose();
                    throw;
                }
            }

            private static void CopyVerifiedFile(string sourcePath,
                string targetPath, string expectedHash)
            {
                EnsurePathDoesNotTraverseReparsePoints(sourcePath,
                    requireExisting: true);
                using FileStream source = OpenExactFile(sourcePath);
                if (!string.Equals(HashStream(source), expectedHash,
                        StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Native package hash mismatch: " + sourcePath);
                source.Position = 0;
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using FileStream target = new FileStream(targetPath,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
                target.Flush(flushToDisk: true);
            }

            private void RetainVerified(string path, string expectedHash)
            {
                FileStream stream = OpenExactFile(path);
                try
                {
                    if (!string.Equals(HashStream(stream), expectedHash,
                            StringComparison.Ordinal))
                        throw new InvalidDataException(
                            "Staged native package hash mismatch: " + path);
                    retainedFiles.Add(stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            private static FileStream OpenExactFile(string path)
            {
                if (!File.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new FileNotFoundException(
                        "Native package file is missing or unsafe.", path);
                FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                if (!GetFileInformationByHandle(stream.SafeFileHandle,
                        out NativeFileInformation information) ||
                    information.NumberOfLinks != 1)
                {
                    stream.Dispose();
                    throw new InvalidDataException(
                        "Native package file identity is unsafe: " + path);
                }
                return stream;
            }

            private static void ValidateNativeDirectory(string path,
                IEnumerable<string> expected)
            {
                EnsurePathDoesNotTraverseReparsePoints(path,
                    requireExisting: true);
                string[] actual = Directory.EnumerateFileSystemEntries(path)
                    .Select(Path.GetFileName)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                string[] wanted = expected.OrderBy(value => value,
                    StringComparer.Ordinal).ToArray();
                if (!actual.SequenceEqual(wanted,
                        StringComparer.Ordinal))
                    throw new InvalidDataException(
                        "Native package layout is incomplete, extra, or " +
                        "case-mismatched: " + path);
            }

            public void Dispose()
            {
                for (int index = retainedFiles.Count - 1; index >= 0; index--)
                    retainedFiles[index].Dispose();
                retainedFiles.Clear();
            }
        }

        private static string HashStream(Stream stream)
        {
            using SHA256 algorithm = SHA256.Create();
            return Convert.ToHexString(algorithm.ComputeHash(stream))
                .ToLowerInvariant();
        }

        internal sealed class NativeInstallerPins
        {
            internal string SourceRevision { get; private set; }
            internal string DriverPackageVersion { get; private set; }
            internal string DriverBuildIdentity { get; private set; }
            internal string BrokerSha256 { get; private set; }
            internal string HelperSha256 { get; private set; }
            internal string ManifestSha256 { get; private set; }
            internal string InfSha256 { get; private set; }
            internal string SysSha256 { get; private set; }
            internal string CatSha256 { get; private set; }
            internal string LockSha256 { get; private set; }

            internal static bool TryLoad(out NativeInstallerPins pins)
            {
                try
                {
                    Dictionary<string, string> values = new(
                        StringComparer.Ordinal);
                    foreach (AssemblyMetadataAttribute attribute in
                             Assembly.GetExecutingAssembly()
                                 .GetCustomAttributes<AssemblyMetadataAttribute>())
                    {
                        if (!values.TryAdd(attribute.Key, attribute.Value))
                            throw new InvalidDataException();
                    }
                    pins = new NativeInstallerPins
                    {
                        SourceRevision = RequireHex(values,
                            "ViiperNativeSourceRevision", 40, 64),
                        DriverPackageVersion = RequireVersion(values,
                            "ViiperNativeDriverPackageVersion"),
                        DriverBuildIdentity = RequireHex(values,
                            "ViiperNativeDriverBuildIdentity", 64, 64),
                        BrokerSha256 = RequireHex(values,
                            "ViiperNativeBrokerSha256", 64, 64),
                        HelperSha256 = RequireHex(values,
                            "ViiperNativeHelperSha256", 64, 64),
                        ManifestSha256 = RequireHex(values,
                            "ViiperNativeManifestSha256", 64, 64),
                        InfSha256 = RequireHex(values,
                            "ViiperNativeInfSha256", 64, 64),
                        SysSha256 = RequireHex(values,
                            "ViiperNativeSysSha256", 64, 64),
                        CatSha256 = RequireHex(values,
                            "ViiperNativeCatSha256", 64, 64),
                        LockSha256 = RequireHex(values,
                            "ViiperNativeLockSha256", 64, 64),
                    };
                    return true;
                }
                catch
                {
                    pins = null;
                    return false;
                }
            }

            private static string RequireHex(
                Dictionary<string, string> values, string name,
                int minimum, int maximum)
            {
                if (!values.TryGetValue(name, out string value) ||
                    value == null || value.Length < minimum ||
                    value.Length > maximum || value.Any(character =>
                        !(character >= '0' && character <= '9') &&
                        !(character >= 'a' && character <= 'f')))
                    throw new InvalidDataException();
                return value;
            }

            private static string RequireVersion(
                Dictionary<string, string> values, string name)
            {
                if (!values.TryGetValue(name, out string value) ||
                    value == null || value.Split('.').Length != 4 ||
                    value.Split('.').Any(part =>
                        !ushort.TryParse(part, out _)))
                    throw new InvalidDataException();
                return value;
            }
        }
    }
}
