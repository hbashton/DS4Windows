using System.Buffers.Binary;
using DS4Windows.Switch2;

// Offline hypotheses only. This file is not compiled into DS4Windows: a new
// envelope can be tested on the already-running opaque packet-plan bridge.
internal static class PlanFramer
{
    internal static Switch2BluetoothLabPlan Apply(Switch2BluetoothLabPlan source, string framing)
    {
        source.Validate(509);
        int prefix = framing switch
        {
            "id0" or "seq8" => 1,
            "id0-seq8" or "len16le" or "id0-len8" or "seq8-len8" => 2,
            _ => throw new ArgumentException("Unknown offline framing hypothesis.", nameof(framing))
        };
        var packets = new Switch2BluetoothLabPacket[source.Packets.Length];
        for (int i = 0; i < packets.Length; i++)
        {
            var input = source.Packets[i];
            if (input.Payload.Length + prefix > 509 || framing.EndsWith("-len8", StringComparison.Ordinal) && input.Payload.Length > 255)
                throw new InvalidDataException("Framed packet exceeds its declared length or ATT budget.");
            var payload = new byte[input.Payload.Length + prefix];
            switch (framing)
            {
                case "seq8": payload[0] = (byte)i; break;
                case "id0-seq8": payload[1] = (byte)i; break;
                case "len16le": BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)input.Payload.Length); break;
                case "id0-len8": payload[1] = (byte)input.Payload.Length; break;
                case "seq8-len8": payload[0] = (byte)i; payload[1] = (byte)input.Payload.Length; break;
            }
            input.Payload.CopyTo(payload, prefix);
            packets[i] = new Switch2BluetoothLabPacket { OffsetMicroseconds = input.OffsetMicroseconds, Payload = payload };
        }
        var result = new Switch2BluetoothLabPlan { Id = source.Id + "-" + framing, Generation = source.Generation,
            HeadsetNotifications = source.HeadsetNotifications, Packets = packets };
        result.Validate(509);
        return result;
    }
}
