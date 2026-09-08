using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

// Cleanup owns actual operation completion, not a cancelled wait. Always try
// restoring common input after headset disable finishes, even if it failed.
internal static class Switch2BluetoothLabNotificationCleanup
{
    internal static async Task<Switch2BluetoothLabNotificationCleanupResult> RunAsync(
        Func<Task<string>> disableHeadset, Func<Task<string>> restoreCommonInput)
    {
        ArgumentNullException.ThrowIfNull(disableHeadset);
        ArgumentNullException.ThrowIfNull(restoreCommonInput);
        string headsetStatus = null, commonInputStatus = null;
        Exception headsetFailure = null, commonInputFailure = null;
        try { headsetStatus = await disableHeadset().ConfigureAwait(false); }
        catch (Exception error) { headsetFailure = error; }
        try { commonInputStatus = await restoreCommonInput().ConfigureAwait(false); }
        catch (Exception error) { commonInputFailure = error; }
        return new Switch2BluetoothLabNotificationCleanupResult(
            headsetStatus, commonInputStatus, headsetFailure, commonInputFailure);
    }
}

internal readonly record struct Switch2BluetoothLabNotificationCleanupResult(
    string HeadsetStatus, string CommonInputStatus,
    Exception HeadsetFailure, Exception CommonInputFailure)
{
    internal bool Fenced => HeadsetFailure != null || CommonInputFailure != null ||
        HeadsetStatus != "Success" || CommonInputStatus != "Success";

    internal void ThrowIfFailed()
    {
        if (HeadsetFailure != null && CommonInputFailure != null)
            throw new AggregateException("Both notification restoration operations failed.",
                HeadsetFailure, CommonInputFailure);
        if (HeadsetFailure != null) ExceptionDispatchInfo.Capture(HeadsetFailure).Throw();
        if (CommonInputFailure != null) ExceptionDispatchInfo.Capture(CommonInputFailure).Throw();
    }
}
