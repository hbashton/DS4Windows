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
using System.Security.Principal;
using System.Threading.Tasks;

namespace DS4Windows;

// Explicit repair only: stop the captured identities, never install, replace,
// start a broker, or modify files/settings/tasks. Normal disposal never uses this.
internal static class ViiperProcessRepair
{
    internal const string HelperArgument = "--stop-viiper-for-repair";
    private const string NoPreservedPath = "-";

    internal static Task StopAsync(IReadOnlyList<PortableBrokerProcessIdentity> captured,
        string preserveImagePath = null) => StopCoreAsync(captured, preserveImagePath,
            IsAdministrator(), new PortableBrokerProcessHost(), LaunchElevatedAsync);

    internal static async Task StopCoreAsync(IReadOnlyList<PortableBrokerProcessIdentity> captured,
        string preserveImagePath, bool administrator, IPortableBrokerProcessHost processHost,
        Func<string, string, Task> elevate)
    {
        RejectLab();
        ArgumentNullException.ThrowIfNull(processHost);
        ArgumentNullException.ThrowIfNull(elevate);
        string encoded = ViiperManagedRepair.EncodeSnapshot(captured);
        PortableBrokerProcessIdentity[] frozen = ViiperManagedRepair.DecodeSnapshot(encoded);
        string preserve = ValidatePreservedPath(preserveImagePath);
        try
        {
            PortableBrokerProcessHost.StopCapturedForRepair(frozen, preserve, processHost);
            return;
        }
        catch (Exception error) when (!administrator && IsAccessDenied(error))
        {
            // Some captured owners may already have exited. Escalation keeps
            // the original authority, never a newly enumerated replacement.
            AssertCapturedSubset(frozen, PortableBrokerProcessHost.CaptureForRepair(processHost));
        }
        await elevate(encoded, preserve).ConfigureAwait(false);
        IReadOnlyList<PortableBrokerProcessIdentity> after = PortableBrokerProcessHost.CaptureForRepair(processHost);
        AssertCapturedSubset(frozen, after);
        if (after.Any(row => !SamePath(row.ExecutablePath, preserve)))
            throw new IOException("The elevated stop helper returned while a captured VIIPER was still running. Its image was not changed.");
    }

    internal static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args == null || !args.Contains(HelperArgument, StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            RejectLab();
            if (args.Length != 3 || !string.Equals(args[0], HelperArgument, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid broker stop helper arguments.");
            PortableBrokerProcessIdentity[] captured = ViiperManagedRepair.DecodeSnapshot(args[1]);
            string preserve = ValidatePreservedPath(args[2] == NoPreservedPath ? null : args[2]);
            if (!IsAdministrator()) throw new UnauthorizedAccessException("Broker stop helper requires administrator rights.");
            PortableBrokerProcessHost.StopCapturedForRepair(captured, preserve);
        }
        catch (UnauthorizedAccessException) { exitCode = 5; }
        catch { exitCode = 1; }
        return true;
    }

    private static async Task LaunchElevatedAsync(string encoded, string preserve)
    {
        string executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !Path.GetFileName(executable).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Broker repair requires the DS4Windows executable, not a runtime host.");
        PortableLabContext.ValidateNoReparsePoints(executable);
        using var pin = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        string preservedArgument = preserve ?? NoPreservedPath;
        if (executable.Length + encoded.Length + preservedArgument.Length + 128 > 30_000)
            throw new IOException("The captured broker identities exceed the safe Windows command-line limit.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable)
        };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(encoded);
        start.ArgumentList.Add(preservedArgument);
        Process helper;
        try { helper = Process.Start(start) ?? throw new IOException("Windows did not start the broker stop helper."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new IOException("Closing the captured VIIPER was cancelled at the Windows administrator prompt.", error); }
        using (helper)
        {
            try { await helper.WaitForExitAsync().WaitAsync(ViiperManagedRepair.ParentDeadline).ConfigureAwait(false); }
            catch (TimeoutException error)
            {
                throw new IOException("The elevated broker stop helper did not finish within 45 seconds. Its state is unknown; do not restart VIIPER until it has exited.", error);
            }
            if (helper.ExitCode != 0)
                throw new IOException("The elevated broker stop helper failed or rejected a changed identity (exit code " + helper.ExitCode + "). No new broker was started.");
        }
    }

    private static void AssertCapturedSubset(IReadOnlyList<PortableBrokerProcessIdentity> captured,
        IReadOnlyList<PortableBrokerProcessIdentity> current)
    {
        foreach (PortableBrokerProcessIdentity now in current)
            if (!captured.Any(old => old.ProcessId == now.ProcessId && old.StartTimeUtcTicks == now.StartTimeUtcTicks &&
                SamePath(old.ExecutablePath, now.ExecutablePath)))
                throw new PortableBrokerStartupException("VIIPER ownership changed after repair was requested. No newly discovered broker was authorized to stop.");
    }

    internal static string ValidatePreservedPath(string path)
    {
        if (path == null) return null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.GetFileName(path).Equals("viiper.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The preserved image must be an explicit local VIIPER path.");
        return Path.GetFullPath(path);
    }

    private static bool SamePath(string first, string second) => second != null &&
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessDenied(Exception error) => error is UnauthorizedAccessException ||
        error is Win32Exception native && native.NativeErrorCode == 5;

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RejectLab()
    {
        if (PortableLabContext.Requested || PortableLabContext.IsActive)
            throw new InvalidOperationException("Portable-lab sessions cannot stop another broker for repair.");
    }
}
