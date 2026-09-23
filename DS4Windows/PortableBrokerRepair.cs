using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows;

internal sealed record PortableBrokerRepairResult(string ViiperPath, bool Changed);

// Offline repair only. The caller must finish controller shutdown and retire
// its selected broker and release its image pin before repair. In particular, this class
// never disposes Current, stops a process, or changes an installed broker.
internal static class PortableBrokerRepair
{
    private const long MaximumImageBytes = 32L * 1024 * 1024;
    private const string LockName = ".viiper-repair.lock";

    internal static string TryGetPortableRoot(string directory,
        Func<IEnumerable<string>> managedRoots = null)
    {
        if (PortableLabContext.Requested || PortableLabContext.IsActive) return null;
        return PortableBrokerContext.FindPortableRoot(directory, managedRoots);
    }

    internal static async Task<PortableBrokerRepairResult> RepairAsync(string directory, string expectedSha256,
        Func<CancellationToken, Task<ViiperPayloadLease>> acquirePayload,
        CancellationToken cancellationToken = default,
        IPortableBrokerProcessHost processHost = null,
        Func<IEnumerable<string>> managedRoots = null,
        Action<string> inspectPath = null,
        IPortableBrokerRepairCommitter committer = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PortableLabContext.Requested || PortableLabContext.IsActive)
                throw new PortableBrokerStartupException("Portable-lab sessions do not install or repair VIIPER.");
            if (expectedSha256 == null || expectedSha256.Length != 64 ||
                !expectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("The compiled VIIPER release digest is invalid.");
            ArgumentNullException.ThrowIfNull(acquirePayload);
            string root = PortableBrokerContext.FindPortableRoot(directory, managedRoots, inspectPath)
                ?? throw new PortableBrokerStartupException("This folder is not a normal DS4Windows portable package. Its broker was not changed.");
            Action<string> inspect = inspectPath ?? PortableLabContext.ValidateNoReparsePoints;
            using var directories = DirectoryPins.Acquire(root);
            // Directory handles deny rename/delete of every ancestor during
            // all awaits; rechecking here also covers the initial resolution.
            if (PortableBrokerContext.FindPortableRoot(root, managedRoots, inspect) != root)
                throw new IOException("Portable package identity changed.");
            string marker = Path.Combine(root, PortableBrokerContext.MarkerFileName);
            using FileStream markerPin = OpenRegularFile(marker, FileAccess.Read, FileShare.Read, create: false);
            using (var reader = new StreamReader(markerPin, leaveOpen: true))
                if (markerPin.Length > 128 || reader.ReadToEnd().TrimEnd('\r', '\n') != PortableBrokerContext.MarkerText)
                    throw new IOException("Portable package marker changed.");

            string destination = Path.Combine(root, "viiper.exe");
            inspect(destination);
            // A healthy active context pins its image. Checking it does not
            // need a writer lock, a process query, a download or a restart.
            ImageState first = ReadImageState(destination);
            if (first.Matches(expectedSha256)) return new(destination, false);

            string lockPath = Path.Combine(root, LockName);
            inspect(lockPath);
            using FileStream mutex = await AcquireDestinationLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
            inspect(destination);
            ImageState original = ReadImageState(destination);
            if (original.Matches(expectedSha256)) return new(destination, false);
            IPortableBrokerProcessHost host = processHost ?? new PortableBrokerProcessHost();
            EnsureDestinationNotRunning(host, destination);
            // A FileShare.Read pin (including a borrowed/owned context's pin)
            // must prevent replacement even when no process is discoverable.
            CheckImageNotPinned(destination, original.Exists);

            using ViiperPayloadLease payload = await acquirePayload(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("No verified VIIPER payload was supplied.");
            string stage = Path.Combine(root, ".viiper-repair-" + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = Path.Combine(root, ".viiper-repair-" + Guid.NewGuid().ToString("N") + ".bak");
            bool committed = false, ownsStage = false;
            try
            {
                inspect(payload.Path);
                inspect(stage);
                await StageAsync(payload.Path, stage, expectedSha256, cancellationToken).ConfigureAwait(false);
                ownsStage = true;
                cancellationToken.ThrowIfCancellationRequested();
                inspect(destination);
                inspect(stage);
                inspect(backup);
                if (ReadImageState(destination) != original)
                    throw new IOException("The portable broker changed while repair was preparing its payload.");
                EnsureDestinationNotRunning(host, destination);
                CheckImageNotPinned(destination, original.Exists);
                // No cancellation after this point: commit/verification or
                // rollback must finish as one transaction.
                IPortableBrokerRepairCommitter files = committer ?? new PortableBrokerRepairCommitter();
                try
                {
                    if (original.Exists) files.Replace(stage, destination, backup);
                    else files.Move(stage, destination);
                    if (!ReadImageState(destination).Matches(expectedSha256))
                        throw new IOException("The repaired broker failed its final release check.");
                    committed = true;
                }
                catch
                {
                    RollBack(files, destination, stage, backup, original, expectedSha256);
                    throw;
                }
                return new(destination, true);
            }
            finally
            {
                if (ownsStage) TryDelete(stage);
                // A failed rollback retains the backup for recovery instead
                // of erasing the only remaining copy of the old image.
                if (committed) TryDelete(backup);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PortableBrokerStartupException) { throw; }
        catch (Exception error)
        {
            throw new PortableBrokerStartupException("The portable VIIPER could not be repaired safely. Close applications using this portable folder, check that the folder is writable, and try again. No installed broker, key, configuration, profiles or startup tasks were changed.", error);
        }
    }

    private static void EnsureDestinationNotRunning(IPortableBrokerProcessHost host, string destination)
    {
        foreach (PortableBrokerProcessIdentity process in host.Snapshot())
        {
            if (process.ProcessId <= 0 || process.StartTimeUtcTicks <= 0 ||
                string.IsNullOrWhiteSpace(process.ExecutablePath) || !Path.IsPathFullyQualified(process.ExecutablePath))
                throw new PortableBrokerStartupException("A VIIPER process could not be identified. Close the portable VIIPER before repairing its file. No process was stopped.");
            if (string.Equals(Path.GetFullPath(process.ExecutablePath), destination, StringComparison.OrdinalIgnoreCase))
                throw new PortableBrokerStartupException("VIIPER is still using this portable folder. Close that broker and retry repair. No existing broker was stopped.");
        }
    }

    private static void CheckImageNotPinned(string path, bool exists)
    {
        if (!exists) return;
        // Delete access is necessary for replacement. This metadata-only
        // handle checks the sharing contract without writing the old image.
        using SafeFileHandle handle = NativeOpen(path, 0x00010000 /* DELETE */,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete), 3, 0x00200000);
        VerifyHandle(handle, directory: false);
    }

    private static async Task<FileStream> AcquireDestinationLockAsync(string path, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return OpenRegularFile(path, FileAccess.ReadWrite, FileShare.None, create: true); }
            catch (Win32Exception error) when (error.NativeErrorCode is 32 or 33 && elapsed.Elapsed < TimeSpan.FromSeconds(30))
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }

    private static async Task StageAsync(string source, string stage, string digest, CancellationToken token)
    {
        using FileStream input = OpenRegularFile(source, FileAccess.Read, FileShare.Read, create: false);
        if (input.Length is < 1 or > MaximumImageBytes) throw new IOException("VIIPER payload size is invalid.");
        bool ownsStage = false;
        try
        {
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.Asynchronous))
            {
                ownsStage = true;
                byte[] buffer = new byte[64 * 1024];
                long copied = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    copied += count;
                    if (copied > MaximumImageBytes) throw new IOException("VIIPER payload exceeded its size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                output.Flush(flushToDisk: true);
            }
            ImageState staged = ReadImageState(stage);
            if (!staged.Matches(digest)) throw new IOException("VIIPER payload does not match this release (" + staged.Length + " bytes, SHA256 " + staged.Digest + ").");
        }
        catch
        {
            if (ownsStage) TryDelete(stage);
            throw;
        }
    }

    private readonly record struct ImageState(bool Exists, long Length, string Digest)
    {
        internal bool Matches(string expected) => Exists && string.Equals(Digest, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static ImageState ReadImageState(string path)
    {
        try
        {
            using FileStream image = OpenRegularFile(path, FileAccess.Read,
                FileShare.Read | FileShare.Delete, create: false);
            if (image.Length > MaximumImageBytes) throw new IOException("The existing VIIPER image exceeds the repair limit.");
            return new(true, image.Length, Convert.ToHexString(SHA256.HashData(image)));
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return default; }
    }

    private static void RollBack(IPortableBrokerRepairCommitter files, string destination,
        string stage, string backup, ImageState original, string expectedDigest)
    {
        try
        {
            // A pre-commit error has nothing to roll back. A post-commit error
            // may restore only the image we just installed, never a later file.
            if (original.Exists && File.Exists(backup))
            {
                if (ReadImageState(backup) != original)
                    throw new IOException("The rollback image changed.");
                ImageState current = ReadImageState(destination);
                if (current.Exists && !current.Matches(expectedDigest))
                    throw new IOException("A different broker appeared after replacement.");
                if (current.Exists) files.Replace(backup, destination, null);
                else files.Move(backup, destination);
                if (ReadImageState(destination) != original)
                    throw new IOException("The original broker could not be restored.");
            }
            else if (!original.Exists && !File.Exists(stage) && ReadImageState(destination).Matches(expectedDigest))
                File.Delete(destination);
        }
        catch
        {
            throw new PortableBrokerStartupException("Portable VIIPER repair did not finish and automatic rollback was blocked. Keep the .viiper-repair-*.bak file in this portable folder for recovery. No key, configuration, profiles, installed broker or startup tasks were changed.");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* Preserve a locked recovery file. */ }
    }

    private static FileStream OpenRegularFile(string path, FileAccess access, FileShare share, bool create)
    {
        uint desiredAccess = access == FileAccess.Read ? 0x80000000 : 0xC0000000;
        SafeFileHandle handle = NativeOpen(path, desiredAccess, (uint)share,
            create ? 4u /* OPEN_ALWAYS */ : 3u, 0x00200000 /* OPEN_REPARSE_POINT */);
        try
        {
            VerifyHandle(handle, directory: false);
            return new FileStream(handle, access);
        }
        catch { handle.Dispose(); throw; }
    }

    private sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new();
        internal static DirectoryPins Acquire(string root)
        {
            var result = new DirectoryPins();
            try
            {
                var chain = new Stack<string>();
                for (DirectoryInfo directory = new(root); directory != null; directory = directory.Parent)
                    chain.Push(directory.FullName);
                while (chain.Count != 0)
                {
                    // Omitting FILE_SHARE_DELETE prevents ancestors from
                    // being renamed/replaced by links while the repair awaits.
                    SafeFileHandle handle = NativeOpen(chain.Pop(), 0x80,
                        (uint)(FileShare.Read | FileShare.Write), 3, 0x02200000);
                    result.handles.Add(handle);
                    VerifyHandle(handle, directory: true);
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

    private static SafeFileHandle NativeOpen(string path, uint access, uint share, uint disposition, uint flags)
    {
        string normalized = Path.GetFullPath(path);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Portable repair requires local, non-device paths.");
        // FileStream transparently handles long paths. Raw CreateFileW must
        // use the extended prefix too, especially for our GUID staging names.
        SafeFileHandle handle = CreateFileW(@"\\?\" + normalized, access, share, IntPtr.Zero,
            disposition, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(code);
        }
        return handle;
    }

    private static void VerifyHandle(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        FileAttributes attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Repair paths must be ordinary files and directories, not links.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileAttributeTagInformation information, uint size);
}

// The seam exercises failures both before and after the atomic OS operation;
// tests never need to start an app, an installer or a real broker.
internal interface IPortableBrokerRepairCommitter
{
    void Replace(string source, string destination, string backup);
    void Move(string source, string destination);
}

internal sealed class PortableBrokerRepairCommitter : IPortableBrokerRepairCommitter
{
    public void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup);
    public void Move(string source, string destination) => File.Move(source, destination);
}
