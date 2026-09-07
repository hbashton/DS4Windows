using System.Text.Json;
using Concentus;
using Concentus.Structs;

// Offline hypothesis check only. This program cannot open a controller, play
// sound, record a microphone, subscribe to GATT, or create an audio endpoint.
// Both fixtures come from the published silence description, NOT this machine.
if (args.Length != 1 || args[0] != "--reference-idle")
{
    Console.Error.WriteLine("Use --reference-idle for an offline codec hypothesis check; no hardware is accessed.");
    return 2;
}

const int sampleRate = 48_000;
const int maximumFrameSamples = sampleRate * 120 / 1000;
foreach (int length in new[] { 3, 50 })
{
    byte[] packet = new byte[length];
    packet[0] = 0xf8;
    packet[1] = 0xff;
    packet[2] = 0xfe;
    using IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(sampleRate, 1);
    float[] decoded = new float[maximumFrameSamples];
    int count = decoder.Decode(packet.AsSpan(), decoded.AsSpan(), maximumFrameSamples, false);
    if (count <= 0 || count > decoded.Length)
        throw new InvalidOperationException("Decoder returned an invalid sample count.");
    double energy = 0, peak = 0;
    for (int index = 0; index < count; index++)
    {
        if (!float.IsFinite(decoded[index])) throw new InvalidOperationException("Non-finite decoder output.");
        energy += (double)decoded[index] * decoded[index];
        peak = Math.Max(peak, Math.Abs(decoded[index]));
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Fixture = length == 3 ? "Known Opus silence" : "Published Switch 2 idle region (synthetic reconstruction)",
        PacketBytes = length,
        SampleRateHz = sampleRate,
        EncodedChannels = OpusPacketInfo.GetNumEncodedChannels(packet),
        SamplesPerChannel = count,
        DurationMilliseconds = 1000.0 * count / sampleRate,
        Rms = Math.Sqrt(energy / count),
        Peak = peak,
        HardwareAccessed = false,
        ControllerCodecConfirmed = false,
        BluetoothPlaybackConfirmed = false,
    }));
}
return 0;
