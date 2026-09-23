using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DS4WinWPF.DS4Forms;

namespace DS4Windows;

internal static class ViiperRecovery
{
    private static int recovering;
    private static int automaticRecoveryAttempted;
    private static int repairRequired;
    internal static bool IsRecovering => Volatile.Read(ref recovering) != 0;
    internal static bool RepairRequired => Volatile.Read(ref repairRequired) != 0 ||
        DS4WinWPF.App.rootHub?.BackendMaintenanceRequiresRepair == true;
    internal static bool TryBeginAutomaticRecovery() => Interlocked.CompareExchange(ref automaticRecoveryAttempted, 1, 0) == 0;

    internal static bool Repair(Window owner)
    {
        if (PortableLabContext.IsActive || Interlocked.CompareExchange(ref recovering, 1, 0) != 0)
            return false;
        try
        {
            if (!ViiperSetupManager.HasSafeRuntimePrerequisites(ViiperSetupManager.GetStatus()))
                throw new IOException("Windows controller-driver setup needs attention before VIIPER can restart safely.");
            // Capture destination once. A retired portable context must never
            // accidentally select the installed broker during the transaction.
            string portableRoot = PortableBrokerRepair.TryGetPortableRoot(Global.exedirpath);
            if (PortableBrokerContext.Current is { } current &&
                !string.Equals(Path.GetDirectoryName(current.ViiperPath), portableRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("This portable folder's identity changed. Restore its portable marker before repairing; no installed copy was changed.");
            string destination = portableRoot == null ? ViiperSetupManager.GetCanonicalViiperExePath() :
                Path.Combine(portableRoot, "viiper.exe");
            PortableRepairProgress.Run<object>(owner,
                "Checking VIIPER and restoring the matching version if needed. DS4Windows will stay open; controller output may pause briefly.",
                async token =>
                {
                    var capturedProcesses = PortableBrokerProcessHost.CaptureForRepair();
                    bool replace = !PortableBrokerMaintenance.MatchesRelease(destination);
                    using ViiperPayloadLease payload = replace
                        ? await new ViiperPayloadProvider().AcquireAsync(Global.exedirpath, token).ConfigureAwait(false) : null;
                    token.ThrowIfCancellationRequested();
                    Action repair = () =>
                    {
                        if (!ViiperSetupManager.HasSafeRuntimePrerequisites(ViiperSetupManager.GetStatus()))
                            throw new IOException("Windows controller-driver readiness changed during repair. No replacement broker was started.");
                        if (portableRoot != null)
                        {
                            try
                            {
                                if (PortableBrokerContext.Current != null)
                                    PortableBrokerContext.RetireCurrentForRepair(capturedProcesses,
                                        () => ViiperProcessRepair.StopAsync(capturedProcesses).GetAwaiter().GetResult());
                                else
                                    ViiperProcessRepair.StopAsync(capturedProcesses).GetAwaiter().GetResult();
                                if (replace)
                                    PortableBrokerRepair.RepairAsync(portableRoot, ViiperSetupManager.SupportedViiperSha256,
                                        _ => Task.FromResult(new ViiperPayloadLease(payload.Path))).GetAwaiter().GetResult();
                                PortableBrokerContext.Initialize(portableRoot);
                                PortableBrokerContext.Current.Start();
                            }
                            catch
                            {
                                // Retain portable identity and its repair UI,
                                // including after failed replacement or launch.
                                if (PortableBrokerContext.Current == null)
                                    PortableBrokerContext.InitializeUnavailable(portableRoot);
                                throw;
                            }
                        }
                        else
                        {
                            ViiperManagedRepair.RepairAsync(payload?.Path ?? destination, CancellationToken.None,
                                capturedProcesses: capturedProcesses).GetAwaiter().GetResult();
                            if (!PortableBrokerMaintenance.MatchesRelease(destination) ||
                                !ViiperSetupManager.TryStartRepairedServer(destination))
                                throw new IOException("VIIPER could not restart. Your DS4Windows window and settings remain available.");
                        }
                        WaitUntilReady(portableRoot != null);
                    };
                    // Once output is stopped, finish replacement and readiness
                    // checks without cancellation halfway through the commit.
                    if (DS4WinWPF.App.rootHub is { } service)
                        service.ExecuteBackendMaintenance(repair);
                    else repair();
                    return null;
                });
            Volatile.Write(ref repairRequired, 0);
            return true;
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref repairRequired, 1);
            Message(owner, "VIIPER repair was canceled. DS4Windows is still open.");
            return false;
        }
        catch (Exception error)
        {
            Volatile.Write(ref repairRequired, 1);
            Message(owner, "VIIPER could not finish repairing. DS4Windows is still open so you can retry.\n\n" + error.Message);
            return false;
        }
        finally { Volatile.Write(ref recovering, 0); }
    }

    private static void WaitUntilReady(bool portable)
    {
        bool Probe(int timeoutMilliseconds, out string failure)
        {
            failure = null;
            if (portable)
            {
                if (!PortableBrokerContext.Current.InspectOwnedProcess(out bool running, out failure) || !running)
                    throw new IOException(failure ?? "VIIPER stopped before it was ready.");
                if (ViiperSetupManager.ProbeServer(ViiperSetupManager.ApiHost, ViiperSetupManager.ApiPort,
                        authenticated: true, out failure, totalTimeoutMilliseconds: timeoutMilliseconds))
                {
                    ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus();
                    if (status.Ready && ViiperSetupManager.HasSafeRuntimePrerequisites(status)) return true;
                    failure = status.DisplayText;
                }
            }
            else
            {
                // A ping alone is not enough: another process could acquire
                // the port after launch. Revalidate identity and prerequisites
                // at the boundary where controller output will resume.
                ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus();
                if (IsManagedRecoveryReady(status, ViiperSetupManager.GetCanonicalViiperExePath())) return true;
                failure = status.DisplayText;
            }
            return false;
        }
        if (ViiperStartupReadiness.Wait(Probe, out string failure)) return;
        throw new IOException("VIIPER restarted but did not answer its connection check. " + failure);
    }

    internal static bool IsManagedRecoveryReady(ViiperPrerequisiteStatus status, string expectedPath) =>
        status?.Ready == true && ViiperSetupManager.HasSafeRuntimePrerequisites(status) &&
        string.Equals(status.ViiperPath, expectedPath, StringComparison.OrdinalIgnoreCase);

    private static void Message(Window owner, string message)
    {
        if (owner?.IsLoaded == true)
            MessageBox.Show(owner, message, "VIIPER needs attention", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, "VIIPER needs attention", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
