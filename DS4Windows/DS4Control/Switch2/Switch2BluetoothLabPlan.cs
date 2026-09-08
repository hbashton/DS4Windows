using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS4Windows.Switch2;

// Transport-only contract shared with the external experiment generator. No
// codec/framing switch belongs here. Payloads can only reach the dedicated
// headphone characteristic on the existing lease; not commands, bonds or flash.
internal sealed class Switch2BluetoothLabPlan
{
    internal const int MaximumJsonBytes = 1024 * 1024;
    public string Id { get; init; } = "";
    public ulong Generation { get; init; }
    public bool HeadsetNotifications { get; init; }
    public Switch2BluetoothLabPacket[] Packets { get; init; } = Array.Empty<Switch2BluetoothLabPacket>();

    internal static Switch2BluetoothLabPlan Parse(ReadOnlySpan<byte> json, ulong generation)
    {
        if (json.Length == 0 || json.Length > MaximumJsonBytes) throw new InvalidDataException("Plan exceeds request limit.");
        var plan = JsonSerializer.Deserialize<Switch2BluetoothLabPlan>(json, new JsonSerializerOptions
        { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (plan == null || plan.Generation != generation) throw new InvalidDataException("Plan belongs to another controller generation.");
        plan.Validate(509);
        return plan;
    }

    internal void Validate(int maximumWriteBytes)
    {
        if (string.IsNullOrEmpty(Id) || Id.Length > 48 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            Generation == 0 || Packets == null || Packets.Length is < 1 or > 1024)
            throw new InvalidDataException("Invalid finite plan identity or packet count.");
        int previous = -1, total = 0, sameDeadline = 0;
        foreach (var packet in Packets)
        {
            if (packet == null || packet.Payload == null || packet.Payload.Length < 1 ||
                packet.Payload.Length > Math.Min(509, maximumWriteBytes) ||
                packet.OffsetMicroseconds < previous || packet.OffsetMicroseconds is < 0 or > 999000)
                throw new InvalidDataException("Invalid packet size or one-second schedule.");
            sameDeadline = packet.OffsetMicroseconds == previous ? sameDeadline + 1 : 1;
            if (sameDeadline > 4) throw new InvalidDataException("At most four packet fragments per scheduled frame.");
            previous = packet.OffsetMicroseconds;
            total += packet.Payload.Length;
        }
        if (Packets[0].OffsetMicroseconds != 0 || total > 256 * 1024)
            throw new InvalidDataException("Plan must begin at zero and stay within the byte budget.");
    }

    internal string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

internal sealed class Switch2BluetoothLabPacket
{
    public int OffsetMicroseconds { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}
