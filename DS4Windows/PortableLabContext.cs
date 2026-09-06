using System;
using System.IO;
using System.Security.Cryptography;

namespace DS4Windows;

/// <summary>
/// Explicit development launch policy, not an installer or a security sandbox.
/// Established before helper dispatch; never inferred from executable location.
/// Production package identity and authenticated transport remain unchanged.
/// </summary>
internal sealed class PortableLabContext : IDisposable
{
    private static PortableLabContext current;
    internal static PortableLabContext Current => current;
    internal static bool IsActive => current != null;
    internal static bool Requested { get; private set; }

    private readonly FileStream backendPin;
    internal string Root { get; }
    internal string DataPath { get; }
    internal string ViiperPath { get; }
    internal string KeyPath => Path.Combine(DataPath, "viiper.key.txt");
    internal string ExpectedSha256 { get; }

    private PortableLabContext(string root, string digest)
    {
        Root = root;
        DataPath = Path.Combine(root, "lab-data");
        ViiperPath = Path.Combine(root, "viiper.exe");
        ExpectedSha256 = digest;
        ValidateNoReparsePoints(ViiperPath);
        ValidateDataTree();
        // Prevent replacement/write of the selected image for this app lifetime.
        // This is a caller-pinned local build, NOT a signed production package.
        backendPin = new FileStream(ViiperPath, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        try
        {
            string actual = Convert.ToHexString(SHA256.HashData(backendPin));
            if (!string.Equals(actual, digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Portable lab VIIPER SHA-256 mismatch.");
        }
        catch
        {
            backendPin.Dispose();
            throw;
        }
    }

    internal static void Initialize(string[] args, string executableDirectory)
    {
        Requested = ContainsLabOption(args);
        current = Create(args, executableDirectory);
    }

    internal static bool ContainsLabOption(string[] args)
    {
        foreach (string arg in args)
            if (arg?.StartsWith("--portable-lab", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    /// <summary>Read-only preparation. No config creation, task/driver repair or process launch.</summary>
    internal static PortableLabContext Create(string[] args, string executableDirectory)
    {
        if (!ContainsLabOption(args)) return null;
        string digest = null;
        bool mini = false, stop = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--portable-lab" when digest == null && i + 1 < args.Length:
                    digest = args[++i];
                    if (digest.Length != 64)
                        throw new ArgumentException("Portable lab requires an independently recorded 64-digit VIIPER SHA-256.");
                    foreach (char c in digest)
                        if (!Uri.IsHexDigit(c)) throw new ArgumentException("Invalid portable lab SHA-256.");
                    break;
                case "-m" when !mini: mini = true; break;
                case "-stop" when !stop: stop = true; break;
                default:
                    throw new ArgumentException("Portable lab accepts only --portable-lab <sha256>, -m and -stop. Maintenance/helper commands are forbidden.");
            }
        }
        if (digest == null) throw new ArgumentException("Missing portable lab SHA-256.");
        string root = ValidateRoot(executableDirectory);
        return new PortableLabContext(root, digest);
    }

    internal static string ValidateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Portable lab requires a local absolute application directory.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A drive root cannot be a portable lab.");
        foreach (Environment.SpecialFolder folder in new[] {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string forbidden = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(forbidden) && IsAtOrBelow(full, forbidden))
                throw new ArgumentException("Portable lab must not use installed or shared application storage.");
        }
        foreach (Environment.SpecialFolder folder in new[] {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments })
        {
            if (string.Equals(full, Path.TrimEndingDirectorySeparator(
                    Environment.GetFolderPath(folder)), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use a dedicated subdirectory for portable tests.");
        }
        ValidateNoReparsePoints(full);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        return full;
    }

    private static bool IsAtOrBelow(string path, string directory) =>
        string.Equals(path, Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static void ValidateNoReparsePoints(string path)
    {
        for (string candidate = Path.GetFullPath(path); !string.IsNullOrEmpty(candidate);
             candidate = Path.GetDirectoryName(candidate))
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Portable lab paths must not traverse reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal void ValidateDataTree()
    {
        ValidateNoReparsePoints(DataPath);
        if (!Directory.Exists(DataPath)) return;
        ValidateDirectory(DataPath);
    }

    private static void ValidateDirectory(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Portable lab data must not contain reparse points.");
            if ((attributes & FileAttributes.Directory) != 0) ValidateDirectory(entry);
        }
    }

    internal void RequireDataPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetFullPath(path),
                DataPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Portable lab configuration cannot leave lab-data.");
        ValidateDataTree();
    }

    internal bool IsVerifiedBackend(string path) => backendPin.CanRead &&
        string.Equals(path, ViiperPath, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => backendPin.Dispose();
}
