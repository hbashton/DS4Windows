using System.Buffers.Binary;
using DS4Windows.Switch2;

// Offline hypotheses only. This file is not compiled into DS4Windows: a new
// envelope can be tested on the already-running opaque packet-plan bridge.
internal static class PlanFramer
{
    internal static Switch2BluetoothLabPlan Apply(Switch2BluetoothLabPlan source, string framing)
    {
        source.Validate(509);
        if (framing == "pcm-pair5") return GroupStereoPcmFrames(source);
        string envelope = framing;
        int fixedLength = 0;
        int paddingAt = framing.IndexOf("-pad", StringComparison.Ordinal);
        if (paddingAt >= 0)
        {
            envelope = framing[..paddingAt];
            fixedLength = framing[(paddingAt + 4)..] switch
            { "64" => 64, "112" => 112, "128" => 128, "256" => 256, "480" => 480, "509" => 509, _ => 0 };
            if (fixedLength == 0 || envelope is not ("len16le" or "id0-len8" or "seq8-len8" or "pro-len8" or "proseq-len8"))
                throw new ArgumentException("Padding requires an explicit bounded encoded-length envelope.", nameof(framing));
        }
        int prefix = envelope switch
        {
            "id0" or "seq8" => 1,
            "id0-seq8" or "len16le" or "id0-len8" or "seq8-len8" => 2,
            "pro-raw" or "proseq-raw" => 33,
            "pro-len8" or "proseq-len8" => 34,
            _ => throw new ArgumentException("Unknown offline framing hypothesis.", nameof(framing))
        };
        var packets = new Switch2BluetoothLabPacket[source.Packets.Length];
        for (int i = 0; i < packets.Length; i++)
        {
            var input = source.Packets[i];
            int wireLength = fixedLength == 0 ? input.Payload.Length + prefix : fixedLength;
            if (input.Payload.Length + prefix > wireLength || wireLength > 509 || envelope.EndsWith("-len8", StringComparison.Ordinal) && input.Payload.Length > 255)
                throw new InvalidDataException("Framed packet exceeds its declared length or ATT budget.");
            var payload = new byte[wireLength];
            switch (envelope)
            {
                case "seq8": payload[0] = (byte)i; break;
                case "id0-seq8": payload[1] = (byte)i; break;
                case "len16le": BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)input.Payload.Length); break;
                case "id0-len8": payload[1] = (byte)input.Payload.Length; break;
                case "seq8-len8": payload[0] = (byte)i; payload[1] = (byte)input.Payload.Length; break;
            }
            if (envelope.StartsWith("pro", StringComparison.Ordinal))
            {
                // The adjacent documented Pro envelope has a leading zero
                // before two 16-byte groups (33 bytes, not 32). Its use on the
                // headphone lane remains an explicit hypothesis. Reuse the
                // production encoder for optional counter-bearing zero-force
                // groups; never duplicate rumble packing or send live rumble.
                if (envelope.StartsWith("proseq", StringComparison.Ordinal) &&
                    !Switch2BluetoothHdRumbleCodec.TryEncodeProController((byte)(i & 15), default, default, payload.AsSpan(0, 33)))
                    throw new InvalidDataException("Silent Pro envelope encoding failed.");
                if (envelope.EndsWith("-len8", StringComparison.Ordinal)) payload[33] = (byte)input.Payload.Length;
            }
            input.Payload.CopyTo(payload, prefix);
            packets[i] = new Switch2BluetoothLabPacket { OffsetMicroseconds = input.OffsetMicroseconds, Payload = payload };
        }
        var result = new Switch2BluetoothLabPlan { Id = source.Id + "-" + framing, Generation = source.Generation,
            HeadsetNotifications = source.HeadsetNotifications, Packets = packets };
        result.Validate(509);
        return result;
    }

    private static Switch2BluetoothLabPlan GroupStereoPcmFrames(Switch2BluetoothLabPlan source)
    {
        // 48 kHz stereo PCM: two 120-sample (480-byte) pieces form one
        // 240-sample frame at 5 ms. The split is an explicit hypothesis, not a
        // transport rule. Only normal-input mode is admitted after the earlier
        // high-rate headset-notification test hit its deadline.
        if (source.HeadsetNotifications || source.Id != "t-pcm-2-2_5-0-raw" || source.Packets.Length != 240)
            throw new InvalidDataException("PCM grouping requires the reviewed normal-input stereo 2.5 ms source.");
        var packets = new Switch2BluetoothLabPacket[source.Packets.Length];
        for (int i = 0; i < packets.Length; i++)
        {
            if (source.Packets[i].Payload.Length != 480 || source.Packets[i].OffsetMicroseconds != i * 2500)
                throw new InvalidDataException("Unexpected PCM frame size or source cadence.");
            packets[i] = new Switch2BluetoothLabPacket
            { OffsetMicroseconds = (i / 2) * 5000, Payload = (byte[])source.Packets[i].Payload.Clone() };
        }
        var result = new Switch2BluetoothLabPlan { Id = source.Id + "-pcm-pair5", Generation = source.Generation, Packets = packets };
        result.Validate(509);
        return result;
    }
}
