using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace DS4Windows.SetupActions
{
    internal static class Program
    {
        private const string RegistryKeyPath = @"SOFTWARE\DS4Windows";
        private const string ManagerRelativePath =
            @"extras\manage-viiper-native-package.ps1";
        private const string MetadataRelativePath =
            "ViiperNativeRuntimeMetadata.json";
        private const string NativePackageRelativePath =
            @"extras\viiper-native-package";
        private const string ManifestFileName = "package-manifest.json";
        private const string ResultPrefix =
            "DS4WINDOWS_VIIPER_NATIVE_RESULT ";
        private const string NativeReceiptValue = "NativePackageReceipt";
        private const string NativeSidValue = "NativePackageTargetUserSid";
        private const string NativeMetadataHashValue =
            "NativePackageMetadataSha256";
        private const string NativeUpdatedValue = "NativePackageUpdatedUtc";

        private static readonly object LogSync = new object();
        private static FileStream logStream;
        private static List<SafeFileHandle> logDirectoryLocks;

        private const uint OpenExisting = 3;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                InitializeProtectedLog();
                WriteLog("=== DS4Windows native setup invocation " +
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                    " ===");

                RequireElevated64BitProcess();
                if (args == null || args.Length == 0)
                {
                    throw new InvalidOperationException(
                        "A setup action is required.");
                }

                var action = args[0].ToLowerInvariant();
                if (action == "preflight")
                {
                    RequireExactArgumentCount(args, 1);
                    return RunWithSetupMutex(() =>
                    {
                        QuiesceDs4Windows();
                        return 0;
                    });
                }

                if (action != "install" && action != "repair" &&
                    action != "uninstall")
                {
                    throw new InvalidOperationException(
                        "Unknown setup action: " + args[0]);
                }

                RequireExactArgumentCount(args, 3);
                if (!string.Equals(args[1], "--target-user-sid",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The only accepted setup argument is " +
                        "--target-user-sid.");
                }
                var targetSid = ValidateTargetUserSid(args[2]);

                return RunWithSetupMutex(() =>
                    RunNativeTransaction(action, targetSid));
            }
            catch (Exception ex)
            {
                WriteLog("Setup could not finish: " + ex);
                return 1;
            }
            finally
            {
                DisposeProtectedLog();
            }
        }

        private static int RunNativeTransaction(string action,
            string targetSid)
        {
            QuiesceDs4Windows();
            var installRoot = ValidateManagedInstallRoot();
            var operation = action == "uninstall" ? "Uninstall" : "Install";
            var verified = VerifyProtectedNativeMedia(installRoot,
                operation == "Install");

            WriteLog("Invoking the manifest-bound native package manager for " +
                operation.ToLowerInvariant() + ".");
            var child = InvokeNativeManager(verified.ManagerPath, operation,
                targetSid);
            var receipt = ParseAndValidateReceipt(child.OutputLines,
                operation.ToLowerInvariant(), child.ExitCode);

            if (child.ExitCode == 0)
            {
                if (operation == "Install")
                {
                    RecordInstalledNativePackage(targetSid,
                        verified.MetadataSha256);
                }
                else
                {
                    ClearInstalledNativePackage();
                }
            }

            WriteLog("Native package manager returned " + child.ExitCode +
                "; rollbackStatus=" + receipt.RollbackStatus +
                "; manualRecoveryRequired=" +
                receipt.ManualRecoveryRequired.ToString(
                    CultureInfo.InvariantCulture).ToLowerInvariant() + ".");

            if (child.ExitCode != 0 && child.ExitCode != 3010)
            {
                throw new NativeManagerException(child.ExitCode,
                    "The native package transaction failed. " +
                    "rollbackStatus=" + receipt.RollbackStatus +
                    ", manualRecoveryRequired=" +
                    receipt.ManualRecoveryRequired.ToString(
                        CultureInfo.InvariantCulture).ToLowerInvariant() + ".");
            }
            return child.ExitCode;
        }

        private static void RequireExactArgumentCount(string[] args,
            int expected)
        {
            if (args.Length != expected)
            {
                throw new InvalidOperationException(
                    "Unexpected setup arguments were supplied.");
            }
        }

        private static void RequireElevated64BitProcess()
        {
            if (!Environment.Is64BitOperatingSystem ||
                !Environment.Is64BitProcess)
            {
                throw new InvalidOperationException(
                    "Native setup requires a 64-bit process on 64-bit Windows.");
            }

            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (!principal.IsInRole(
                        WindowsBuiltInRole.Administrator))
                {
                    throw new UnauthorizedAccessException(
                        "Native setup must be launched by the elevated " +
                        "per-machine installer engine.");
                }
            }
        }

        private static string ValidateTargetUserSid(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Regex.IsMatch(value, @"^S-\d(?:-\d+){2,14}$",
                    RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException(
                    "The target user SID is malformed.");
            }

            var sid = new SecurityIdentifier(value);
            if (!string.Equals(sid.Value, value,
                    StringComparison.OrdinalIgnoreCase) ||
                sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                sid.IsWellKnown(
                    WellKnownSidType.BuiltinAdministratorsSid) ||
                sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
                sid.IsWellKnown(WellKnownSidType.NetworkServiceSid))
            {
                throw new InvalidOperationException(
                    "The target SID must identify the interactive " +
                    "DS4Windows user.");
            }

            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var profile = machine.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\" +
                       @"ProfileList\" + sid.Value))
            {
                if (string.IsNullOrWhiteSpace(
                        profile?.GetValue("ProfileImagePath") as string))
                {
                    throw new InvalidOperationException(
                        "Windows has no registered profile for the target SID.");
                }
            }
            return sid.Value;
        }

        private static string ValidateManagedInstallRoot()
        {
            var programFiles = Path.GetFullPath(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles))
                .TrimEnd(Path.DirectorySeparatorChar);
            var installRoot = Path.GetFullPath(Path.Combine(programFiles,
                    "DS4Windows"))
                .TrimEnd(Path.DirectorySeparatorChar);

            if (!Directory.Exists(installRoot))
            {
                throw new DirectoryNotFoundException(
                    "The protected DS4Windows installation is unavailable: " +
                    installRoot);
            }
            EnsureDirectoryPathHasNoReparsePoints(installRoot);
            RequireProtectedDirectoryAcl(installRoot);
            return installRoot;
        }

        private static void RequireProtectedDirectoryAcl(string path)
        {
            RequireProtectedAcl(Directory.GetAccessControl(path,
                AccessControlSections.Owner |
                AccessControlSections.Access), path);
        }

        private static void RequireProtectedFileAcl(string path)
        {
            RequireProtectedAcl(File.GetAccessControl(path,
                AccessControlSections.Owner |
                AccessControlSections.Access), path);
        }

        private static void RequireProtectedAcl(
            FileSystemSecurity security, string path)
        {
            var trustedWriteSids = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid,
                    null).Value,
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null).Value,
                // NT SERVICE\TrustedInstaller
                "S-1-5-80-956008885-3418522649-1831038044-" +
                    "1853292631-2271478464",
            };
            // Use only atomic mutation and ACL-control bits here. Composite
            // values such as Write, Modify, and FullControl also contain
            // ordinary read/synchronize bits; intersecting those composites
            // would incorrectly reject the default Program Files
            // ReadAndExecute grants.
            const FileSystemRights mutationOrAclControlRights =
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.WriteAttributes |
                FileSystemRights.Delete |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;
            const long genericWrite = 0x40000000L;
            const long genericAll = 0x10000000L;

            var owner = ((SecurityIdentifier)security.GetOwner(
                typeof(SecurityIdentifier))).Value;
            if (!trustedWriteSids.Contains(owner))
            {
                throw new UnauthorizedAccessException(
                    "The protected installer path has an untrusted " +
                    "owner: " + path);
            }

            var rules = security.GetAccessRules(true, true,
                typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>();
            foreach (var rule in rules)
            {
                var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
                var rights = (long)rule.FileSystemRights;
                var grantsWrite =
                    (rights & (long)mutationOrAclControlRights) != 0 ||
                    (rights & genericWrite) != 0 ||
                    (rights & genericAll) != 0;
                var creatorOwnerInheritOnly =
                    string.Equals(sid,
                        new SecurityIdentifier(
                            WellKnownSidType.CreatorOwnerSid,
                            null).Value,
                        StringComparison.OrdinalIgnoreCase) &&
                    (rule.PropagationFlags &
                     PropagationFlags.InheritOnly) != 0;
                if (rule.AccessControlType == AccessControlType.Allow &&
                    grantsWrite && !trustedWriteSids.Contains(sid) &&
                    !creatorOwnerInheritOnly)
                {
                    throw new UnauthorizedAccessException(
                        "The managed Program Files directory grants " +
                        "write access outside the trusted installer " +
                        "principals: " + path);
                }
            }
        }

        private static VerifiedMedia VerifyProtectedNativeMedia(
            string installRoot, bool requireCompleteInstallMedia)
        {
            var manifestPath = Path.Combine(installRoot, ManifestFileName);
            RequireOrdinaryFile(manifestPath);
            var serializer = new JavaScriptSerializer
                { MaxJsonLength = 16 * 1024 * 1024 };
            PackageManifest manifest;
            try
            {
                manifest = serializer.Deserialize<PackageManifest>(
                    File.ReadAllText(manifestPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "The installed package manifest is malformed.", ex);
            }

            if (manifest == null || manifest.schema != 1 ||
                !string.Equals(manifest.product, "DS4Windows",
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.architecture, "x64",
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(manifest.version) ||
                manifest.files == null || manifest.files.Count == 0)
            {
                throw new InvalidDataException(
                    "The installed package manifest contract is invalid.");
            }

            var entries = new Dictionary<string, ManifestFile>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.files)
            {
                if (entry == null || entry.size < 0 ||
                    !Regex.IsMatch(entry.sha256 ?? string.Empty,
                        "^[0-9A-Fa-f]{64}$",
                        RegexOptions.CultureInvariant))
                {
                    throw new InvalidDataException(
                        "The installed package manifest contains an " +
                        "invalid file record.");
                }
                var relative = ValidateRelativeManifestPath(entry.path);
                if (entries.ContainsKey(relative))
                {
                    throw new InvalidDataException(
                        "The installed package manifest contains a " +
                        "case-insensitive duplicate path: " + relative);
                }
                entries.Add(relative, entry);
            }

            var required = new[]
            {
                ManagerRelativePath,
                MetadataRelativePath,
            };
            foreach (var relative in required)
            {
                VerifyManifestEntry(installRoot, relative, entries);
            }
            if (requireCompleteInstallMedia)
            {
                VerifyManifestEntry(installRoot, "DS4Windows.exe", entries);
            }

            var packageRoot = Path.Combine(installRoot,
                NativePackageRelativePath);
            if (!Directory.Exists(packageRoot))
            {
                throw new DirectoryNotFoundException(
                    "The installed native package tree is missing.");
            }
            EnsureDirectoryPathHasNoReparsePoints(packageRoot);
            RequireProtectedDirectoryAcl(packageRoot);

            var actualPackageFiles = EnumerateOrdinaryFiles(packageRoot)
                .Select(path => RelativePath(installRoot, path))
                .OrderBy(path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            var manifestPackageFiles = entries.Keys
                .Where(path => path.StartsWith(
                    NativePackageRelativePath +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            var manifestPackageSet = new HashSet<string>(
                manifestPackageFiles, StringComparer.OrdinalIgnoreCase);
            if (actualPackageFiles.Any(path =>
                    !manifestPackageSet.Contains(path)))
            {
                throw new InvalidDataException(
                    "The installed native package contains an unbound file.");
            }

            if (requireCompleteInstallMedia)
            {
                if (actualPackageFiles.Count == 0)
                {
                    throw new InvalidDataException(
                        "The installed native package tree is empty.");
                }
                if (!actualPackageFiles.SequenceEqual(manifestPackageFiles,
                        StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The installed native package inventory does not " +
                        "match the signed MSI manifest.");
                }
                foreach (var relative in manifestPackageFiles)
                {
                    VerifyManifestEntry(installRoot, relative, entries);
                }
            }

            var managerPath = Path.Combine(installRoot,
                ManagerRelativePath);
            var metadataPath = Path.Combine(installRoot,
                MetadataRelativePath);
            return new VerifiedMedia(managerPath,
                ComputeSha256(metadataPath));
        }

        private static string ValidateRelativeManifestPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 ||
                path.StartsWith("/", StringComparison.Ordinal) ||
                path.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The package manifest contains an unsafe path.");
            }
            var components = path.Split('/');
            if (components.Any(component =>
                    string.IsNullOrWhiteSpace(component) ||
                    component == "." || component == ".."))
            {
                throw new InvalidDataException(
                    "The package manifest contains an unsafe path.");
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(),
                components);
        }

        private static void VerifyManifestEntry(string installRoot,
            string relativePath,
            IDictionary<string, ManifestFile> entries)
        {
            var normalized = relativePath.Replace('/',
                Path.DirectorySeparatorChar);
            if (!entries.TryGetValue(normalized, out var entry))
            {
                throw new InvalidDataException(
                    "The signed MSI manifest does not bind " +
                    relativePath + ".");
            }

            var fullPath = Path.GetFullPath(Path.Combine(installRoot,
                normalized));
            var prefix = installRoot.TrimEnd(
                    Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A package manifest path escapes Program Files.");
            }
            RequireOrdinaryFile(fullPath);
            var info = new FileInfo(fullPath);
            if (info.Length != entry.size ||
                !string.Equals(ComputeSha256(fullPath), entry.sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Installed package media failed its signed hash " +
                    "contract: " + relativePath);
            }
        }

        private static IList<string> EnumerateOrdinaryFiles(string root)
        {
            var files = new List<string>();
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if ((directory.Attributes &
                     FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The native package contains a reparse-point " +
                        "directory: " + directory.FullName);
                }
                RequireProtectedDirectoryAcl(directory.FullName);
                foreach (var entry in directory.GetFileSystemInfos())
                {
                    if ((entry.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            "The native package contains a reparse point: " +
                            entry.FullName);
                    }
                    var childDirectory = entry as DirectoryInfo;
                    if (childDirectory != null)
                    {
                        pending.Push(childDirectory);
                    }
                    else
                    {
                        RequireOrdinaryFile(entry.FullName);
                        files.Add(entry.FullName);
                    }
                }
            }
            return files;
        }

        private static string RelativePath(string root, string path)
        {
            var prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "An installed file escapes the managed root.");
            }
            return fullPath.Substring(prefix.Length);
        }

        private static ChildResult InvokeNativeManager(string managerPath,
            string operation, string targetSid)
        {
            var powerShell = Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");
            RequireOrdinaryFile(powerShell);

            var arguments =
                "-NoLogo -NoProfile -NonInteractive " +
                "-ExecutionPolicy Bypass -File " + Quote(managerPath) +
                " -Operation " + operation +
                " -TargetUserSID " + Quote(targetSid);
            var lines = new List<string>();
            var outputSync = new object();

            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(powerShell,
                    arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(managerPath),
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (outputSync) lines.Add(e.Data);
                    WriteLog("[manager:stdout] " + e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (outputSync) lines.Add(e.Data);
                    WriteLog("[manager:stderr] " + e.Data);
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "PowerShell could not start the native manager.");
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                lock (outputSync)
                {
                    return new ChildResult(process.ExitCode,
                        new List<string>(lines));
                }
            }
        }

        private static NativeReceipt ParseAndValidateReceipt(
            IEnumerable<string> lines, string expectedOperation,
            int actualExitCode)
        {
            var records = lines
                .Where(line => line != null &&
                    line.StartsWith(ResultPrefix,
                        StringComparison.Ordinal))
                .Select(line => line.Substring(ResultPrefix.Length))
                .ToList();
            if (records.Count != 1)
            {
                throw new InvalidDataException(
                    "The native manager emitted " + records.Count +
                    " structured result records; exactly one is required.");
            }

            var match = Regex.Match(records[0],
                "^\\{\"schemaVersion\":1,\"operation\":\"" +
                "(install|uninstall)\",\"exitCode\":([0-9]+)," +
                "\"succeeded\":(true|false)," +
                "\"rebootRequired\":(true|false)," +
                "\"rollbackStatus\":\"([a-z-]+)\"," +
                "\"manualRecoveryRequired\":(true|false)\\}$",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new InvalidDataException(
                    "The native manager result record is malformed or " +
                    "contains an unexpected schema.");
            }

            var receipt = new NativeReceipt
            {
                Operation = match.Groups[1].Value,
                ExitCode = int.Parse(match.Groups[2].Value,
                    CultureInfo.InvariantCulture),
                Succeeded = bool.Parse(match.Groups[3].Value),
                RebootRequired = bool.Parse(match.Groups[4].Value),
                RollbackStatus = match.Groups[5].Value,
                ManualRecoveryRequired =
                    bool.Parse(match.Groups[6].Value),
            };
            if (!string.Equals(receipt.Operation, expectedOperation,
                    StringComparison.Ordinal) ||
                receipt.ExitCode != actualExitCode)
            {
                throw new InvalidDataException(
                    "The native manager result does not match the " +
                    "requested operation or process exit code.");
            }

            if (actualExitCode == 0)
            {
                if (!receipt.Succeeded || receipt.RebootRequired ||
                    receipt.ManualRecoveryRequired ||
                    receipt.RollbackStatus != "not-required")
                {
                    throw new InvalidDataException(
                        "The success receipt is internally inconsistent.");
                }
            }
            else if (actualExitCode == 3010)
            {
                if (receipt.Succeeded || !receipt.RebootRequired ||
                    receipt.ManualRecoveryRequired ||
                    receipt.RollbackStatus != "safely-settled")
                {
                    throw new InvalidDataException(
                        "The reboot receipt is internally inconsistent.");
                }
            }
            else if (receipt.Succeeded || receipt.RebootRequired ||
                (receipt.RollbackStatus == "not-started" &&
                 receipt.ManualRecoveryRequired) ||
                (receipt.RollbackStatus ==
                     "unverified-see-transaction-log" &&
                 !receipt.ManualRecoveryRequired) ||
                (receipt.RollbackStatus != "not-started" &&
                 receipt.RollbackStatus !=
                     "unverified-see-transaction-log"))
            {
                throw new InvalidDataException(
                    "The failure receipt is internally inconsistent.");
            }
            return receipt;
        }

        private static void RecordInstalledNativePackage(string targetSid,
            string metadataHash)
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine,
                       RegistryView.Registry64))
            using (var key = machine.CreateSubKey(RegistryKeyPath,
                       writable: true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException(
                        "The installer coordination registry key is " +
                        "unavailable.");
                }
                key.SetValue(NativeReceiptValue, "Installed",
                    RegistryValueKind.String);
                key.SetValue(NativeSidValue, targetSid,
                    RegistryValueKind.String);
                key.SetValue(NativeMetadataHashValue, metadataHash,
                    RegistryValueKind.String);
                key.SetValue(NativeUpdatedValue,
                    DateTime.UtcNow.ToString("O",
                        CultureInfo.InvariantCulture),
                    RegistryValueKind.String);
            }
        }

        private static void ClearInstalledNativePackage()
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine,
                       RegistryView.Registry64))
            using (var key = machine.OpenSubKey(RegistryKeyPath,
                       writable: true))
            {
                key?.DeleteValue(NativeReceiptValue, false);
                key?.DeleteValue(NativeSidValue, false);
                key?.DeleteValue(NativeMetadataHashValue, false);
                key?.DeleteValue(NativeUpdatedValue, false);
            }
        }

        private static int RunWithSetupMutex(Func<int> action)
        {
            using (var setupMutex = new Mutex(false,
                       @"Global\DS4Windows-VIIPER-Native-Setup"))
            {
                var owned = false;
                try
                {
                    try
                    {
                        owned = setupMutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }
                    if (!owned)
                    {
                        WriteLog("Another native setup transaction owns " +
                            "the global mutex.");
                        return 1618;
                    }
                    return action();
                }
                finally
                {
                    if (owned)
                    {
                        try { setupMutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        private static void QuiesceDs4Windows()
        {
            var expectedPath = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                "DS4Windows", "DS4Windows.exe"));
            var blocked = new List<string>();
            foreach (var process in Process.GetProcessesByName(
                         "DS4Windows"))
            {
                try
                {
                    string path;
                    try
                    {
                        path = Path.GetFullPath(
                            process.MainModule?.FileName ??
                            string.Empty);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "Could not authenticate a running " +
                            "DS4Windows process before setup.", ex);
                    }

                    if (process.CloseMainWindow() &&
                        process.WaitForExit(5000))
                    {
                        continue;
                    }
                    if (string.Equals(path, expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000))
                        {
                            blocked.Add(path);
                        }
                    }
                    else
                    {
                        blocked.Add(path);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
            if (blocked.Count != 0)
            {
                throw new InvalidOperationException(
                    "Close every portable or managed DS4Windows process " +
                    "before continuing. Still running: " +
                    string.Join(", ", blocked.Distinct(
                        StringComparer.OrdinalIgnoreCase)));
            }
        }

        private static void EnsureDirectoryPathHasNoReparsePoints(
            string path)
        {
            var resolved = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(resolved);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException(
                    "A rooted directory path is required.");
            }
            var cursor = root;
            var relative = resolved.Substring(root.Length);
            foreach (var component in relative.Split(
                         new[]
                         {
                             Path.DirectorySeparatorChar,
                             Path.AltDirectorySeparatorChar,
                         },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, component);
                if (!Directory.Exists(cursor) &&
                    !File.Exists(cursor))
                {
                    continue;
                }
                var attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                {
                    throw new InvalidOperationException(
                        "A protected path traverses a reparse point or " +
                        "ordinary file: " + cursor);
                }
            }
        }

        private static void RequireOrdinaryFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "A rooted file path is required.");
            }
            EnsureDirectoryPathHasNoReparsePoints(directory);
            if (!File.Exists(fullPath) ||
                (File.GetAttributes(fullPath) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new FileNotFoundException(
                    "A required ordinary file is unavailable.", fullPath);
            }
            RequireProtectedFileAcl(fullPath);
        }

        private static string ComputeSha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open,
                       FileAccess.Read, FileShare.Read))
            {
                return BitConverter.ToString(
                        algorithm.ComputeHash(stream))
                    .Replace("-", string.Empty);
            }
        }

        private static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0)
            {
                throw new InvalidOperationException(
                    "A process argument is invalid.");
            }
            if (value.IndexOf('"') >= 0)
            {
                throw new InvalidOperationException(
                    "A process argument contains a quote.");
            }
            return "\"" + value + "\"";
        }

        private static void InitializeProtectedLog()
        {
            var programData = Path.GetFullPath(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData))
                .TrimEnd(Path.DirectorySeparatorChar);
            EnsureDirectoryPathHasNoReparsePoints(programData);

            var directorySecurity = CreateProtectedDirectorySecurity();
            var fileSecurity = CreateProtectedFileSecurity();
            var acquiredDirectoryLocks = new List<SafeFileHandle>();
            FileStream acquiredLogStream = null;
            try
            {
                acquiredDirectoryLocks.Add(
                    OpenAndValidateOrdinaryDirectory(programData));
                var productDirectory = Path.Combine(programData,
                    "DS4Windows");
                acquiredDirectoryLocks.Add(
                    CreateOrLockProtectedDirectory(productDirectory,
                        directorySecurity));
                var installerDirectory = Path.Combine(productDirectory,
                    "Installer");
                acquiredDirectoryLocks.Add(
                    CreateOrLockProtectedDirectory(installerDirectory,
                        directorySecurity));

                acquiredLogStream = OpenOrCreateProtectedLogFile(
                    Path.Combine(installerDirectory,
                        "setup-actions.log"), fileSecurity);
                logDirectoryLocks = acquiredDirectoryLocks;
                logStream = acquiredLogStream;
                acquiredLogStream = null;
                acquiredDirectoryLocks = null;
            }
            finally
            {
                acquiredLogStream?.Dispose();
                if (acquiredDirectoryLocks != null)
                {
                    for (var index = acquiredDirectoryLocks.Count - 1;
                         index >= 0; index--)
                    {
                        acquiredDirectoryLocks[index].Dispose();
                    }
                }
            }
        }

        private static DirectorySecurity CreateProtectedDirectorySecurity()
        {
            var security = new DirectorySecurity();
            ConfigureProtectedSecurity(security,
                InheritanceFlags.ContainerInherit |
                InheritanceFlags.ObjectInherit);
            return security;
        }

        private static FileSecurity CreateProtectedFileSecurity()
        {
            var security = new FileSecurity();
            ConfigureProtectedSecurity(security, InheritanceFlags.None);
            return security;
        }

        private static void ConfigureProtectedSecurity(
            FileSystemSecurity security,
            InheritanceFlags inheritance)
        {
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid, null);
            security.SetOwner(administrators);
            security.SetGroup(administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { administrators, system })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid, FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None,
                    AccessControlType.Allow));
            }
        }

        private static SafeFileHandle OpenAndValidateOrdinaryDirectory(
            string path)
        {
            var handle = OpenDirectoryWithoutDeleteSharing(path);
            try
            {
                RequireOrdinaryDirectory(path);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static SafeFileHandle CreateOrLockProtectedDirectory(
            string path, DirectorySecurity expectedSecurity)
        {
            if (!PathEntryExists(path))
            {
                // The ACL is supplied to CreateDirectory itself. Never create
                // an inherited directory and tighten it afterward.
                Directory.CreateDirectory(path, expectedSecurity);
            }

            var handle = OpenDirectoryWithoutDeleteSharing(path);
            try
            {
                RequireOrdinaryDirectory(path);
                var actualSecurity = Directory.GetAccessControl(path,
                    AccessControlSections.Owner |
                    AccessControlSections.Group |
                    AccessControlSections.Access);
                RequireExactLogSecurity(actualSecurity, path,
                    InheritanceFlags.ContainerInherit |
                    InheritanceFlags.ObjectInherit);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static SafeFileHandle OpenDirectoryWithoutDeleteSharing(
            string path)
        {
            var handle = CreateFileW(path, FileListDirectory,
                FileShare.Read | FileShare.Write, IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error,
                    "Could not lock the protected setup-log directory: " +
                    path);
            }
            return handle;
        }

        private static FileStream OpenOrCreateProtectedLogFile(
            string path, FileSecurity expectedSecurity)
        {
            FileStream stream = null;
            SafeFileHandle existingHandle = null;
            try
            {
                if (PathEntryExists(path))
                {
                    existingHandle = CreateFileW(path,
                        (uint)(FileSystemRights.Read |
                               FileSystemRights.Write),
                        FileShare.Read, IntPtr.Zero, OpenExisting,
                        FileFlagOpenReparsePoint, IntPtr.Zero);
                    if (existingHandle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new Win32Exception(error,
                            "Could not exclusively lock the existing " +
                            "setup log.");
                    }
                    stream = new FileStream(existingHandle,
                        FileAccess.ReadWrite, 4096, false);
                    existingHandle = null;
                }
                else
                {
                    stream = new FileStream(path, FileMode.CreateNew,
                        FileSystemRights.Read | FileSystemRights.Write,
                        FileShare.Read, 4096, FileOptions.WriteThrough,
                        expectedSecurity);
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "The setup log is not an ordinary file.");
                }
                var actualSecurity = stream.GetAccessControl();
                RequireExactLogSecurity(actualSecurity, path,
                    InheritanceFlags.None);

                // Truncate only through the verified, no-share-delete handle;
                // never delete and recreate a path that can be swapped.
                stream.SetLength(0);
                stream.Position = 0;
                stream.Flush(true);
                var result = stream;
                stream = null;
                return result;
            }
            finally
            {
                stream?.Dispose();
                existingHandle?.Dispose();
            }
        }

        private static void RequireExactLogSecurity(
            FileSystemSecurity security, string path,
            InheritanceFlags expectedInheritance)
        {
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid, null);
            var expectedSids = new HashSet<string>(
                StringComparer.Ordinal)
            {
                administrators.Value,
                system.Value,
            };
            var owner = ((SecurityIdentifier)security.GetOwner(
                typeof(SecurityIdentifier))).Value;
            var group = ((SecurityIdentifier)security.GetGroup(
                typeof(SecurityIdentifier))).Value;
            if (!string.Equals(owner, administrators.Value,
                    StringComparison.Ordinal) ||
                !string.Equals(group, administrators.Value,
                    StringComparison.Ordinal) ||
                !security.AreAccessRulesProtected)
            {
                throw new UnauthorizedAccessException(
                    "The setup-log path has an unexpected owner, group, " +
                    "or inherited DACL: " + path);
            }

            var rules = security.GetAccessRules(true, true,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();
            if (rules.Count != 2)
            {
                throw new UnauthorizedAccessException(
                    "The setup-log path has an unexpected access-rule " +
                    "count: " + path);
            }
            foreach (var rule in rules)
            {
                var sid = ((SecurityIdentifier)
                    rule.IdentityReference).Value;
                if (!expectedSids.Remove(sid) || rule.IsInherited ||
                    rule.AccessControlType != AccessControlType.Allow ||
                    rule.FileSystemRights != FileSystemRights.FullControl ||
                    rule.InheritanceFlags != expectedInheritance ||
                    rule.PropagationFlags != PropagationFlags.None)
                {
                    throw new UnauthorizedAccessException(
                        "The setup-log path has an unexpected access " +
                        "rule: " + path);
                }
            }
            if (expectedSids.Count != 0)
            {
                throw new UnauthorizedAccessException(
                    "The setup-log path is missing a trusted principal: " +
                    path);
            }
        }

        private static void RequireOrdinaryDirectory(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The setup-log path is not an ordinary directory: " +
                    path);
            }
        }

        private static bool PathEntryExists(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static void WriteLog(string message)
        {
            try
            {
                lock (LogSync)
                {
                    if (logStream == null) return;
                    var bytes = new UTF8Encoding(false).GetBytes(
                        DateTime.UtcNow.ToString("O",
                            CultureInfo.InvariantCulture) + " " +
                        message + Environment.NewLine);
                    logStream.Write(bytes, 0, bytes.Length);
                    logStream.Flush(true);
                }
            }
            catch { }
        }

        private static void DisposeProtectedLog()
        {
            lock (LogSync)
            {
                try
                {
                    logStream?.Flush(true);
                }
                catch { }
                try
                {
                    logStream?.Dispose();
                }
                catch { }
                logStream = null;
                if (logDirectoryLocks != null)
                {
                    for (var index = logDirectoryLocks.Count - 1;
                         index >= 0; index--)
                    {
                        try { logDirectoryLocks[index].Dispose(); }
                        catch { }
                    }
                    logDirectoryLocks = null;
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        private sealed class PackageManifest
        {
            public int schema { get; set; }
            public string product { get; set; }
            public string version { get; set; }
            public string architecture { get; set; }
            public List<ManifestFile> files { get; set; }
        }

        private sealed class ManifestFile
        {
            public string path { get; set; }
            public long size { get; set; }
            public string sha256 { get; set; }
        }

        private sealed class VerifiedMedia
        {
            internal VerifiedMedia(string managerPath,
                string metadataSha256)
            {
                ManagerPath = managerPath;
                MetadataSha256 = metadataSha256;
            }

            internal string ManagerPath { get; }
            internal string MetadataSha256 { get; }
        }

        private sealed class ChildResult
        {
            internal ChildResult(int exitCode,
                IList<string> outputLines)
            {
                ExitCode = exitCode;
                OutputLines = outputLines;
            }

            internal int ExitCode { get; }
            internal IList<string> OutputLines { get; }
        }

        private sealed class NativeReceipt
        {
            internal string Operation { get; set; }
            internal int ExitCode { get; set; }
            internal bool Succeeded { get; set; }
            internal bool RebootRequired { get; set; }
            internal string RollbackStatus { get; set; }
            internal bool ManualRecoveryRequired { get; set; }
        }

        private sealed class NativeManagerException : Exception
        {
            internal NativeManagerException(int exitCode,
                string message) : base(message)
            {
                ExitCode = exitCode;
            }

            internal int ExitCode { get; }
        }
    }
}
