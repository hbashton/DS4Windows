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

internal enum Switch2ProUsbOwnedFeedbackActivationCreateFailure : byte
{
    None = 0,
    MissingDependency,
    InvalidAuthority,
    InvalidOperationBound,
    CompositeAuthenticationRejected,
    OutputAdoptionRejected,
    DependencyThrew,
    QuarantineRequired,
}

/// <summary>
/// Dormant-only construction result. RetainedBundle is deliberately published
/// on every post-admission failure: a one-shot output adoption may already have
/// crossed even when a later managed constructor reports failure.
/// </summary>
internal readonly struct Switch2ProUsbOwnedFeedbackActivationCreateResult
{
    internal Switch2ProUsbOwnedFeedbackActivationCreateResult(
        Switch2ProUsbOwnedFeedbackActivationCreateFailure failure,
        Switch2ProUsbOwnedCompositeLeaseBundle retainedBundle = null)
    {
        Failure = failure;
        RetainedBundle = retainedBundle;
    }

    internal Switch2ProUsbOwnedFeedbackActivationCreateFailure Failure
    {
        get;
    }

    internal Switch2ProUsbOwnedCompositeLeaseBundle RetainedBundle { get; }

    internal bool RequiresRetention => RetainedBundle != null;

    internal bool Succeeded => Failure ==
        Switch2ProUsbOwnedFeedbackActivationCreateFailure.None;
}

/// <summary>
/// One dormant, manually driven canonical feedback composition. Construction
/// atomically adopts a never-attempted native output lane before this object can
/// issue its one-shot Dormant proof. Prepare performs no output. Commit is the
/// sole point which admits lane creation and PumpOnce.
///
/// Terminal neutralization has one serialized order: seal producer admission;
/// drain the bridge's exact retained operation; deliver the canonical runtime's
/// exact Stop through the existing pump, sink, physical writer, and bridge;
/// retire the pump and sink; then authenticate a fresh no-retained bridge
/// revision. No dependency is invoked while this object's private gate is held.
/// This type creates no timer, cadence, callback, worker, registration, or
/// hardware path.
/// </summary>
internal sealed class Switch2ProUsbOwnedFeedbackActivationLifetime :
    ISwitch2ProUsbOwnedFeedbackActivationLifetime,
    ISwitch2VirtualFeedbackSessionOwner
{
    private const int MaximumTimeoutMilliseconds =
        Switch2ProUsbInputTransportOwner.MaximumDisposeTimeoutMilliseconds;

    private readonly object gate = new();
    private readonly object dormantProofFence = new();
    private readonly object credentialFence = new();
    private readonly object terminalProofFence = new();
    private readonly Switch2ProUsbOwnedCompositeLeaseBundle bundle;
    private readonly Switch2ProUsbOwnedCompositeAuthority authority;
    private readonly Switch2PhysicalInputLifetime lifetime;
    private readonly ControllerFeedbackStateLanePump pump;
    private readonly Switch2ProUsbOwnedHdRumbleTransportBridge bridge;
    private readonly Switch2HdRumbleDeliverySink sink;
    private readonly ISwitch2ProUsbStartupCommandLease playerLedCommandLease;
    private readonly object playerLedCommandFence = new();

    private Switch2ProUsbOwnedFeedbackActivationState state =
        Switch2ProUsbOwnedFeedbackActivationState.Dormant;
    private ulong stateRevision = 1;
    private readonly ulong activationSequence = 1;
    private bool dormantProofTaken;
    private bool dormantProofConsumed;
    private bool prepareCredentialConsumed;
    private Switch2VirtualFeedbackSession activeVirtualFeedbackSession;
    private ulong nextVirtualFeedbackOwnershipEpoch;
    private ulong nextPlayerLedCommandSequence;
    private int operationActive;
    private string wirePublicationStage = "NotEntered";
    private ControllerFeedbackPumpDisposition wirePublicationDisposition;

    public string DescribeWirePublication()
    {
        lock (gate)
            return $"stage={wirePublicationStage}, ownerState={state}, " +
                $"pump={wirePublicationDisposition}, physical={sink.LastFailure}";
    }

    private Switch2ProUsbOwnedFeedbackActivationLifetime(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2PhysicalInputLifetime lifetime,
        ControllerFeedbackStateLanePump pump,
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge,
        Switch2HdRumbleDeliverySink sink,
        ISwitch2ProUsbStartupCommandLease playerLedCommandLease)
    {
        this.bundle = bundle;
        this.authority = authority;
        this.lifetime = lifetime;
        this.pump = pump;
        this.bridge = bridge;
        this.sink = sink;
        this.playerLedCommandLease = playerLedCommandLease;
    }

    internal static bool TryCreate(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        int operationWaitMilliseconds,
        out Switch2ProUsbOwnedFeedbackActivationLifetime feedback,
        out Switch2ProUsbOwnedFeedbackActivationCreateResult result,
        Switch2HdRumbleFeedbackPolicy policy =
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility)
    {
        feedback = null;
        if (bundle == null)
        {
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                    MissingDependency);
            return false;
        }
        Switch2PhysicalInputLifetime lifetime = bundle.Lifetime;
        if (!authority.IsValid || !lifetime.IsValid ||
            authority.DeviceGeneration !=
                lifetime.SessionDescriptor.DeviceGeneration ||
            authority.TransportGeneration !=
                lifetime.SessionDescriptor.TransportGeneration)
        {
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                    InvalidAuthority, bundle);
            return false;
        }
        if (operationWaitMilliseconds <= 0 ||
            operationWaitMilliseconds > MaximumTimeoutMilliseconds ||
            policy is not (Switch2HdRumbleFeedbackPolicy.
                SdlBodyOnlyCompatibility or
                Switch2HdRumbleFeedbackPolicy.
                    SideLocalImpulseDualBandSaturating))
        {
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                    InvalidOperationBound, bundle);
            return false;
        }

        ISwitch2ProUsbOwnedCompositeLease composite;
        try
        {
            if (!bundle.TryGetBoundedOutputLease(authority, out composite) ||
                composite == null ||
                composite.MaximumOutputOperationMilliseconds <
                    operationWaitMilliseconds ||
                !composite.AuthenticatesComposite(
                    Switch2ControllerModel.ProController2,
                    authority.DeviceGeneration,
                    authority.TransportGeneration))
            {
                result = new(
                    Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                        CompositeAuthenticationRejected, bundle);
                return false;
            }
        }
        catch
        {
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                    DependencyThrew, bundle);
            return false;
        }

        if (!ControllerFeedbackStateLanePump.TryCreate(
                authority.DeviceGeneration, authority.TransportGeneration,
                out ControllerFeedbackStateLanePump pump))
        {
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                    DependencyThrew, bundle);
            return false;
        }

        // This fence is created here and never accepted from a caller. The
        // returned narrow capability is not exposed by this factory/lifetime.
        object outputOwnerFence = new();
        ISwitch2ProUsbOwnedFeedbackOutputLease output = null;
        bool adopted;
        try
        {
            adopted = composite.TryAdoptDormantFeedbackOutput(
                outputOwnerFence, out output);
        }
        catch
        {
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                    QuarantineRequired, bundle);
            return false;
        }
        if (!adopted || output == null)
        {
            result = new(adopted != (output != null) ?
                    Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                        QuarantineRequired :
                    Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                        OutputAdoptionRejected,
                bundle);
            return false;
        }

        try
        {
            var bridge = new Switch2ProUsbOwnedHdRumbleTransportBridge(output,
                authority.DeviceGeneration, authority.TransportGeneration,
                operationWaitMilliseconds);
            var writer = new Switch2ProUsbHdRumblePhysicalWriter(bridge,
                authority.DeviceGeneration, authority.TransportGeneration);
            var sink = new Switch2HdRumbleDeliverySink(writer,
                authority.DeviceGeneration, authority.TransportGeneration,
                policy);
            feedback = new Switch2ProUsbOwnedFeedbackActivationLifetime(bundle,
                authority, lifetime, pump, bridge, sink, composite);
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.None);
            return true;
        }
        catch
        {
            // Adoption is already one-shot. The bundle remains the exact
            // transitive owner; callers must keep it for terminal attention.
            feedback = null;
            result = new(
                Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                    QuarantineRequired, bundle);
            return false;
        }
    }

    public Switch2ProUsbOwnedFeedbackActivationState ActivationState
    {
        get { lock (gate) { return state; } }
    }

    internal int OperationWaitMilliseconds =>
        bridge.OperationWaitMilliseconds;

    public bool Authenticates(
        in Switch2ProUsbOwnedCompositeAuthority candidate) =>
        candidate.IsValid && candidate.Equals(authority) && lifetime.IsValid &&
        candidate.DeviceGeneration ==
            lifetime.SessionDescriptor.DeviceGeneration &&
        candidate.TransportGeneration ==
            lifetime.SessionDescriptor.TransportGeneration;

    /// <summary>
    /// Creates the canonical NativeGame ingress before activation so profile
    /// staging can establish feedback readiness before the USB input worker is
    /// released. Publications remain fenced until the owned-composite
    /// participant commits this feedback lifetime.
    /// </summary>
    internal bool TryCreateVirtualFeedbackSession(
        ControllerFeedbackSource source,
        out Switch2VirtualFeedbackSession session)
    {
        session = null;
        lock (gate)
        {
            if (state is Switch2ProUsbOwnedFeedbackActivationState.Aborted or
                    Switch2ProUsbOwnedFeedbackActivationState.
                        NeutralAndQuiescent or
                    Switch2ProUsbOwnedFeedbackActivationState.DisconnectedAndQuiescent or
                    Switch2ProUsbOwnedFeedbackActivationState.Quarantined or
                    Switch2ProUsbOwnedFeedbackActivationState.
                        SequenceExhausted ||
                activeVirtualFeedbackSession != null ||
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
            activeVirtualFeedbackSession = session;
            return true;
        }
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
            if (state !=
                    Switch2ProUsbOwnedFeedbackActivationState.Committed ||
                !ReferenceEquals(activeVirtualFeedbackSession, session))
            {
                return false;
            }
        }

        // Rejected broker frames do not own the current effect's tuning.
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
        // Expired ordering watermarks and terminal Stop need no live Frame
        // to re-render. Their canonical Stop/no-op must still be pumped;
        // writer/claim contention remains a failure, not permission to ACK.
        if (presentationRefreshRequired &&
            !pump.TryRefreshCurrentPresentation(nowMicroseconds, allowNoFrame: true))
        {
            return false;
        }
        wirePublicationStage = "PhysicalPump";
        ControllerFeedbackPumpDisposition result = TryPumpOnce(
            nowMicroseconds, out _);
        wirePublicationDisposition = result;
        return result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None or
            ControllerFeedbackPumpDisposition.RetryPending;
    }

    public bool TryRefreshXboxOutputPolicy(Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame frame, Switch2XboxFeedbackPolicy policy)
    {
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0) return false;
        try
        {
            lock (gate)
            {
                if (state != Switch2ProUsbOwnedFeedbackActivationState.Committed ||
                    !ReferenceEquals(activeVirtualFeedbackSession, session)) return false;
            }
            bool refreshed = Switch2XboxFeedbackPolicy.TryRefresh(pump, sink, frame,
                policy, CurrentMicroseconds());
            if (bridge.State == Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined)
            {
                LatchQuarantine();
                return false;
            }
            return refreshed;
        }
        catch { LatchQuarantine(); return false; }
        finally { Volatile.Write(ref operationActive, 0); }
    }

    public bool TryPublishAndPump(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress,
        in ControllerFeedbackActuatorState feedbackState,
        Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ulong expiresAtMicroseconds = 0)
    {
        lock (gate)
        {
            if (state !=
                    Switch2ProUsbOwnedFeedbackActivationState.Committed ||
                !ReferenceEquals(activeVirtualFeedbackSession, session))
            {
                return false;
            }
        }

        ulong nowMicroseconds = CurrentMicroseconds();
        ulong lifetime = Switch2VirtualFeedbackSession.RemainingLifetime(
            nowMicroseconds, expiresAtMicroseconds);
        ControllerFeedbackActuatorState effectiveState = lifetime == 0 ?
            default : feedbackState;
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
        ControllerFeedbackPumpDisposition result = TryPumpOnce(
            nowMicroseconds, out _);
        return result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None or
            ControllerFeedbackPumpDisposition.RetryPending;
    }

    public bool TryPublishSourcePreservedAndPump(
        Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress,
        in ControllerFeedbackActuatorState feedbackState,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        in Switch2HdRumbleBodyTuning bodyTuning,
        ulong expiresAtMicroseconds = 0)
    {
        lock (gate)
        {
            if (state !=
                    Switch2ProUsbOwnedFeedbackActivationState.Committed ||
                !ReferenceEquals(activeVirtualFeedbackSession, session))
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
                Switch2HdRumbleImpulseTuning.Default, bodyTuning, out _) ||
            !ingress.TryPublish(feedbackState, nowMicroseconds, lifetime,
                out ControllerFeedbackFrame frame))
        {
            return false;
        }
        if (!sink.TryStageSourcePreservedSynthesis(frame, fidelity, left,
                right))
        {
            _ = ingress.TryPublishTerminalStop(nowMicroseconds);
            _ = TryPumpOnce(nowMicroseconds, out _);
            return false;
        }
        // Rich PCM/native oscillator values are intentionally not encoded in
        // the canonical marker. Force one new presentation revision after the
        // exact groups are staged, while leaving ordinary canonical lease
        // renewals deduplicated in TryPublishAndPump.
        if (!pump.TryRefreshCurrentPresentation(nowMicroseconds))
        {
            _ = ingress.TryPublishTerminalStop(nowMicroseconds);
            _ = TryPumpOnce(nowMicroseconds, out _);
            return false;
        }

        ControllerFeedbackPumpDisposition result = TryPumpOnce(
            nowMicroseconds, out _);
        return result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None or
            ControllerFeedbackPumpDisposition.RetryPending;
    }

    public bool TryStageImpulseReleasePresentation(
        Switch2VirtualFeedbackSession session,
        in ControllerFeedbackFrame canonicalFrame, ushort leftTrigger,
        ushort rightTrigger, ulong presentationRevision)
    {
        lock (gate)
        {
            return state ==
                    Switch2ProUsbOwnedFeedbackActivationState.Committed &&
                ReferenceEquals(activeVirtualFeedbackSession, session) &&
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
            if (state !=
                    Switch2ProUsbOwnedFeedbackActivationState.Committed ||
                !ReferenceEquals(activeVirtualFeedbackSession, session))
            {
                return false;
            }
        }
        ulong nowMicroseconds = CurrentMicroseconds();
        if (!pump.TryRefreshCurrentPresentation(nowMicroseconds))
        {
            return false;
        }
        ControllerFeedbackPumpDisposition result = TryPumpOnce(
            nowMicroseconds, out _);
        bool pumped = result is ControllerFeedbackPumpDisposition.Delivered or
            ControllerFeedbackPumpDisposition.None or
            ControllerFeedbackPumpDisposition.RetryPending;
        return pumped &&
            sink.HasPresentedImpulseReleaseRevision(canonicalFrame,
                presentationRevision);
    }

    public bool TryClearImpulseReleasePresentation(
        Switch2VirtualFeedbackSession session)
    {
        lock (gate)
        {
            return state ==
                    Switch2ProUsbOwnedFeedbackActivationState.Committed &&
                ReferenceEquals(activeVirtualFeedbackSession, session) &&
                sink.TryClearImpulseReleasePresentation();
        }
    }

    public bool TryRequestPlayerLedMask(
        Switch2VirtualFeedbackSession session, byte playerLedMask)
    {
        if (!TryMapUsbPlayerLedCommand(playerLedMask,
                out Switch2PlayerLedCommand command) ||
            Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            ulong sequence;
            lock (gate)
            {
                if (state !=
                        Switch2ProUsbOwnedFeedbackActivationState.Committed ||
                    !ReferenceEquals(activeVirtualFeedbackSession, session) ||
                    nextPlayerLedCommandSequence == ulong.MaxValue)
                {
                    return false;
                }
                sequence = ++nextPlayerLedCommandSequence;
            }

            Span<byte> request = stackalloc byte[
                Switch2UsbCommandCodec.RequestLength];
            if (!Switch2UsbCommandCodec.TryWritePlayerLedRequest(command,
                    request, out _))
            {
                return false;
            }

            var claim = new Switch2ProUsbStartupCommandClaim(
                playerLedCommandFence, playerLedCommandLease, lifetime,
                Switch2ProUsbStartupStep.SetPlayerLed, sequence);
            Switch2ProUsbStartupCommandCompletion completion;
            try
            {
                completion = playerLedCommandLease.Execute(claim, request,
                    bridge.OperationWaitMilliseconds);
            }
            catch
            {
                LatchQuarantine();
                return false;
            }

            bool exact = completion.Outcome ==
                    Switch2ProUsbStartupCommandOutcome.
                        ExactResponseCompleted &&
                completion.Claim.Authenticates(playerLedCommandFence,
                    playerLedCommandLease, lifetime,
                    Switch2ProUsbStartupStep.SetPlayerLed, sequence) &&
                completion.ReportedStep ==
                    Switch2ProUsbStartupStep.SetPlayerLed &&
                completion.ResponseProof ==
                    Switch2ProUsbStartupResponseProofKind.
                        PlayerLedResponseValidatedByCodec;
            if (exact)
            {
                return true;
            }
            if (completion.Outcome !=
                Switch2ProUsbStartupCommandOutcome.ProvenNotConsumed)
            {
                LatchQuarantine();
            }
            return false;
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    private static bool TryMapUsbPlayerLedCommand(byte playerLedMask,
        out Switch2PlayerLedCommand command)
    {
        command = playerLedMask switch
        {
            0x00 => Switch2PlayerLedCommand.AllOff,
            0x01 => Switch2PlayerLedCommand.Player1Only,
            0x03 => Switch2PlayerLedCommand.Player2Only,
            0x07 => Switch2PlayerLedCommand.Player3Only,
            0x0F => Switch2PlayerLedCommand.Player4Only,
            _ => default,
        };
        return playerLedMask is 0x00 or 0x01 or 0x03 or 0x07 or 0x0F;
    }

    public bool TryRetireSession(Switch2VirtualFeedbackSession session,
        ControllerFeedbackIngress ingress)
    {
        Switch2ProUsbOwnedFeedbackActivationState observed;
        lock (gate)
        {
            if (!ReferenceEquals(activeVirtualFeedbackSession, session))
            {
                return false;
            }
            observed = state;
        }

        ulong nowMicroseconds = CurrentMicroseconds();
        if (observed == Switch2ProUsbOwnedFeedbackActivationState.Committed)
        {
            if (!ingress.TryPublishTerminalStop(nowMicroseconds))
            {
                return false;
            }
            bool neutral = false;
            for (int attempt = 0; attempt < 3 && !neutral; attempt++)
            {
                ControllerFeedbackPumpDisposition result = TryPumpOnce(
                    nowMicroseconds, out _);
                neutral = result is ControllerFeedbackPumpDisposition.
                        Delivered or
                    ControllerFeedbackPumpDisposition.None;
            }
            if (!neutral)
            {
                return false;
            }
        }

        if (!ingress.TryRetire())
        {
            return false;
        }
        lock (gate)
        {
            if (!ReferenceEquals(activeVirtualFeedbackSession, session))
            {
                return false;
            }
            activeVirtualFeedbackSession = null;
            return true;
        }
    }

    public bool AuthenticatesQuiescenceResult(
        in Switch2ProUsbOwnedCompositeAuthority candidate,
        in Switch2ProUsbOwnedFeedbackQuiescenceResult result)
    {
        if (!Authenticates(candidate))
        {
            return false;
        }
        lock (gate)
        {
            bool terminalMatches = result.Outcome ==
                    Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                        ExactNeutralAndQuiescent &&
                (state is Switch2ProUsbOwnedFeedbackActivationState.
                        NeutralAndQuiescent or
                    Switch2ProUsbOwnedFeedbackActivationState.Aborted) ||
                result.Outcome == Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                    ExactDisconnectedAndQuiescent &&
                state == Switch2ProUsbOwnedFeedbackActivationState.DisconnectedAndQuiescent;
            return terminalMatches &&
                result.AuthenticatesExact(this, terminalProofFence, authority,
                    stateRevision);
        }
    }

    public bool TryTakeDormantQuiescenceProof(
        in Switch2ProUsbOwnedCompositeAuthority candidate,
        out Switch2ProUsbOwnedFeedbackDormantQuiescenceProof proof)
    {
        proof = default;
        if (!Authenticates(candidate) ||
            Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            lock (gate)
            {
                if (state != Switch2ProUsbOwnedFeedbackActivationState.
                        Dormant ||
                    dormantProofTaken || dormantProofConsumed)
                {
                    return false;
                }

                dormantProofTaken = true;
                proof = new Switch2ProUsbOwnedFeedbackDormantQuiescenceProof(
                    this, dormantProofFence, authority, lifetime,
                    activationSequence);
                return proof.IsValid;
            }
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    public Switch2ProUsbOwnedFeedbackActivationResult TryPrepareActivation(
        in Switch2ProUsbOwnedCompositeAuthority candidate,
        in Switch2ProUsbOwnedFeedbackDormantQuiescenceProof dormantProof,
        int timeoutMilliseconds)
    {
        if (!Authenticates(candidate) || !IsValidTimeout(timeoutMilliseconds) ||
            Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                authority);
        }

        try
        {
            lock (gate)
            {
                if (state != Switch2ProUsbOwnedFeedbackActivationState.Dormant ||
                    !dormantProofTaken || dormantProofConsumed ||
                    !dormantProof.Authenticates(this, dormantProofFence,
                        authority, lifetime, activationSequence))
                {
                    return Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                        authority);
                }

                dormantProofConsumed = true;
                state = Switch2ProUsbOwnedFeedbackActivationState.
                    PrepareInProgress;
                if (!TryAdvanceRevisionNoLock())
                {
                    return ActivationUncertainNoLock(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Prepare);
                }

                var credential =
                    new Switch2ProUsbOwnedFeedbackPrepareCredential(this,
                        credentialFence, authority, lifetime,
                        activationSequence);
                state = Switch2ProUsbOwnedFeedbackActivationState.Prepared;
                if (!TryAdvanceRevisionNoLock())
                {
                    return ActivationUncertainNoLock(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Prepare);
                }
                return Switch2ProUsbOwnedFeedbackActivationResult.Prepared(
                    authority, credential);
            }
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    public Switch2ProUsbOwnedFeedbackActivationResult TryCommitPrepared(
        in Switch2ProUsbOwnedFeedbackPrepareCredential credential,
        int timeoutMilliseconds)
    {
        if (!IsValidTimeout(timeoutMilliseconds) ||
            Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                authority);
        }

        try
        {
            lock (gate)
            {
                if (state != Switch2ProUsbOwnedFeedbackActivationState.Prepared ||
                    prepareCredentialConsumed ||
                    !credential.Authenticates(this, credentialFence, authority,
                        lifetime, activationSequence))
                {
                    return Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                        authority);
                }

                prepareCredentialConsumed = true;
                state = Switch2ProUsbOwnedFeedbackActivationState.
                    CommitInProgress;
                if (!TryAdvanceRevisionNoLock())
                {
                    return ActivationUncertainNoLock(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Commit);
                }
                state = Switch2ProUsbOwnedFeedbackActivationState.Committed;
                if (!TryAdvanceRevisionNoLock())
                {
                    return ActivationUncertainNoLock(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Commit);
                }
                return Switch2ProUsbOwnedFeedbackActivationResult.Succeeded(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                    authority);
            }
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    public Switch2ProUsbOwnedFeedbackActivationResult TryAbortPrepared(
        in Switch2ProUsbOwnedFeedbackPrepareCredential credential,
        int timeoutMilliseconds)
    {
        if (!IsValidTimeout(timeoutMilliseconds) ||
            Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                Switch2ProUsbOwnedFeedbackActivationOperation.Abort,
                authority);
        }

        try
        {
            lock (gate)
            {
                if (state != Switch2ProUsbOwnedFeedbackActivationState.Prepared ||
                    prepareCredentialConsumed ||
                    !credential.Authenticates(this, credentialFence, authority,
                        lifetime, activationSequence))
                {
                    return Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Abort,
                        authority);
                }
                prepareCredentialConsumed = true;
                state = Switch2ProUsbOwnedFeedbackActivationState.
                    AbortInProgress;
                if (!TryAdvanceRevisionNoLock())
                {
                    return ActivationUncertainNoLock(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Abort);
                }
            }

            // No lane or PumpOnce was admitted before Commit. These calls
            // therefore prove the structurally sealed/no-write branch and do
            // not synthesize a physical Stop.
            if (!pump.SealPublications() ||
                !TryAuthenticateNoRetainedBridge(out _) ||
                !pump.TryStopAndRetire(0, sink, maxAttempts: 0) ||
                !pump.IsRetired || !sink.TryRetire() || !sink.IsRetired)
            {
                return QuarantineActivation(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Abort);
            }

            lock (gate)
            {
                state = Switch2ProUsbOwnedFeedbackActivationState.Aborted;
                if (!TryAdvanceRevisionNoLock())
                {
                    return ActivationUncertainNoLock(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Abort);
                }
                return Switch2ProUsbOwnedFeedbackActivationResult.Succeeded(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Abort,
                    authority);
            }
        }
        catch
        {
            return QuarantineActivation(
                Switch2ProUsbOwnedFeedbackActivationOperation.Abort);
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    internal bool TryCreateLane(ControllerFeedbackPublicationOrigin origin,
        ControllerFeedbackSource source, ulong ownershipEpoch,
        ulong timeToLiveMicroseconds, ulong renewalIntervalMicroseconds,
        out ControllerFeedbackStateLanePump.Lane lane)
    {
        lane = null;
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return false;
        }
        try
        {
            lock (gate)
            {
                if (state !=
                    Switch2ProUsbOwnedFeedbackActivationState.Committed)
                {
                    return false;
                }
            }

            bool created = pump.TryCreateLane(origin, source, ownershipEpoch,
                timeToLiveMicroseconds, renewalIntervalMicroseconds,
                out lane);
            if (!created)
            {
                lane = null;
            }
            return created;
        }
        catch
        {
            LatchQuarantine();
            lane = null;
            return false;
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    internal ControllerFeedbackPumpDisposition TryPumpOnce(
        ulong nowMicroseconds, out ControllerFeedbackDelivery delivery)
    {
        delivery = default;
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return ControllerFeedbackPumpDisposition.Busy;
        }
        try
        {
            lock (gate)
            {
                if (state !=
                    Switch2ProUsbOwnedFeedbackActivationState.Committed)
                {
                    return ControllerFeedbackPumpDisposition.None;
                }
            }

            ControllerFeedbackPumpDisposition disposition = pump.PumpOnce(
                nowMicroseconds, sink, out delivery);
            if (disposition < ControllerFeedbackPumpDisposition.None ||
                disposition > ControllerFeedbackPumpDisposition.Retired ||
                disposition == ControllerFeedbackPumpDisposition.Retired ||
                bridge.State ==
                    Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined)
            {
                LatchQuarantine();
            }
            return disposition;
        }
        catch
        {
            LatchQuarantine();
            delivery = default;
            return ControllerFeedbackPumpDisposition.RetryPending;
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    /// <summary>
    /// USB counterpart of the authenticated native ProfileEffect seam. It
    /// shares the activation operation fence and never acquires a second
    /// output handle or bypasses the canonical feedback pump.
    /// </summary>
    internal bool TryPublishNativeProfileEffectAndPump(
        ControllerFeedbackStateLanePump.Lane lane,
        in ControllerFeedbackActuatorState feedbackState,
        in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right) =>
        TryPublishNativeLocalEffectAndPump(lane, feedbackState, left, right,
            ControllerFeedbackPublicationOrigin.ProfileEffect,
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2ProfileEffect);

    internal bool TryPublishNativePreviewAndPump(
        ControllerFeedbackStateLanePump.Lane lane,
        in ControllerFeedbackActuatorState feedbackState,
        in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right) =>
        TryPublishNativeLocalEffectAndPump(lane, feedbackState, left, right,
            ControllerFeedbackPublicationOrigin.TestPreview,
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2TestPreview);

    private bool TryPublishNativeLocalEffectAndPump(
        ControllerFeedbackStateLanePump.Lane lane,
        in ControllerFeedbackActuatorState feedbackState,
        in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right,
        ControllerFeedbackPublicationOrigin origin,
        Switch2HdRumbleFeedbackFidelity fidelity)
    {
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return false;
        }
        try
        {
            lock (gate)
            {
                if (state !=
                    Switch2ProUsbOwnedFeedbackActivationState.Committed)
                {
                    return false;
                }
            }
            if (!pump.AuthenticatesLane(lane,
                    origin,
                    ControllerFeedbackSource.Xbox360VirtualDevice) ||
                !ControllerFeedbackClock.TryGetTimestampMicroseconds(
                    out ulong nowMicroseconds) ||
                !lane.TryPublish(feedbackState, nowMicroseconds,
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

            ControllerFeedbackPumpDisposition disposition = pump.PumpOnce(
                nowMicroseconds, sink, out _);
            return disposition is ControllerFeedbackPumpDisposition.
                    Delivered or
                ControllerFeedbackPumpDisposition.None or
                ControllerFeedbackPumpDisposition.RetryPending;
        }
        catch
        {
            LatchQuarantine();
            return false;
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    public Switch2ProUsbOwnedFeedbackQuiescenceResult
        TryNeutralizeAndQuiesce(
            in Switch2ProUsbOwnedCompositeAuthority candidate,
            int timeoutMilliseconds)
    {
        long deadline = StartDeadline(timeoutMilliseconds);
        if (!Authenticates(candidate) ||
            !IsValidTimeout(timeoutMilliseconds))
        {
            return QuiescenceResult(
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete);
        }
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return QuiescenceResult(
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete);
        }

        try
        {
            lock (gate)
            {
                if (state == Switch2ProUsbOwnedFeedbackActivationState.DisconnectedAndQuiescent)
                {
                    return QuiescenceResultNoLock(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                        ExactDisconnectedAndQuiescent);
                }
                if (state ==
                    Switch2ProUsbOwnedFeedbackActivationState.Quarantined ||
                    state ==
                    Switch2ProUsbOwnedFeedbackActivationState.SequenceExhausted)
                {
                    return QuiescenceResultNoLock(
                        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                            OutcomeUncertain);
                }
                if (state is
                        Switch2ProUsbOwnedFeedbackActivationState.
                            NeutralAndQuiescent or
                        Switch2ProUsbOwnedFeedbackActivationState.Aborted)
                {
                    return QuiescenceResultNoLock(
                        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                            ExactNeutralAndQuiescent);
                }
                if (state !=
                        Switch2ProUsbOwnedFeedbackActivationState.Committed &&
                    state != Switch2ProUsbOwnedFeedbackActivationState.
                        NeutralizeInProgress)
                {
                    return QuiescenceResultNoLock(
                        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                            ProvenIncomplete);
                }
                if (state ==
                    Switch2ProUsbOwnedFeedbackActivationState.Committed)
                {
                    state = Switch2ProUsbOwnedFeedbackActivationState.
                        NeutralizeInProgress;
                    prepareCredentialConsumed = true;
                    dormantProofConsumed = true;
                    if (!TryAdvanceRevisionNoLock())
                    {
                        return QuiescenceResultNoLock(
                            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                                OutcomeUncertain);
                    }
                }
            }

            if (!pump.SealPublications())
            {
                return QuarantineQuiescence();
            }

            Switch2ProUsbOwnedHdRumbleBridgeState bridgeState = bridge.State;
            if (bridgeState ==
                Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined)
            {
                return QuarantineQuiescence();
            }
            if (bridgeState ==
                    Switch2ProUsbOwnedHdRumbleBridgeState.RetainedOperation &&
                RemainingMilliseconds(deadline, timeoutMilliseconds) <
                    bridge.OperationWaitMilliseconds)
            {
                return QuiescenceResult(
                    Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                        ProvenIncomplete);
            }
            if (!TryAuthenticateNoRetainedBridge(out bool retainedForRetry))
            {
                return retainedForRetry ?
                    QuiescenceResult(
                        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                            ProvenIncomplete) :
                    QuarantineQuiescence();
            }

            if (TryQuiesceDisconnectedOutput(out var disconnectedResult))
            {
                return disconnectedResult;
            }

            // The canonical Stop can start one bridge operation using the
            // configured fixed wait. Never enter it when the remaining managed
            // budget cannot cover that wait.
            if (RemainingMilliseconds(deadline, timeoutMilliseconds) <
                bridge.OperationWaitMilliseconds)
            {
                return QuiescenceResult(
                    Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                        ProvenIncomplete);
            }

            bool pumpRetired = pump.TryTerminalNeutralAndRetire(0, sink,
                maxAttempts: 1);
            if (bridge.State ==
                Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined)
            {
                return QuarantineQuiescence();
            }
            if (!pumpRetired)
            {
                // The attempted write itself may be the first definite native
                // removal observation. It still must leave no retained I/O.
                if (TryQuiesceDisconnectedOutput(out disconnectedResult))
                {
                    return disconnectedResult;
                }
                // A successful Stop followed by a failed pump retirement is a
                // contradiction; retrying would mint a second terminal epoch.
                return sink.HasExactTerminalStop ? QuarantineQuiescence() :
                    QuiescenceResult(
                        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                            ProvenIncomplete);
            }
            if (!pump.IsRetired || !sink.HasExactTerminalStop)
            {
                return QuarantineQuiescence();
            }
            if (!sink.TryRetire() || !sink.IsRetired)
            {
                return QuarantineQuiescence();
            }

            // Final proof is deliberately after pump then sink retirement and
            // must be a fresh current no-retained bridge revision.
            if (!TryAuthenticateNoRetainedBridge(out retainedForRetry) ||
                retainedForRetry)
            {
                return QuarantineQuiescence();
            }

            lock (gate)
            {
                state = Switch2ProUsbOwnedFeedbackActivationState.
                    NeutralAndQuiescent;
                if (!TryAdvanceRevisionNoLock())
                {
                    return QuiescenceResultNoLock(
                        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                            OutcomeUncertain);
                }
                return QuiescenceResultNoLock(
                    Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                        ExactNeutralAndQuiescent);
            }
        }
        catch
        {
            return QuarantineQuiescence();
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    private bool TryQuiesceDisconnectedOutput(
        out Switch2ProUsbOwnedFeedbackQuiescenceResult result)
    {
        result = default;
        if (!bridge.TrySealDisconnectedOutput())
        {
            return false;
        }
        if (!pump.TryRetireDisconnectedTarget() || !sink.TryRetireDisconnectedTarget())
        {
            result = QuiescenceResult(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete);
            return true;
        }
        if (!TryAuthenticateNoRetainedBridge(out bool retainedForRetry) || retainedForRetry)
        {
            result = QuarantineQuiescence();
            return true;
        }
        lock (gate)
        {
            state = Switch2ProUsbOwnedFeedbackActivationState.DisconnectedAndQuiescent;
            result = TryAdvanceRevisionNoLock() ?
                QuiescenceResultNoLock(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent) :
                QuiescenceResultNoLock(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.OutcomeUncertain);
        }
        return true;
    }

    private bool TryAuthenticateNoRetainedBridge(out bool retainedForRetry)
    {
        retainedForRetry = false;
        Switch2ProUsbOwnedHdRumbleDrainResult drain =
            bridge.TryRetireRetainedOperation();
        if (!drain.HasValidInvariants() || !bridge.Authenticates(drain))
        {
            return false;
        }
        switch (drain.Outcome)
        {
            case Switch2ProUsbOwnedHdRumbleDrainOutcome.NoRetainedOperation:
            case Switch2ProUsbOwnedHdRumbleDrainOutcome.
                    ExactOperationQuiescent:
                return bridge.State ==
                    Switch2ProUsbOwnedHdRumbleBridgeState.NoRetainedOperation;
            case Switch2ProUsbOwnedHdRumbleDrainOutcome.RetainedForRetry:
                retainedForRetry = true;
                return false;
            case Switch2ProUsbOwnedHdRumbleDrainOutcome.Busy:
            case Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined:
            default:
                return false;
        }
    }

    private Switch2ProUsbOwnedFeedbackActivationResult QuarantineActivation(
        Switch2ProUsbOwnedFeedbackActivationOperation operation)
    {
        LatchQuarantine();
        return Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(operation,
            authority);
    }

    private Switch2ProUsbOwnedFeedbackActivationResult
        ActivationUncertainNoLock(
            Switch2ProUsbOwnedFeedbackActivationOperation operation)
    {
        state = Switch2ProUsbOwnedFeedbackActivationState.SequenceExhausted;
        return Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(operation,
            authority);
    }

    private Switch2ProUsbOwnedFeedbackQuiescenceResult QuarantineQuiescence()
    {
        LatchQuarantine();
        return QuiescenceResult(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.OutcomeUncertain);
    }

    private void LatchQuarantine()
    {
        lock (gate)
        {
            if (state is Switch2ProUsbOwnedFeedbackActivationState.
                    NeutralAndQuiescent or
                Switch2ProUsbOwnedFeedbackActivationState.DisconnectedAndQuiescent or
                Switch2ProUsbOwnedFeedbackActivationState.Aborted)
            {
                return;
            }
            state = Switch2ProUsbOwnedFeedbackActivationState.Quarantined;
            TryAdvanceRevisionNoLock();
        }
    }

    private Switch2ProUsbOwnedFeedbackQuiescenceResult QuiescenceResult(
        Switch2ProUsbOwnedFeedbackQuiescenceOutcome outcome)
    {
        lock (gate)
        {
            return QuiescenceResultNoLock(outcome);
        }
    }

    private Switch2ProUsbOwnedFeedbackQuiescenceResult QuiescenceResultNoLock(
        Switch2ProUsbOwnedFeedbackQuiescenceOutcome outcome) =>
        Switch2ProUsbOwnedFeedbackQuiescenceResult.Exact(outcome, this,
            terminalProofFence, authority.DeviceGeneration,
            authority.TransportGeneration, stateRevision);

    private bool TryAdvanceRevisionNoLock()
    {
        if (stateRevision == ulong.MaxValue)
        {
            state = Switch2ProUsbOwnedFeedbackActivationState.
                SequenceExhausted;
            return false;
        }
        stateRevision++;
        return true;
    }

    private static bool IsValidTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds >= 0 &&
        timeoutMilliseconds <= MaximumTimeoutMilliseconds;

    private static long StartDeadline(int timeoutMilliseconds)
    {
        long now = Environment.TickCount64;
        return timeoutMilliseconds <= 0 ? now :
            now > long.MaxValue - timeoutMilliseconds ? long.MaxValue :
            now + timeoutMilliseconds;
    }

    private static int RemainingMilliseconds(long deadline,
        int originalTimeoutMilliseconds)
    {
        if (originalTimeoutMilliseconds <= 0)
        {
            return 0;
        }
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 :
            (int)Math.Min(remaining, originalTimeoutMilliseconds);
    }

    private static ulong CurrentMicroseconds() =>
        ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong value) ?
            value : 0;
}
