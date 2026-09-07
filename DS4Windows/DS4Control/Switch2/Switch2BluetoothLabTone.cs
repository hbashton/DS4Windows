using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
internal sealed class Switch2BluetoothLabTone
{
    internal const int SampleRate = 48000;
    internal const double SourcePeak = .005;
    internal const int AudibleMilliseconds = 500;
    internal const int TailMilliseconds = 100;
    internal byte[][] Packets { get; private init; }
    internal int IntervalMilliseconds { get; private init; }
    internal int Channels { get; private init; }
    internal int PrefixBytes { get; private init; }
    internal string Hypothesis { get; private init; }

    internal static bool IsActiveTone(string command) => command is
        "active-opus5" or "active-opus20" or "active-rumble-opus5" or "active-rumble-opus20" or
        "active-length-opus5" or "active-length-opus20" or "active-rumble-length-opus5" or "active-rumble-length-opus20";

    internal static bool IsTone(string command) => IsActiveTone(command) || command is
        "tone-opus5" or "tone-opus20" or "tone-rumble-opus5" or "tone-rumble-opus20" or
        "tone-length-opus5" or "tone-length-opus20" or "tone-rumble-length-opus5" or "tone-rumble-length-opus20";

    internal static Switch2BluetoothLabTone Create(string command)
    {
        if (!IsTone(command)) throw new ArgumentException("Unknown finite tone hypothesis.", nameof(command));
        if (IsActiveTone(command)) command = "tone-" + command.Substring(7);
        int interval = command.EndsWith("20", StringComparison.Ordinal) ? 20 : 5;
        int prefix = command.StartsWith("tone-rumble-", StringComparison.Ordinal) ? 32 : 0;
        bool lengthPrefix = command.Contains("-length-", StringComparison.Ordinal);
        if (lengthPrefix) prefix++;
        const int channels = 2;
        int samples = SampleRate * interval / 1000;
        int count = (AudibleMilliseconds + TailMilliseconds) / interval;
        using var encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = 80000; // 50 / 200 bytes per 5 / 20 ms, including Opus TOC.
        encoder.UseVBR = false;
        encoder.Complexity = 5;
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
            int length = encoder.Encode(pcm.AsSpan(), samples, encoded.AsSpan(), encoded.Length);
            if (length <= 0 || length > 255) throw new InvalidOperationException("Unexpected bounded Opus frame size.");
            var packet = new byte[prefix + length];
            // The 32-byte prefix hypothesis reserves two silent rumble blocks.
            // Zero blocks do not invent command, volume or firmware fields.
            // One-byte length is an additional hypothesis motivated by the
            // length-prefixed *input* audio region; output parity is NOT known.
            if (lengthPrefix) packet[prefix - 1] = (byte)length;
            encoded.AsSpan(0, length).CopyTo(packet.AsSpan(prefix));
            packets[frame] = packet;
        }
        return new Switch2BluetoothLabTone { Packets = packets, IntervalMilliseconds = interval,
            Channels = channels, PrefixBytes = prefix, Hypothesis = command };
    }

    internal async Task<object> SendAsync(int maximumWriteBytes,
        Func<byte[], Task<bool>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        foreach (byte[] packet in Packets)
            if (packet.Length > maximumWriteBytes)
                throw new InvalidOperationException("Frame exceeds negotiated single-ATT capacity; never split unknown framing.");
        var clock = Stopwatch.StartNew();
        long origin = Stopwatch.GetTimestamp();
        using var waiter = new ViiperHighResolutionWaiter();
        using var stopped = new ManualResetEvent(false);
        using var interrupted = new AutoResetEvent(false);
        using var registration = cancellationToken.Register(() => stopped.Set());
        int sent = 0, dropped = 0;
        var timings = new List<double>(Packets.Length);
        // One admitted write at a time. Actual write completion is awaited even
        // after cancellation; the probe/lease own its lifetime until this returns.
        for (int i = 0; i < Packets.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double due = i * IntervalMilliseconds;
            // Reuse the existing per-worker Windows timer. Task.Delay rounded
            // short delays to ~15 ms on this machine during b91 measurements.
            var wait = waiter.WaitUntil(origin + (long)(due * Stopwatch.Frequency / 1000), stopped, interrupted);
            cancellationToken.ThrowIfCancellationRequested();
            if (wait != ViiperDeadlineWaitResult.DeadlineReached) throw new InvalidOperationException("Audio pacing wait interrupted.");
            double late = clock.Elapsed.TotalMilliseconds - due;
            if (late > 100) throw new TimeoutException("Audio scheduler stalled; bounded test aborted.");
            if (late >= IntervalMilliseconds) { dropped++; continue; } // never burst stale audio to catch up
            timings.Add(clock.Elapsed.TotalMilliseconds);
            if (!await write(Packets[i]).ConfigureAwait(false))
                throw new InvalidOperationException("Headphone write rejected; no replay.");
            sent++;
        }
        return new { Hypothesis, SampleRate, Channels, IntervalMilliseconds, PrefixBytes,
            SourcePeak, AudibleMilliseconds, TailMilliseconds, Sent = sent, Dropped = dropped,
            PacketBytes = Packets[0].Length, MaximumWriteBytes = maximumWriteBytes,
            ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, WriteStartMs = timings,
            BluetoothPlaybackConfirmed = false };
    }
}
