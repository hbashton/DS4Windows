using System;
using System.IO;

namespace DS4WinWPF;

internal static class StartupRegistrationPolicy
{
    internal const string ManagedTaskDescription = "DS4Windows managed startup task v1";

    internal static bool ShouldRepairTask(bool exists, bool enabled,
        bool owned, bool matchesCurrentConfiguration) =>
        exists && enabled && owned && !matchesCurrentConfiguration;

    internal static bool OwnsTask(string description, bool currentUser,
        bool expectedContract, bool recognizedLegacyExecutable) =>
        currentUser && expectedContract &&
        (string.Equals(description, ManagedTaskDescription, StringComparison.Ordinal) ||
         (string.IsNullOrWhiteSpace(description) && recognizedLegacyExecutable));

    internal static bool AuthorizesTaskHelper(bool elevated, string currentSid, string requestedSid) =>
        elevated && !string.IsNullOrWhiteSpace(currentSid) &&
        string.Equals(currentSid, requestedSid, StringComparison.OrdinalIgnoreCase);

    internal static void RemoveShortcut(string path, Func<string, bool> isOwned)
    {
        if (!File.Exists(path)) return;
        if (!isOwned(path))
            throw new InvalidOperationException(
                "The startup shortcut belongs to another application and was not changed.");
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The startup shortcut is a filesystem link and was not changed.");
        try
        {
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
            if (File.Exists(path))
                throw new IOException("The startup shortcut remained after removal.");
        }
        catch
        {
            if (File.Exists(path))
            {
                try { File.SetAttributes(path, attributes); } catch { }
            }
            throw;
        }
    }
}

internal enum StartupRegistrationMode { Disabled, Program, Task }

internal readonly record struct StartupRegistrationState(bool Program, bool Task)
{
    internal bool Enabled => Program || Task;
    internal bool Matches(StartupRegistrationMode mode) => mode switch
    {
        StartupRegistrationMode.Disabled => !Enabled,
        StartupRegistrationMode.Program => Program && !Task,
        StartupRegistrationMode.Task => Task && !Program,
        _ => false,
    };
}

internal readonly record struct StartupRegistrationChangeResult(
    bool Success, StartupRegistrationState State, string Error);

internal static class StartupRegistrationChange
{
    internal static StartupRegistrationChangeResult Apply(
        StartupRegistrationMode requested, StartupRegistrationState previous,
        Func<StartupRegistrationState> read, Action<StartupRegistrationMode> change)
    {
        try
        {
            change(requested);
            StartupRegistrationState actual = read();
            return actual.Matches(requested)
                ? new(true, actual, null)
                : new(false, actual, "Windows did not confirm the requested startup setting.");
        }
        catch (Exception error)
        {
            // Never show a failed write as a saved preference. If inspection
            // also fails, retain the last verified state rather than guess.
            StartupRegistrationState actual = previous;
            try { actual = read(); } catch { }
            return new(false, actual, error.Message);
        }
    }
}
