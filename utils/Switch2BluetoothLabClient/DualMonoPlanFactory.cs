using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using DS4Windows.Switch2;

// Offline hypothesis: two separately encoded 50-byte mono channels, motivated
// by the observed 50-byte mono-compatible INPUT audio region. No claim that
// Nintendo headphone output uses this codec, channel layout or envelope.
internal static class DualMonoPlanFactory
{
    internal static Switch2BluetoothLabPlan Create(string mode, int frameMilliseconds, ulong generation, string framing)
    {
        if (mode is not ("t" or "a") || frameMilliseconds is not (5 or 20) || generation == 0 ||
            framing is not ("raw" or "len8" or "lengths" or "id0-len8"))
            throw new ArgumentException("Expected t/a, 5/20 ms and raw/len8/lengths/id0-len8 dual-mono framing.");
        // Reuse the reviewed stereo PCM source verbatim before deinterleaving;
        // do not duplicate waveform/gain/fade/silence generation.
        var source = Switch2BluetoothLabTone.Create("t:pcm:2:2.5:0:raw");
        int samplesPerFrame = frameMilliseconds * 48;
        using var leftEncoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        using var rightEncoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        foreach (var encoder in new[] { leftEncoder, rightEncoder })
        {
            encoder.Bitrate = 400000 / frameMilliseconds; // exactly 50 encoded bytes per channel/frame
            encoder.UseVBR = false;
            encoder.Complexity = 5;
            encoder.ForceChannels = 1;
            encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
        }
        short[] left = new short[samplesPerFrame], right = new short[samplesPerFrame];
        byte[] encodedLeft = new byte[50], encodedRight = new byte[50];
        var packets = new Switch2BluetoothLabPacket[600 / frameMilliseconds];
        for (int frame = 0; frame < packets.Length; frame++)
        {
            for (int sample = 0; sample < samplesPerFrame; sample++)
            {
                int position = frame * samplesPerFrame + sample;
                var pcm = source.Packets[position / 120].AsSpan((position % 120) * 4, 4);
                left[sample] = BinaryPrimitives.ReadInt16LittleEndian(pcm);
                right[sample] = BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]);
            }
            if (leftEncoder.Encode(left, samplesPerFrame, encodedLeft, 50) != 50 ||
                rightEncoder.Encode(right, samplesPerFrame, encodedRight, 50) != 50)
                throw new InvalidDataException("Unexpected fixed-size dual-mono encoding.");
            int prefix = framing == "id0-len8" ? 1 : 0;
            byte[] value = new byte[framing == "raw" ? 100 : 102 + prefix];
            if (framing == "raw") { encodedLeft.CopyTo(value, 0); encodedRight.CopyTo(value, 50); }
            else if (framing == "lengths")
            {
                value[0] = value[1] = 50;
                encodedLeft.CopyTo(value, 2); encodedRight.CopyTo(value, 52);
            }
            else
            {
                value[prefix] = value[prefix + 51] = 50;
                encodedLeft.CopyTo(value, prefix + 1); encodedRight.CopyTo(value, prefix + 52);
            }
            packets[frame] = new Switch2BluetoothLabPacket { OffsetMicroseconds = frame * frameMilliseconds * 1000, Payload = value };
        }
        var result = new Switch2BluetoothLabPlan { Id = $"{mode}-dualmono-{frameMilliseconds}-{framing}",
            Generation = generation, HeadsetNotifications = mode == "a", Packets = packets };
        result.Validate(509);
        return result;
    }
}
