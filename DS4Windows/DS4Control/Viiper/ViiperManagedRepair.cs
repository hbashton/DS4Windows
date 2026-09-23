/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows;

// Broker-only maintenance. No MSI, tasks, configuration, keys, profiles,
// migrations, mapper exit, download, or broker restart occurs in this helper.
internal static class ViiperManagedRepair
{
    internal const string HelperArgument = "--repair-installed-viiper";
    internal static readonly TimeSpan HelperDeadline = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ParentDeadline = TimeSpan.FromSeconds(45);
    internal const int MaximumSnapshotBytes = 16 * 1024;

    internal static async Task RepairAsync(string payloadPath, CancellationToken cancellationToken = default,
        IReadOnlyList<PortableBrokerProcessIdentity> capturedProcesses = null)
    {
        RejectLab();
        cancellationToken.ThrowIfCancellationRequested();
        // Capture once before UAC. A later broker is not implicitly authorized
        // by the original repair action, even if it reuses the same PID/path.
        PortableBrokerProcessIdentity[] captured = FreezeSnapshot(capturedProcesses ??
            PortableBrokerProcessHost.CaptureForRepair());
        if (IsAdministrator())
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(HelperDeadline);
            await RepairCapturedCoreAsync(payloadPath, ViiperSetupManager.GetCanonicalViiperExePath(),
                ViiperSetupManager.SupportedViiperSha256, true, captured, deadline.Token).ConfigureAwait(false);
            return;
        }

        // The source lease is held by our caller until this helper exits.
        // Validate before UAC as well as independently inside the elevated helper.
        using FileStream source = OpenVerifiedPayload(payloadPath, ViiperSetupManager.SupportedViiperSha256);
        string executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !Path.GetFileName(executable).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Managed VIIPER repair requires the DS4Windows executable, not a runtime host.");
        PortableLabContext.ValidateNoReparsePoints(executable);
        using var executablePin = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)
        };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(Path.GetFullPath(payloadPath));
        string encodedSnapshot = EncodeSnapshot(captured);
        if (executable.Length + Path.GetFullPath(payloadPath).Length + encodedSnapshot.Length + 128 > 30_000)
            throw new IOException("The verified repair identity exceeds the safe Windows command-line limit.");
        start.ArgumentList.Add(encodedSnapshot);
        Process helper;
        try { helper = Process.Start(start) ?? throw new IOException("Windows did not start the broker repair helper."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new IOException("VIIPER repair was cancelled at the Windows administrator prompt.", error); }
        using (helper)
        {
            try
            {
                // Once elevated work starts, caller cancellation must not let
                // the UI assume completion while the helper is still committing.
                await helper.WaitForExitAsync().WaitAsync(ParentDeadline).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new IOException("The elevated VIIPER repair helper did not finish within 45 seconds. Its state is unknown; do not restart the broker until that helper has exited.", error);
            }
            if (helper.ExitCode != 0)
                throw new IOException("The elevated VIIPER repair helper rejected or failed the repair (exit code " + helper.ExitCode + "). The broker was not restarted.");
        }
    }

    internal static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args == null || !args.Contains(HelperArgument, StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            RejectLab();
            if (args.Length != 3 || !string.Equals(args[0], HelperArgument, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid managed broker repair arguments.");
            PortableBrokerProcessIdentity[] captured = DecodeSnapshot(args[2]);
            if (!IsAdministrator()) throw new UnauthorizedAccessException("Broker repair helper requires administrator rights.");
            using var deadline = new CancellationTokenSource(HelperDeadline);
            RepairCapturedCoreAsync(args[1], ViiperSetupManager.GetCanonicalViiperExePath(),
                ViiperSetupManager.SupportedViiperSha256, true, captured, deadline.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { exitCode = 3; }
        catch (UnauthorizedAccessException) { exitCode = 5; }
        catch { exitCode = 1; }
        return true;
    }

    internal static Task RepairCapturedCoreAsync(string payloadPath, string destination,
        string expectedSha256, bool administrator, IReadOnlyList<PortableBrokerProcessIdentity> capturedProcesses,
        CancellationToken token = default, IPortableBrokerProcessHost processHost = null,
        IPortableBrokerRepairCommitter committer = null)
    {
        PortableBrokerProcessIdentity[] captured = FreezeSnapshot(capturedProcesses);
        return RepairCoreAsync(payloadPath, destination, expectedSha256, administrator,
            _ => PortableBrokerProcessHost.StopCapturedForRepair(captured, processHost: processHost), token, committer);
    }

    internal static string EncodeSnapshot(IReadOnlyList<PortableBrokerProcessIdentity> capturedProcesses)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(FreezeSnapshot(capturedProcesses));
        if (bytes.Length > MaximumSnapshotBytes) throw new InvalidDataException("The repair process snapshot is too large.");
        return Convert.ToBase64String(bytes);
    }

    internal static PortableBrokerProcessIdentity[] DecodeSnapshot(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > ((MaximumSnapshotBytes + 2) / 3) * 4)
            throw new InvalidDataException("Invalid repair process snapshot size.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("Invalid repair process snapshot encoding.", error); }
        if (bytes.Length > MaximumSnapshotBytes) throw new InvalidDataException("The repair process snapshot is too large.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 256)
                throw new InvalidDataException("Invalid repair process snapshot shape.");
            var rows = new List<PortableBrokerProcessIdentity>();
            foreach (JsonElement row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid process identity.");
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in row.EnumerateObject())
                    if (!fields.Add(property.Name) || property.Name is not ("ProcessId" or "StartTimeUtcTicks" or "ExecutablePath"))
                        throw new InvalidDataException("Duplicate or unknown process identity field.");
                if (fields.Count != 3) throw new InvalidDataException("Incomplete process identity.");
                rows.Add(new(row.GetProperty("ProcessId").GetInt32(), row.GetProperty("StartTimeUtcTicks").GetInt64(),
                    row.GetProperty("ExecutablePath").GetString()));
            }
            return FreezeSnapshot(rows);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid repair process snapshot.", error); }
    }

    private static PortableBrokerProcessIdentity[] FreezeSnapshot(IReadOnlyList<PortableBrokerProcessIdentity> rows)
    {
        if (rows == null || rows.Count > 256) throw new InvalidDataException("A bounded process snapshot is required.");
        var frozen = new PortableBrokerProcessIdentity[rows.Count];
        var pids = new HashSet<int>();
        for (int index = 0; index < frozen.Length; index++)
        {
            PortableBrokerProcessIdentity row = rows[index];
            if (row.ProcessId <= 0 || row.StartTimeUtcTicks <= 0 || row.StartTimeUtcTicks > DateTime.MaxValue.Ticks ||
                string.IsNullOrWhiteSpace(row.ExecutablePath) || !Path.IsPathFullyQualified(row.ExecutablePath) ||
                row.ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(row.ExecutablePath), "viiper.exe", StringComparison.OrdinalIgnoreCase) ||
                !pids.Add(row.ProcessId))
                throw new InvalidDataException("Only complete, distinct VIIPER process identities can be authorized for repair.");
            frozen[index] = row with { ExecutablePath = Path.GetFullPath(row.ExecutablePath) };
        }
        return frozen;
    }

    // Explicit offline seam: production wrappers never accept another target,
    // hash or authority. Tests use only their own temporary directory and fake stop.
    internal static async Task RepairCoreAsync(string payloadPath, string destination,
        string expectedSha256, bool administrator, Action<string> stopSelected,
        CancellationToken token = default, IPortableBrokerRepairCommitter committer = null)
    {
        RejectLab();
        token.ThrowIfCancellationRequested();
        if (!administrator) throw new UnauthorizedAccessException("Managed broker repair requires administrator rights.");
        ArgumentNullException.ThrowIfNull(stopSelected);
        string target = LocalPath(destination);
        if (!string.Equals(Path.GetFileName(target), "viiper.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the broker executable can be replaced.", nameof(destination));
        string directory = Path.GetDirectoryName(target);
        using FileStream source = OpenVerifiedPayload(payloadPath, expectedSha256);
        // Only the broker subdirectory of an existing installation may be
        // created; this cannot create or migrate the DS4Windows application.
        using var parentPins = Directory.Exists(directory) ? null : DirectoryPins.Acquire(Path.GetDirectoryName(directory));
        if (!Directory.Exists(directory))
        {
            if (!string.Equals(Path.GetFileName(directory), "VIIPER", StringComparison.OrdinalIgnoreCase))
                throw new DirectoryNotFoundException("The managed DS4Windows installation is missing. Run full setup first.");
            PortableLabContext.ValidateNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
        }
        using var directories = DirectoryPins.Acquire(directory);
        PortableLabContext.ValidateNoReparsePoints(target);
        string originalHash = ExistingHash(target);

        string mutexPath = Path.Combine(directory, ".viiper-managed-repair.lock");
        PortableLabContext.ValidateNoReparsePoints(mutexPath);
        using var mutex = new FileStream(mutexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ExistingHash(target) != originalHash) throw new IOException("The installed broker changed before repair began.");
        if (string.Equals(originalHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            stopSelected(target);
            return;
        }
        string stage = Path.Combine(directory, ".viiper-managed-repair-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = Path.Combine(directory, ".viiper-managed-repair-" + Guid.NewGuid().ToString("N") + ".bak");
        bool ownsStage = false, committed = false;
        IPortableBrokerRepairCommitter files = committer ?? new PortableBrokerRepairCommitter();
        try
        {
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.Asynchronous))
            {
                ownsStage = true;
                source.Position = 0;
                await source.CopyToAsync(output, 64 * 1024, token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            using (OpenVerifiedPayload(stage, expectedSha256)) { }
            token.ThrowIfCancellationRequested();
            if (ExistingHash(target) != originalHash) throw new IOException("The installed broker changed while its replacement was prepared.");
            stopSelected(target);
            token.ThrowIfCancellationRequested();
            PortableLabContext.ValidateNoReparsePoints(target);
            PortableLabContext.ValidateNoReparsePoints(stage);
            if (ExistingHash(target) != originalHash) throw new IOException("The installed broker changed while its process was stopped.");
            // No cancellation inside atomic commit/verification/rollback.
            try
            {
                if (originalHash != null) files.Replace(stage, target, backup);
                else files.Move(stage, target);
                using (OpenVerifiedPayload(target, expectedSha256)) { }
                committed = true;
            }
            catch
            {
                RollBack(files, target, stage, backup, originalHash, expectedSha256);
                throw;
            }
        }
        finally
        {
            if (ownsStage) TryDelete(stage);
            if (committed) TryDelete(backup);
        }
    }

    private static void RollBack(IPortableBrokerRepairCommitter files, string target,
        string stage, string backup, string originalHash, string expectedHash)
    {
        if (originalHash != null && File.Exists(backup))
        {
            if (ExistingHash(backup) != originalHash)
                throw new IOException("Managed repair rollback image changed. Preserve the .bak file for recovery.");
            string current = ExistingHash(target);
            if (current != null && !string.Equals(current, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Managed repair destination changed unexpectedly. Preserve the .bak file for recovery.");
            if (current == null) files.Move(backup, target);
            else files.Replace(backup, target, null);
            if (ExistingHash(target) != originalHash)
                throw new IOException("Managed repair could not restore the original broker. Preserve the .bak file for recovery.");
        }
        else if (originalHash == null && !File.Exists(stage) &&
                 string.Equals(ExistingHash(target), expectedHash, StringComparison.OrdinalIgnoreCase))
            File.Delete(target);
    }

    private static FileStream OpenVerifiedPayload(string path, string expectedHash)
    {
        if (expectedHash == null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact release hash is required.");
        string sourcePath = LocalPath(path);
        PortableLabContext.ValidateNoReparsePoints(sourcePath);
        var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (source.Length is < 1 or > ViiperPayloadProvider.MaximumPayloadBytes ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(source)), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The repair payload does not match the compiled VIIPER release.");
            return source;
        }
        catch { source.Dispose(); throw; }
    }

    private static string ExistingHash(string path)
    {
        PortableLabContext.ValidateNoReparsePoints(path);
        try
        {
            using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (image.Length > ViiperPayloadProvider.MaximumPayloadBytes) throw new IOException("The installed broker exceeds the repair size limit.");
            return Convert.ToHexString(SHA256.HashData(image));
        }
        catch (FileNotFoundException) { return null; }
    }

    private static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("A fully qualified local path is required.");
        return Path.GetFullPath(path);
    }

    private static void RejectLab()
    {
        if (PortableLabContext.Requested || PortableLabContext.IsActive)
            throw new InvalidOperationException("Portable-lab sessions cannot repair the installed VIIPER broker.");
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new();
        internal static DirectoryPins Acquire(string directory)
        {
            var result = new DirectoryPins();
            try
            {
                var chain = new Stack<string>();
                for (DirectoryInfo node = new(directory); node != null; node = node.Parent) chain.Push(node.FullName);
                while (chain.Count != 0)
                {
                    SafeFileHandle handle = CreateFileW(chain.Pop(), 0x80 /* READ_ATTRIBUTES */,
                        (uint)(FileShare.Read | FileShare.Write), IntPtr.Zero, 3,
                        0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
                    result.handles.Add(handle);
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation info, 8))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                        (info.Attributes & (uint)FileAttributes.Directory) == 0)
                        throw new IOException("Managed repair directories must not be links.");
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        public void Dispose()
        {
            for (int index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation { internal uint Attributes; internal uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInformation information, uint size);
}
