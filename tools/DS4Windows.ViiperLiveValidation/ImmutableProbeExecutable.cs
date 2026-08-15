using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace DS4Windows.ViiperLiveValidation;

/// <summary>
/// Holds a deny-write/delete handle to an exact packaged observer for the
/// complete validation run. Every child process is then proven to have been
/// created from that same NTFS file identity before any receipt is accepted.
/// </summary>
internal sealed class ImmutableProbeExecutable : IDisposable
{
    private readonly FileStream lockedStream;
    private readonly byte[] expectedHash;
    private int disposed;

    internal ImmutableProbeExecutable(FileBindingEvidence binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Path = SourceBindings.RequireRegularFile(binding.Path, binding.Role);
        lockedStream = new FileStream(Path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        LockedFileIdentity = WindowsFileIdentity.Read(
            lockedStream.SafeFileHandle);
        expectedHash = Convert.FromHexString(binding.Sha256);
        Evidence = new ProbeExecutionEvidence
        {
            Role = binding.Role,
            Path = Path,
            LockedFileIdentity = LockedFileIdentity,
            Sha256 = binding.Sha256,
            Length = binding.Length,
        };
        RevalidateLockedBytes(binding.Length, binding.Sha256);
    }

    internal string Path { get; }
    internal string LockedFileIdentity { get; }
    internal ProbeExecutionEvidence Evidence { get; }

    internal void VerifyStartedProcess(Process process)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(process);
        Evidence.LaunchCount++;
        Evidence.LastProcessId = process.Id;
        try
        {
            string imagePath = WindowsProcessImage.Query(process.Handle);
            string exactImagePath = SourceBindings.RequireRegularFile(
                imagePath, Evidence.Role + " launched process image");
            using var processImage = new FileStream(exactImagePath,
                FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
                FileOptions.SequentialScan);
            string processIdentity = WindowsFileIdentity.Read(
                processImage.SafeFileHandle);
            byte[] processHash = SHA256.HashData(processImage);

            Evidence.LastProcessImagePath = exactImagePath;
            Evidence.LastProcessFileIdentity = processIdentity;
            bool exact = string.Equals(processIdentity, LockedFileIdentity,
                    StringComparison.Ordinal) &&
                processImage.Length == Evidence.Length &&
                CryptographicOperations.FixedTimeEquals(processHash,
                    expectedHash);
            if (!exact)
            {
                throw new ViiperIdentityException(
                    $"Launched {Evidence.Role} process did not retain the locked source-bound file identity: lockedIdentity={LockedFileIdentity} processIdentity={processIdentity} expectedLength={Evidence.Length} processLength={processImage.Length}.");
            }

            RevalidateLockedBytes(Evidence.Length, Evidence.Sha256);
        }
        catch
        {
            Evidence.AllLaunchesExact = false;
            throw;
        }
    }

    internal void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (!Evidence.AllLaunchesExact)
        {
            throw new ViiperIdentityException(
                $"At least one {Evidence.Role} launch was not bound to its immutable packaged executable.");
        }
        RevalidateLockedBytes(Evidence.Length, Evidence.Sha256);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lockedStream.Dispose();
        }
    }

    private void RevalidateLockedBytes(long expectedLength,
        string expectedSha256)
    {
        if (lockedStream.Length != expectedLength)
        {
            throw new ViiperIdentityException(
                $"Locked {EvidenceRole()} length changed: expected={expectedLength} actual={lockedStream.Length}.");
        }
        lockedStream.Position = 0;
        byte[] actual = SHA256.HashData(lockedStream);
        lockedStream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedHash))
        {
            throw new ViiperIdentityException(
                $"Locked {EvidenceRole()} SHA-256 changed: expected={expectedSha256} actual={Convert.ToHexString(actual).ToLowerInvariant()}.");
        }
    }

    private string EvidenceRole() => Evidence.Role;
}

internal static class WindowsFileIdentity
{
    internal static string Read(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "VIIPER live validation requires Windows file identities.");
        }
        if (!GetFileInformationByHandle(handle,
                out ByHandleFileInformation information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Could not read the exact Windows file identity.");
        }
        ulong fileIndex = ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow;
        return information.VolumeSerialNumber.ToString("x8") + ":" +
            fileIndex.ToString("x16");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);
}

internal static class WindowsProcessImage
{
    internal static string Query(IntPtr processHandle)
    {
        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, buffer,
                ref length) || length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Could not bind the launched probe process image.");
        }
        return System.IO.Path.GetFullPath(buffer.ToString(0, (int)length));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process,
        uint flags, StringBuilder executableName, ref uint size);
}
