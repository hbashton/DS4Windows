using System;
using DS4Windows.InputDevices;

namespace DS4Windows.Switch2;

// Diagnostic-only counters owned by the existing publication gate. These are
// accepted physical-report publication gaps, not radio packet-loss evidence.
internal struct Switch2InputGapTelemetry
{
    private long accepted, busy, duplicates, invalid, clockEpoch;
    private long first, last, frequency, intervals, maximumGap;
    private long over10, over20, over50, over100;

    internal void ObservePublicationBusy() => busy++;

    internal void Observe(long timestampQpc, long qpcFrequency)
    {
        accepted++;
        if (timestampQpc < 0 || qpcFrequency <= 0)
        {
            invalid++;
            ResetClock(0, 0);
            return;
        }
        if (frequency != qpcFrequency || timestampQpc < last)
        {
            ResetClock(timestampQpc, qpcFrequency);
            return;
        }
        if (timestampQpc == last)
        {
            duplicates++;
            return;
        }
        long gap = timestampQpc - last;
        last = timestampQpc;
        intervals++;
        maximumGap = Math.Max(maximumGap, gap);
        double milliseconds = gap * 1000.0 / frequency;
        if (milliseconds > 10) over10++;
        if (milliseconds > 20) over20++;
        if (milliseconds > 50) over50++;
        if (milliseconds > 100) over100++;
    }

    private void ResetClock(long timestamp, long qpcFrequency)
    {
        clockEpoch++;
        first = last = timestamp;
        frequency = qpcFrequency;
        intervals = maximumGap = over10 = over20 = over50 = over100 = 0;
    }

    internal readonly Switch2InputGapCounters Snapshot() => new(accepted, busy,
        duplicates, invalid, clockEpoch, first, last, frequency, intervals,
        maximumGap, over10, over20, over50, over100);
}

// Public getters are serialized only by the existing cold lab-probe worker.
// No addresses, paths, controller input values, device names or MACs are stored.
internal readonly record struct Switch2InputGapCounters(
    long AcceptedPublications, long PublicationBusy, long DuplicateTimestamps,
    long InvalidTimestamps, long ClockEpoch, long FirstCompletionQpc,
    long LastCompletionQpc, long QpcFrequency, long IntervalCount,
    long MaximumGapQpc, long GapsOver10Milliseconds, long GapsOver20Milliseconds,
    long GapsOver50Milliseconds, long GapsOver100Milliseconds)
{
    public double MaximumGapMilliseconds => QpcFrequency > 0
        ? MaximumGapQpc * 1000.0 / QpcFrequency : 0;
}

internal readonly record struct Switch2InputGapSnapshot(
    bool Enabled, string Measurement, ulong RuntimeGeneration,
    InputDeviceType Model, Switch2Transport Transport,
    Switch2JoyConRuntimeBindingMode BindingMode, ulong PairEpoch,
    ulong LeftDeviceGeneration, ulong LeftTransportGeneration,
    ulong RightDeviceGeneration, ulong RightTransportGeneration,
    Switch2RuntimeInputDeviceState RuntimeState, bool TerminalReserved,
    long SnapshotQpc, long SnapshotQpcFrequency, Switch2InputGapCounters Counters)
{
    public string ModelName => Model.ToString();
    public string TransportName => Transport.ToString();
    public string RuntimeStateName => RuntimeState.ToString();
}
