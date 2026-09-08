#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS4Windows.Switch2;

// Explicit portable-lab contract, not a general command or GATT API. Only
// documented receiver-related packet shapes are admitted. In particular, the
// 17/02 payload's rate/channel/frame interpretation is a research hypothesis;
// only 18/03 value 07 is observed and other admitted low-bit values are hypotheses.
// No claim is made that 18/03 state is volatile or that an ACK proves playback.
internal sealed class Switch2BluetoothLabReceiverPlan
{
    internal const int MaximumJsonBytes = 1024 * 1024;
    internal const int MaximumResponseBytes = 16;
    public string Id { get; init; } = "";
    public ulong Generation { get; init; }
    public bool HeadsetNotifications { get; init; }
    public bool AllowExperimentalParameters { get; init; }
    public byte[][] Commands { get; init; } = Array.Empty<byte[]>();
    public Switch2BluetoothLabPlan? Audio { get; init; }

    internal static Switch2BluetoothLabReceiverPlan Parse(ReadOnlySpan<byte> json, ulong generation)
    {
        if (json.Length == 0 || json.Length > MaximumJsonBytes)
            throw new InvalidDataException("Receiver plan exceeds request limit.");
        var plan = JsonSerializer.Deserialize<Switch2BluetoothLabReceiverPlan>(json, new JsonSerializerOptions
        { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (plan == null || plan.Generation != generation)
            throw new InvalidDataException("Receiver plan belongs to another controller generation.");
        plan.Validate();
        return plan;
    }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Id) || Id.Length > 48 ||
            Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            Generation == 0 || Commands == null || Commands.Length is < 1 or > 8)
            throw new InvalidDataException("Invalid finite receiver plan identity or command count.");
        bool maskSelected = false;
        foreach (byte[] request in Commands)
        {
            if (request == null || !IsAllowedRequest(request))
                throw new InvalidDataException("Receiver plan contains a command outside the closed research allowlist.");
            if (!AllowExperimentalParameters &&
                ((request[0] == 0x17 && (request[12] != 2 || request[13] != 0xF0 || request[14] != 0)) ||
                 (request[0] == 0x18 && request[3] == 0x03 && request[8] != 7)))
                throw new InvalidDataException("Unobserved receiver parameter variants require explicit experimental opt-in.");
            if (request[0] != 0x0C) continue;
            if (request[3] == 0x02) maskSelected = true;
            else if (!maskSelected)
                throw new InvalidDataException("Pro feature mask selection must precede enable in the same plan.");
        }
        if (Audio == null) return;
        if (Audio.Generation != Generation || Audio.HeadsetNotifications)
            throw new InvalidDataException("Audio must share the receiver generation and its owner's headset window.");
        Audio.Validate(509);
    }

    internal static bool IsAllowedRequest(ReadOnlySpan<byte> request)
    {
        if (request.Length < 8 || request[1] != 0x91 || request[2] != 0x01 ||
            request[4] != 0 || request[6] != 0 || request[7] != 0) return false;
        return request[0] switch
        {
            0x0C => request.Length == 12 && request[3] is 0x02 or 0x04 && request[5] == 4 &&
                request[8] == 0x2F && request[9] == 0 && request[10] == 0 && request[11] == 0,
            0x17 => request.Length == 15 && request[3] == 0x02 && request[5] == 7 &&
                BinaryPrimitives.ReadUInt32LittleEndian(request.Slice(8, 4)) == 48000 &&
                request[12] is 1 or 2 && BinaryPrimitives.ReadUInt16LittleEndian(request.Slice(13, 2)) is 120 or 240 or 480 or 960,
            0x18 when request[3] == 0x01 => request.Length == 8 && request[5] == 0,
            0x18 when request[3] == 0x03 => request.Length == 9 && request[5] == 1 && request[8] <= 7,
            _ => false,
        };
    }

    internal static bool IsAcknowledged(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        if (!IsAllowedRequest(request) || response.Length < 8 || response.Length > MaximumResponseBytes ||
            response[0] != request[0] || response[1] != 1 || response[2] != 1 ||
            response[3] != request[3] || response[4] != 0x10 || response[5] != 0x78 ||
            response[6] != 0 || response[7] != 0) return false;
        return request[0] switch
        {
            0x0C => response.Length == 12 && response[8] == 0 && response[9] == 0 && response[10] == 0 && response[11] == 0,
            0x17 => response.Length == 8,
            0x18 when request[3] == 0x01 => response.Length == 16,
            0x18 when request[3] == 0x03 => response.Length == 9 && response[8] == request[8],
            _ => false,
        };
    }
}
