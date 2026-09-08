using System.Buffers.Binary;
using Concentus;
using DS4Windows.Switch2;

// Offline adjacent-protocol hypothesis, not a discovered Nintendo BLE format.
// libnx's console hwopus IPC input and Xiph's opus_demo test streams prepend
// u32 BE encoded size and u32 BE entropy-coder final range to an Opus packet:
// https://github.com/switchbrew/libnx/blob/master/nx/include/switch/services/hwopus.h
// https://github.com/xiph/opus/blob/main/src/opus_demo.c
// Neither source establishes use of this envelope by controller headphones.
internal static class HwOpusPlanFactory
{
    internal static Switch2BluetoothLabPlan Create(string mode, ulong generation)
    {
        if (mode is not ("t" or "a") || generation == 0)
            throw new ArgumentException("Expected t/a and a nonzero generation for the fixed 20 ms stereo hwopus hypothesis.");

        // Reuse reviewed encoding, gain, fades and quiet tail verbatim. These
        // are the same 200-byte frames already used by the raw Opus trial.
        var source = Switch2BluetoothLabTone.Create("t:opus:2:20:80:raw");
        using var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        short[] decoded = new short[960 * 2];
        var packets = new Switch2BluetoothLabPacket[source.Packets.Length];
        for (int frame = 0; frame < packets.Length; frame++)
        {
            byte[] encoded = source.Packets[frame];
            if (encoded.Length != 200 || decoder.Decode(encoded, decoded.AsSpan(), 960, false) != 960)
                throw new InvalidDataException("Unexpected fixed 20 ms stereo Opus source.");
            // A conforming decoder reaches the encoder's final range after the
            // same packet. Tests cross-check against an actual source encoder;
            // do not invent a checksum or substitute a fixed zero range.
            byte[] value = new byte[208];
            BinaryPrimitives.WriteUInt32BigEndian(value, (uint)encoded.Length);
            BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(4), decoder.FinalRange);
            encoded.CopyTo(value, 8);
            packets[frame] = new Switch2BluetoothLabPacket
            {
                OffsetMicroseconds = frame * 20000,
                Payload = value
            };
        }

        var plan = new Switch2BluetoothLabPlan
        {
            Id = $"{mode}-hwopus-20-stereo-range",
            Generation = generation,
            HeadsetNotifications = mode == "a",
            Packets = packets
        };
        plan.Validate(509);
        return plan;
    }
}
