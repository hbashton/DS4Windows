using System;

namespace DS4Windows.Switch2;

internal static class Switch2BluetoothLabHeadsetHeader
{
    // Dedicated 112-byte headset report only, not common05 or Pro09. Retain
    // no PCM, buttons, motion or encoded microphone bytes in diagnostics.
    internal static bool TryRead(ReadOnlySpan<byte> report, out byte jack, out byte length, out bool opusIdle)
    {
        jack = length = 0;
        opusIdle = false;
        if (report.Length != 112 || report[14] is not (0 or 50)) return false;
        jack = report[13]; length = report[14];
        opusIdle = length == 50 && report[15] == 0xf8 && report[16] == 0xff && report[17] == 0xfe;
        for (int i = 18; i < 65 && opusIdle; i++) opusIdle &= report[i] == 0;
        return true;
    }
}
