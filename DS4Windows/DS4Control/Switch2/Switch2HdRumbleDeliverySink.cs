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

namespace DS4Windows.Switch2;

internal enum Switch2HdRumblePhysicalWriteOutcome : byte
{
    Invalid = 0,
    Succeeded,
    ProvenRejected,
    OutcomeUncertain,
}

internal enum Switch2HdRumblePhysicalWriteFailure : byte
{
    None = 0,
    InvalidSubmission,
    StaleLifetime,
    Busy,
    TransportRejected,
    TransportEnded,
    DependencyThrew,
}

/// <summary>
/// Typed result from the one transport-owned physical output writer. An
/// uncertain result is never success evidence; the canonical feedback owner
/// retains the exact delivery and may retry it idempotently.
/// </summary>
internal readonly struct Switch2HdRumblePhysicalWriteResult
{
    private Switch2HdRumblePhysicalWriteResult(
        Switch2HdRumblePhysicalWriteOutcome outcome,
        Switch2HdRumblePhysicalWriteFailure failure)
    {
        Outcome = outcome;
        Failure = failure;
    }

    internal Switch2HdRumblePhysicalWriteOutcome Outcome { get; }

    internal Switch2HdRumblePhysicalWriteFailure Failure { get; }

    internal bool IsValid => Outcome switch
    {
        Switch2HdRumblePhysicalWriteOutcome.Succeeded =>
            Failure == Switch2HdRumblePhysicalWriteFailure.None,
        Switch2HdRumblePhysicalWriteOutcome.ProvenRejected or
            Switch2HdRumblePhysicalWriteOutcome.OutcomeUncertain =>
            Failure is >= Switch2HdRumblePhysicalWriteFailure.
                InvalidSubmission and <=
                Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
        _ => false,
    };

    internal bool Succeeded => IsValid && Outcome ==
        Switch2HdRumblePhysicalWriteOutcome.Succeeded;

    internal bool IsUncertain => IsValid && Outcome ==
        Switch2HdRumblePhysicalWriteOutcome.OutcomeUncertain;

    internal static Switch2HdRumblePhysicalWriteResult Success() => new(
        Switch2HdRumblePhysicalWriteOutcome.Succeeded,
        Switch2HdRumblePhysicalWriteFailure.None);

    internal static Switch2HdRumblePhysicalWriteResult Reject(
        Switch2HdRumblePhysicalWriteFailure failure)
    {
        if (failure == Switch2HdRumblePhysicalWriteFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
        return new Switch2HdRumblePhysicalWriteResult(
            Switch2HdRumblePhysicalWriteOutcome.ProvenRejected, failure);
    }

    internal static Switch2HdRumblePhysicalWriteResult Uncertain(
        Switch2HdRumblePhysicalWriteFailure failure)
    {
        if (failure == Switch2HdRumblePhysicalWriteFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
        return new Switch2HdRumblePhysicalWriteResult(
            Switch2HdRumblePhysicalWriteOutcome.OutcomeUncertain, failure);
    }
}

/// <summary>
/// Complete side-separated value submitted to a transport writer. Framing,
/// packet counters, cadence, OS calls, and completion evidence belong to that
/// writer rather than to the canonical feedback or translation layers.
/// </summary>
internal readonly struct Switch2HdRumblePhysicalSubmission
{
    private Switch2HdRumblePhysicalSubmission(
        ControllerFeedbackCommand command,
        Switch2HdRumbleFeedbackFidelity fidelity,
        Switch2HdRumbleGroup left, Switch2HdRumbleGroup right,
        ulong deviceGeneration, ulong transportGeneration,
        ulong deliveryEpoch, ControllerFeedbackSource source,
        ulong sequence, ulong ownershipEpoch,
        ulong timestampMicroseconds, ulong timeToLiveMicroseconds)
    {
        Command = command;
        Fidelity = fidelity;
        Left = left;
        Right = right;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        DeliveryEpoch = deliveryEpoch;
        Source = source;
        Sequence = sequence;
        OwnershipEpoch = ownershipEpoch;
        TimestampMicroseconds = timestampMicroseconds;
        TimeToLiveMicroseconds = timeToLiveMicroseconds;
    }

    internal ControllerFeedbackCommand Command { get; }

    internal Switch2HdRumbleFeedbackFidelity Fidelity { get; }

    internal Switch2HdRumbleGroup Left { get; }

    internal Switch2HdRumbleGroup Right { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal ulong DeliveryEpoch { get; }

    internal ControllerFeedbackSource Source { get; }

    internal ulong Sequence { get; }

    internal ulong OwnershipEpoch { get; }

    internal ulong TimestampMicroseconds { get; }

    internal ulong TimeToLiveMicroseconds { get; }

    internal bool IsStop => Command == ControllerFeedbackCommand.Stop;

    internal bool IsNeutral => Command is ControllerFeedbackCommand.Neutral or
        ControllerFeedbackCommand.Stop;

    internal bool HasValidInvariants()
    {
        if (DeviceGeneration == 0 || TransportGeneration == 0 ||
            DeliveryEpoch == 0 || Fidelity is <
                Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral or >
                Switch2HdRumbleFeedbackFidelity.
                    NativeSwitch2TestPreview)
        {
            return false;
        }

        if (IsStop)
        {
            return Fidelity ==
                    Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral &&
                Source == 0 && Sequence == 0 && OwnershipEpoch == 0 &&
                TimestampMicroseconds == 0 && TimeToLiveMicroseconds == 0 &&
                Left.Equals(default) && Right.Equals(default);
        }

        if ((Command != ControllerFeedbackCommand.Apply &&
                Command != ControllerFeedbackCommand.Neutral) ||
            Source < ControllerFeedbackSource.XboxOneVirtualDevice ||
            Source > ControllerFeedbackSource.Switch2VirtualDevice ||
            Sequence == 0 || OwnershipEpoch == 0 ||
            TimeToLiveMicroseconds == 0 ||
            TimeToLiveMicroseconds >
                ControllerFeedbackFrame.MaxTimeToLiveMicroseconds)
        {
            return false;
        }

        if (Command == ControllerFeedbackCommand.Apply)
        {
            return Fidelity is
                Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility or
                Switch2HdRumbleFeedbackFidelity.
                    SideLocalImpulseApproximation or
                Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough or
                Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand or
                Switch2HdRumbleFeedbackFidelity.
                    DualSenseAdaptiveTriggerApproximation or
                Switch2HdRumbleFeedbackFidelity.
                    NativeSwitch2ProfileEffect or
                Switch2HdRumbleFeedbackFidelity.
                    NativeSwitch2TestPreview;
        }

        return Fidelity ==
                Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral &&
            HasZeroAmplitude(Left) && HasZeroAmplitude(Right);
    }

    internal static bool TryCreateFrame(
        in Switch2HdRumbleFeedbackSynthesis synthesis,
        ulong deliveryEpoch,
        out Switch2HdRumblePhysicalSubmission submission)
    {
        submission = new Switch2HdRumblePhysicalSubmission(
            synthesis.Command, synthesis.Fidelity, synthesis.Left,
            synthesis.Right, synthesis.DeviceGeneration,
            synthesis.TransportGeneration, deliveryEpoch, synthesis.Source,
            synthesis.Sequence, synthesis.OwnershipEpoch,
            synthesis.TimestampMicroseconds,
            synthesis.TimeToLiveMicroseconds);
        if (submission.HasValidInvariants())
        {
            return true;
        }
        submission = default;
        return false;
    }

    internal static Switch2HdRumblePhysicalSubmission CreateStop(
        ulong deviceGeneration, ulong transportGeneration,
        ulong deliveryEpoch) => new(ControllerFeedbackCommand.Stop,
            Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral,
            default, default, deviceGeneration, transportGeneration,
            deliveryEpoch, default, 0, 0, 0, 0);

    /// <summary>
    /// Rebinds one already-validated joined-pair submission to an exact
    /// physical half. The complete left/right synthesis is preserved; the
    /// half-specific writer selects its own group during packet encoding.
    /// </summary>
    internal bool TryRebind(ulong deviceGeneration,
        ulong transportGeneration,
        out Switch2HdRumblePhysicalSubmission rebound)
    {
        rebound = new Switch2HdRumblePhysicalSubmission(Command, Fidelity,
            Left, Right, deviceGeneration, transportGeneration,
            DeliveryEpoch, Source, Sequence, OwnershipEpoch,
            TimestampMicroseconds, TimeToLiveMicroseconds);
        if (rebound.HasValidInvariants())
        {
            return true;
        }
        rebound = default;
        return false;
    }

    private static bool HasZeroAmplitude(in Switch2HdRumbleGroup group) =>
        !group.First.HasNonzeroAmplitude &&
        !group.Second.HasNonzeroAmplitude &&
        !group.Third.HasNonzeroAmplitude;
}

/// <summary>
/// Exact physical output lifetime. The implementation owns the OS handle,
/// transport framing/counter, pacing, completion proof, and neutral retries.
/// Authentication must be reference/generation-only and must perform no I/O.
/// </summary>
internal interface ISwitch2HdRumblePhysicalWriter
{
    bool Authenticates(ulong deviceGeneration, ulong transportGeneration);

    Switch2HdRumblePhysicalWriteResult TryWrite(
        in Switch2HdRumblePhysicalSubmission submission);
}

/// <summary>
/// Final canonical-feedback-to-Switch-2 boundary. It adds no arbitration or
/// queue: one existing <see cref="ControllerFeedbackStateLanePump"/> calls it
/// only after claiming and admitting the canonical event. The physical writer
/// is invoked outside the sink gate, with at most one call in flight.
/// </summary>
internal sealed class Switch2HdRumbleDeliverySink :
    IControllerFeedbackDeliverySink
{
    private readonly object gate = new();
    private readonly ISwitch2HdRumblePhysicalWriter writer;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;

    private ControllerFeedbackDelivery lastDelivered;
    private ControllerFeedbackDelivery unresolvedDelivery;
    private Switch2HdRumbleFeedbackPolicy selectedPolicy;
    private Switch2HdRumbleFeedbackPolicy unresolvedPolicy;
    private Switch2HdRumbleFeedbackPolicy lastDeliveredPolicy;
    private Switch2HdRumbleImpulseTuning selectedImpulseTuning;
    private Switch2HdRumbleImpulseTuning unresolvedImpulseTuning;
    private Switch2HdRumbleImpulseTuning lastDeliveredImpulseTuning;
    private Switch2HdRumbleBodyTuning selectedBodyTuning;
    private Switch2HdRumbleBodyTuning unresolvedBodyTuning;
    private Switch2HdRumbleBodyTuning lastDeliveredBodyTuning;
    private ulong currentDeliveryEpoch;
    private bool currentEpochStopped;
    private bool hasUnresolvedDelivery;
    private bool hasDeliveredFramePolicy;
    private bool hasSourcePreservedSynthesis;
    private ControllerFeedbackFrame sourcePreservedFrame;
    private Switch2HdRumbleGroup sourcePreservedLeft;
    private Switch2HdRumbleGroup sourcePreservedRight;
    private Switch2HdRumbleFeedbackFidelity sourcePreservedFidelity;
    private ControllerFeedbackFrame impulseReleaseFrame;
    private ushort impulseReleaseLeftTrigger;
    private ushort impulseReleaseRightTrigger;
    private ulong impulseReleasePresentationRevision;
    private ulong lastDeliveredImpulseReleaseRevision;
    private ushort unresolvedImpulseReleaseLeftTrigger;
    private ushort unresolvedImpulseReleaseRightTrigger;
    private ulong unresolvedImpulseReleaseRevision;
    private bool hasImpulseReleasePresentation;
    private bool unresolvedUsesImpulseRelease;
    private ControllerFeedbackFrame xboxPolicyFrame;
    private Switch2XboxFeedbackPolicy xboxPolicy;
    private ulong xboxPolicyRevision;
    private ulong lastDeliveredXboxPolicyRevision;
    private ulong unresolvedXboxPolicyRevision;
    private int writeActive;
    private int retired;
    private int uncertain;
    private Switch2HdRumblePhysicalWriteFailure lastFailure;

    internal Switch2HdRumbleDeliverySink(
        ISwitch2HdRumblePhysicalWriter writer,
        ulong deviceGeneration, ulong transportGeneration,
        Switch2HdRumbleFeedbackPolicy policy =
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        if (deviceGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceGeneration));
        }
        if (transportGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transportGeneration));
        }
        if (policy is not (Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating or
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility) ||
            !TryAuthenticate(writer, deviceGeneration, transportGeneration,
                out _))
        {
            throw new ArgumentException(
                "The physical writer does not authenticate this lifetime.",
                nameof(writer));
        }

        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        selectedPolicy = policy;
        selectedImpulseTuning = Switch2HdRumbleImpulseTuning.Default;
        selectedBodyTuning = Switch2HdRumbleBodyTuning.Default;
    }

    internal bool IsRetired => Volatile.Read(ref retired) != 0;

    internal bool HasUncertainWrite => Volatile.Read(ref uncertain) != 0;

    /// <summary>
    /// Pure exact terminal fact. Epoch zero is intentionally insufficient: an
    /// empty sink may otherwise retire without ever driving neutral output.
    /// </summary>
    internal bool HasExactTerminalStop
    {
        get
        {
            lock (gate)
            {
                return !hasUnresolvedDelivery && currentDeliveryEpoch != 0 &&
                    currentEpochStopped && lastDelivered.HasValidInvariants() &&
                    lastDelivered.Disposition ==
                        ControllerFeedbackDeliveryDisposition.Stop &&
                    lastDelivered.DeviceGeneration == deviceGeneration &&
                    lastDelivered.TransportGeneration == transportGeneration;
            }
        }
    }

    internal Switch2HdRumblePhysicalWriteFailure LastFailure
    {
        get
        {
            lock (gate)
            {
                return lastFailure;
            }
        }
    }

    /// <summary>
    /// Selects the profile policy for the next newly admitted canonical
    /// delivery. An uncertain exact retry retains the policy captured by its
    /// original attempt, so a live profile change cannot mutate retry bytes.
    /// </summary>
    internal bool TrySelectPolicy(Switch2HdRumbleFeedbackPolicy policy)
    {
        return TrySelectPolicy(policy, out _);
    }

    internal bool TrySelectPolicy(Switch2HdRumbleFeedbackPolicy policy,
        out bool presentationRefreshRequired)
    {
        return TrySelectConfiguration(policy,
            Switch2HdRumbleImpulseTuning.Default,
            Switch2HdRumbleBodyTuning.Default,
            out presentationRefreshRequired);
    }

    internal bool TrySelectConfiguration(
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        out bool presentationRefreshRequired) => TrySelectConfiguration(
            policy, impulseTuning, Switch2HdRumbleBodyTuning.Default,
            out presentationRefreshRequired);

    internal bool TrySelectConfiguration(
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        out bool presentationRefreshRequired)
    {
        presentationRefreshRequired = false;
        if (policy is not (Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating or
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility) ||
            !impulseTuning.IsValid || !bodyTuning.IsValid)
        {
            return false;
        }

        Switch2HdRumbleImpulseTuning effectiveImpulseTuning = policy ==
                Switch2HdRumbleFeedbackPolicy.
                    SideLocalImpulseDualBandSaturating ?
            impulseTuning : Switch2HdRumbleImpulseTuning.Default;

        lock (gate)
        {
            if (IsRetired)
            {
                return false;
            }
            presentationRefreshRequired = selectedPolicy != policy ||
                selectedImpulseTuning != effectiveImpulseTuning ||
                selectedBodyTuning != bodyTuning ||
                hasDeliveredFramePolicy &&
                    (lastDeliveredPolicy != policy ||
                    lastDeliveredImpulseTuning != effectiveImpulseTuning ||
                    lastDeliveredBodyTuning != bodyTuning);
            selectedPolicy = policy;
            selectedImpulseTuning = effectiveImpulseTuning;
            selectedBodyTuning = bodyTuning;
            return true;
        }
    }

    internal bool TryStageXboxOutputPolicy(in ControllerFeedbackFrame frame,
        Switch2XboxFeedbackPolicy policy, out ulong revision)
    {
        revision = 0;
        if (!frame.HasValidInvariants() || frame.IsStop ||
            frame.Source is not (ControllerFeedbackSource.XboxOneVirtualDevice or
                ControllerFeedbackSource.XboxSeriesVirtualDevice) ||
            frame.DeviceGeneration != deviceGeneration ||
            frame.TransportGeneration != transportGeneration) return false;
        lock (gate)
        {
            if (IsRetired || xboxPolicyRevision == ulong.MaxValue) return false;
            // Never enable an impulse component that was not selected when
            // this game frame was published. Off/on cannot revive an old effect.
            var available = xboxPolicyRevision != 0 && xboxPolicyFrame == frame ?
                xboxPolicy : new Switch2XboxFeedbackPolicy(true,
                    selectedPolicy == Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating);
            xboxPolicy = available.Intersect(policy);
            xboxPolicyFrame = frame;
            revision = ++xboxPolicyRevision;
            return true;
        }
    }

    internal bool HasPresentedXboxOutputPolicy(in ControllerFeedbackFrame frame,
        ulong revision)
    {
        lock (gate)
            return !IsRetired && !hasUnresolvedDelivery && revision != 0 &&
                lastDelivered.Disposition == ControllerFeedbackDeliveryDisposition.Frame &&
                lastDelivered.Frame == frame && lastDeliveredXboxPolicyRevision == revision;
    }

    /// <summary>
    /// Stages richer source data for one exact canonical frame. Arbitration,
    /// admission, Stop semantics, and physical ownership remain in the normal
    /// runtime; this value only replaces the lossy synthesis step after that
    /// same frame wins delivery.
    /// </summary>
    internal bool TryStageSourcePreservedSynthesis(
        in ControllerFeedbackFrame frame,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right)
    {
        bool validSource = fidelity switch
        {
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough =>
                frame.Source ==
                    ControllerFeedbackSource.Switch2VirtualDevice,
            Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand =>
                frame.Source is ControllerFeedbackSource.
                        DualSenseVirtualDevice or
                    ControllerFeedbackSource.DualSenseEdgeVirtualDevice,
            Switch2HdRumbleFeedbackFidelity.
                    DualSenseAdaptiveTriggerApproximation =>
                frame.Source is ControllerFeedbackSource.
                        DualSenseVirtualDevice or
                    ControllerFeedbackSource.DualSenseEdgeVirtualDevice,
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2ProfileEffect =>
                frame.Source ==
                    ControllerFeedbackSource.Xbox360VirtualDevice,
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2TestPreview =>
                frame.Source ==
                    ControllerFeedbackSource.Xbox360VirtualDevice,
            _ => false,
        };
        if (!validSource || !frame.HasValidInvariants() ||
            frame.Command != ControllerFeedbackCommand.Apply ||
            frame.DeviceGeneration != deviceGeneration ||
            frame.TransportGeneration != transportGeneration)
        {
            return false;
        }

        lock (gate)
        {
            if (IsRetired || hasUnresolvedDelivery)
            {
                return false;
            }
            sourcePreservedFrame = frame;
            sourcePreservedLeft = left;
            sourcePreservedRight = right;
            sourcePreservedFidelity = fidelity;
            hasSourcePreservedSynthesis = true;
            return true;
        }
    }

    /// <summary>
    /// Stages one presentation-only continuation of an already canonical Xbox
    /// impulse-trigger frame. The canonical sequence, owner, TTL, and delivery
    /// epoch remain unchanged; only the downstream trigger amplitudes may be
    /// reduced by the session's bounded release envelope.
    /// </summary>
    internal bool TryStageImpulseReleasePresentation(
        in ControllerFeedbackFrame frame, ushort leftTrigger,
        ushort rightTrigger, ulong presentationRevision)
    {
        if (!frame.HasValidInvariants() || frame.Source is not (
                ControllerFeedbackSource.XboxOneVirtualDevice or
                ControllerFeedbackSource.XboxSeriesVirtualDevice) ||
            frame.DeviceGeneration != deviceGeneration ||
            frame.TransportGeneration != transportGeneration ||
            presentationRevision == 0)
        {
            return false;
        }

        lock (gate)
        {
            if (IsRetired || hasImpulseReleasePresentation &&
                    impulseReleaseFrame == frame &&
                    presentationRevision <=
                        impulseReleasePresentationRevision)
            {
                return false;
            }
            impulseReleaseFrame = frame;
            impulseReleaseLeftTrigger = leftTrigger;
            impulseReleaseRightTrigger = rightTrigger;
            impulseReleasePresentationRevision = presentationRevision;
            hasImpulseReleasePresentation = true;
            return true;
        }
    }

    internal bool HasPresentedImpulseReleaseRevision(
        in ControllerFeedbackFrame frame, ulong presentationRevision)
    {
        lock (gate)
        {
            return presentationRevision != 0 && !hasUnresolvedDelivery &&
                lastDelivered.Disposition ==
                    ControllerFeedbackDeliveryDisposition.Frame &&
                lastDelivered.Frame == frame &&
                lastDeliveredImpulseReleaseRevision >=
                    presentationRevision;
        }
    }

    internal bool TryClearImpulseReleasePresentation()
    {
        lock (gate)
        {
            if (IsRetired)
            {
                return false;
            }
            hasImpulseReleasePresentation = false;
            impulseReleaseFrame = default;
            impulseReleaseLeftTrigger = default;
            impulseReleaseRightTrigger = default;
            impulseReleasePresentationRevision = default;
            return true;
        }
    }

    internal bool TryRetire()
    {
        if (Interlocked.CompareExchange(ref writeActive, 1, 0) != 0)
        {
            return false;
        }
        try
        {
            lock (gate)
            {
                if (hasUnresolvedDelivery ||
                    currentDeliveryEpoch != 0 && !currentEpochStopped)
                {
                    return false;
                }
                Volatile.Write(ref retired, 1);
                return true;
            }
        }
        finally
        {
            Volatile.Write(ref writeActive, 0);
        }
    }

    /// <summary>
    /// Requires the owner to prove exact native removal, seal the transport,
    /// and drain retained I/O first. Does not manufacture a delivered Stop.
    /// </summary>
    internal bool TryRetireDisconnectedTarget()
    {
        if (Interlocked.CompareExchange(ref writeActive, 1, 0) != 0)
        {
            return false;
        }
        try
        {
            lock (gate)
            {
                Volatile.Write(ref retired, 1);
                return true;
            }
        }
        finally
        {
            Volatile.Write(ref writeActive, 0);
        }
    }

    public bool TryDeliver(in ControllerFeedbackDelivery delivery)
    {
        if (IsRetired || !delivery.HasValidInvariants() ||
            delivery.DeviceGeneration != deviceGeneration ||
            delivery.TransportGeneration != transportGeneration ||
            Interlocked.CompareExchange(ref writeActive, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            if (!TryAuthenticate(writer, deviceGeneration,
                    transportGeneration, out bool authenticationThrew))
            {
                return RecordFailure(authenticationThrew ?
                        Switch2HdRumblePhysicalWriteFailure.DependencyThrew :
                        Switch2HdRumblePhysicalWriteFailure.StaleLifetime,
                    uncertainOutcome: false, default, default);
            }

            Switch2HdRumbleFeedbackPolicy deliveryPolicy;
            Switch2HdRumbleImpulseTuning deliveryImpulseTuning;
            Switch2HdRumbleBodyTuning deliveryBodyTuning;
            bool useSourcePreservedSynthesis;
            Switch2HdRumbleGroup preservedLeft;
            Switch2HdRumbleGroup preservedRight;
            Switch2HdRumbleFeedbackFidelity preservedFidelity;
            bool useImpulseReleaseSynthesis;
            ushort releaseLeftTrigger;
            ushort releaseRightTrigger;
            ulong releasePresentationRevision;
            ulong deliveryXboxPolicyRevision;
            lock (gate)
            {
                if (IsRetired)
                {
                    return false;
                }
                bool exactUnresolvedRetry = hasUnresolvedDelivery &&
                    unresolvedDelivery == delivery;
                deliveryPolicy = exactUnresolvedRetry ? unresolvedPolicy :
                    selectedPolicy;
                deliveryImpulseTuning = exactUnresolvedRetry ?
                    unresolvedImpulseTuning :
                    selectedImpulseTuning;
                deliveryBodyTuning = exactUnresolvedRetry ?
                    unresolvedBodyTuning :
                    selectedBodyTuning;
                bool useXboxPolicy = !exactUnresolvedRetry && xboxPolicyRevision != 0 &&
                    delivery.Disposition == ControllerFeedbackDeliveryDisposition.Frame &&
                    delivery.Origin == ControllerFeedbackPublicationOrigin.NativeGame &&
                    delivery.Frame == xboxPolicyFrame;
                deliveryXboxPolicyRevision = exactUnresolvedRetry ? unresolvedXboxPolicyRevision :
                    useXboxPolicy ? xboxPolicyRevision : 0;
                if (useXboxPolicy)
                {
                    if (!xboxPolicy.OutputEnabled || !xboxPolicy.ImpulseEnabled)
                        deliveryPolicy = Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility;
                    if (!xboxPolicy.OutputEnabled)
                        _ = Switch2HdRumbleBodyTuning.TryCreate(0, out deliveryBodyTuning);
                }
                useSourcePreservedSynthesis =
                    hasSourcePreservedSynthesis &&
                    delivery.Disposition ==
                        ControllerFeedbackDeliveryDisposition.Frame &&
                    delivery.Frame == sourcePreservedFrame;
                preservedLeft = sourcePreservedLeft;
                preservedRight = sourcePreservedRight;
                preservedFidelity = sourcePreservedFidelity;
                useImpulseReleaseSynthesis = exactUnresolvedRetry ?
                    unresolvedUsesImpulseRelease :
                    hasImpulseReleasePresentation &&
                        delivery.Disposition ==
                            ControllerFeedbackDeliveryDisposition.Frame &&
                        delivery.Frame == impulseReleaseFrame &&
                        deliveryPolicy == Switch2HdRumbleFeedbackPolicy.
                            SideLocalImpulseDualBandSaturating;
                releaseLeftTrigger = exactUnresolvedRetry ?
                    unresolvedImpulseReleaseLeftTrigger :
                    impulseReleaseLeftTrigger;
                releaseRightTrigger = exactUnresolvedRetry ?
                    unresolvedImpulseReleaseRightTrigger :
                    impulseReleaseRightTrigger;
                releasePresentationRevision = exactUnresolvedRetry ?
                    unresolvedImpulseReleaseRevision :
                    impulseReleasePresentationRevision;

                bool exactPresentationRefresh =
                    !hasUnresolvedDelivery && delivery.Disposition ==
                        ControllerFeedbackDeliveryDisposition.Frame &&
                    lastDelivered == delivery &&
                    (deliveryPolicy != lastDeliveredPolicy ||
                        deliveryImpulseTuning !=
                            lastDeliveredImpulseTuning ||
                        deliveryBodyTuning != lastDeliveredBodyTuning ||
                        deliveryXboxPolicyRevision != lastDeliveredXboxPolicyRevision ||
                        useSourcePreservedSynthesis ||
                        useImpulseReleaseSynthesis &&
                            releasePresentationRevision !=
                                lastDeliveredImpulseReleaseRevision);
                if (!hasUnresolvedDelivery && lastDelivered == delivery &&
                    !exactPresentationRefresh)
                {
                    return true;
                }
                if (!CanAdmitNoLock(delivery, exactPresentationRefresh))
                {
                    return false;
                }
            }

            Switch2HdRumblePhysicalSubmission submission;
            if (delivery.Disposition ==
                ControllerFeedbackDeliveryDisposition.Stop)
            {
                submission = Switch2HdRumblePhysicalSubmission.CreateStop(
                    deviceGeneration, transportGeneration,
                    delivery.DeliveryEpoch);
            }
            else
            {
                if (!ControllerFeedbackClock.TryGetTimestampMicroseconds(
                        out ulong nowMicroseconds))
                {
                    return RecordFailure(
                        Switch2HdRumblePhysicalWriteFailure.
                            InvalidSubmission,
                        uncertainOutcome: false, default, default);
                }
                Switch2HdRumbleFeedbackSynthesis synthesis;
                if (useSourcePreservedSynthesis)
                {
                    ControllerFeedbackFrame frame = delivery.Frame;
                    Switch2HdRumbleGroup tunedLeft =
                        Switch2HdRumbleFeedbackTranslator.
                            ScaleSourcePreservedGroup(preservedLeft,
                                deliveryBodyTuning);
                    Switch2HdRumbleGroup tunedRight =
                        Switch2HdRumbleFeedbackTranslator.
                            ScaleSourcePreservedGroup(preservedRight,
                                deliveryBodyTuning);
                    synthesis = new Switch2HdRumbleFeedbackSynthesis(
                        frame.Source, frame.Command, preservedFidelity,
                        tunedLeft, tunedRight, frame.Sequence,
                        frame.DeviceGeneration, frame.TransportGeneration,
                        frame.OwnershipEpoch, frame.TimestampMicroseconds,
                        frame.TimeToLiveMicroseconds);
                }
                else if (useImpulseReleaseSynthesis)
                {
                    if (!TryCreateImpulseReleaseFrame(delivery.Frame,
                            releaseLeftTrigger, releaseRightTrigger,
                            out ControllerFeedbackFrame releaseFrame) ||
                        !Switch2HdRumbleFeedbackTranslator.TryTranslate(
                            releaseFrame, nowMicroseconds, deliveryPolicy,
                            deliveryImpulseTuning, deliveryBodyTuning,
                            out synthesis))
                    {
                        return RecordFailure(
                            Switch2HdRumblePhysicalWriteFailure.
                                InvalidSubmission,
                            uncertainOutcome: false, default, default);
                    }
                }
                else if (!Switch2HdRumbleFeedbackTranslator.TryTranslate(
                    delivery.Frame, nowMicroseconds, deliveryPolicy,
                    deliveryImpulseTuning, deliveryBodyTuning,
                    out synthesis))
                {
                    return RecordFailure(
                        Switch2HdRumblePhysicalWriteFailure.
                            InvalidSubmission,
                        uncertainOutcome: false, default, default);
                }
                if (!synthesis.IsFreshAt(nowMicroseconds) ||
                    !Switch2HdRumblePhysicalSubmission.TryCreateFrame(
                        synthesis, delivery.DeliveryEpoch, out submission))
                {
                    return RecordFailure(
                        Switch2HdRumblePhysicalWriteFailure.
                            InvalidSubmission,
                        uncertainOutcome: false, default, default);
                }
            }

            Switch2HdRumblePhysicalWriteResult result;
            try
            {
                result = writer.TryWrite(submission);
            }
            catch
            {
                return RecordFailure(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
                    uncertainOutcome: true, delivery, deliveryPolicy,
                    deliveryImpulseTuning, deliveryBodyTuning,
                    useImpulseReleaseSynthesis, releaseLeftTrigger,
                    releaseRightTrigger, releasePresentationRevision, deliveryXboxPolicyRevision);
            }
            if (!result.IsValid)
            {
                return RecordFailure(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
                    uncertainOutcome: true, delivery, deliveryPolicy,
                    deliveryImpulseTuning, deliveryBodyTuning,
                    useImpulseReleaseSynthesis, releaseLeftTrigger,
                    releaseRightTrigger, releasePresentationRevision, deliveryXboxPolicyRevision);
            }
            if (!result.Succeeded)
            {
                return RecordFailure(result.Failure, result.IsUncertain,
                    result.IsUncertain ? delivery : default,
                    result.IsUncertain ? deliveryPolicy : default,
                    result.IsUncertain ? deliveryImpulseTuning : default,
                    result.IsUncertain ? deliveryBodyTuning : default,
                    result.IsUncertain && useImpulseReleaseSynthesis,
                    result.IsUncertain ? releaseLeftTrigger : default,
                    result.IsUncertain ? releaseRightTrigger : default,
                    result.IsUncertain ? releasePresentationRevision :
                        default, result.IsUncertain ? deliveryXboxPolicyRevision : default);
            }

            lock (gate)
            {
                // There is one in-flight writer and retirement uses the same
                // fence, so the admitted delivery cannot have been replaced.
                currentDeliveryEpoch = delivery.DeliveryEpoch;
                currentEpochStopped = delivery.Disposition ==
                    ControllerFeedbackDeliveryDisposition.Stop;
                lastDelivered = delivery;
                lastDeliveredXboxPolicyRevision = deliveryXboxPolicyRevision;
                if (delivery.Disposition ==
                    ControllerFeedbackDeliveryDisposition.Frame)
                {
                    lastDeliveredPolicy = deliveryPolicy;
                    lastDeliveredImpulseTuning = deliveryImpulseTuning;
                    lastDeliveredBodyTuning = deliveryBodyTuning;
                    lastDeliveredImpulseReleaseRevision =
                        useImpulseReleaseSynthesis ?
                            releasePresentationRevision : 0;
                    hasDeliveredFramePolicy = true;
                }
                else
                {
                    lastDeliveredPolicy = default;
                    lastDeliveredImpulseTuning = default;
                    lastDeliveredBodyTuning = default;
                    lastDeliveredImpulseReleaseRevision = 0;
                    hasDeliveredFramePolicy = false;
                }
                unresolvedDelivery = default;
                unresolvedPolicy = default;
                unresolvedImpulseTuning = default;
                unresolvedBodyTuning = default;
                unresolvedImpulseReleaseLeftTrigger = default;
                unresolvedImpulseReleaseRightTrigger = default;
                unresolvedImpulseReleaseRevision = default;
                unresolvedXboxPolicyRevision = 0;
                unresolvedUsesImpulseRelease = false;
                hasUnresolvedDelivery = false;
                if (useSourcePreservedSynthesis ||
                    delivery.Disposition ==
                        ControllerFeedbackDeliveryDisposition.Stop)
                {
                    hasSourcePreservedSynthesis = false;
                    sourcePreservedFrame = default;
                    sourcePreservedLeft = default;
                    sourcePreservedRight = default;
                    sourcePreservedFidelity = default;
                }
                if (delivery.Disposition ==
                    ControllerFeedbackDeliveryDisposition.Stop)
                {
                    hasImpulseReleasePresentation = false;
                    impulseReleaseFrame = default;
                    impulseReleaseLeftTrigger = default;
                    impulseReleaseRightTrigger = default;
                    impulseReleasePresentationRevision = default;
                }
                Volatile.Write(ref uncertain, 0);
                lastFailure = Switch2HdRumblePhysicalWriteFailure.None;
                return true;
            }
        }
        finally
        {
            Volatile.Write(ref writeActive, 0);
        }
    }

    private bool CanAdmitNoLock(in ControllerFeedbackDelivery delivery,
        bool allowExactPresentationRefresh)
    {
        if (hasUnresolvedDelivery)
        {
            if (unresolvedDelivery == delivery)
            {
                return true;
            }
            return CanResolveUncertaintyNoLock(delivery);
        }

        if (currentDeliveryEpoch == 0)
        {
            return true;
        }
        if (delivery.DeliveryEpoch < currentDeliveryEpoch)
        {
            return false;
        }
        if (delivery.DeliveryEpoch > currentDeliveryEpoch)
        {
            return currentEpochStopped;
        }
        if (currentEpochStopped)
        {
            // The exact successful Stop was handled by the idempotence check.
            // A different same-epoch Stop cannot authenticate that owner.
            return false;
        }
        if (delivery.Disposition ==
            ControllerFeedbackDeliveryDisposition.Stop)
        {
            return lastDelivered.Origin == delivery.Origin;
        }

        if (allowExactPresentationRefresh && lastDelivered == delivery)
        {
            return true;
        }

        // A frame update inside one ownership epoch must stay in the same
        // canonical origin/source/ownership lifetime and advance ordering.
        ControllerFeedbackFrame previous = lastDelivered.Frame;
        ControllerFeedbackFrame next = delivery.Frame;
        if (lastDelivered.Disposition !=
                ControllerFeedbackDeliveryDisposition.Frame ||
            lastDelivered.Origin != delivery.Origin ||
            previous.Source != next.Source ||
            previous.OwnershipEpoch != next.OwnershipEpoch ||
            next.Sequence <= previous.Sequence ||
            next.TimestampMicroseconds < previous.TimestampMicroseconds)
        {
            return false;
        }
        return true;
    }

    private bool CanResolveUncertaintyNoLock(
        in ControllerFeedbackDelivery delivery)
    {
        if (unresolvedDelivery.Disposition !=
                ControllerFeedbackDeliveryDisposition.Frame ||
            delivery.DeliveryEpoch != unresolvedDelivery.DeliveryEpoch ||
            delivery.Origin != unresolvedDelivery.Origin)
        {
            return false;
        }
        if (delivery.Disposition ==
            ControllerFeedbackDeliveryDisposition.Stop)
        {
            return true;
        }
        if (delivery.Disposition !=
            ControllerFeedbackDeliveryDisposition.Frame)
        {
            return false;
        }

        ControllerFeedbackFrame previous = unresolvedDelivery.Frame;
        ControllerFeedbackFrame next = delivery.Frame;
        return previous.Source == next.Source &&
            previous.OwnershipEpoch == next.OwnershipEpoch &&
            next.Sequence > previous.Sequence &&
            next.TimestampMicroseconds >= previous.TimestampMicroseconds;
    }

    private bool RecordFailure(Switch2HdRumblePhysicalWriteFailure failure,
        bool uncertainOutcome,
        in ControllerFeedbackDelivery attemptedDelivery,
        Switch2HdRumbleFeedbackPolicy attemptedPolicy,
        Switch2HdRumbleImpulseTuning attemptedImpulseTuning = default,
        Switch2HdRumbleBodyTuning attemptedBodyTuning = default,
        bool attemptedUsesImpulseRelease = false,
        ushort attemptedImpulseReleaseLeftTrigger = default,
        ushort attemptedImpulseReleaseRightTrigger = default,
        ulong attemptedImpulseReleaseRevision = default,
        ulong attemptedXboxPolicyRevision = default)
    {
        lock (gate)
        {
            lastFailure = failure;
            if (uncertainOutcome)
            {
                unresolvedDelivery = attemptedDelivery;
                unresolvedPolicy = attemptedPolicy;
                unresolvedImpulseTuning = attemptedImpulseTuning;
                unresolvedBodyTuning = attemptedBodyTuning;
                unresolvedUsesImpulseRelease =
                    attemptedUsesImpulseRelease;
                unresolvedImpulseReleaseLeftTrigger =
                    attemptedImpulseReleaseLeftTrigger;
                unresolvedImpulseReleaseRightTrigger =
                    attemptedImpulseReleaseRightTrigger;
                unresolvedImpulseReleaseRevision =
                    attemptedImpulseReleaseRevision;
                unresolvedXboxPolicyRevision = attemptedXboxPolicyRevision;
                hasUnresolvedDelivery = true;
                Volatile.Write(ref uncertain, 1);
            }
        }
        return false;
    }

    private static bool TryCreateImpulseReleaseFrame(
        in ControllerFeedbackFrame canonicalFrame, ushort leftTrigger,
        ushort rightTrigger, out ControllerFeedbackFrame releaseFrame)
    {
        ControllerFeedbackCommand command =
            canonicalFrame.BodyLow == 0 && canonicalFrame.BodyHigh == 0 &&
            leftTrigger == 0 && rightTrigger == 0 ?
                ControllerFeedbackCommand.Neutral :
                ControllerFeedbackCommand.Apply;
        return ControllerFeedbackFrame.TryCreate(canonicalFrame.Source,
            command, canonicalFrame.Actuators, canonicalFrame.BodyLow,
            canonicalFrame.BodyHigh, leftTrigger, rightTrigger,
            canonicalFrame.Sequence, canonicalFrame.DeviceGeneration,
            canonicalFrame.TransportGeneration,
            canonicalFrame.OwnershipEpoch,
            canonicalFrame.TimestampMicroseconds,
            canonicalFrame.TimeToLiveMicroseconds, out releaseFrame);
    }

    private static bool TryAuthenticate(ISwitch2HdRumblePhysicalWriter writer,
        ulong deviceGeneration, ulong transportGeneration,
        out bool dependencyThrew)
    {
        try
        {
            dependencyThrew = false;
            return writer.Authenticates(deviceGeneration,
                transportGeneration);
        }
        catch
        {
            dependencyThrew = true;
            return false;
        }
    }
}
