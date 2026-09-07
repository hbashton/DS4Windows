/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Threading;

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothFeedbackRetirementFailure : byte
{
    None = 0,
    InvalidState,
    SealRejected,
    TerminalDeliveryRejected,
    PumpNotRetired,
    SinkMissingTerminal,
    SinkRetirementRejected,
}

/// <summary>
/// Transport-neutral owner boundary for one virtual-controller feedback
/// session.
/// Bluetooth and USB retain distinct physical writers and lifecycle engines,
/// while this seam preserves one canonical CFBK ingress contract.
/// </summary>
internal interface ISwitch2VirtualFeedbackSessionOwner
{
    // Failure-only diagnostics. Never consulted for admission or ACK decisions.
    string DescribeWirePublication() => "owner diagnostics unavailable";

    bool TryPublishAndPump(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress, ReadOnlySpan<byte> wire,
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning);

    bool TryPublishAndPump(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress,
        in ControllerFeedbackActuatorState state,
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ulong expiresAtMicroseconds = 0);

    bool TryPublishSourcePreservedAndPump(
        Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress,
        in ControllerFeedbackActuatorState state,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ulong expiresAtMicroseconds = 0);

    bool TryStageImpulseReleasePresentation(
        Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame canonicalFrame, ushort leftTrigger,
        ushort rightTrigger, ulong presentationRevision);

    bool TryRefreshCurrentPresentation(
        Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame canonicalFrame,
        ulong presentationRevision);

    bool TryClearImpulseReleasePresentation(
        Switch2VirtualFeedbackSession session);

    bool TryRefreshXboxOutputPolicy(Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame frame, Switch2XboxFeedbackPolicy policy);

    bool TryRequestPlayerLedMask(Switch2VirtualFeedbackSession session,
        byte playerLedMask);

    bool TryRetireSession(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress);
}

/// <summary>
/// One virtual-controller feedback stream bound to one physical Switch 2
/// output lifetime. It owns no mapping or transport. Zero-delay feedback enters
/// the canonical runtime synchronously. An explicit profile delay lazily adds
/// one ordered, generation-fenced session timer; the lifetime's sole pump still
/// performs every physical write.
/// </summary>
internal sealed class Switch2VirtualFeedbackSession
{
    private const int MaximumQueuedFeedback = 8_192;

    private readonly object gate = new();
    private readonly ISwitch2VirtualFeedbackSessionOwner owner;
    private readonly ControllerFeedbackIngress ingress;
    private Queue<ScheduledFeedback> delayedFeedback;
    private Timer delayedTimer;
    private Timer impulseReleaseTimer;
    private Switch2ImpulseReleaseEnvelope impulseReleaseEnvelope;
    private ControllerFeedbackFrame impulseReleaseCanonicalFrame;
    private ulong nextImpulseReleasePresentationRevision;
    private bool hasImpulseReleaseCanonicalFrame;
    private int selectedDelayMilliseconds;
    private long selectedProfileRevision;
    private Switch2DualSenseDelayedPolicyGuard selectedDelayedPolicyGuard;
    private ulong publicationRevision;
    private bool terminalBrokerStop;
    private bool disconnectedRetired;
    private bool active = true;

    internal Switch2VirtualFeedbackSession(
        ISwitch2VirtualFeedbackSessionOwner owner,
        ControllerFeedbackIngress ingress, ulong ownershipEpoch)
    {
        this.owner = owner;
        this.ingress = ingress;
        OwnershipEpoch = ownershipEpoch;
    }

    internal ulong OwnershipEpoch { get; }

    internal bool WasRetiredDisconnected => Volatile.Read(ref disconnectedRetired);
    internal bool IsRetired => !Volatile.Read(ref active) || WasRetiredDisconnected;

    internal string DescribeWirePublication()
    {
        lock (gate)
        {
            bool hasPublished = ingress.TryReadPublishedFrame(out var published);
            return $"sessionActive={active}, terminal={terminalBrokerStop}, " +
                $"publishedSequence={(hasPublished ? published.Sequence : 0)}, " +
                owner.DescribeWirePublication();
        }
    }

    internal bool TryCaptureXboxPolicyRevision(out ulong revision)
    {
        // A profile update can originate on the input queue. Do not take the
        // session gate held across bounded physical output. This is only a
        // snapshot; the worker revalidates it under the gate before acting.
        revision = Volatile.Read(ref publicationRevision);
        return Volatile.Read(ref active) && !Volatile.Read(ref terminalBrokerStop) &&
            ingress != null && ingress.Source is (
                ControllerFeedbackSource.XboxOneVirtualDevice or
                ControllerFeedbackSource.XboxSeriesVirtualDevice);
    }

    internal bool TryRefreshXboxOutputPolicy(Switch2XboxFeedbackPolicy policy,
        ulong? expectedPublicationRevision = null)
    {
        lock (gate)
        {
            if (!active || ingress == null || ingress.Source is not (
                    ControllerFeedbackSource.XboxOneVirtualDevice or
                    ControllerFeedbackSource.XboxSeriesVirtualDevice) ||
                ingress.HasPublishedTerminalStop) return false;
            if (expectedPublicationRevision.HasValue &&
                expectedPublicationRevision.Value != publicationRevision) return true;
            // Profile timing applies to future game effects, never to removing
            // an already-presented component. Stale queued tuning is discarded.
            ClearDelayedFeedbackNoLock(disposeTimer: false);
            ClearImpulseReleaseNoLock(disposeTimer: false);
            return !ingress.TryReadPublishedFrame(out var frame) ||
                owner.TryRefreshXboxOutputPolicy(this, frame, policy);
        }
    }

    internal bool TryPublish(ReadOnlySpan<byte> wire,
        bool mapImpulseTriggersToHdRumble = false,
        bool dynamicImpulseFrequency = true,
        int fixedImpulseFrequencyLevel =
            Switch2HdRumbleImpulseTuning.DefaultFixedFrequencyLevel,
        int impulseStrengthLevel =
            Switch2HdRumbleImpulseTuning.DefaultStrengthLevel,
        int bodyStrengthPercent =
            Switch2HdRumbleBodyTuning.DefaultStrengthPercent,
        bool xboxBodyCarrierMode = false,
        int xboxBodyFrequencyLevel =
            Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel,
        int rumbleDelayMilliseconds =
            Switch2RumbleDelay.DefaultMilliseconds,
        long profileRevision = 0,
        Func<Switch2XboxFeedbackPolicy> readLiveXboxPolicy = null)
    {
        if (!ControllerFeedbackFrame.TryReadFrom(wire,
                out ControllerFeedbackFrame parsedFrame)) return false;
        if (parsedFrame.IsStop)
        {
            lock (gate)
            {
                if (!active || publicationRevision == ulong.MaxValue ||
                    !ingress.AuthenticatesDelayedFrame(parsedFrame)) return false;
                publicationRevision++;
                // A terminal broker command is a lifecycle boundary, not a
                // delayed game effect. Preserve its exact sequence/timestamp;
                // neither profile tuning nor a queued Apply may postpone it.
                bool published = owner.TryPublishAndPump(this, ingress, wire,
                        Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
                        Switch2HdRumbleImpulseTuning.Default,
                        Switch2HdRumbleBodyTuning.Default);
                if (ingress.HasPublishedTerminalStop)
                {
                    Volatile.Write(ref terminalBrokerStop, true);
                    ClearDelayedFeedbackNoLock(disposeTimer: false);
                    ClearImpulseReleaseNoLock(disposeTimer: false);
                }
                return published;
            }
        }
        if (!Switch2HdRumbleImpulseTuning.TryCreate(dynamicImpulseFrequency,
                fixedImpulseFrequencyLevel, impulseStrengthLevel,
                out Switch2HdRumbleImpulseTuning impulseTuning) ||
            !Switch2HdRumbleBodyTuning.TryCreate(bodyStrengthPercent,
                xboxBodyCarrierMode, xboxBodyFrequencyLevel,
                out Switch2HdRumbleBodyTuning bodyTuning))
        {
            return false;
        }
        Switch2HdRumbleFeedbackPolicy policy =
            mapImpulseTriggersToHdRumble ?
                Switch2HdRumbleFeedbackPolicy.
                    SideLocalImpulseDualBandSaturating :
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility;
        lock (gate)
        {
            if (!active || !Switch2RumbleDelay.IsValid(
                    rumbleDelayMilliseconds) ||
                publicationRevision == ulong.MaxValue ||
                !ingress.AuthenticatesDelayedFrame(parsedFrame))
            {
                return false;
            }
            Volatile.Write(ref publicationRevision, publicationRevision + 1);
            Span<byte> policyWire = stackalloc byte[ControllerFeedbackFrame.SerializedLength];
            scoped ReadOnlySpan<byte> effectiveWire = wire;
            if (readLiveXboxPolicy != null)
            {
                // The callback is a bounded profile snapshot, never I/O or
                // owner re-entry. Expose this identity before its final read:
                // an edit either affects that read or queues this revision.
                Switch2XboxFeedbackPolicy livePolicy;
                try { livePolicy = readLiveXboxPolicy(); }
                catch { return false; }
                if (!livePolicy.OutputEnabled)
                {
                    if (!ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(parsedFrame,
                            outputEnabled: false, out var suppressedFrame) ||
                        !suppressedFrame.TryWriteTo(policyWire)) return false;
                    parsedFrame = suppressedFrame;
                    effectiveWire = policyWire;
                    rumbleDelayMilliseconds = 0;
                }
                if (!livePolicy.OutputEnabled || !livePolicy.ImpulseEnabled)
                    policy = Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility;
            }
            if (rumbleDelayMilliseconds == 0)
            {
                ClearDelayedFeedbackNoLock(disposeTimer: false);
                selectedDelayMilliseconds = 0;
                selectedProfileRevision = profileRevision;
                if (policy == Switch2HdRumbleFeedbackPolicy.
                    SdlBodyOnlyCompatibility)
                {
                    ClearImpulseReleaseNoLock(disposeTimer: false);
                    return owner.TryPublishAndPump(this, ingress, effectiveWire,
                        policy, impulseTuning, bodyTuning);
                }
                return DispatchWireNoLock(parsedFrame, policy,
                    impulseTuning, bodyTuning, effectiveWire);
            }
            return TryQueueDelayedNoLock(DelayedFeedback.ForWire(parsedFrame,
                policy, impulseTuning, bodyTuning),
                rumbleDelayMilliseconds, profileRevision);
        }
    }

    internal bool TryPublish(in ControllerFeedbackActuatorState state,
        bool mapImpulseTriggersToHdRumble = false,
        bool dynamicImpulseFrequency = true,
        int fixedImpulseFrequencyLevel =
            Switch2HdRumbleImpulseTuning.DefaultFixedFrequencyLevel,
        int impulseStrengthLevel =
            Switch2HdRumbleImpulseTuning.DefaultStrengthLevel,
        int bodyStrengthPercent =
            Switch2HdRumbleBodyTuning.DefaultStrengthPercent,
        bool xboxBodyCarrierMode = false,
        int xboxBodyFrequencyLevel =
            Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel,
        int rumbleDelayMilliseconds =
            Switch2RumbleDelay.DefaultMilliseconds,
        long profileRevision = 0,
        ulong expiresAtMicroseconds = 0,
        Switch2DualSenseDelayedPolicyGuard delayedPolicyGuard = default)
    {
        if (!Switch2HdRumbleImpulseTuning.TryCreate(dynamicImpulseFrequency,
                fixedImpulseFrequencyLevel, impulseStrengthLevel,
                out Switch2HdRumbleImpulseTuning impulseTuning) ||
            !Switch2HdRumbleBodyTuning.TryCreate(bodyStrengthPercent,
                xboxBodyCarrierMode, xboxBodyFrequencyLevel,
                out Switch2HdRumbleBodyTuning bodyTuning))
        {
            return false;
        }
        Switch2HdRumbleFeedbackPolicy policy =
            mapImpulseTriggersToHdRumble ?
                Switch2HdRumbleFeedbackPolicy.
                    SideLocalImpulseDualBandSaturating :
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility;
        lock (gate)
        {
            if (!active || !Switch2RumbleDelay.IsValid(
                    rumbleDelayMilliseconds) ||
                (expiresAtMicroseconds != 0 && rumbleDelayMilliseconds != 0) ||
                publicationRevision == ulong.MaxValue)
            {
                return false;
            }
            publicationRevision++;
            if (rumbleDelayMilliseconds == 0)
            {
                ClearDelayedFeedbackNoLock(disposeTimer: false);
                ClearImpulseReleaseNoLock(disposeTimer: false);
                selectedDelayMilliseconds = 0;
                selectedProfileRevision = profileRevision;
                return owner.TryPublishAndPump(this, ingress, state,
                    policy, impulseTuning, bodyTuning, expiresAtMicroseconds);
            }
            return TryQueueDelayedNoLock(DelayedFeedback.ForState(state,
                policy, impulseTuning, bodyTuning, delayedPolicyGuard),
                rumbleDelayMilliseconds, profileRevision);
        }
    }

    internal bool TryPublishSourcePreserved(
        in ControllerFeedbackActuatorState state,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        int bodyStrengthPercent =
            Switch2HdRumbleBodyTuning.DefaultStrengthPercent,
        bool xboxBodyCarrierMode = false,
        int xboxBodyFrequencyLevel =
            Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel,
        int rumbleDelayMilliseconds =
            Switch2RumbleDelay.DefaultMilliseconds,
        long profileRevision = 0,
        ulong expiresAtMicroseconds = 0,
        Switch2DualSenseDelayedPolicyGuard delayedPolicyGuard = default)
    {
        if (!Switch2HdRumbleBodyTuning.TryCreate(bodyStrengthPercent,
                xboxBodyCarrierMode, xboxBodyFrequencyLevel,
                out Switch2HdRumbleBodyTuning bodyTuning))
        {
            return false;
        }
        lock (gate)
        {
            if (!active || !Switch2RumbleDelay.IsValid(
                    rumbleDelayMilliseconds) ||
                (expiresAtMicroseconds != 0 && rumbleDelayMilliseconds != 0) ||
                fidelity is not (
                    Switch2HdRumbleFeedbackFidelity.
                        NativeSwitch2PassThrough or
                    Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand or
                    Switch2HdRumbleFeedbackFidelity.
                        DualSenseAdaptiveTriggerApproximation) ||
                publicationRevision == ulong.MaxValue)
            {
                return false;
            }
            publicationRevision++;
            if (rumbleDelayMilliseconds == 0)
            {
                ClearDelayedFeedbackNoLock(disposeTimer: false);
                ClearImpulseReleaseNoLock(disposeTimer: false);
                selectedDelayMilliseconds = 0;
                selectedProfileRevision = profileRevision;
                return owner.TryPublishSourcePreservedAndPump(this,
                    ingress, state, fidelity, left, right, bodyTuning,
                    expiresAtMicroseconds);
            }
            return TryQueueDelayedNoLock(
                DelayedFeedback.ForSourcePreserved(state, fidelity, left,
                    right, bodyTuning, delayedPolicyGuard), rumbleDelayMilliseconds,
                profileRevision);
        }
    }

    /// <summary>
    /// One atomic compare-and-publish for a local conversion-policy refresh.
    /// All session publications advance the watermark before calling an owner:
    /// a failed physical delivery can still have changed canonical state. This
    /// prevents a cached source packet from overtaking a newer Neutral/Stop or
    /// another producer, including a failed-but-admitted publication.
    /// </summary>
    internal bool TryPublishPolicyFeedback(
        in ControllerFeedbackActuatorState state, bool sourcePreserved,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        int bodyStrengthPercent, bool xboxBodyCarrierMode,
        int xboxBodyFrequencyLevel, int rumbleDelayMilliseconds,
        long profileRevision, ulong expiresAtMicroseconds,
        ulong expectedPublicationRevision, out ulong resultingPublicationRevision,
        Switch2DualSenseDelayedPolicyGuard delayedPolicyGuard = default)
    {
        lock (gate)
        {
            resultingPublicationRevision = 0;
            if (!active || (expectedPublicationRevision != 0 &&
                    expectedPublicationRevision != publicationRevision))
            {
                return false;
            }
            ulong beforePublicationRevision = publicationRevision;
            bool accepted = sourcePreserved ? TryPublishSourcePreserved(state,
                fidelity, left, right, bodyStrengthPercent, xboxBodyCarrierMode,
                xboxBodyFrequencyLevel, rumbleDelayMilliseconds, profileRevision,
                expiresAtMicroseconds, delayedPolicyGuard) : TryPublish(state,
                    bodyStrengthPercent: bodyStrengthPercent,
                    xboxBodyCarrierMode: xboxBodyCarrierMode,
                    xboxBodyFrequencyLevel: xboxBodyFrequencyLevel,
                    rumbleDelayMilliseconds: rumbleDelayMilliseconds,
                    profileRevision: profileRevision,
                    expiresAtMicroseconds: expiresAtMicroseconds,
                    delayedPolicyGuard: delayedPolicyGuard);
            // A failed owner dispatch may already have admitted canonical
            // state. Return only this attempt's newly consumed watermark so
            // its caller can retain cleanup-only authority. Never expose an
            // unrelated current watermark after pre-admission validation fails.
            if (publicationRevision != beforePublicationRevision)
            {
                resultingPublicationRevision = publicationRevision;
            }
            return accepted;
        }
    }

    internal bool TryRequestPlayerLedMask(byte playerLedMask)
    {
        lock (gate)
        {
            return active && owner.TryRequestPlayerLedMask(this,
                playerLedMask);
        }
    }

    // A live policy edit may remove components from an accepted event, but
    // must not renew that event's lifetime. Resolve at final owner publication
    // time, not when the UI captured the request. Expired state becomes Neutral.
    internal static ulong RemainingLifetime(ulong nowMicroseconds,
        ulong expiresAtMicroseconds) => expiresAtMicroseconds == 0 ?
            ControllerFeedbackFrame.MaxTimeToLiveMicroseconds :
            expiresAtMicroseconds <= nowMicroseconds ? 0 :
            Math.Min(ControllerFeedbackFrame.MaxTimeToLiveMicroseconds,
                expiresAtMicroseconds - nowMicroseconds);

    internal bool TryRetire()
    {
        lock (gate)
        {
            if (disconnectedRetired) return true;
            if (publicationRevision != ulong.MaxValue)
            {
                publicationRevision++;
            }
            ClearDelayedFeedbackNoLock(disposeTimer: true);
            ClearImpulseReleaseNoLock(disposeTimer: true);
            bool retired = active && owner.TryRetireSession(this, ingress);
            if (ingress?.HasPublishedTerminalStop == true)
                Volatile.Write(ref terminalBrokerStop, true);
            if (!retired)
            {
                return false;
            }
            Volatile.Write(ref active, false);
        }
        return true;
    }

    // Only the physical lifetime owner calls this after exact disconnect/drain
    // proof and pump sealing. Local disposal is not a delivered broker Stop.
    internal void RetireDisconnectedTarget()
    {
        lock (gate)
        {
            Volatile.Write(ref active, false);
            Volatile.Write(ref disconnectedRetired, true);
            if (publicationRevision != ulong.MaxValue) publicationRevision++;
            ClearDelayedFeedbackNoLock(disposeTimer: true);
            ClearImpulseReleaseNoLock(disposeTimer: true);
        }
    }

    private bool TryQueueDelayedNoLock(in DelayedFeedback feedback,
        int delayMilliseconds, long profileRevision)
    {
        if (selectedDelayMilliseconds != delayMilliseconds ||
            selectedProfileRevision != profileRevision ||
            selectedDelayedPolicyGuard != feedback.PolicyGuard)
        {
            ClearDelayedFeedbackNoLock(disposeTimer: false);
            selectedDelayMilliseconds = delayMilliseconds;
            selectedProfileRevision = profileRevision;
            selectedDelayedPolicyGuard = feedback.PolicyGuard;
        }

        delayedFeedback ??= new Queue<ScheduledFeedback>();
        if (delayedFeedback.Count >= MaximumQueuedFeedback)
        {
            // Prefer the newest complete state over a stale backlog whose Stop
            // might otherwise be unable to enter the bounded queue.
            delayedFeedback.Clear();
        }
        long dueMilliseconds = Environment.TickCount64 + delayMilliseconds;
        bool scheduleTimer = delayedFeedback.Count == 0;
        delayedFeedback.Enqueue(new ScheduledFeedback(feedback,
            dueMilliseconds));
        if (scheduleTimer)
        {
            ScheduleDelayedTimerNoLock(dueMilliseconds);
        }
        return true;
    }

    private void DelayedTimerTick(object state)
    {
        try
        {
            lock (gate)
            {
                if (!active || delayedFeedback == null)
                {
                    return;
                }
                long nowMilliseconds = Environment.TickCount64;
                while (delayedFeedback.Count != 0 &&
                    delayedFeedback.Peek().DueMilliseconds <= nowMilliseconds)
                {
                    ScheduledFeedback scheduled = delayedFeedback.Dequeue();
                    _ = DispatchDelayedNoLock(scheduled.Feedback);
                    nowMilliseconds = Environment.TickCount64;
                }
                if (delayedFeedback.Count != 0)
                {
                    ScheduleDelayedTimerNoLock(
                        delayedFeedback.Peek().DueMilliseconds);
                }
            }
        }
        catch
        {
            // Feedback scheduling is best effort and must never terminate a
            // process or controller input worker. The owner remains the sole
            // source of physical retry/quarantine evidence.
        }
    }

    private bool DispatchDelayedNoLock(in DelayedFeedback feedback)
    {
        if (!feedback.PolicyGuard.IsCurrent)
        {
            // Profile loads may run on the input queue; stream replacement
            // can also precede its queued-media cleanup. Final flags and the
            // captured stream generation are read here without locks or I/O
            // on input. Never admit old rich media while the existing control
            // worker or recovery drain is preparing its release.
            ClearDelayedFeedbackNoLock(disposeTimer: false);
            ClearImpulseReleaseNoLock(disposeTimer: false);
            if (publicationRevision == ulong.MaxValue)
            {
                return false;
            }
            publicationRevision++;
            return owner.TryPublishAndPump(this, ingress,
                default(ControllerFeedbackActuatorState),
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
                Switch2HdRumbleImpulseTuning.Default, feedback.BodyTuning);
        }
        switch (feedback.Kind)
        {
            case DelayedFeedbackKind.Wire:
                if (!ControllerFeedbackClock.TryGetTimestampMicroseconds(
                        out ulong nowMicroseconds) ||
                    !ControllerFeedbackFrame.TryCreate(
                        feedback.Frame.Source, feedback.Frame.Command,
                        feedback.Frame.Actuators, feedback.Frame.BodyLow,
                        feedback.Frame.BodyHigh,
                        feedback.Frame.LeftTrigger,
                        feedback.Frame.RightTrigger,
                        feedback.Frame.Sequence,
                        feedback.Frame.DeviceGeneration,
                        feedback.Frame.TransportGeneration,
                        feedback.Frame.OwnershipEpoch, nowMicroseconds,
                        feedback.Frame.TimeToLiveMicroseconds,
                        out ControllerFeedbackFrame delayedFrame))
                {
                    return false;
                }
                Span<byte> wire = stackalloc byte[
                    ControllerFeedbackFrame.SerializedLength];
                return delayedFrame.TryWriteTo(wire) &&
                    DispatchWireNoLock(delayedFrame, feedback.Policy,
                        feedback.ImpulseTuning, feedback.BodyTuning);

            case DelayedFeedbackKind.State:
                ClearImpulseReleaseNoLock(disposeTimer: false);
                return owner.TryPublishAndPump(this, ingress,
                    feedback.State, feedback.Policy,
                    feedback.ImpulseTuning, feedback.BodyTuning);

            case DelayedFeedbackKind.SourcePreserved:
                ClearImpulseReleaseNoLock(disposeTimer: false);
                return owner.TryPublishSourcePreservedAndPump(this, ingress,
                    feedback.State, feedback.Fidelity, feedback.Left,
                    feedback.Right, feedback.BodyTuning);

            default:
                return false;
        }
    }

    private bool DispatchWireNoLock(in ControllerFeedbackFrame frame,
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ReadOnlySpan<byte> originalWire = default)
    {
        if (policy != Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating || frame.IsStop ||
            frame.Source is not (
                ControllerFeedbackSource.XboxOneVirtualDevice or
                ControllerFeedbackSource.XboxSeriesVirtualDevice))
        {
            ClearImpulseReleaseNoLock(disposeTimer: false);
            return PublishWireFrameNoLock(frame, policy, impulseTuning,
                bodyTuning, originalWire);
        }
        if (!ControllerFeedbackClock.TryGetTimestampMicroseconds(
                out ulong nowMicroseconds))
        {
            return false;
        }

        impulseReleaseEnvelope.Update(frame.LeftTrigger,
            frame.RightTrigger, nowMicroseconds, out ushort leftTrigger,
            out ushort rightTrigger);
        bool usesReleasePresentation = leftTrigger != frame.LeftTrigger ||
            rightTrigger != frame.RightTrigger;
        if (usesReleasePresentation)
        {
            if (nextImpulseReleasePresentationRevision == ulong.MaxValue)
            {
                ClearImpulseReleaseNoLock(disposeTimer: false);
                return false;
            }
            ulong revision = ++nextImpulseReleasePresentationRevision;
            if (!owner.TryStageImpulseReleasePresentation(this, frame,
                    leftTrigger, rightTrigger, revision))
            {
                return false;
            }
            impulseReleaseCanonicalFrame = frame;
            hasImpulseReleaseCanonicalFrame = true;
        }

        bool published = PublishWireFrameNoLock(frame, policy,
            impulseTuning, bodyTuning, originalWire);
        if (impulseReleaseEnvelope.HasPendingRelease &&
            hasImpulseReleaseCanonicalFrame)
        {
            ScheduleImpulseReleaseTimerNoLock();
        }
        else
        {
            _ = owner.TryClearImpulseReleasePresentation(this);
            hasImpulseReleaseCanonicalFrame = false;
            StopImpulseReleaseTimerNoLock();
        }
        return published;
    }

    private bool PublishWireFrameNoLock(in ControllerFeedbackFrame frame,
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ReadOnlySpan<byte> originalWire)
    {
        if (originalWire.Length == ControllerFeedbackFrame.SerializedLength)
        {
            return owner.TryPublishAndPump(this, ingress, originalWire,
                policy, impulseTuning, bodyTuning);
        }
        Span<byte> wire = stackalloc byte[
            ControllerFeedbackFrame.SerializedLength];
        return frame.TryWriteTo(wire) && owner.TryPublishAndPump(this,
            ingress, wire, policy, impulseTuning, bodyTuning);
    }

    private void ImpulseReleaseTimerTick(object state)
    {
        try
        {
            lock (gate)
            {
                if (!active || !hasImpulseReleaseCanonicalFrame ||
                    !ControllerFeedbackClock.TryGetTimestampMicroseconds(
                        out ulong nowMicroseconds))
                {
                    return;
                }

                impulseReleaseEnvelope.Resolve(nowMicroseconds,
                    out ushort leftTrigger, out ushort rightTrigger);
                if (nextImpulseReleasePresentationRevision == ulong.MaxValue)
                {
                    ClearImpulseReleaseNoLock(disposeTimer: false);
                    return;
                }
                ulong revision = ++nextImpulseReleasePresentationRevision;
                bool staged = owner.TryStageImpulseReleasePresentation(this,
                    impulseReleaseCanonicalFrame, leftTrigger, rightTrigger,
                    revision);
                bool presented = staged &&
                    owner.TryRefreshCurrentPresentation(this,
                        impulseReleaseCanonicalFrame, revision);
                bool frameExpired = nowMicroseconds >=
                        impulseReleaseCanonicalFrame.TimestampMicroseconds &&
                    nowMicroseconds - impulseReleaseCanonicalFrame.
                        TimestampMicroseconds >=
                    impulseReleaseCanonicalFrame.TimeToLiveMicroseconds;
                if (presented &&
                        !impulseReleaseEnvelope.HasPendingRelease ||
                    frameExpired)
                {
                    if (presented)
                    {
                        _ = owner.TryClearImpulseReleasePresentation(this);
                    }
                    hasImpulseReleaseCanonicalFrame = false;
                    StopImpulseReleaseTimerNoLock();
                    return;
                }
                ScheduleImpulseReleaseTimerNoLock();
            }
        }
        catch
        {
            // A presentation envelope cannot be allowed to terminate the
            // controller input or transport worker. Terminal lifecycle still
            // owns the mandatory physical neutral.
        }
    }

    private void ScheduleImpulseReleaseTimerNoLock()
    {
        impulseReleaseTimer ??= new Timer(ImpulseReleaseTimerTick, null,
            Timeout.Infinite, Timeout.Infinite);
        _ = impulseReleaseTimer.Change(
            Switch2ImpulseReleaseEnvelope.PresentationIntervalMilliseconds,
            Timeout.Infinite);
    }

    private void StopImpulseReleaseTimerNoLock()
    {
        if (impulseReleaseTimer != null)
        {
            _ = impulseReleaseTimer.Change(Timeout.Infinite,
                Timeout.Infinite);
        }
    }

    private void ClearImpulseReleaseNoLock(bool disposeTimer)
    {
        _ = owner.TryClearImpulseReleasePresentation(this);
        impulseReleaseEnvelope.Clear();
        impulseReleaseCanonicalFrame = default;
        hasImpulseReleaseCanonicalFrame = false;
        StopImpulseReleaseTimerNoLock();
        if (disposeTimer && impulseReleaseTimer != null)
        {
            impulseReleaseTimer.Dispose();
            impulseReleaseTimer = null;
        }
    }

    private void ScheduleDelayedTimerNoLock(long dueMilliseconds)
    {
        long remaining = Math.Max(1L,
            dueMilliseconds - Environment.TickCount64);
        int due = (int)Math.Min(int.MaxValue, remaining);
        delayedTimer ??= new Timer(DelayedTimerTick, null,
            Timeout.Infinite, Timeout.Infinite);
        _ = delayedTimer.Change(due, Timeout.Infinite);
    }

    private void ClearDelayedFeedbackNoLock(bool disposeTimer)
    {
        delayedFeedback?.Clear();
        if (delayedTimer == null)
        {
            return;
        }
        _ = delayedTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (disposeTimer)
        {
            delayedTimer.Dispose();
            delayedTimer = null;
        }
    }

    private enum DelayedFeedbackKind : byte
    {
        Invalid = 0,
        Wire,
        State,
        SourcePreserved,
    }

    private readonly struct ScheduledFeedback
    {
        internal ScheduledFeedback(in DelayedFeedback feedback,
            long dueMilliseconds)
        {
            Feedback = feedback;
            DueMilliseconds = dueMilliseconds;
        }

        internal DelayedFeedback Feedback { get; }

        internal long DueMilliseconds { get; }
    }

    private readonly struct DelayedFeedback
    {
        private DelayedFeedback(DelayedFeedbackKind kind,
            in ControllerFeedbackFrame frame,
            in ControllerFeedbackActuatorState state,
            Switch2HdRumbleFeedbackPolicy policy,
            in Switch2HdRumbleImpulseTuning impulseTuning,
            in Switch2HdRumbleBodyTuning bodyTuning,
            Switch2HdRumbleFeedbackFidelity fidelity,
            in Switch2HdRumbleGroup left,
            in Switch2HdRumbleGroup right,
            Switch2DualSenseDelayedPolicyGuard policyGuard = default)
        {
            Kind = kind;
            Frame = frame;
            State = state;
            Policy = policy;
            ImpulseTuning = impulseTuning;
            BodyTuning = bodyTuning;
            Fidelity = fidelity;
            Left = left;
            Right = right;
            PolicyGuard = policyGuard;
        }

        internal DelayedFeedbackKind Kind { get; }
        internal ControllerFeedbackFrame Frame { get; }
        internal ControllerFeedbackActuatorState State { get; }
        internal Switch2HdRumbleFeedbackPolicy Policy { get; }
        internal Switch2HdRumbleImpulseTuning ImpulseTuning { get; }
        internal Switch2HdRumbleBodyTuning BodyTuning { get; }
        internal Switch2HdRumbleFeedbackFidelity Fidelity { get; }
        internal Switch2HdRumbleGroup Left { get; }
        internal Switch2HdRumbleGroup Right { get; }
        internal Switch2DualSenseDelayedPolicyGuard PolicyGuard { get; }

        internal static DelayedFeedback ForWire(
            in ControllerFeedbackFrame frame,
            Switch2HdRumbleFeedbackPolicy policy,
            in Switch2HdRumbleImpulseTuning impulseTuning,
            in Switch2HdRumbleBodyTuning bodyTuning) => new(
                DelayedFeedbackKind.Wire, frame, default, policy,
                impulseTuning, bodyTuning,
                Switch2HdRumbleFeedbackFidelity.Invalid, default, default);

        internal static DelayedFeedback ForState(
            in ControllerFeedbackActuatorState state,
            Switch2HdRumbleFeedbackPolicy policy,
            in Switch2HdRumbleImpulseTuning impulseTuning,
            in Switch2HdRumbleBodyTuning bodyTuning,
            Switch2DualSenseDelayedPolicyGuard policyGuard = default) => new(
                DelayedFeedbackKind.State, default, state, policy,
                impulseTuning, bodyTuning,
                Switch2HdRumbleFeedbackFidelity.Invalid, default, default, policyGuard);

        internal static DelayedFeedback ForSourcePreserved(
            in ControllerFeedbackActuatorState state,
            Switch2HdRumbleFeedbackFidelity fidelity,
            in Switch2HdRumbleGroup left,
            in Switch2HdRumbleGroup right,
            in Switch2HdRumbleBodyTuning bodyTuning,
            Switch2DualSenseDelayedPolicyGuard policyGuard = default) => new(
                DelayedFeedbackKind.SourcePreserved, default, state,
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
                Switch2HdRumbleImpulseTuning.Default, bodyTuning,
                fidelity, left, right, policyGuard);
    }
}

/// <summary>
/// Canonical CFBK-to-BLE-HD-rumble composition for one standalone physical
/// Switch 2 controller or one explicitly joined Joy-Con 2 pair. The input
/// runtime owns this exact object transitively; transport retirement cannot
/// begin until it has delivered terminal neutral and retired its sole logical
/// writer.
/// </summary>
internal sealed class Switch2BluetoothFeedbackLifetime :
    ISwitch2VirtualFeedbackSessionOwner
{
    private readonly object gate = new();
    private readonly Switch2ControllerModel model;
    private readonly Switch2ControllerModel secondaryModel;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;
    private readonly ControllerFeedbackStateLanePump pump;
    private readonly Switch2HdRumbleDeliverySink sink;
    private readonly ISwitch2BluetoothPlayerLedTransportLease
        playerLedTransport;
    private readonly ulong playerLedDeviceGeneration;
    private readonly ulong playerLedTransportGeneration;
    private readonly ISwitch2BluetoothPlayerLedTransportLease
        secondaryPlayerLedTransport;
    private readonly ulong secondaryDeviceGeneration;
    private readonly ulong secondaryTransportGeneration;
    private readonly bool joinedPair;
    private readonly ISwitch2BluetoothDisconnectedOutputProof disconnectedOutputProof;
    private ISwitch2BluetoothDisconnectedOutputProof joinedLeftReleaseProof;
    private ISwitch2BluetoothDisconnectedOutputProof joinedRightReleaseProof;
    private Switch2JoyConJoinedHdRumblePhysicalWriter joinedPhysicalWriter;
    private Switch2VirtualFeedbackSession activeSession;
    private ulong nextVirtualFeedbackOwnershipEpoch;
    private bool activated;
    private bool stopping;
    private bool retired;
    private Switch2BluetoothFeedbackRetirementFailure lastRetirementFailure;
    private string wirePublicationStage = "NotEntered";
    private ControllerFeedbackPumpDisposition wirePublicationDisposition;

    public string DescribeWirePublication()
    {
        lock (gate)
            return $"stage={wirePublicationStage}, activated={activated}, stopping={stopping}, " +
                $"retired={retired}, pump={wirePublicationDisposition}, physical={sink.LastFailure}";
    }

    private Switch2BluetoothFeedbackLifetime(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration,
        ControllerFeedbackStateLanePump pump,
        Switch2HdRumbleDeliverySink sink,
        ISwitch2BluetoothPlayerLedTransportLease playerLedTransport,
        Switch2ControllerModel secondaryModel =
            Switch2ControllerModel.Unknown,
        ulong playerLedDeviceGeneration = 0,
        ulong playerLedTransportGeneration = 0,
        ulong secondaryDeviceGeneration = 0,
        ulong secondaryTransportGeneration = 0,
        ISwitch2BluetoothPlayerLedTransportLease
            secondaryPlayerLedTransport = null,
        ISwitch2BluetoothDisconnectedOutputProof disconnectedOutputProof = null)
    {
        this.model = model;
        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        this.pump = pump;
        this.sink = sink;
        this.playerLedTransport = playerLedTransport;
        this.playerLedDeviceGeneration = playerLedDeviceGeneration == 0 ?
            deviceGeneration : playerLedDeviceGeneration;
        this.playerLedTransportGeneration = playerLedTransportGeneration == 0 ?
            transportGeneration : playerLedTransportGeneration;
        this.secondaryModel = secondaryModel;
        this.secondaryDeviceGeneration = secondaryDeviceGeneration;
        this.secondaryTransportGeneration = secondaryTransportGeneration;
        this.secondaryPlayerLedTransport = secondaryPlayerLedTransport;
        joinedPair = secondaryModel != Switch2ControllerModel.Unknown;
        this.disconnectedOutputProof = disconnectedOutputProof;
    }

    internal static bool TryCreate(
        ISwitch2BluetoothHdRumbleBindableTransportLease lease,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, out Switch2BluetoothFeedbackLifetime owner,
        Switch2HdRumbleFeedbackPolicy policy =
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility)
    {
        owner = null;
        if (lease == null || !lease.HasHdRumbleOutput ||
            deviceGeneration == 0 || transportGeneration == 0 ||
            !lease.Authenticates(model, deviceGeneration,
                transportGeneration) ||
            !ControllerFeedbackStateLanePump.TryCreate(deviceGeneration,
                transportGeneration, out ControllerFeedbackStateLanePump pump))
        {
            return false;
        }

        try
        {
            var writer = new Switch2BluetoothHdRumblePhysicalWriter(lease,
                model, deviceGeneration, transportGeneration);
            var sink = new Switch2HdRumbleDeliverySink(writer,
                deviceGeneration, transportGeneration, policy);
            owner = new Switch2BluetoothFeedbackLifetime(model,
                deviceGeneration, transportGeneration, pump, sink,
                lease as ISwitch2BluetoothPlayerLedTransportLease,
                disconnectedOutputProof: lease as ISwitch2BluetoothDisconnectedOutputProof);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryCreateJoined(
        ISwitch2BluetoothHdRumbleBindableTransportLease leftLease,
        ISwitch2BluetoothHdRumbleBindableTransportLease rightLease,
        ulong logicalDeviceGeneration, ulong logicalTransportGeneration,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration,
        out Switch2BluetoothFeedbackLifetime owner,
        Switch2HdRumbleFeedbackPolicy policy =
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility)
    {
        owner = null;
        if (leftLease == null || rightLease == null ||
            !leftLease.HasHdRumbleOutput || !rightLease.HasHdRumbleOutput ||
            logicalDeviceGeneration == 0 || logicalTransportGeneration == 0 ||
            !leftLease.Authenticates(Switch2ControllerModel.JoyCon2Left,
                leftDeviceGeneration, leftTransportGeneration) ||
            !rightLease.Authenticates(Switch2ControllerModel.JoyCon2Right,
                rightDeviceGeneration, rightTransportGeneration) ||
            !ControllerFeedbackStateLanePump.TryCreate(
                logicalDeviceGeneration, logicalTransportGeneration,
                out ControllerFeedbackStateLanePump pump))
        {
            return false;
        }

        try
        {
            var leftWriter = new Switch2BluetoothHdRumblePhysicalWriter(
                leftLease, Switch2ControllerModel.JoyCon2Left,
                leftDeviceGeneration, leftTransportGeneration);
            var rightWriter = new Switch2BluetoothHdRumblePhysicalWriter(
                rightLease, Switch2ControllerModel.JoyCon2Right,
                rightDeviceGeneration, rightTransportGeneration);
            var joinedWriter =
                new Switch2JoyConJoinedHdRumblePhysicalWriter(leftWriter,
                    rightWriter, logicalDeviceGeneration,
                    logicalTransportGeneration, leftDeviceGeneration,
                    leftTransportGeneration, rightDeviceGeneration,
                    rightTransportGeneration);
            var sink = new Switch2HdRumbleDeliverySink(joinedWriter,
                logicalDeviceGeneration, logicalTransportGeneration, policy);
            owner = new Switch2BluetoothFeedbackLifetime(
                Switch2ControllerModel.JoyCon2Left,
                logicalDeviceGeneration, logicalTransportGeneration, pump,
                sink, leftLease as ISwitch2BluetoothPlayerLedTransportLease,
                Switch2ControllerModel.JoyCon2Right,
                leftDeviceGeneration, leftTransportGeneration,
                rightDeviceGeneration, rightTransportGeneration,
                rightLease as ISwitch2BluetoothPlayerLedTransportLease);
            owner.joinedPhysicalWriter = joinedWriter;
            owner.joinedLeftReleaseProof = leftLease as ISwitch2BluetoothDisconnectedOutputProof;
            owner.joinedRightReleaseProof = rightLease as ISwitch2BluetoothDisconnectedOutputProof;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool IsRetired
    {
        get { lock (gate) { return retired; } }
    }

    internal Switch2BluetoothFeedbackRetirementFailure
        LastRetirementFailure
    {
        get { lock (gate) { return lastRetirementFailure; } }
    }

    internal Switch2HdRumblePhysicalWriteFailure LastPhysicalWriteFailure =>
        sink.LastFailure;

    internal bool Authenticates(Switch2ControllerModel candidateModel,
        ulong candidateDeviceGeneration,
        ulong candidateTransportGeneration) =>
        !joinedPair &&
        candidateModel == model &&
        candidateDeviceGeneration == deviceGeneration &&
        candidateTransportGeneration == transportGeneration;

    internal bool AuthenticatesJoined(ulong candidateDeviceGeneration,
        ulong candidateTransportGeneration) => joinedPair &&
        candidateDeviceGeneration == deviceGeneration &&
        candidateTransportGeneration == transportGeneration;

    internal bool TryActivate()
    {
        lock (gate)
        {
            if (stopping || retired)
            {
                return false;
            }
            activated = true;
            return true;
        }
    }

    internal bool TryCreateLane(
        ControllerFeedbackPublicationOrigin origin,
        ControllerFeedbackSource source, ulong ownershipEpoch,
        ulong timeToLiveMicroseconds, ulong renewalIntervalMicroseconds,
        out ControllerFeedbackStateLanePump.Lane lane)
    {
        lane = null;
        lock (gate)
        {
            if (!activated || stopping || retired)
            {
                return false;
            }
        }

        return pump.TryCreateLane(origin, source, ownershipEpoch,
            timeToLiveMicroseconds, renewalIntervalMicroseconds, out lane);
    }

    internal ControllerFeedbackPumpDisposition TryPumpOnce(
        ulong nowMicroseconds, out ControllerFeedbackDelivery delivery)
    {
        delivery = default;
        lock (gate)
        {
            if (!activated || stopping || retired)
            {
                return ControllerFeedbackPumpDisposition.None;
            }
        }

        return pump.PumpOnce(nowMicroseconds, sink, out delivery);
    }

    /// <summary>
    /// Presents an exact Switch 2-native profile effect through the already
    /// authenticated ProfileEffect lane and this lifetime's sole writer. The
    /// canonical marker remains the arbitration identity; the native groups
    /// are consumed only if that exact frame wins delivery.
    /// </summary>
    internal bool TryPublishNativeProfileEffectAndPump(
        ControllerFeedbackStateLanePump.Lane lane,
        in ControllerFeedbackActuatorState state,
        in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right) =>
        TryPublishNativeLocalEffectAndPump(lane, state, left, right,
            ControllerFeedbackPublicationOrigin.ProfileEffect,
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2ProfileEffect);

    internal bool TryPublishNativePreviewAndPump(
        ControllerFeedbackStateLanePump.Lane lane,
        in ControllerFeedbackActuatorState state,
        in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right) =>
        TryPublishNativeLocalEffectAndPump(lane, state, left, right,
            ControllerFeedbackPublicationOrigin.TestPreview,
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2TestPreview);

    private bool TryPublishNativeLocalEffectAndPump(
        ControllerFeedbackStateLanePump.Lane lane,
        in ControllerFeedbackActuatorState state,
        in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right,
        ControllerFeedbackPublicationOrigin origin,
        Switch2HdRumbleFeedbackFidelity fidelity)
    {
        lock (gate)
        {
            if (!activated || stopping || retired)
            {
                return false;
            }
        }
        if (!pump.AuthenticatesLane(lane,
                origin,
                ControllerFeedbackSource.Xbox360VirtualDevice) ||
            !ControllerFeedbackClock.TryGetTimestampMicroseconds(
                out ulong nowMicroseconds) ||
            !lane.TryPublish(state, nowMicroseconds,
                out ControllerFeedbackFrame frame))
        {
            return false;
        }
        if (!sink.TryStageSourcePreservedSynthesis(frame, fidelity,
                left, right) ||
            !pump.TryRefreshCurrentPresentation(nowMicroseconds))
        {
            _ = lane.TryWithdraw(nowMicroseconds);
            _ = pump.PumpOnce(nowMicroseconds, sink, out _);
            return false;
        }

        ControllerFeedbackPumpDisposition result = pump.PumpOnce(
            nowMicroseconds, sink, out _);
        return result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None;
    }

    internal Switch2BluetoothPlayerLedRequestResult TryRequestPlayerLed(
        byte playerNumber)
    {
        lock (gate)
        {
            if (!activated || stopping || retired)
            {
                return Switch2BluetoothPlayerLedRequestResult.Reject(
                    Switch2BluetoothPlayerLedRequestFailure.StaleLifetime);
            }
            if (playerLedTransport == null ||
                !playerLedTransport.HasPlayerLedOutput)
            {
                return Switch2BluetoothPlayerLedRequestResult.Reject(
                    Switch2BluetoothPlayerLedRequestFailure.
                        OutputUnavailable);
            }
            Switch2BluetoothPlayerLedRequestResult primary =
                RequestPlayerLed(playerLedTransport, playerNumber,
                    exactMask: false, model, playerLedDeviceGeneration,
                    playerLedTransportGeneration);
            if (!joinedPair || !primary.Accepted)
            {
                return primary;
            }
            if (secondaryPlayerLedTransport == null ||
                !secondaryPlayerLedTransport.HasPlayerLedOutput)
            {
                return Switch2BluetoothPlayerLedRequestResult.Reject(
                    Switch2BluetoothPlayerLedRequestFailure.OutputUnavailable);
            }
            return RequestPlayerLed(secondaryPlayerLedTransport, playerNumber,
                exactMask: false, secondaryModel, secondaryDeviceGeneration,
                secondaryTransportGeneration);
        }
    }

    private static Switch2BluetoothPlayerLedRequestResult RequestPlayerLed(
        ISwitch2BluetoothPlayerLedTransportLease transport, byte value,
        bool exactMask, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration) =>
        exactMask ? transport.TryRequestPlayerLedMask(value, expectedModel,
            expectedDeviceGeneration, expectedTransportGeneration) :
        transport.TryRequestPlayerLed(value, expectedModel,
            expectedDeviceGeneration, expectedTransportGeneration);

    internal bool TryCreateVirtualFeedbackSession(
        ControllerFeedbackSource source,
        out Switch2VirtualFeedbackSession session)
    {
        session = null;
        lock (gate)
        {
            if (stopping || retired || activeSession != null ||
                source < ControllerFeedbackSource.XboxOneVirtualDevice ||
                source > ControllerFeedbackSource.Switch2VirtualDevice ||
                nextVirtualFeedbackOwnershipEpoch == ulong.MaxValue)
            {
                return false;
            }

            ulong ownershipEpoch =
                ++nextVirtualFeedbackOwnershipEpoch;
            if (!pump.TryCreateBrokerIngress(source, ownershipEpoch,
                    out ControllerFeedbackIngress ingress))
            {
                return false;
            }

            session = new Switch2VirtualFeedbackSession(this, ingress,
                ownershipEpoch);
            activeSession = session;
            return true;
        }
    }

    internal bool TryAbortUnpublished()
    {
        Switch2VirtualFeedbackSession session;
        lock (gate)
        {
            if (stopping || retired || activated)
            {
                return false;
            }
            stopping = true;
            session = activeSession;
        }

        if (session != null && !session.TryRetire())
        {
            return false;
        }
        bool complete = pump.SealPublications() &&
            pump.TryStopAndRetire(0, sink, maxAttempts: 0) &&
            pump.IsRetired && sink.TryRetire() && sink.IsRetired;
        lock (gate)
        {
            retired = complete;
        }
        return complete;
    }

    internal bool TryRetireDisconnectedTarget()
    {
        // This method is deliberately unavailable to a joined writer: one
        // missing half does not prove that its surviving actuator is stopped.
        if (joinedPair || disconnectedOutputProof == null ||
            !disconnectedOutputProof.IsDisconnectedAndReleased(model,
                deviceGeneration, transportGeneration)) return false;

        Switch2VirtualFeedbackSession session;
        lock (gate)
        {
            if (retired) return true;
            stopping = true;
            session = activeSession;
        }
        if (!pump.SealPublications()) return false;
        session?.RetireDisconnectedTarget();
        if (!pump.TryRetireDisconnectedTarget() || !sink.TryRetireDisconnectedTarget())
            return false;
        lock (gate)
        {
            activeSession = null;
            retired = true;
            lastRetirementFailure = Switch2BluetoothFeedbackRetirementFailure.None;
        }
        return true;
    }

    internal bool TryStopJoinedAfterPhysicalLoss(int maxAttempts)
    {
        if (!joinedPair || joinedPhysicalWriter == null || maxAttempts <= 0) return false;
        bool leftReleased = joinedLeftReleaseProof?.IsDisconnectedAndReleased(
            Switch2ControllerModel.JoyCon2Left, playerLedDeviceGeneration, playerLedTransportGeneration) == true;
        bool rightReleased = joinedRightReleaseProof?.IsDisconnectedAndReleased(
            Switch2ControllerModel.JoyCon2Right, secondaryDeviceGeneration, secondaryTransportGeneration) == true;
        if (!leftReleased && !rightReleased) return false;
        Switch2VirtualFeedbackSession session;
        lock (gate)
        {
            if (retired) return true;
            if (stopping || !activated) return false;
            stopping = true;
            session = activeSession;
        }
        if (!pump.SealPublications()) return false;
        // Seal delayed producers without claiming a feedback ACK from the
        // absent target. The joined writer admits only survivor Stops now.
        session?.RetireDisconnectedTarget();
        bool stopped = false;
        for (int attempt = 0; attempt < maxAttempts && !stopped; attempt++)
            stopped = joinedPhysicalWriter.TryStopSurvivingTargets(leftReleased, rightReleased);
        if (!stopped || !pump.TryRetireDisconnectedTarget() || !sink.TryRetireDisconnectedTarget()) return false;
        lock (gate)
        {
            activeSession = null;
            retired = true;
            lastRetirementFailure = Switch2BluetoothFeedbackRetirementFailure.None;
        }
        return true;
    }

    internal bool TryStopAndRetire(int maxAttempts)
    {
        Switch2VirtualFeedbackSession session;
        lock (gate)
        {
            if (retired)
            {
                return true;
            }
            if (stopping || !activated || maxAttempts <= 0)
            {
                lastRetirementFailure =
                    Switch2BluetoothFeedbackRetirementFailure.InvalidState;
                return false;
            }
            stopping = true;
            session = activeSession;
        }

        if (session != null && !session.TryRetire())
        {
            lock (gate)
            {
                lastRetirementFailure =
                    Switch2BluetoothFeedbackRetirementFailure.
                        TerminalDeliveryRejected;
            }
            return false;
        }
        Switch2BluetoothFeedbackRetirementFailure retirementFailure =
            Switch2BluetoothFeedbackRetirementFailure.None;
        if (!pump.SealPublications())
        {
            retirementFailure =
                Switch2BluetoothFeedbackRetirementFailure.SealRejected;
        }
        else if (!(sink.HasExactTerminalStop ?
                pump.TryStopAndRetire(CurrentMicroseconds(), sink,
                    maxAttempts) :
                pump.TryTerminalNeutralAndRetire(CurrentMicroseconds(), sink,
                    maxAttempts)))
        {
            retirementFailure = Switch2BluetoothFeedbackRetirementFailure.
                TerminalDeliveryRejected;
        }
        else if (!pump.IsRetired)
        {
            retirementFailure =
                Switch2BluetoothFeedbackRetirementFailure.PumpNotRetired;
        }
        else if (!sink.HasExactTerminalStop)
        {
            retirementFailure = Switch2BluetoothFeedbackRetirementFailure.
                SinkMissingTerminal;
        }
        else if (!sink.TryRetire() || !sink.IsRetired)
        {
            retirementFailure = Switch2BluetoothFeedbackRetirementFailure.
                SinkRetirementRejected;
        }
        bool complete = retirementFailure ==
            Switch2BluetoothFeedbackRetirementFailure.None;
        lock (gate)
        {
            retired = complete;
            lastRetirementFailure = retirementFailure;
        }
        return complete;
    }

    public bool TryPublishAndPump(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress, ReadOnlySpan<byte> wire,
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning)
    {
        wirePublicationStage = "OwnerLifetime";
        lock (gate)
        {
            if (stopping || retired ||
                !activated ||
                !ReferenceEquals(activeSession, session))
            {
                return false;
            }
        }

        // Reject stale/foreign broker frames before they can change the
        // presentation configuration of the current accepted effect.
        wirePublicationStage = "CanonicalIngress";
        if (!ingress.TryPublish(wire)) return false;
        wirePublicationStage = "SinkConfiguration";
        if (!sink.TrySelectConfiguration(policy, impulseTuning, bodyTuning,
                out bool presentationRefreshRequired))
        {
            return false;
        }
        wirePublicationStage = "PresentationRefresh";
        ulong nowMicroseconds = CurrentMicroseconds();
        // A valid expired watermark can leave no live Frame, or queue Stop
        // for the old effect. Refresh only if there is a frame; still pump the
        // canonical event before ACK. Writer/claim contention is not a no-op.
        if (presentationRefreshRequired &&
            !pump.TryRefreshCurrentPresentation(nowMicroseconds, allowNoFrame: true))
        {
            return false;
        }
        wirePublicationStage = "PhysicalPump";
        ControllerFeedbackPumpDisposition result = pump.PumpOnce(
            nowMicroseconds, sink, out _);
        wirePublicationDisposition = result;
        return result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None;
    }

    public bool TryPublishAndPump(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress,
        in ControllerFeedbackActuatorState state,
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ulong expiresAtMicroseconds = 0)
    {
        lock (gate)
        {
            if (stopping || retired || !activated ||
                !ReferenceEquals(activeSession, session))
            {
                return false;
            }
        }

        ulong nowMicroseconds = CurrentMicroseconds();
        ulong lifetime = Switch2VirtualFeedbackSession.RemainingLifetime(
            nowMicroseconds, expiresAtMicroseconds);
        ControllerFeedbackActuatorState effectiveState = lifetime == 0 ?
            default : state;
        if (!sink.TrySelectConfiguration(policy, impulseTuning, bodyTuning,
                out bool presentationRefreshRequired) ||
            !ingress.TryPublish(effectiveState, nowMicroseconds,
                lifetime == 0 ? ControllerFeedbackFrame.MaxTimeToLiveMicroseconds : lifetime))
        {
            return false;
        }
        if (presentationRefreshRequired &&
            !pump.TryRefreshCurrentPresentation(nowMicroseconds))
        {
            return false;
        }
        ControllerFeedbackPumpDisposition result = pump.PumpOnce(
            nowMicroseconds, sink, out _);
        return result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None;
    }

    public bool TryPublishSourcePreservedAndPump(
        Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress,
        in ControllerFeedbackActuatorState state,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ulong expiresAtMicroseconds = 0)
    {
        lock (gate)
        {
            if (stopping || retired || !activated ||
                !ReferenceEquals(activeSession, session))
            {
                return false;
            }
        }

        ulong nowMicroseconds = CurrentMicroseconds();
        ulong lifetime = Switch2VirtualFeedbackSession.RemainingLifetime(
            nowMicroseconds, expiresAtMicroseconds);
        if (lifetime == 0)
        {
            return TryPublishAndPump(session, ingress,
                default(ControllerFeedbackActuatorState),
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
                Switch2HdRumbleImpulseTuning.Default, bodyTuning);
        }
        if (!sink.TrySelectConfiguration(
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
                Switch2HdRumbleImpulseTuning.Default, bodyTuning,
                out bool presentationRefreshRequired) ||
            !ingress.TryPublish(state, nowMicroseconds, lifetime,
                out ControllerFeedbackFrame frame))
        {
            return false;
        }
        if (!sink.TryStageSourcePreservedSynthesis(frame, fidelity, left,
                right))
        {
            _ = ingress.TryPublishTerminalStop(nowMicroseconds);
            _ = pump.PumpOnce(nowMicroseconds, sink, out _);
            return false;
        }
        // The canonical marker deliberately does not flatten source PCM or
        // oscillator groups. Two distinct rich frames can therefore have the
        // same four-actuator marker. Re-present the exact newly staged frame;
        // ordinary canonical lease renewals still retain their normal
        // deduplication path in TryPublishAndPump.
        if (!pump.TryRefreshCurrentPresentation(nowMicroseconds))
        {
            _ = ingress.TryPublishTerminalStop(nowMicroseconds);
            _ = pump.PumpOnce(nowMicroseconds, sink, out _);
            return false;
        }

        ControllerFeedbackPumpDisposition result = pump.PumpOnce(
            nowMicroseconds, sink, out _);
        return result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None;
    }

    public bool TryStageImpulseReleasePresentation(
        Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame canonicalFrame, ushort leftTrigger,
        ushort rightTrigger, ulong presentationRevision)
    {
        lock (gate)
        {
            return !stopping && !retired && activated &&
                ReferenceEquals(activeSession, session) &&
                sink.TryStageImpulseReleasePresentation(canonicalFrame,
                    leftTrigger, rightTrigger, presentationRevision);
        }
    }

    public bool TryRefreshCurrentPresentation(
        Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame canonicalFrame,
        ulong presentationRevision)
    {
        lock (gate)
        {
            if (stopping || retired || !activated ||
                !ReferenceEquals(activeSession, session))
            {
                return false;
            }
        }
        ulong nowMicroseconds = CurrentMicroseconds();
        if (!pump.TryRefreshCurrentPresentation(nowMicroseconds))
        {
            return false;
        }
        ControllerFeedbackPumpDisposition result = pump.PumpOnce(
            nowMicroseconds, sink, out _);
        bool pumped = result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None;
        return pumped &&
            sink.HasPresentedImpulseReleaseRevision(canonicalFrame,
                presentationRevision);
    }

    public bool TryClearImpulseReleasePresentation(
        Switch2VirtualFeedbackSession session)
    {
        lock (gate)
        {
            return !stopping && !retired && activated &&
                ReferenceEquals(activeSession, session) &&
                sink.TryClearImpulseReleasePresentation();
        }
    }

    public bool TryRequestPlayerLedMask(
        Switch2VirtualFeedbackSession session, byte playerLedMask)
    {
        if ((playerLedMask & 0xF0) != 0)
        {
            return false;
        }

        lock (gate)
        {
            if (stopping || retired || !activated ||
                !ReferenceEquals(activeSession, session))
            {
                return false;
            }
            if (playerLedTransport == null ||
                !playerLedTransport.HasPlayerLedOutput)
            {
                return false;
            }
            Switch2BluetoothPlayerLedRequestResult primary =
                RequestPlayerLed(playerLedTransport, playerLedMask,
                    exactMask: true, model, playerLedDeviceGeneration,
                    playerLedTransportGeneration);
            if (!joinedPair || !primary.Accepted)
            {
                return primary.Accepted;
            }
            if (secondaryPlayerLedTransport == null ||
                !secondaryPlayerLedTransport.HasPlayerLedOutput)
            {
                return false;
            }
            return RequestPlayerLed(secondaryPlayerLedTransport,
                playerLedMask, exactMask: true, secondaryModel,
                secondaryDeviceGeneration,
                secondaryTransportGeneration).Accepted;
        }
    }

    public bool TryRefreshXboxOutputPolicy(Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame frame, Switch2XboxFeedbackPolicy policy)
    {
        lock (gate)
        {
            if (stopping || retired || !activated ||
                !ReferenceEquals(activeSession, session)) return false;
        }
        return Switch2XboxFeedbackPolicy.TryRefresh(pump, sink, frame, policy,
            CurrentMicroseconds());
    }

    public bool TryRetireSession(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress)
    {
        bool shouldPump;
        lock (gate)
        {
            if (!ReferenceEquals(activeSession, session))
            {
                return false;
            }
            shouldPump = activated && !retired;
        }

        ulong nowMicroseconds = CurrentMicroseconds();
        if (!ingress.TryPublishTerminalStop(nowMicroseconds))
        {
            return false;
        }

        bool neutral = !shouldPump;
        for (int attempt = 0; attempt < 3 && !neutral; attempt++)
        {
            ControllerFeedbackPumpDisposition result = pump.PumpOnce(
                nowMicroseconds, sink, out _);
            neutral = result is ControllerFeedbackPumpDisposition.Delivered or
                ControllerFeedbackPumpDisposition.None;
        }
        if (!neutral || !ingress.TryRetire())
        {
            return false;
        }

        lock (gate)
        {
            if (!ReferenceEquals(activeSession, session))
            {
                return false;
            }
            activeSession = null;
        }
        return true;
    }

    private static ulong CurrentMicroseconds() =>
        ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong value) ?
            value : 0;
}
