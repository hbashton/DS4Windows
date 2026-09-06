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

/// <summary>
/// Exact reasons why a candidate full-duplex Pro Controller 2 USB lease could
/// not cross the dormant owned-composite boundary. This boundary is separate
/// from the current read-only Windows adapter: it documents and enforces the
/// proof a future native implementation must supply before startup or output
/// may be composed with the admitted input lifetime.
/// </summary>
internal enum Switch2ProUsbOwnedCompositeAdmissionFailure : byte
{
    None = 0,
    MissingLease,
    InvalidLifetime,
    WrongModelOrTransport,
    RegistrationRejected,
    LeaseLifetimeRejected,
    LeaseLifetimeMismatch,
    AuthenticationRejected,
    InvalidOutputOperationBound,
    DependencyThrew,
}

/// <summary>
/// Full-duplex native acquisition seam for one exact physical USB composite.
/// A successful call must return one object which owns the admitted MI_00 HID
/// input/output handle, the admitted MI_01 command handle, and the process-local
/// physical-container reservation. It must not open a second HID output or
/// command handle, or delegate a facet to a separately acquired lifetime.
///
/// The concrete Windows implementation is dormant and internal: no production
/// discovery, registry, persona, or ControlService path constructs it. The
/// current <see cref="Switch2ProUsbWindowsAdapter"/> remains the separate
/// read-only discovery/open path.
/// </summary>
internal interface ISwitch2ProUsbOwnedCompositeNativeAdapter
{
    bool TryOpenOwnedComposite(
        in Switch2PhysicalInputRegistration registration,
        in Switch2PhysicalInputLifetime lifetime,
        out ISwitch2ProUsbOwnedCompositeLease lease);
}

/// <summary>
/// One exact full-duplex physical lifetime. The same object is the input lease,
/// startup-command lease, and retained output lease. The stronger output method
/// is intentionally distinct from the existing fire-and-forget
/// <see cref="ISwitch2ProUsbHdRumbleTransportLease"/> contract.
///
/// TryWriteReportBounded consumes the span before returning. Its timeout is the
/// maximum cumulative managed wait budget for native quiescence: accounting
/// starts before native submission and deducts synchronous phase time before
/// every wait. It is not a hard wall-clock bound on synchronous Win32 begin,
/// cancel, free, or CloseHandle calls, whose APIs expose no nonblocking deadline
/// contract. A completed result proves this attempt's native submission is
/// quiescent. A proven-rejected result proves only that this attempt owns no
/// submission; an earlier lane operation may still be active. Budget expiry may
/// instead return an outcome-uncertain attempt carrying the exact retained
/// operation claim: its buffer, native storage, and lease remain owned, no
/// replacement output is permitted, and a late completion is contained until
/// TryRetireOutputOperation proves exact quiescence. This split is required
/// because Windows cancellation is a request, not a completion guarantee.
/// Registration, Lifetime, MaximumOutputOperationMilliseconds, and the result
/// of AuthenticatesComposite for the admitted generations must remain immutable
/// until exact retirement. Reading those facts must remain pure and perform no
/// acquisition or controller I/O.
/// </summary>
internal interface ISwitch2ProUsbOwnedCompositeLease :
    ISwitch2ProUsbReadOnlyCompositeLease,
    ISwitch2ProUsbStartupCommandLease
{
    /// <summary>
    /// Maximum supported cumulative managed quiescence-wait budget for one
    /// output attempt. It must be positive and no greater than the shared USB
    /// lifecycle ceiling. This is not a hard bound on synchronous OS calls.
    /// </summary>
    int MaximumOutputOperationMilliseconds { get; }

    /// <summary>
    /// Pure, non-I/O authentication for this exact physical lifetime.
    /// </summary>
    bool AuthenticatesComposite(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration);

    /// <summary>
    /// Pure, non-I/O provenance check for one exact currently retained output
    /// operation. A true result requires this lease's private claim fence,
    /// immutable generations, current sequence, and current active retained
    /// state to match the supplied claim. Quarantine does not make a still-
    /// active exact claim unauthentic: true is required until that operation is
    /// exactly cleared. It must return false after the operation becomes
    /// exactly quiescent or is replaced, and it must not rediscover, cancel,
    /// drain, or otherwise mutate the output lane.
    ///
    /// The fail-closed default keeps older offline fakes from accidentally
    /// authenticating numeric generations as claim provenance. A concrete
    /// owned-output implementation must override it before the bounded bridge
    /// may retain or retire one of its claims.
    /// </summary>
    bool AuthenticatesOutputOperationClaim(
        in Switch2ProUsbOwnedOutputOperationClaim claim) => false;

    /// <summary>
    /// The managed quiescence-wait budget must be positive and no greater than
    /// MaximumOutputOperationMilliseconds. The historical method/property names
    /// are retained for contract compatibility; they do not promise a hard
    /// whole-call wall-clock deadline for synchronous Win32 calls.
    /// </summary>
    Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        int timeoutMilliseconds);

    /// <summary>
    /// Requests cancellation and drains only the exact retained output
    /// operation. RequestRejected performs no I/O or state transition.
    /// RetainedForRetry preserves the same claim and forbids a replacement
    /// write. Quarantined preserves the whole composite for terminal
    /// attention. Only ExactOperationQuiescent permits output lifecycle
    /// retirement and eventual whole-lease disposal. timeoutMilliseconds is a
    /// cumulative managed native-quiescence wait budget; synchronous native
    /// cancellation/free calls themselves have no hard deadline guarantee.
    /// </summary>
    Switch2ProUsbOwnedOutputRetirementResult TryRetireOutputOperation(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        int timeoutMilliseconds);

    /// <summary>
    /// Atomically transfers a never-attempted output lane to one dormant
    /// feedback composition. Success requires that no output attempt has ever
    /// crossed the lane's admission point, that no native output operation is
    /// owned, and that the whole composite remains unsealed and unquarantined.
    /// The caller-created owner fence must remain private to the feedback
    /// factory/lifetime. A successful transfer is one-shot: every direct
    /// TryWriteReportBounded/TryRetireOutputOperation call through this full
    /// composite view is thereafter rejected without native I/O, and only the
    /// returned narrow capability can operate the output lane. Disposal
    /// invalidates that capability and the lane can never be adopted again.
    ///
    /// The fail-closed default prevents an implementation which has not added
    /// an exact never-started/adoption proof from manufacturing Dormant state.
    /// </summary>
    bool TryAdoptDormantFeedbackOutput(object ownerFence,
        out ISwitch2ProUsbOwnedFeedbackOutputLease outputLease)
    {
        outputLease = null;
        return false;
    }
}

/// <summary>
/// Narrow, adoption-bound output capability supplied only to the dormant
/// canonical feedback bridge. Deliberately, the full owned-composite lease does
/// not implement this interface: an input/startup/disposal alias cannot be
/// passed to the bridge or used to bypass the one-shot output transfer.
/// </summary>
internal interface ISwitch2ProUsbOwnedFeedbackOutputLease
{
    /// <summary>
    /// Seal this adopted output after a definite native removal on its exact
    /// HID handle. Requires no pending native operation. Never inferred from
    /// discovery absence, timeout, cancellation or a generic write failure.
    /// </summary>
    bool TrySealDisconnectedOutput() => false;

    int MaximumOutputOperationMilliseconds { get; }

    bool AuthenticatesComposite(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration);

    bool AuthenticatesOutputOperationClaim(
        in Switch2ProUsbOwnedOutputOperationClaim claim);

    Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        int timeoutMilliseconds);

    Switch2ProUsbOwnedOutputRetirementResult TryRetireOutputOperation(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        int timeoutMilliseconds);
}

/// <summary>
/// Pre-disposal feedback hook. A successful result must mean that new
/// canonical feedback is sealed and either the exact terminal neutral report
/// completed or the exact adopted native output was sealed on definite device
/// removal. These are distinct outcomes; neither may retain output I/O.
/// Only then may a composition
/// owner retire the startup/command lease and invoke the existing input
/// participant's StopAndQuiesce operation.
///
/// The dormant activation lifetime implements this through the existing
/// canonical pump/sink, physical writer, and adoption-bound owned bridge.
/// </summary>
internal interface ISwitch2ProUsbOwnedFeedbackLifetime
{
    bool Authenticates(
        in Switch2ProUsbOwnedCompositeAuthority authority);

    Switch2ProUsbOwnedFeedbackQuiescenceResult TryNeutralizeAndQuiesce(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        int timeoutMilliseconds);

    /// <summary>
    /// Pure current-state authentication for a terminal result. Numeric
    /// generations are insufficient: exact success must bind the issuing
    /// feedback lifetime, its private terminal fence, a monotonic state
    /// revision, and the issuer's current terminal state. The fail-closed
    /// default prevents an unupgraded fake from authorizing physical disposal.
    /// </summary>
    bool AuthenticatesQuiescenceResult(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2ProUsbOwnedFeedbackQuiescenceResult result) => false;
}

internal enum Switch2ProUsbOwnedFeedbackQuiescenceOutcome : byte
{
    Invalid = 0,
    ExactNeutralAndQuiescent,
    ProvenIncomplete,
    OutcomeUncertain,
    ExactDisconnectedAndQuiescent,
}

/// <summary>
/// Terminal result returned by the feedback hook. Exact authorization requires
/// the issuer/fence/revision authentication seam in addition to generations.
/// OutcomeUncertain never permits command retirement or input disposal.
/// </summary>
internal readonly struct Switch2ProUsbOwnedFeedbackQuiescenceResult
{
    private readonly object issuer;
    private readonly object terminalFence;

    private Switch2ProUsbOwnedFeedbackQuiescenceResult(
        Switch2ProUsbOwnedFeedbackQuiescenceOutcome outcome,
        ulong deviceGeneration, ulong transportGeneration,
        object issuer = null, object terminalFence = null,
        ulong stateRevision = 0)
    {
        this.issuer = issuer;
        this.terminalFence = terminalFence;
        Outcome = outcome;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        StateRevision = stateRevision;
    }

    internal Switch2ProUsbOwnedFeedbackQuiescenceOutcome Outcome { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal ulong StateRevision { get; }

    internal bool HasValidInvariants() =>
        Outcome is >=
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                    ExactNeutralAndQuiescent and <=
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent &&
        DeviceGeneration != 0 && TransportGeneration != 0;

    internal bool Authenticates(
        in Switch2ProUsbOwnedCompositeAuthority authority) =>
        HasValidInvariants() && authority.IsValid &&
        DeviceGeneration == authority.DeviceGeneration &&
        TransportGeneration == authority.TransportGeneration;

    internal bool AuthenticatesExact(object expectedIssuer,
        object expectedTerminalFence,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        ulong expectedStateRevision) => Authenticates(authority) &&
        issuer != null && terminalFence != null &&
        ReferenceEquals(issuer, expectedIssuer) &&
        ReferenceEquals(terminalFence, expectedTerminalFence) &&
        StateRevision != 0 && StateRevision == expectedStateRevision;

    internal static Switch2ProUsbOwnedFeedbackQuiescenceResult Complete(
        ulong deviceGeneration, ulong transportGeneration) => new(
        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
            ExactNeutralAndQuiescent,
        deviceGeneration, transportGeneration);

    internal static Switch2ProUsbOwnedFeedbackQuiescenceResult Incomplete(
        ulong deviceGeneration, ulong transportGeneration) => new(
        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
        deviceGeneration, transportGeneration);

    internal static Switch2ProUsbOwnedFeedbackQuiescenceResult Uncertain(
        ulong deviceGeneration, ulong transportGeneration) => new(
        Switch2ProUsbOwnedFeedbackQuiescenceOutcome.OutcomeUncertain,
        deviceGeneration, transportGeneration);

    internal static Switch2ProUsbOwnedFeedbackQuiescenceResult Exact(
        Switch2ProUsbOwnedFeedbackQuiescenceOutcome outcome, object issuer,
        object terminalFence, ulong deviceGeneration,
        ulong transportGeneration, ulong stateRevision) =>
        outcome is >= Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent and <=
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent &&
            issuer != null && terminalFence != null &&
            deviceGeneration != 0 && transportGeneration != 0 &&
            stateRevision != 0 ?
            new(outcome, deviceGeneration, transportGeneration, issuer,
                terminalFence, stateRevision) : default;
}

/// <summary>
/// Opaque, one-owner capability for a successfully admitted bundle. Copies of
/// the capability authenticate only the issuing bundle; matching numeric
/// generations from another bundle are insufficient.
/// </summary>
internal readonly struct Switch2ProUsbOwnedCompositeAuthority :
    IEquatable<Switch2ProUsbOwnedCompositeAuthority>
{
    private readonly Switch2ProUsbOwnedCompositeLeaseBundle issuer;

    internal Switch2ProUsbOwnedCompositeAuthority(
        Switch2ProUsbOwnedCompositeLeaseBundle issuer,
        in Switch2PhysicalInputLifetime lifetime)
    {
        this.issuer = issuer;
        DeviceGeneration = lifetime.SessionDescriptor.DeviceGeneration;
        TransportGeneration = lifetime.SessionDescriptor.TransportGeneration;
    }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal bool IsValid => issuer != null && DeviceGeneration != 0 &&
        TransportGeneration != 0;

    internal bool Authenticates(
        Switch2ProUsbOwnedCompositeLeaseBundle expected,
        in Switch2PhysicalInputLifetime expectedLifetime) =>
        ReferenceEquals(issuer, expected) && expectedLifetime.IsValid &&
        DeviceGeneration ==
            expectedLifetime.SessionDescriptor.DeviceGeneration &&
        TransportGeneration ==
            expectedLifetime.SessionDescriptor.TransportGeneration;

    public bool Equals(Switch2ProUsbOwnedCompositeAuthority other) =>
        ReferenceEquals(issuer, other.issuer) &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration;

    public override bool Equals(object obj) =>
        obj is Switch2ProUsbOwnedCompositeAuthority other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : issuer.GetHashCode(), DeviceGeneration,
        TransportGeneration);
}

/// <summary>
/// Offline admission proof for a future full-duplex lease. Admission validates
/// one exact object once; one successful authority take then gives a future
/// composition owner access to all three views of that same object. This type
/// opens no device and performs no startup, input, output, or retirement I/O.
/// It is admission shape only, not a lifecycle coordinator: it does not enforce
/// startup-before-commit, neutral-before-retire, or revoke a view already given
/// to the sole authority owner.
/// </summary>
internal sealed class Switch2ProUsbOwnedCompositeLeaseBundle
{
    private const Switch2ControllerModel RequiredModel =
        Switch2ControllerModel.ProController2;

    private readonly ISwitch2ProUsbOwnedCompositeLease lease;
    private readonly Switch2PhysicalInputLifetime lifetime;
    private int authorityTaken;

    private Switch2ProUsbOwnedCompositeLeaseBundle(
        ISwitch2ProUsbOwnedCompositeLease lease,
        in Switch2PhysicalInputLifetime lifetime)
    {
        this.lease = lease;
        this.lifetime = lifetime;
    }

    internal Switch2PhysicalInputLifetime Lifetime => lifetime;

    internal static bool TryAdmit(
        ISwitch2ProUsbOwnedCompositeLease lease,
        in Switch2PhysicalInputLifetime expectedLifetime,
        out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        out Switch2ProUsbOwnedCompositeAdmissionFailure failure)
    {
        bundle = null;
        if (lease == null)
        {
            failure =
                Switch2ProUsbOwnedCompositeAdmissionFailure.MissingLease;
            return false;
        }
        if (!expectedLifetime.IsValid)
        {
            failure =
                Switch2ProUsbOwnedCompositeAdmissionFailure.InvalidLifetime;
            return false;
        }
        if (expectedLifetime.Registration.ProtocolIdentity.Model !=
                RequiredModel ||
            expectedLifetime.Registration.ProtocolIdentity.Transport !=
                Switch2Transport.Usb)
        {
            failure = Switch2ProUsbOwnedCompositeAdmissionFailure.
                WrongModelOrTransport;
            return false;
        }

        Switch2PhysicalInputRegistration registration;
        Switch2PhysicalInputLifetime leaseLifetime;
        int maximumOutputOperationMilliseconds;
        bool authenticates;
        try
        {
            registration = lease.Registration;
            leaseLifetime = lease.Lifetime;
            maximumOutputOperationMilliseconds =
                lease.MaximumOutputOperationMilliseconds;
            authenticates = lease.AuthenticatesComposite(RequiredModel,
                expectedLifetime.SessionDescriptor.DeviceGeneration,
                expectedLifetime.SessionDescriptor.TransportGeneration);
        }
        catch
        {
            failure =
                Switch2ProUsbOwnedCompositeAdmissionFailure.DependencyThrew;
            return false;
        }

        if (!registration.Equals(expectedLifetime.Registration))
        {
            failure = Switch2ProUsbOwnedCompositeAdmissionFailure.
                RegistrationRejected;
            return false;
        }
        if (!leaseLifetime.IsValid)
        {
            failure = Switch2ProUsbOwnedCompositeAdmissionFailure.
                LeaseLifetimeRejected;
            return false;
        }
        if (!leaseLifetime.Equals(expectedLifetime))
        {
            failure = Switch2ProUsbOwnedCompositeAdmissionFailure.
                LeaseLifetimeMismatch;
            return false;
        }
        if (!authenticates)
        {
            failure = Switch2ProUsbOwnedCompositeAdmissionFailure.
                AuthenticationRejected;
            return false;
        }
        if (maximumOutputOperationMilliseconds <= 0 ||
            maximumOutputOperationMilliseconds >
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds)
        {
            failure = Switch2ProUsbOwnedCompositeAdmissionFailure.
                InvalidOutputOperationBound;
            return false;
        }

        bundle = new Switch2ProUsbOwnedCompositeLeaseBundle(lease,
            expectedLifetime);
        failure = Switch2ProUsbOwnedCompositeAdmissionFailure.None;
        return true;
    }

    /// <summary>
    /// Gives the future composition owner its sole capability. The returned
    /// value may be copied by that owner, but a second take cannot create a
    /// competing composition owner.
    /// </summary>
    internal bool TryTakeAuthority(
        out Switch2ProUsbOwnedCompositeAuthority authority)
    {
        if (Interlocked.CompareExchange(ref authorityTaken, 1, 0) != 0)
        {
            authority = default;
            return false;
        }

        authority = new Switch2ProUsbOwnedCompositeAuthority(this, lifetime);
        return true;
    }

    internal bool TryGetInputLease(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        out ISwitch2ProUsbReadOnlyCompositeLease inputLease)
    {
        if (!Authenticates(authority))
        {
            inputLease = null;
            return false;
        }
        inputLease = lease;
        return true;
    }

    internal bool TryGetStartupLease(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        out ISwitch2ProUsbStartupCommandLease startupLease)
    {
        if (!Authenticates(authority))
        {
            startupLease = null;
            return false;
        }
        startupLease = lease;
        return true;
    }

    internal bool TryGetCalibrationLease(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        out ISwitch2ProUsbCalibrationCommandLease calibrationLease)
    {
        if (!Authenticates(authority) ||
            lease is not ISwitch2ProUsbCalibrationCommandLease supported)
        {
            calibrationLease = null;
            return false;
        }
        calibrationLease = supported;
        return true;
    }

    internal bool TryGetBoundedOutputLease(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        out ISwitch2ProUsbOwnedCompositeLease outputLease)
    {
        if (!Authenticates(authority))
        {
            outputLease = null;
            return false;
        }
        outputLease = lease;
        return true;
    }

    private bool Authenticates(
        in Switch2ProUsbOwnedCompositeAuthority authority)
    {
        if (Volatile.Read(ref authorityTaken) == 0 ||
            !authority.Authenticates(this, lifetime))
        {
            return false;
        }

        try
        {
            Switch2PhysicalInputRegistration registration = lease.Registration;
            Switch2PhysicalInputLifetime currentLifetime = lease.Lifetime;
            int outputBound = lease.MaximumOutputOperationMilliseconds;
            bool authenticates = lease.AuthenticatesComposite(RequiredModel,
                lifetime.SessionDescriptor.DeviceGeneration,
                lifetime.SessionDescriptor.TransportGeneration);
            return registration.Equals(lifetime.Registration) &&
                currentLifetime.Equals(lifetime) && outputBound > 0 &&
                outputBound <=
                    Switch2ProUsbInputTransportOwner.
                        MaximumDisposeTimeoutMilliseconds && authenticates;
        }
        catch
        {
            return false;
        }
    }
}
