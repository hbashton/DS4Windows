using System;

namespace DS4Windows.Switch2;

// Optional portable-lab observation only. Fixed-size counters, no input
// injection, allocation, logging, locks or new worker on the report path.
internal struct Switch2MouseTraceWindow
{
    internal const int MaximumWindows = 30;
    private int windows;
    private long start, frequency;
    private bool started;
    private Switch2MouseTraceSide left, right;
    private Switch2MouseTraceSummary summary;

    internal bool TrySample(in Switch2JoyConRawInputStatus raw,
        out Switch2MouseTraceSummary result)
    {
        result = default;
        if (windows >= MaximumWindows || !raw.IsValid ||
            raw.QpcFrequency <= 0 || raw.CompletionTimestampQpc < 0)
            return false;
        if (!started || frequency != raw.QpcFrequency || raw.CompletionTimestampQpc < start)
        {
            started = true;
            frequency = raw.QpcFrequency;
            start = raw.CompletionTimestampQpc;
            left = right = default;
            summary = default;
        }
        summary.Reports++;
        if (raw.LeftPresent && raw.LeftHasCommonMotion)
            summary.LeftGyroPeak = Math.Max(summary.LeftGyroPeak, Peak(raw.LeftGyroscope));
        if (raw.RightPresent && raw.RightHasCommonMotion)
            summary.RightGyroPeak = Math.Max(summary.RightGyroPeak, Peak(raw.RightGyroscope));
        summary.LeftChanges += left.Observe(raw.LeftPresent && raw.LeftHasCommonMotion,
            raw.LeftDeviceGeneration, raw.LeftTransportGeneration, raw.LeftIrX, raw.LeftIrY);
        summary.RightChanges += right.Observe(raw.RightPresent && raw.RightHasCommonMotion,
            raw.RightDeviceGeneration, raw.RightTransportGeneration, raw.RightIrX, raw.RightIrY);
        if ((raw.CompletionTimestampQpc - start) / (double)frequency < 2.0)
            return false;
        summary.LeftPresent = raw.LeftPresent;
        summary.RightPresent = raw.RightPresent;
        summary.LeftIrX = raw.LeftIrX;
        summary.LeftIrY = raw.LeftIrY;
        summary.LeftIrDistance = raw.LeftIrDistance;
        summary.LeftIrRoughness = raw.LeftIrRoughness;
        summary.RightIrX = raw.RightIrX;
        summary.RightIrY = raw.RightIrY;
        summary.RightIrDistance = raw.RightIrDistance;
        summary.RightIrRoughness = raw.RightIrRoughness;
        result = summary;
        summary = default;
        start = raw.CompletionTimestampQpc;
        windows++;
        return true;
    }

    private static int Peak(in Switch2Vector3Raw value) => Math.Max(
        Math.Abs((int)value.X), Math.Max(Math.Abs((int)value.Y), Math.Abs((int)value.Z)));

    private struct Switch2MouseTraceSide
    {
        private bool seen;
        private ulong device, transport;
        private ushort x, y;

        internal int Observe(bool present, ulong nextDevice, ulong nextTransport,
            ushort nextX, ushort nextY)
        {
            bool changed = present && seen && device == nextDevice &&
                transport == nextTransport && (x != nextX || y != nextY);
            seen = present;
            device = nextDevice;
            transport = nextTransport;
            x = nextX;
            y = nextY;
            return changed ? 1 : 0;
        }
    }
}

internal struct Switch2MouseTraceSummary
{
    internal int Reports, LeftChanges, RightChanges;
    internal int LeftGyroPeak, RightGyroPeak;
    internal GyroOutMode GyroMode;
    internal bool ZLHeld, LHeld, RHeld;
    internal double MappedYaw, MappedPitch;
    internal bool LeftPresent, RightPresent;
    internal ushort LeftIrX, LeftIrY, LeftIrDistance, LeftIrRoughness;
    internal ushort RightIrX, RightIrY, RightIrDistance, RightIrRoughness;
    internal bool Enabled, HighRate, CustomMapper;
    internal Switch2IrMouseSource Source;
    internal Switch2IrActivationThreshold LeftThreshold, RightThreshold;
}
