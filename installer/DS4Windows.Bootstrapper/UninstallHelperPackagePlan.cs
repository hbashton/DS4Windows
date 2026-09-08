using System;
using WixToolset.BootstrapperApplicationApi;

namespace DS4Windows.Bootstrapper
{
    internal static class UninstallHelperPackagePlan
    {
        internal static bool TryGetState(string packageId, LaunchAction action,
            RelationType relation, bool infrastructureRecoveryPass,
            out RequestState state)
        {
            bool preflight = string.Equals(packageId,
                "CloseRunningApplicationsForUninstall", StringComparison.OrdinalIgnoreCase);
            bool cleanup = string.Equals(packageId, "PostUninstallCleanup",
                StringComparison.OrdinalIgnoreCase);
            bool infrastructure = string.Equals(packageId, "ViiperUsbipUninstall",
                StringComparison.OrdinalIgnoreCase);
            state = RequestState.None;
            if (!preflight && !cleanup && !infrastructure) return false;

            // An isolated recovery pass may touch only ViiperUsbipSetup.
            if (infrastructureRecoveryPass) return true;

            if (action == LaunchAction.Install || action == LaunchAction.Repair)
            {
                // These one-shot helpers must not execute during installation,
                // but their payloads must survive deletion of the downloaded
                // bundle. Requesting None leaves uninstall dependent on it (#76).
                state = RequestState.Cache;
            }
            else if (action == LaunchAction.Uninstall &&
                (preflight || relation != RelationType.Upgrade))
            {
                // Burn unwinds in reverse chain order. Always quiesce first;
                // an outgoing upgrade must preserve shared infrastructure.
                state = RequestState.Present;
            }

            return true;
        }
    }
}
