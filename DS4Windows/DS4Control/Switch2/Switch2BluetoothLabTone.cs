using System;
using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;

namespace DS4Windows.Switch2;

/// <summary>
/// Finite, synthetic headphone-format hypotheses, NOT a production encoder.
/// The dedicated UUID and 17/02 setup are evidenced by ndeadly d1c5a7f.
/// Opus is a candidate from the published F8 FF FE idle audio (switch2mac
/// ea6719f); neither raw packets nor a rumble prefix are confirmed framing.
/// Never consume a microphone, stream a file, or accept arbitrary wire bytes.
/// </summary>
internal sealed partial class Switch2BluetoothLabTone
{
    internal const int SampleRate = 48000;
    internal const double SourcePeak = .005;
    internal const int AudibleMilliseconds = 500;
    internal const int TailMilliseconds = 100;
    internal byte[][] Packets { get; private init; } = Array.Empty<byte[]>();
    internal double IntervalMilliseconds { get; private init; }
    internal int Channels { get; private init; }
    internal int PrefixBytes { get; private init; }
    internal string Hypothesis { get; private init; } = "";
    internal string Codec { get; private init; } = "";
    internal int Bitrate { get; private init; }

    internal static bool IsActiveTone(string command) => Switch2BluetoothLabCandidate.TryParse(command, out var candidate) && candidate.Active;

    internal static bool IsTone(string command) => Switch2BluetoothLabCandidate.TryParse(command, out _);

    internal static Switch2BluetoothLabTone Create(string command)
    {
        if (!Switch2BluetoothLabCandidate.TryParse(command, out var candidate)) throw new ArgumentException("Unknown finite tone hypothesis.", nameof(command));
        command = Switch2BluetoothLabCandidate.WithoutHeadset(command);
        double interval = candidate.IntervalMilliseconds;
        int prefix = candidate.PrefixBytes, channels = candidate.Channels, samples = candidate.Samples;
        int count = (AudibleMilliseconds + TailMilliseconds) * 1000 / candidate.FrameMicroseconds;
        using var encoder = candidate.Pcm ? null : OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        if (encoder != null)
        {
            encoder.Bitrate = candidate.Bitrate;
            encoder.UseVBR = false;
            encoder.Complexity = 5;
            if (candidate.ForceFullband)
            {
                encoder.ForceChannels = channels;
                encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
            }
        }
        var pcm = new short[samples * channels];
        var encoded = new byte[512];
        var packets = new byte[count][];
        for (int frame = 0; frame < count; frame++)
        {
            for (int i = 0; i < samples; i++)
            {
                int position = frame * samples + i;
                int audibleSamples = SampleRate * AudibleMilliseconds / 1000;
                double fade = position >= audibleSamples ? 0 :
                    Math.Max(0, Math.Min(1, Math.Min(position / 480.0, (audibleSamples - 1 - position) / 480.0)));
                for (int channel = 0; channel < channels; channel++)
                    pcm[i * channels + channel] = (short)(32767 * SourcePeak * fade *
                        Math.Sin(2 * Math.PI * (channel == 0 ? 440 : 660) * position / SampleRate));
            }
            int length;
            if (candidate.Pcm)
            {
                length = pcm.Length * 2;
                for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(encoded.AsSpan(i * 2), pcm[i]);
            }
            else
            {
                if (encoder == null) throw new InvalidOperationException("Missing external encoder.");
                length = encoder.Encode(pcm.AsSpan(), samples, encoded.AsSpan(), encoded.Length);
            }
            if (length <= 0 || length + prefix > 509 || candidate.LengthPrefix && length > 255)
                throw new InvalidOperationException("Unexpected bounded audio frame size.");
            var packet = new byte[prefix + length];
            // The 32-byte prefix hypothesis reserves two silent rumble blocks.
            // Zero blocks do not invent command, volume or firmware fields.
            // One-byte length is an additional hypothesis motivated by the
            // length-prefixed *input* audio region; output parity is NOT known.
            if (candidate.LengthPrefix) packet[prefix - 1] = (byte)length;
            encoded.AsSpan(0, length).CopyTo(packet.AsSpan(prefix));
            packets[frame] = packet;
        }
        return new Switch2BluetoothLabTone { Packets = packets, IntervalMilliseconds = interval,
            Channels = channels, PrefixBytes = prefix, Hypothesis = command,
            Codec = candidate.Pcm ? "pcm16le" : "opus", Bitrate = candidate.Bitrate };
    }

}
