/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// One immutable Xbox CFBK target binding. It admits only the exact source,
    /// device/transport generations, ownership epoch, and increasing sequence
    /// minted for this virtual-device lifecycle. Stop is terminal.
    /// </summary>
    internal sealed class XboxOnePhysicalFeedbackSession
    {
        private readonly object gate = new();
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;
        private readonly ulong ownershipEpoch;
        private readonly DS4Device targetDevice;
        private ulong lastSequence;
        private bool retired;
        private bool retiring;
        private bool hasPublished;
        private bool terminalPublished;
        private bool failureNotified;
        private ulong lastNowMicroseconds;
        private ulong expiryMicroseconds;
        private ControllerFeedbackStateLanePump pump;
        private ControllerFeedbackIngress ingress;
        private PhysicalStateSink sink;
        private TimeProvider timeProvider;
        private ITimer expiryTimer;
        private Action onFailure;
        private Func<bool> isOutputEnabled;

        private XboxOnePhysicalFeedbackSession(ulong deviceGeneration,
            ulong transportGeneration, ulong ownershipEpoch,
            DS4Device targetDevice)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
            this.ownershipEpoch = ownershipEpoch;
            this.targetDevice = targetDevice;
        }

        internal static bool TryCreate(XboxOneAuthorizedFeedbackBinding binding,
            DS4Device targetDevice,
            out XboxOnePhysicalFeedbackSession session)
        {
            session = null;
            if (binding == null || binding.Source !=
                    (byte)ControllerFeedbackSource.XboxOneVirtualDevice ||
                binding.DeviceGeneration == 0 ||
                binding.TransportGeneration == 0 ||
                binding.OwnershipEpoch == 0 || targetDevice == null)
            {
                return false;
            }

            session = new XboxOnePhysicalFeedbackSession(
                binding.DeviceGeneration, binding.TransportGeneration,
                binding.OwnershipEpoch, targetDevice);
            return true;
        }

        internal bool Targets(DS4Device device) =>
            ReferenceEquals(targetDevice, device);

        // Profile capture must not wait behind a physical state setter.
        internal bool TryCaptureOutputPolicySequence(out ulong sequence)
        {
            sequence = Volatile.Read(ref lastSequence);
            return pump != null && sequence != 0;
        }

        internal bool TrySuppressCurrentOutput(ulong expectedSequence)
        {
            lock (gate)
            {
                if (pump == null || retired || retiring || expectedSequence == 0 ||
                    expectedSequence != lastSequence) return true;
                if (!TryReadClockNoLock(out ulong now))
                {
                    FailNoLock();
                    return false;
                }
                // Restrict presentation, not the authenticated frame/TTL. The
                // same pump delivers neutral, preserving its retry and expiry.
                sink.OutputSuppressed = true;
                _ = pump.TryRefreshCurrentPresentation(now);
                return TryPumpNoLock(ref now);
            }
        }

        /// <summary>
        /// Production owner. Successful publication proves acceptance by the
        /// existing physical state setters, not completion of a HID write.
        /// The canonical pump owns expiry/neutralization; the timer wakes this
        /// same owner without bypassing canonical arbitration or the mapper.
        /// </summary>
        internal static bool TryCreateOwned(
            XboxOneAuthorizedFeedbackBinding binding, DS4Device targetDevice,
            Func<ControllerFeedbackActuatorState, bool, bool> publishPhysicalState,
            out XboxOnePhysicalFeedbackSession session,
            TimeProvider timeProvider = null, Action onFailure = null,
            Func<bool> isOutputEnabled = null)
        {
            session = null;
            if (publishPhysicalState == null ||
                !TryCreate(binding, targetDevice, out var created) ||
                !ControllerFeedbackStateLanePump.TryCreate(
                    binding.DeviceGeneration, binding.TransportGeneration,
                    out created.pump) ||
                !created.pump.TryCreateBrokerIngress(
                    ControllerFeedbackSource.XboxOneVirtualDevice,
                    binding.OwnershipEpoch, out created.ingress))
            {
                return false;
            }
            created.sink = new PhysicalStateSink(publishPhysicalState);
            created.timeProvider = timeProvider ?? TimeProvider.System;
            created.onFailure = onFailure;
            created.isOutputEnabled = isOutputEnabled;
            session = created;
            return true;
        }

        internal bool TryPublish(ReadOnlySpan<byte> wire)
        {
            lock (gate)
            {
                if (pump == null || retired || retiring)
                {
                    return false;
                }
                if (!TryReadClockNoLock(out ulong now))
                {
                    FailNoLock();
                    return false;
                }
                if (!TryDecodeBoundFrame(wire, now, out var frame) ||
                    !ingress.TryPublish(wire))
                {
                    return false;
                }
                bool refreshSuppressedPresentation = sink.OutputSuppressed;
                Volatile.Write(ref lastSequence, frame.Sequence);
                hasPublished = true;
                if (frame.IsStop)
                {
                    return TryRetireNoLock(now);
                }
                // Publish the sequence before sampling live policy. An off
                // edit either precedes this read or captures this sequence
                // for the worker; it cannot fall between both protections.
                try { sink.OutputSuppressed = !(isOutputEnabled?.Invoke() ?? true); }
                catch
                {
                    FailNoLock();
                    return false;
                }
                // A new accepted frame can resume even when its actuator
                // values equal the previously suppressed canonical state.
                if (refreshSuppressedPresentation || sink.OutputSuppressed)
                    _ = pump.TryRefreshCurrentPresentation(now);
                expiryMicroseconds = frame.TimestampMicroseconds >
                        ulong.MaxValue - frame.TimeToLiveMicroseconds ?
                    ulong.MaxValue : frame.TimestampMicroseconds +
                        frame.TimeToLiveMicroseconds;
                // Resample after each state setter: acceptance can straddle
                // expiry, in which case neutral is due before returning.
                if (!TryPumpNoLock(ref now) ||
                    now < expiryMicroseconds &&
                        !ScheduleNoLock(RemainingTime(expiryMicroseconds, now)))
                {
                    FailNoLock();
                    return false;
                }
                if (now >= expiryMicroseconds)
                {
                    StopTimerNoLock();
                }
                return true;
            }
        }

        internal bool TryAccept(ReadOnlySpan<byte> wire,
            ulong nowMicroseconds, out ControllerFeedbackActuatorState state,
            out bool terminal)
        {
            state = default;
            terminal = false;
            // Codec-only compatibility path: an owned production session may
            // not bypass its serialized state delivery and expiry pump.
            if (pump != null || !TryDecodeBoundFrame(wire,
                    nowMicroseconds, out var frame))
            {
                return false;
            }

            lock (gate)
            {
                if (retired || frame.Sequence <= lastSequence)
                {
                    return false;
                }
                lastSequence = frame.Sequence;
                terminal = frame.IsStop;
                retired = terminal;
            }

            if (frame.Command == ControllerFeedbackCommand.Apply)
            {
                state = new ControllerFeedbackActuatorState(frame.BodyLow,
                    frame.BodyHigh, frame.LeftTrigger, frame.RightTrigger);
            }
            return true;
        }

        internal bool TryRetire()
        {
            lock (gate)
            {
                if (pump != null)
                {
                    _ = TryReadClockNoLock(out _);
                    return TryRetireNoLock(lastNowMicroseconds);
                }
                if (retired)
                {
                    return true;
                }
                retired = true;
                return true;
            }
        }

        private bool TryDecodeBoundFrame(ReadOnlySpan<byte> wire,
            ulong nowMicroseconds, out ControllerFeedbackFrame frame) =>
            ControllerFeedbackFrame.TryReadFrom(wire, out frame) &&
            frame.Source == ControllerFeedbackSource.XboxOneVirtualDevice &&
            frame.DeviceGeneration == deviceGeneration &&
            frame.TransportGeneration == transportGeneration &&
            frame.OwnershipEpoch == ownershipEpoch &&
            frame.IsFreshAt(nowMicroseconds);

        private bool TryReadClockNoLock(out ulong now)
        {
            now = lastNowMicroseconds;
            try
            {
                long timestamp = timeProvider.GetTimestamp();
                long frequency = timeProvider.TimestampFrequency;
                if (timestamp < 0 || frequency <= 0 ||
                    !ControllerFeedbackClock.TryConvertQpcTicks(
                        (ulong)timestamp, (ulong)frequency, out ulong observed) ||
                    observed < lastNowMicroseconds)
                {
                    return false;
                }
                now = lastNowMicroseconds = observed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryPumpNoLock(ref ulong now)
        {
            try
            {
                // An expired predecessor may require Stop before a newly
                // accepted state is presented. The canonical pump selects it.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var result = pump.PumpOnce(now, sink, out _);
                    if (result == ControllerFeedbackPumpDisposition.None)
                    {
                        return true;
                    }
                    if (result != ControllerFeedbackPumpDisposition.Delivered)
                    {
                        return false;
                    }
                    if (!TryReadClockNoLock(out now))
                    {
                        return false;
                    }
                }
                return false;
            }
            catch
            {
                // A state setter can fail after partial acceptance. Preserve
                // the pump's unresolved event; never claim a successful ACK.
                return false;
            }
        }

        private bool TryRetireNoLock(ulong now)
        {
            if (retired)
            {
                return true;
            }
            retiring = true;
            StopTimerNoLock();
            try { expiryTimer?.Dispose(); }
            catch { /* Admission is already fenced under gate. */ }
            expiryTimer = null;
            // No callback can cross this gate after retirement, even when
            // physical neutral fails. Such a failure remains synchronously
            // retryable, never a late write into a successor session.
            if (!terminalPublished)
            {
                terminalPublished = ingress.TryPublishTerminalStop(now);
                _ = ingress.TryRetire();
            }
            try
            {
                bool neutral = hasPublished ?
                    pump.TryTerminalNeutralAndRetire(now, sink, maxAttempts: 3) :
                    pump.TryStopAndRetire(now, sink, maxAttempts: 3);
                retired = terminalPublished && neutral;
                return retired;
            }
            catch
            {
                return false;
            }
        }

        private void ExpiryTick(object state)
        {
            lock (gate)
            {
                if (retired || retiring)
                {
                    return;
                }
                if (!TryReadClockNoLock(out ulong now))
                {
                    FailNoLock();
                    return;
                }
                if (!TryPumpNoLock(ref now) ||
                    now < expiryMicroseconds &&
                        !ScheduleNoLock(RemainingTime(expiryMicroseconds, now)))
                {
                    FailNoLock();
                }
                else if (now >= expiryMicroseconds)
                {
                    StopTimerNoLock();
                }
            }
        }

        private void FailNoLock()
        {
            _ = TryRetireNoLock(lastNowMicroseconds);
            if (!failureNotified)
            {
                failureNotified = true;
                try { onFailure?.Invoke(); }
                catch { /* Diagnostics must not escape a timer callback. */ }
            }
        }

        private bool ScheduleNoLock(TimeSpan due)
        {
            try
            {
                if (expiryTimer == null)
                {
                    expiryTimer = timeProvider.CreateTimer(ExpiryTick, null, due,
                        Timeout.InfiniteTimeSpan);
                    return expiryTimer != null;
                }
                return expiryTimer.Change(due, Timeout.InfiniteTimeSpan);
            }
            catch { return false; }
        }

        private void StopTimerNoLock()
        {
            try
            {
                _ = expiryTimer?.Change(Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }
            catch { /* The serialized retirement fence rejects queued ticks. */ }
        }

        private static TimeSpan RemainingTime(ulong expiry, ulong now) =>
            TimeSpan.FromTicks((long)Math.Min(
                expiry > now ? expiry - now : 1, 250_000UL) * 10);

        private sealed class PhysicalStateSink : IControllerFeedbackDeliverySink
        {
            private readonly Func<ControllerFeedbackActuatorState, bool, bool> publish;
            internal bool OutputSuppressed;

            internal PhysicalStateSink(
                Func<ControllerFeedbackActuatorState, bool, bool> publish) =>
                this.publish = publish;

            public bool TryDeliver(in ControllerFeedbackDelivery delivery)
            {
                ControllerFeedbackActuatorState state = default;
                bool release = delivery.Disposition ==
                    ControllerFeedbackDeliveryDisposition.Stop;
                if (!release && !OutputSuppressed && delivery.Frame.Command ==
                    ControllerFeedbackCommand.Apply)
                {
                    state = new ControllerFeedbackActuatorState(
                        delivery.Frame.BodyLow, delivery.Frame.BodyHigh,
                        delivery.Frame.LeftTrigger, delivery.Frame.RightTrigger);
                }
                return publish(state, release || delivery.Frame.IsStop);
            }
        }
    }

    // Cold profile wake only. Disabling is restrictive until the next accepted
    // frame; there is no enable request that can resurrect this sequence.
    internal sealed record XboxOnePhysicalOutputSuppressionRequest(
        XboxOnePhysicalFeedbackSession Session, int DeviceIndex,
        long StreamGeneration, ulong Sequence);

    /// <summary>
    /// Physical capability projection for the four canonical Xbox actuators.
    /// DualSense-class targets keep body and impulse channels separate. A
    /// conventional two-motor target deterministically folds left impulse into
    /// its heavy/left motor and right impulse into its light/right motor using
    /// max, avoiding additive clipping.
    /// </summary>
    internal static class XboxOneCanonicalFeedbackAdapter
    {
        internal static void ProjectPhysical(
            in ControllerFeedbackActuatorState state,
            bool hasIndependentTriggerActuators, out byte heavySlow,
            out byte lightFast, out byte leftImpulse,
            out byte rightImpulse)
        {
            ushort heavy = state.BodyLow;
            ushort light = state.BodyHigh;
            if (!hasIndependentTriggerActuators)
            {
                heavy = Math.Max(heavy, state.LeftTrigger);
                light = Math.Max(light, state.RightTrigger);
            }

            heavySlow = ScaleUShort(heavy);
            lightFast = ScaleUShort(light);
            leftImpulse = hasIndependentTriggerActuators ?
                ScaleUShort(state.LeftTrigger) : (byte)0;
            rightImpulse = hasIndependentTriggerActuators ?
                ScaleUShort(state.RightTrigger) : (byte)0;
        }

        private static byte ScaleUShort(ushort value) =>
            (byte)((value + 128) / 257);
    }
}
