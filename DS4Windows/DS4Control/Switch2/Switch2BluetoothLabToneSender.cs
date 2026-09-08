using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

// Legacy fixed-command sender remains for existing clients. The encoder's
// other partial contains no I/O and can be compiled into the external tool.
internal sealed partial class Switch2BluetoothLabTone
{
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
        for (int i = 0; i < Packets.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double due = i * IntervalMilliseconds;
            var wait = waiter.WaitUntil(origin + (long)(due * Stopwatch.Frequency / 1000), stopped, interrupted);
            cancellationToken.ThrowIfCancellationRequested();
            if (wait != ViiperDeadlineWaitResult.DeadlineReached) throw new InvalidOperationException("Audio pacing wait interrupted.");
            double late = clock.Elapsed.TotalMilliseconds - due;
            if (late > 100) throw new TimeoutException("Audio scheduler stalled; bounded test aborted.");
            if (late >= IntervalMilliseconds) { dropped++; continue; }
            timings.Add(clock.Elapsed.TotalMilliseconds);
            if (!await write(Packets[i]).ConfigureAwait(false)) throw new InvalidOperationException("Headphone write rejected; no replay.");
            sent++;
        }
        return new { Hypothesis, Codec, Bitrate, SampleRate, Channels, IntervalMilliseconds, PrefixBytes,
            SourcePeak, AudibleMilliseconds, TailMilliseconds, Sent = sent, Dropped = dropped,
            PacketBytes = Packets[0].Length, MaximumWriteBytes = maximumWriteBytes,
            ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, WriteStartMs = timings,
            BluetoothPlaybackConfirmed = false };
    }
}
