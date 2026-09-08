using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace DS4Windows;

internal static class ExecutableAliasFiles
{
    internal static void Create(string executablePath, string name)
    {
        string[] targets = GetAliasPaths(executablePath, name);
        string[] sources = GetSourcePaths(executablePath);
        var readers = new List<FileStream>();
        var writers = new List<FileStream>();
        var created = new List<string>();
        bool completed = false;
        try
        {
            // Validate/open all inputs before creating anything. Retain the
            // read handles until completion so a sidecar cannot change midway.
            foreach (string source in sources)
                readers.Add(OpenRead(source));
            foreach (string target in targets)
            {
                if (File.Exists(target) || Directory.Exists(target))
                    throw new IOException("That executable name is already in use. Choose another name.");
            }
            // Publish the executable last: an incomplete alias is not runnable.
            for (int i = 0; i < targets.Length; i++)
            {
                var writer = new FileStream(targets[i], FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                writers.Add(writer);
                created.Add(targets[i]);
                readers[i].CopyTo(writer);
                writer.Flush(flushToDisk: true);
            }
            completed = true;
        }
        finally
        {
            foreach (FileStream writer in writers) writer.Dispose();
            foreach (FileStream reader in readers) reader.Dispose();
            if (!completed)
            {
                // Only remove files this attempt created; never a colliding
                // destination or any pre-existing user file.
                foreach (string path in created)
                {
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    internal static bool RemoveOwned(string executablePath, string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        string[] targets;
        try { targets = GetAliasPaths(executablePath, name); }
        catch (ArgumentException) { return false; }
        string[] sources = GetSourcePaths(executablePath);
        // The previous alias can be the currently running executable. Do not
        // remove its sidecars or any file which no longer matches our copy.
        for (int i = 0; i < targets.Length; i++)
        {
            if (Directory.Exists(targets[i])) return false;
            if (!File.Exists(targets[i])) continue;
            using FileStream expected = OpenRead(sources[i]);
            using FileStream actual = OpenRead(targets[i]);
            if (expected.Length != actual.Length ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), SHA256.HashData(actual)))
                return false;
        }
        // Executable first. If Windows still owns it, leave its sidecars too.
        for (int i = targets.Length - 1; i >= 0; i--)
            if (File.Exists(targets[i])) File.Delete(targets[i]);
        return true;
    }

    private static string[] GetSourcePaths(string executablePath) => new[]
    {
        GetSidecarPath(executablePath, ".runtimeconfig.json"),
        GetSidecarPath(executablePath, ".deps.json"),
        executablePath,
    };

    private static string GetSidecarPath(string executablePath, string suffix)
    {
        string aliasSidecar = Path.ChangeExtension(executablePath, suffix);
        // Renaming an apphost does not rename its embedded managed assembly.
        // Hand-renamed hosts can still use the package's canonical sidecars.
        return File.Exists(aliasSidecar) ? aliasSidecar :
            Path.Combine(Path.GetDirectoryName(executablePath), "DS4Windows" + suffix);
    }

    private static string[] GetAliasPaths(string executablePath, string name)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        string stem = name?.Split('.')[0];
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() ||
            name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name is "." or ".." ||
            string.Equals(name, "DS4Windows", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, Path.GetFileNameWithoutExtension(executablePath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "CON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "PRN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "AUX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Choose a different executable name without a folder path or reserved Windows name.", nameof(name));
        return new[]
        {
            Path.Combine(root, name + ".runtimeconfig.json"),
            Path.Combine(root, name + ".deps.json"),
            Path.Combine(root, name + ".exe"),
        };
    }

    private static FileStream OpenRead(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Executable alias files must not be symbolic links.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
