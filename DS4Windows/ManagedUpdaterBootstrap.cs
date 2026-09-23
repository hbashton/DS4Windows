using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Win32;

namespace DS4Windows;

internal sealed record ManagedUpdaterTicket(string InstallRoot, PortableUpdaterTicket Image);

// Installed updates use the verified AIO installer. Stage the updater outside
// Program Files so checking for updates never needs a batch file or an elevated
// copy into the running installation. The installer owns elevation and repair.
internal static class ManagedUpdaterBootstrap
{
    internal static readonly Version MinimumVersion = new(2, 0, 8, 0);

    internal static string FindManagedRoot(string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        PortableLabContext.ValidateNoReparsePoints(root);
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey key = machine.OpenSubKey(@"SOFTWARE\DS4Windows", writable: false);
            object value = key?.GetValue("InstallPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) continue;
            if (value is not string path || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidDataException("The registered DS4Windows installation path is invalid.");
            string registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (registered == Path.GetPathRoot(registered))
                throw new InvalidDataException("A drive root is not a DS4Windows installation.");
            PortableLabContext.ValidateNoReparsePoints(registered);
            if (string.Equals(root, registered, StringComparison.OrdinalIgnoreCase)) return root;
        }
        return null;
    }

    internal static async Task<ManagedUpdaterTicket> PrepareAsync(HttpClient client, string directory,
        CancellationToken cancellationToken = default)
    {
        string root = FindManagedRoot(directory) ?? throw new InvalidDataException("The installed update target is not registered.");
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DS4Windows", "Updates", "verified-updater");
        PortableLabContext.ValidateNoReparsePoints(cache);
        Directory.CreateDirectory(cache);
        string ValidateCache(string value)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!string.Equals(full, cache, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The updater cache destination changed.");
            PortableLabContext.ValidateNoReparsePoints(full);
            return full;
        }
        var image = await PortableUpdaterBootstrap.PrepareCoreAsync(client, cache, ValidateCache,
            MinimumVersion, cancellationToken).ConfigureAwait(false);
        return new(root, image);
    }

    internal static ProcessStartInfo CreateStartInfo(ManagedUpdaterTicket ticket, string releaseTag,
        string launchExe, Func<string, string> validateRoot = null)
    {
        if (ticket?.Image == null || ticket.Image.Version < MinimumVersion ||
            string.IsNullOrWhiteSpace(releaseTag) || releaseTag.Length > 128 ||
            !System.Text.RegularExpressions.Regex.IsMatch(releaseTag, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") ||
            string.IsNullOrWhiteSpace(launchExe) || launchExe != Path.GetFileName(launchExe) ||
            launchExe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !launchExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installed update request is invalid.");
        string root = (validateRoot ?? FindManagedRoot)(ticket.InstallRoot);
        if (root == null || !string.Equals(root, ticket.InstallRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registered DS4Windows installation changed. Retry the update.");
        if (!string.Equals(ticket.Image.FilePath, Path.Combine(ticket.Image.Root, "DS4Updater.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The updater image does not belong to the verified cache.");
        PortableLabContext.ValidateNoReparsePoints(ticket.Image.FilePath);
        var start = new ProcessStartInfo(ticket.Image.FilePath) { UseShellExecute = false, WorkingDirectory = ticket.Image.Root };
        foreach (string arg in new[] { "--managed-safe-v1", "--targetDirectory", root,
                     "--releaseTag", releaseTag, "--launchExe", launchExe }) start.ArgumentList.Add(arg);
        return start;
    }

    internal static bool Launch(ManagedUpdaterTicket ticket, string releaseTag, string launchExe,
        Func<ProcessStartInfo, bool> start = null, Func<string, string> validateRoot = null)
    {
        var info = CreateStartInfo(ticket, releaseTag, launchExe, validateRoot);
        using var image = new FileStream(ticket.Image.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (image.Length != ticket.Image.Size || !Convert.ToHexString(SHA256.HashData(image))
                .Equals(ticket.Image.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared updater changed before launch.");
        if (start != null) return start(info);
        using var child = Process.Start(info);
        return child != null;
    }
}
