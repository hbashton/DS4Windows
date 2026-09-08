using System;

namespace DS4Windows.Switch2;

// Shared with the external client. This is a finite synthetic-audio grammar,
// never a raw byte/UUID/setup-command API. No file, gain or duration parameters.
internal readonly record struct Switch2BluetoothLabCandidate(
    bool Active, bool Pcm, int Channels, int FrameMicroseconds, int Bitrate,
    int PrefixBytes, bool LengthPrefix, bool ForceFullband)
{
    internal double IntervalMilliseconds => FrameMicroseconds / 1000.0;
    internal int Samples => 48 * FrameMicroseconds / 1000;

    internal static bool TryParse(string command, out Switch2BluetoothLabCandidate result)
    {
        result = default;
        if (command == null || command.Length > 32) return false;
        bool active = command.StartsWith("active-", StringComparison.Ordinal);
        string legacy = active ? "tone-" + command.Substring(7) : command;
        if (legacy is "tone-opus5" or "tone-opus20" or "tone-rumble-opus5" or "tone-rumble-opus20" or
            "tone-length-opus5" or "tone-length-opus20" or "tone-rumble-length-opus5" or "tone-rumble-length-opus20")
        {
            bool length = legacy.Contains("-length-", StringComparison.Ordinal);
            result = new(active, false, 2, legacy.EndsWith("20", StringComparison.Ordinal) ? 20000 : 5000,
                80000, (legacy.StartsWith("tone-rumble-", StringComparison.Ordinal) ? 32 : 0) + (length ? 1 : 0), length, false);
            return true;
        }

        string[] fields = command.Split(':');
        if (fields.Length != 6 || fields[0] is not ("t" or "a") || fields[1] is not ("opus" or "pcm")) return false;
        int channels = fields[2] switch { "1" => 1, "2" => 2, _ => 0 };
        int micros = fields[3] switch { "2.5" => 2500, "5" => 5000, "10" => 10000, "20" => 20000, _ => 0 };
        bool pcm = fields[1] == "pcm";
        int bitrate = fields[4] switch { "0" => 0, "20" => 20000, "40" => 40000, "80" => 80000, "160" => 160000, _ => -1 };
        int prefix = fields[5] switch { "raw" => 0, "len" => 1, "rum" => 32, "rumlen" => 33, _ => -1 };
        if (channels == 0 || micros == 0 || prefix < 0 || bitrate < 0 ||
            (pcm ? bitrate != 0 || micros > 5000 : bitrate == 0)) return false;
        int bytes = pcm ? 48 * micros / 1000 * channels * 2 : (int)((long)bitrate * micros / 8000000);
        bool lengthPrefix = prefix is 1 or 33;
        // Preflight before setup/notification changes. Negotiated MTU is checked
        // again immediately before transmission; unknown framing is never split.
        if (bytes < 10 || bytes + prefix > 509 || lengthPrefix && bytes > 255) return false;
        result = new(fields[0] == "a", pcm, channels, micros, bitrate, prefix, lengthPrefix, !pcm);
        return true;
    }

    internal static string WithoutHeadset(string command) => command.StartsWith("active-", StringComparison.Ordinal)
        ? "tone-" + command.Substring(7) : command.StartsWith("a:", StringComparison.Ordinal) ? "t:" + command.Substring(2) : command;
}
