using System;
using System.Diagnostics;
using System.IO;

namespace DS4Windows;

// Scheduler-independent task data keeps the ownership decision and the actual
// registration boundary testable without reading or changing Windows tasks.
internal sealed record ViiperStartupTaskState
{
    internal string Description { get; init; }
    internal bool Enabled { get; init; }
    internal int ActionCount { get; init; }
    internal int TriggerCount { get; init; }
    internal bool ExecutableAction { get; init; }
    internal bool LogonTrigger { get; init; }
    internal bool Highest { get; init; }
    internal bool InteractiveToken { get; init; }
    internal string PrincipalSid { get; init; }
    internal bool AllUsersLogon { get; init; }
    internal string LogonUserSid { get; init; }
    internal string ExecutablePath { get; init; }
    internal string Arguments { get; init; }
    internal string WorkingDirectory { get; init; }
    internal ProcessPriorityClass Priority { get; init; }
}

internal interface IViiperStartupTaskStore
{
    ViiperStartupTaskState Read();
    void Write(ViiperStartupTaskState state, bool updateExisting);
    void Delete();
}

internal static class ViiperStartupTaskPolicy
{
    internal static ViiperStartupTaskState Create(string executablePath,
        string currentSid) => new()
    {
        Description = DS4WinWPF.StartupRegistrationPolicy.ManagedTaskDescription,
        Enabled = true,
        ActionCount = 1,
        TriggerCount = 1,
        ExecutableAction = true,
        LogonTrigger = true,
        Highest = true,
        InteractiveToken = true,
        PrincipalSid = currentSid,
        AllUsersLogon = true,
        ExecutablePath = Path.GetFullPath(executablePath),
        Arguments = ViiperSetupManager.ViiperServerArguments,
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)),
        Priority = ProcessPriorityClass.High,
    };

    internal static bool IsValid(ViiperStartupTaskState task,
        string executablePath, string currentSid) =>
        task != null && !string.IsNullOrWhiteSpace(currentSid) &&
        string.Equals(task.Description,
            DS4WinWPF.StartupRegistrationPolicy.ManagedTaskDescription,
            StringComparison.Ordinal) && task.Enabled && task.ActionCount == 1 &&
        task.TriggerCount == 1 && task.ExecutableAction && task.LogonTrigger &&
        task.Highest && task.InteractiveToken &&
        task.Priority == ProcessPriorityClass.High &&
        ViiperSetupManager.IsExactViiperExecutablePath(task.ExecutablePath,
            executablePath) &&
        string.Equals(task.Arguments?.Trim(),
            ViiperSetupManager.ViiperServerArguments, StringComparison.Ordinal) &&
        ViiperSetupManager.IsExactViiperExecutablePath(
            Path.Combine(task.WorkingDirectory ?? string.Empty, "viiper.exe"),
            executablePath) &&
        string.Equals(task.PrincipalSid, currentSid, StringComparison.OrdinalIgnoreCase) &&
        (task.AllUsersLogon || string.Equals(task.LogonUserSid, currentSid,
            StringComparison.OrdinalIgnoreCase));

    internal static void Register(string executablePath, string currentSid,
        string canonicalPath, IViiperStartupTaskStore store)
    {
        if (string.IsNullOrWhiteSpace(currentSid) ||
            !ViiperSetupManager.IsExactViiperExecutablePath(executablePath, canonicalPath))
            throw new InvalidOperationException(
                "VIIPER startup must target the installed backend for the current Windows account.");

        ViiperStartupTaskState existing = store.Read();
        RequireOwnership(existing, currentSid);
        // Updating in place lets a rejected Scheduler write leave the previous
        // registration intact. Create-only also preserves a foreign task that
        // appears after an initially empty read.
        store.Write(Create(executablePath, currentSid), updateExisting: existing != null);
    }

    internal static void Remove(string currentSid, IViiperStartupTaskStore store)
    {
        ViiperStartupTaskState existing = store.Read();
        RequireOwnership(existing, currentSid);
        if (existing != null) store.Delete();
    }

    private static void RequireOwnership(ViiperStartupTaskState task, string currentSid)
    {
        if (task == null) return;
        bool currentUser = !string.IsNullOrWhiteSpace(currentSid) &&
            string.Equals(task.PrincipalSid, currentSid, StringComparison.OrdinalIgnoreCase) &&
            (task.AllUsersLogon || string.Equals(task.LogonUserSid, currentSid,
                StringComparison.OrdinalIgnoreCase));
        bool contract = task.ActionCount == 1 && task.TriggerCount == 1 &&
            task.ExecutableAction && task.LogonTrigger && task.Highest &&
            task.InteractiveToken &&
            (string.Equals(task.Arguments?.Trim(), "server", StringComparison.Ordinal) ||
             string.Equals(task.Arguments?.Trim(), ViiperSetupManager.ViiperServerArguments,
                 StringComparison.Ordinal)) &&
            ViiperSetupManager.IsExactViiperExecutablePath(
                Path.Combine(task.WorkingDirectory ?? string.Empty, "viiper.exe"),
                task.ExecutablePath);
        // Task names and executable locations alone do not establish ownership.
        // All unmarked legacy tasks require the installer's identity checks and
        // XML backup before adoption, including those at the installed path.
        bool marked = string.Equals(task.Description,
            DS4WinWPF.StartupRegistrationPolicy.ManagedTaskDescription,
            StringComparison.Ordinal);
        if (!marked || !currentUser || !contract)
            throw new InvalidOperationException(
                "RunVIIPER belongs to another startup configuration and was preserved. " +
                "Run setup to recover a verified older DS4Windows registration.");
    }

    internal static void RefreshOnLaunch(bool portableSession,
        string canonicalPath, Func<string> selectRuntime,
        Func<string, bool> isSelectable, Func<bool> startupEnabled,
        Action<string> persistRuntime, Func<string, bool> ensureTask)
    {
        if (portableSession) return;
        string selectedPath = selectRuntime();
        if (isSelectable(selectedPath)) persistRuntime(selectedPath);
        // A runtime preference can point at a verified portable package. The
        // installed logon task belongs to the installed backend and must never
        // follow that preference, including on an elevated ordinary launch.
        if (!startupEnabled() || !isSelectable(canonicalPath)) return;
        ensureTask(canonicalPath);
    }

    internal static bool TryStartVerifiedServer(
        Func<(bool Owned, bool Running)> inspectOwnership,
        Func<bool> probeServer, Func<bool> launchServer)
    {
        var ownership = inspectOwnership();
        if (!ownership.Owned) return false;
        return ownership.Running ? probeServer() : launchServer();
    }
}
