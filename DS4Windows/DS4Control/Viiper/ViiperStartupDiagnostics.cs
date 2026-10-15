using System.Collections.Generic;

namespace DS4Windows
{
    internal static class ViiperStartupDiagnostics
    {
        internal static IEnumerable<string> Describe(ViiperPrerequisiteStatus status)
        {
            yield return $"USB/IP (usbip-win2): installed version {status.UsbipVersion ?? "unknown or missing"}; required {ViiperSetupManager.RequiredUsbipVersion}";
            yield return $"USB/IP runtime: {(status.UsbipRuntimeReady ? "ready (driver communication succeeded)" : "not ready")}";
            if (!status.UsbipRuntimeReady && !string.IsNullOrWhiteSpace(status.UsbipProbeMessage))
                yield return "USB/IP diagnostic: " + status.UsbipProbeMessage;
            yield return $"USB/IP package verification: {(status.UsbipExecutableSafe && status.UsbipDriverFilesSafe ? "passed" : "not passed")}";
            yield return $"VIIPER: supported version {ViiperSetupManager.SupportedViiperReleaseTag.TrimStart('v')}; selected executable {(status.ViiperPackageCurrent ? "verified" : "not verified")}; server {(status.ServerRunning ? "running and reachable" : "not ready")}";
            yield return status.Ready ? "VIIPER virtual-controller backend ready"
                : "VIIPER virtual-controller backend not ready: " + status.DisplayText;
        }
    }
}
