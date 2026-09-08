using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

// Stable sender: external tools own encoding/framing. One admitted real write
// remains owned until completion, including cancellation and notification restore.
internal static class Switch2BluetoothLabPacketSender
{
    internal static async Task<object> SendAsync(Switch2BluetoothLabPlan plan, int maximumWriteBytes,
        Func<byte[], Task<bool>> write, CancellationToken cancellationToken)
    {
        plan.Validate(maximumWriteBytes);
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        string fingerprint = plan.Fingerprint();
        using var waiter = new ViiperHighResolutionWaiter();
        using var stopped = new ManualResetEvent(false);
        using var interrupted = new AutoResetEvent(false);
        using var registration = cancellationToken.Register(() => stopped.Set());
        long origin = Stopwatch.GetTimestamp();
        var timings = new List<double>(plan.Packets.Length);
        for (int index = 0; index < plan.Packets.Length; index++)
        {
            var packet = plan.Packets[index];
            long due = origin + packet.OffsetMicroseconds * Stopwatch.Frequency / 1000000;
            var result = waiter.WaitUntil(due, stopped, interrupted);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != ViiperDeadlineWaitResult.DeadlineReached) throw new InvalidOperationException("Plan wait interrupted.");
            double elapsed = (Stopwatch.GetTimestamp() - origin) * 1000.0 / Stopwatch.Frequency;
            // Abort instead of deleting unknown codec fragments or burst-replaying
            // stale state. The failed schedule must not be reported as a valid test.
            int next = index + 1;
            while (next < plan.Packets.Length && plan.Packets[next].OffsetMicroseconds == packet.OffsetMicroseconds) next++;
            double allowedLatenessMs = next < plan.Packets.Length
                ? Math.Min(25, (plan.Packets[next].OffsetMicroseconds - packet.OffsetMicroseconds) / 1000.0) : 25;
            if (elapsed - packet.OffsetMicroseconds / 1000.0 >= allowedLatenessMs)
                throw new TimeoutException("Plan missed the next frame deadline; no stale replay.");
            timings.Add(elapsed);
            if (!await write(packet.Payload).ConfigureAwait(false)) throw new InvalidOperationException("Plan write rejected; no replay.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new { plan.Id, Fingerprint = fingerprint, Sent = timings.Count, WriteStartMs = timings,
            BluetoothPlaybackConfirmed = false };
    }
}
