using System;
using System.IO;
using Microsoft.Win32;

namespace DS4Windows;

internal static class InstallationModeInfo
{
    internal static string Read()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            using var registration = machine.OpenSubKey(@"SOFTWARE\DS4Windows");
            return Describe(Global.exelocation, registration?.GetValue("InstallPath") as string);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return "Deployment mode: unknown (installation registration could not be read)";
        }
    }

    internal static string Describe(string executable, string installedFolder)
    {
        if (!string.IsNullOrWhiteSpace(installedFolder) &&
            string.Equals(CanonicalFolder(Path.GetDirectoryName(executable)), CanonicalFolder(installedFolder), StringComparison.OrdinalIgnoreCase))
            return "Deployment mode: Installed (running from the registered installation folder)";
        return "Deployment mode: Portable (no matching installer registration)";
    }

    private static string CanonicalFolder(string folder)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(folder.Trim().Trim('"')));
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            directory = directory.ResolveLinkTarget(true) as DirectoryInfo ?? directory;
        return directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
