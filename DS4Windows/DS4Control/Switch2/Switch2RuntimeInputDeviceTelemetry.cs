using System.Diagnostics;
using System.Threading;

namespace DS4Windows.Switch2;

public sealed partial class Switch2RuntimeInputDevice
{
    private readonly bool inputGapTelemetryEnabled = Switch2BluetoothLabProbe.IsInputGapEnabled;
    private Switch2InputGapTelemetry inputGapTelemetry;
    private Switch2BluetoothLabProbe inputGapProbe;

    internal Switch2InputGapSnapshot CaptureInputGapSnapshot()
    {
        lock (publicationGate)
            return new(inputGapTelemetryEnabled, "accepted-physical-report-publication-gaps",
                RuntimeGeneration, DeviceType, transport, joyConBindingMode, pairEpoch,
                leftDeviceGeneration, leftTransportGeneration,
                rightDeviceGeneration, rightTransportGeneration,
                runtimeState, terminalNeutralReserved, Stopwatch.GetTimestamp(),
                Stopwatch.Frequency, inputGapTelemetry.Snapshot());
    }

    // Called only at cold StartUpdate while already owning publicationGate.
    private void StartInputGapProbeNoLock()
    {
        if (inputGapTelemetryEnabled && inputGapProbe == null)
            inputGapProbe = Switch2BluetoothLabProbe.TryCreateInputGapProbe(
                CaptureInputGapSnapshot);
    }

    // A terminal request may originate in an input callback. Only enqueue its
    // already-owned probe for cancellation: no pipe disposal/join on that path.
    private void StopInputGapProbeNoLock()
    {
        Switch2BluetoothLabProbe probe = inputGapProbe;
        inputGapProbe = null;
        if (probe != null)
            ThreadPool.QueueUserWorkItem(static state =>
                { _ = ((Switch2BluetoothLabProbe)state).StopAsync(); }, probe);
    }
}
