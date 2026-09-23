using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DS4WinWPF.DS4Forms;

namespace DS4Windows;

internal static class PortableBrokerMaintenance
{
    internal static bool MatchesRelease(string path) => MatchesRelease(path, ViiperSetupManager.SupportedViiperSha256);

    private static bool MatchesRelease(string path, string expectedSha256)
    {
        try
        {
            PortableLabContext.ValidateNoReparsePoints(path);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > ViiperPayloadProvider.MaximumPayloadBytes) return false;
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)),
                expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool EnsureStartupPayload(string directory)
    {
        if (PortableLabContext.IsActive) return false;
        string root = PortableBrokerRepair.TryGetPortableRoot(directory);
        if (root == null) return false;
        var host = new PortableBrokerProcessHost();
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        string destination = Path.Combine(root, "viiper.exe");
        bool replace = !MatchesRelease(destination);
        bool conflicting = captured.Any(process => !string.Equals(process.ExecutablePath,
            destination, StringComparison.OrdinalIgnoreCase));
        if (!replace && !conflicting) return false;
        PortableRepairProgress.Run<object>(null,
            "Restoring VIIPER in this portable folder. An identified conflicting VIIPER may be closed. If needed, DS4Windows will download the matching version. Your profiles and settings will stay where they are.",
            async token =>
            {
                await PrepareStartupAsync(root, ViiperSetupManager.SupportedViiperSha256,
                    new ViiperPayloadProvider().AcquireAsync, captured, host,
                    cancellationToken: token,
                    stopCaptured: (identities, preserve) => ViiperProcessRepair.StopAsync(identities, preserve)).ConfigureAwait(false);
                return null;
            });
        return true;
    }

    // The normal startup caller already owns the duplicate-app gate. Capture
    // identities before waiting for a payload, then stop only that frozen set.
    // A matching same-path broker remains available for normal verified borrow.
    internal static async Task PrepareStartupAsync(string directory, string expectedSha256,
        Func<string, CancellationToken, Task<ViiperPayloadLease>> acquirePayload,
        IReadOnlyList<PortableBrokerProcessIdentity> captured,
        IPortableBrokerProcessHost processHost, Func<IEnumerable<string>> managedRoots = null,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<PortableBrokerProcessIdentity>, string, Task> stopCaptured = null)
    {
        string root = PortableBrokerRepair.TryGetPortableRoot(directory, managedRoots)
            ?? throw new PortableBrokerStartupException("Startup recovery requires a verified normal portable folder.");
        string destination = Path.Combine(root, "viiper.exe");
        bool replace = !MatchesRelease(destination, expectedSha256);
        using ViiperPayloadLease payload = replace
            ? await acquirePayload(root, cancellationToken).ConfigureAwait(false) : null;
        if (replace && (payload == null || !MatchesRelease(payload.Path, expectedSha256)))
            throw new PortableBrokerStartupException("The recovery payload does not match this release. No VIIPER process was stopped.");
        cancellationToken.ThrowIfCancellationRequested();
        string preservedImage = replace ? null : destination;
        if (stopCaptured != null) await stopCaptured(captured, preservedImage).ConfigureAwait(false);
        else PortableBrokerProcessHost.StopCapturedForRepair(captured, preservedImage, processHost);
        if (replace)
        {
            // Cancellation ends before mutation. Complete the bounded replace
            // or rollback after retiring the exact captured broker identities.
            await PortableBrokerRepair.RepairAsync(root, expectedSha256,
                _ => Task.FromResult(new ViiperPayloadLease(payload.Path)), processHost: processHost,
                managedRoots: managedRoots).ConfigureAwait(false);
        }
    }
}
