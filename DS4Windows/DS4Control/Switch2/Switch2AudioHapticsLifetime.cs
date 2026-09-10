using System.Threading;

namespace DS4Windows.Switch2;

internal interface ISwitch2AudioHapticsLifetime
{
    bool TryCreateAudioLane(out ControllerFeedbackStateLanePump.Lane lane);
    bool TryPublishAudio(ControllerFeedbackStateLanePump.Lane lane,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        AudioHapticsMode mode, ulong capturedMicroseconds, ulong nowMicroseconds);
    bool TryWithdrawAudio(ControllerFeedbackStateLanePump.Lane lane, ulong nowMicroseconds);
}

internal static class Switch2AudioHapticsLifetime
{
    internal const ulong MaximumSampleAgeMicroseconds = 24_000;

    internal static bool Publish(ControllerFeedbackStateLanePump pump,
        Switch2HdRumbleDeliverySink sink, ControllerFeedbackStateLanePump.Lane lane,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        AudioHapticsMode mode, ulong captured, ulong now)
    {
        if (captured > now || now - captured >= MaximumSampleAgeMicroseconds ||
            sink.HasUncertainWrite || !pump.AuthenticatesLane(lane,
                ControllerFeedbackPublicationOrigin.AudioHaptics, ControllerFeedbackSource.LocalAudioHaptics)) return false;
        // This is an explicit internal audio marker, not invented game feedback.
        // Its timestamp is capture time; admission cannot renew a queued sample.
        if (!lane.TryPublish(new ControllerFeedbackActuatorState(1, 1, 0, 0),
                captured, out var frame)) return false;
        if (!sink.TryStageAudioHaptics(frame, left, right, mode))
        {
            lane.TryWithdraw(now);
            return false;
        }
        pump.TryRefreshCurrentPresentation(now, allowNoFrame: true);
        Drain(pump, sink, now);
        return true;
    }

    internal static bool Withdraw(ControllerFeedbackStateLanePump pump,
        Switch2HdRumbleDeliverySink sink, ControllerFeedbackStateLanePump.Lane lane, ulong now)
    {
        if (!pump.AuthenticatesLane(lane, ControllerFeedbackPublicationOrigin.AudioHaptics,
                ControllerFeedbackSource.LocalAudioHaptics) || !lane.TryWithdraw(now)) return false;
        Drain(pump, sink, now);
        return true;
    }

    private static void Drain(ControllerFeedbackStateLanePump pump,
        Switch2HdRumbleDeliverySink sink, ulong now)
    {
        var result = pump.PumpOnce(now, sink, out var delivery);
        // An ownership handover requires its exact neutral before the successor.
        if (result == ControllerFeedbackPumpDisposition.Delivered &&
            delivery.Disposition == ControllerFeedbackDeliveryDisposition.Stop)
            pump.PumpOnce(now, sink, out _);
    }
}

internal sealed partial class Switch2BluetoothFeedbackLifetime : ISwitch2AudioHapticsLifetime
{
    public bool TryCreateAudioLane(out ControllerFeedbackStateLanePump.Lane lane)
    {
        sink.EnableAudioComposition(pump);
        return TryCreateLane(ControllerFeedbackPublicationOrigin.AudioHaptics,
            ControllerFeedbackSource.LocalAudioHaptics, 1,
            Switch2AudioHapticsLifetime.MaximumSampleAgeMicroseconds, 12_000, out lane);
    }

    public bool TryPublishAudio(ControllerFeedbackStateLanePump.Lane lane,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        AudioHapticsMode mode, ulong capturedMicroseconds, ulong nowMicroseconds)
    {
        if (!Monitor.TryEnter(rumbleTransactionGate)) return false;
        try
        {
            if (!CanServiceRumbleMaintenance) return false;
            return WakeAfterRumblePublication(Switch2AudioHapticsLifetime.Publish(pump,
                sink, lane, left, right, mode, capturedMicroseconds, nowMicroseconds));
        }
        finally { Monitor.Exit(rumbleTransactionGate); }
    }

    public bool TryWithdrawAudio(ControllerFeedbackStateLanePump.Lane lane, ulong nowMicroseconds)
    {
        if (!Monitor.TryEnter(rumbleTransactionGate)) return false;
        try
        {
            if (!CanServiceRumbleMaintenance) return false;
            return WakeAfterRumblePublication(Switch2AudioHapticsLifetime.Withdraw(pump, sink, lane, nowMicroseconds));
        }
        finally { Monitor.Exit(rumbleTransactionGate); }
    }
}

internal sealed partial class Switch2ProUsbOwnedFeedbackActivationLifetime : ISwitch2AudioHapticsLifetime
{
    public bool TryCreateAudioLane(out ControllerFeedbackStateLanePump.Lane lane)
    {
        sink.EnableAudioComposition(pump);
        return TryCreateLane(ControllerFeedbackPublicationOrigin.AudioHaptics,
            ControllerFeedbackSource.LocalAudioHaptics, 1,
            Switch2AudioHapticsLifetime.MaximumSampleAgeMicroseconds, 12_000, out lane);
    }

    public bool TryPublishAudio(ControllerFeedbackStateLanePump.Lane lane,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        AudioHapticsMode mode, ulong capturedMicroseconds, ulong nowMicroseconds)
    {
        if (!Monitor.TryEnter(rumbleTransactionGate)) return false;
        try
        {
            if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0) return false;
            try
            {
                if (!CanServiceRumbleMaintenance) return false;
                return WakeAfterRumblePublication(Switch2AudioHapticsLifetime.Publish(pump,
                    sink, lane, left, right, mode, capturedMicroseconds, nowMicroseconds));
            }
            finally { Volatile.Write(ref operationActive, 0); }
        }
        finally { Monitor.Exit(rumbleTransactionGate); }
    }

    public bool TryWithdrawAudio(ControllerFeedbackStateLanePump.Lane lane, ulong nowMicroseconds)
    {
        if (!Monitor.TryEnter(rumbleTransactionGate)) return false;
        try
        {
            if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0) return false;
            try
            {
                if (!CanServiceRumbleMaintenance) return false;
                return WakeAfterRumblePublication(Switch2AudioHapticsLifetime.Withdraw(pump, sink, lane, nowMicroseconds));
            }
            finally { Volatile.Write(ref operationActive, 0); }
        }
        finally { Monitor.Exit(rumbleTransactionGate); }
    }
}
