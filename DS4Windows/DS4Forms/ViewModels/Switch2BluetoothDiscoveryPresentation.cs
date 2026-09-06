using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms.ViewModels;

internal static class Switch2BluetoothDiscoveryPresentation
{
    internal static string Describe(Switch2BluetoothDiscoveryStatus status,
        int candidateCount) => status?.State switch
    {
        Switch2BluetoothDiscoveryState.Stopped =>
            "Bluetooth discovery is stopped. Use DS4Windows' Stop/Start controls to start discovery.",
        Switch2BluetoothDiscoveryState.Starting =>
            "Starting Bluetooth discovery. Refresh in a moment to check its status.",
        Switch2BluetoothDiscoveryState.Scanning when candidateCount > 0 =>
            $"Bluetooth discovery is active. {candidateCount} controller{(candidateCount == 1 ? string.Empty : "s")} available to associate or reconnect.",
        Switch2BluetoothDiscoveryState.Scanning =>
            "Bluetooth discovery is active. No new controllers found yet. Wake an associated controller to reconnect; use sync mode only to associate a new controller, then refresh.",
        Switch2BluetoothDiscoveryState.Unavailable =>
            "Windows did not provide a usable Bluetooth adapter. Check that Bluetooth is available and turned on, then stop and start DS4Windows to retry discovery.",
        Switch2BluetoothDiscoveryState.StartFailed =>
            $"Bluetooth discovery could not start{DescribeFailure(status.Failure)}. Stop and start DS4Windows to retry; check the log if it fails again.",
        Switch2BluetoothDiscoveryState.Interrupted =>
            "Bluetooth discovery stopped unexpectedly. Stop and start DS4Windows to retry discovery.",
        Switch2BluetoothDiscoveryState.Stopping =>
            "Bluetooth discovery is still cleaning up. Wait a moment, then retry Stop before starting or associating another controller.",
        Switch2BluetoothDiscoveryState.CleanupFailed =>
            "Bluetooth cleanup did not complete safely. Discovery cannot restart yet. Check the log; do not launch a second instance to bypass cleanup.",
        _ => "Bluetooth discovery status is unavailable. Refresh to check again.",
    };

    internal static string ActionLabel(bool isRemembered) =>
        isRemembered ? "Reconnect selected" : "Associate selected";

    internal static string DescribeReconnect(Switch2BluetoothWindowsAssociationResult result) =>
        result.Succeeded ? "Controller reconnected. Its existing association was preserved. Joy-Con halves may still need a pair selection." :
        result.Failure == Switch2BluetoothWindowsAssociationFailure.SlotActivationRejected ?
            "The controller was reached, but its controller slot could not activate. Its association was preserved. Check the log, then wake it and retry after cleanup completes." :
            $"Reconnect failed: {result.Failure}. Its association was not changed. Wake it and refresh after cleanup completes; check the log if it remains unavailable.";

    private static string DescribeFailure(Switch2BluetoothWindowsScanStartFailure failure) =>
        failure == Switch2BluetoothWindowsScanStartFailure.None ? string.Empty :
            $" ({failure})";
}
