using System;

namespace DS4Windows;

internal readonly record struct ViiperDependencyStatus(bool UsbipDriverFilesSafe,
    string UsbipDriverIntegrityMessage, bool CitrixUsbMonitorConflict, string CitrixUsbMonitorConflictMessage);

// Driver files and filter configuration can change while Settings remains
// open. Cold startup/repair boundaries request a fresh authoritative snapshot;
// ordinary status rendering can reuse it without rehashing drivers or WMI.
internal sealed class ViiperDependencyReadiness
{
    private readonly object gate = new();
    private readonly Func<ViiperDependencyStatus> inspect;
    private ViiperDependencyStatus? cached;

    internal ViiperDependencyReadiness(Func<ViiperDependencyStatus> inspect) =>
        this.inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));

    internal ViiperDependencyStatus Read(bool refresh = false)
    {
        lock (gate)
        {
            // Revoke old success before inspecting. If inspection throws,
            // another caller must not inherit the previous trusted result.
            if (refresh) cached = null;
            cached ??= inspect();
            return cached.Value;
        }
    }
}
