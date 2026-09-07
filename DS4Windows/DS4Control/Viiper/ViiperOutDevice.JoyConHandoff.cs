using System;
using System.IO;
using System.Threading;
using DS4Windows.Switch2;

namespace DS4Windows;

public sealed partial class ViiperOutDevice
{
    private readonly object joyConFeedbackHandoffGate = new();
    private volatile bool joyConFeedbackPaused;
    private bool joyConXboxFeedbackRebound, joyConBrokerStopped;
    private ulong joyConFeedbackResumeTimestamp, joyConFeedbackDeviceGeneration,
        joyConFeedbackTransportGeneration;

    internal bool TryPauseJoyConFeedback()
    {
        lock (joyConFeedbackHandoffGate)
        {
            if (!connected || joyConFeedbackPaused) return false;
            joyConFeedbackPaused = true;
        }
        // Non-Xbox callbacks stop at admission; Xbox keeps consuming validated
        // wire sequences so the same broker stream stays alive during the join.
        if (!feedbackCallbacksIdle.WaitOne(5_000)) return false;
        feedbackDispatchBuffer.ClearPending();
        return true;
    }

    internal void CancelJoyConFeedbackPause()
    {
        lock (joyConFeedbackHandoffGate)
            if (Volatile.Read(ref switch2FeedbackSession)?.IsRetired != true)
                joyConFeedbackPaused = false;
    }

    internal void ResumeJoyConFeedback(int slot, Switch2RuntimeInputDevice target)
    {
        lock (joyConFeedbackHandoffGate)
        {
            Switch2VirtualFeedbackSession previous = Volatile.Read(ref switch2FeedbackSession);
            if (!connected || !joyConFeedbackPaused || joyConBrokerStopped ||
                previous != null && !previous.IsRetired ||
                !ReferenceEquals(Program.rootHub?.DS4Controllers[slot], target) ||
                !target.TryGetFeedbackBinding(out ulong deviceGeneration, out ulong transportGeneration))
                throw new IOException("The retained virtual pad has no exact released feedback predecessor.");

            Interlocked.Exchange(ref switch2FeedbackSession, null);
            BindPhysicalController(slot);
            if (viiperType == ViiperVirtualDeviceType.XboxOne)
            {
                if (xboxOneFeedbackBinding == null ||
                    !target.TryCreateVirtualFeedbackSession((ControllerFeedbackSource)xboxOneFeedbackBinding.Source,
                        deviceGeneration, transportGeneration, out var session))
                    throw new IOException("The joined controller rejected the retained Xbox feedback route.");
                Interlocked.Exchange(ref switch2FeedbackSession, session);
                joyConFeedbackDeviceGeneration = deviceGeneration;
                joyConFeedbackTransportGeneration = transportGeneration;
                joyConXboxFeedbackRebound = true;
            }
            else PrepareSwitch2VirtualFeedbackSession();
            if (!ControllerFeedbackClock.TryGetTimestampMicroseconds(out joyConFeedbackResumeTimestamp))
                throw new IOException("The feedback handoff clock is unavailable.");
            feedbackDispatchBuffer.ClearPending();
            joyConFeedbackPaused = false;
        }
    }

    /// <summary>
    /// Keep the immutable broker identity intact. Only after authenticating
    /// its source, generations, ordering and original freshness may a retained
    /// pad route a frame to a new physical session. Timestamp/TTL/Stop semantics
    /// are preserved; queued pre-handoff rumble is consumed but not replayed.
    /// </summary>
    internal static bool TryTranslateRetainedXboxFeedback(in ControllerFeedbackFrame frame,
        XboxOneAuthorizedFeedbackBinding binding, ulong lastSequence, ulong resumeTimestamp,
        ulong deviceGeneration, ulong transportGeneration, ulong epoch,
        out ControllerFeedbackFrame translated, out bool suppressed)
    {
        translated = default;
        suppressed = false;
        if (binding == null || !frame.HasValidInvariants() || frame.Sequence <= lastSequence ||
            frame.Source != (ControllerFeedbackSource)binding.Source ||
            frame.DeviceGeneration != binding.DeviceGeneration ||
            frame.TransportGeneration != binding.TransportGeneration ||
            frame.OwnershipEpoch != binding.OwnershipEpoch ||
            frame.TimeToLiveMicroseconds != binding.TimeToLiveMicroseconds ||
            !ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now) || !frame.IsFreshAt(now))
            return false;
        // A broker Stop must still reach the new session even if its timestamp
        // predates the resume boundary. While paused it seals the handoff.
        if (resumeTimestamp == ulong.MaxValue || !frame.IsStop && frame.TimestampMicroseconds < resumeTimestamp)
        { suppressed = true; return true; }
        return ControllerFeedbackFrame.TryCreate(frame.Source, frame.Command, frame.Actuators,
            frame.BodyLow, frame.BodyHigh, frame.LeftTrigger, frame.RightTrigger, frame.Sequence,
            deviceGeneration, transportGeneration, epoch, frame.TimestampMicroseconds,
            frame.TimeToLiveMicroseconds, out translated);
    }
}
