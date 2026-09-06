/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DS4Windows.Switch2;

/// <summary>
/// Construction-only hook which binds a native input handoff to the exact
/// runtime owner before the read facet can escape into its transport.
/// Implementations must not start input, output, or command I/O.
/// </summary>
internal interface ISwitch2ProUsbRuntimeInputAdoptionBinder
{
    bool TryBindRuntimeOwner(Switch2ProUsbRuntimeOwner owner,
        in InputControllerRegistration registration);
}

internal enum Switch2ProUsbOwnedCompositeInputAdoptionState : byte
{
    Invalid = 0,
    Ready,
    RuntimeBound,
    HandoffInProgress,
    HandedOff,
    CredentialPublished,
    Adopted,
    Quarantined,
    SequenceExhausted,
}

internal enum Switch2ProUsbOwnedCompositeInputAdoptionFailure : byte
{
    None = 0,
    MissingDependency,
    InvalidAuthority,
    AuthorityRejected,
    CompositeAlreadyClaimed,
    InvalidLifetime,
    RuntimeBindingRejected,
    DifferentRuntimeAlreadyBound,
    InvalidState,
    OperationAlreadyInProgress,
    RegistrationMismatch,
    FacetRejected,
    FacetMismatch,
    SequenceExhausted,
    InvalidCredential,
    StaleCredential,
    CredentialAlreadyConsumed,
    RuntimeCreationRejected,
    DependencyThrew,
    QuarantineRequired,
}

/// <summary>
/// Copyable proof minted at the exact owned-bundle input-facet handoff. The
/// issuing object and mediated facet are private reference fences; matching
/// numeric generations, registrations, or lifetimes from another issuer are
/// insufficient. Exactly one copy can be consumed.
/// </summary>
internal readonly struct Switch2ProUsbOwnedCompositeInputAdoptionCredential :
    IEquatable<Switch2ProUsbOwnedCompositeInputAdoptionCredential>
{
    private readonly Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer;
    private readonly Switch2ProUsbOwnedCompositeInputFacetLease facetFence;
    private readonly Switch2ProUsbRuntimeOwner runtimeOwner;
    private readonly InputControllerRegistration runtimeRegistration;
    private readonly Switch2PhysicalInputLifetime lifetime;
    private readonly Switch2ProUsbOwnedCompositeAuthority bundleAuthority;

    internal Switch2ProUsbOwnedCompositeInputAdoptionCredential(
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer,
        Switch2ProUsbOwnedCompositeInputFacetLease facetFence,
        Switch2ProUsbRuntimeOwner runtimeOwner,
        in InputControllerRegistration runtimeRegistration,
        in Switch2PhysicalInputLifetime lifetime,
        in Switch2ProUsbOwnedCompositeAuthority bundleAuthority,
        ulong sequence)
    {
        this.issuer = issuer;
        this.facetFence = facetFence;
        this.runtimeOwner = runtimeOwner;
        this.runtimeRegistration = runtimeRegistration;
        this.lifetime = lifetime;
        this.bundleAuthority = bundleAuthority;
        Sequence = sequence;
    }

    internal ulong DeviceGeneration =>
        lifetime.SessionDescriptor.DeviceGeneration;

    internal ulong TransportGeneration =>
        lifetime.SessionDescriptor.TransportGeneration;

    internal ulong Sequence { get; }

    internal bool IsValid => issuer != null && facetFence != null &&
        runtimeOwner != null && runtimeRegistration.Device != null &&
        runtimeRegistration.Generation != 0 &&
        runtimeRegistration.OwnershipKind ==
            InputControllerOwnershipKind.Switch2Runtime &&
        ReferenceEquals(runtimeRegistration.Owner, runtimeOwner) &&
        lifetime.IsValid && bundleAuthority.IsValid && Sequence != 0;

    internal bool TryConsume(
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime,
        Switch2ProUsbRuntimeOwner expectedRuntimeOwner,
        in InputControllerRegistration expectedRuntimeRegistration,
        out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure)
    {
        if (issuer == null)
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                InvalidCredential;
            return false;
        }
        return issuer.TryConsumeCredential(this, expectedAuthority,
            expectedLifetime, expectedRuntimeOwner,
            expectedRuntimeRegistration, out failure);
    }

    /// <summary>
    /// Gives the sole composition owner the issuer's one-shot proof only after
    /// the mediated input facet has completed runtime retirement. A copied or
    /// foreign credential cannot authenticate a different owner, registration,
    /// authority, or lifetime. A construction-rollback proof is consumed and
    /// rejected fail closed; it can never authorize whole-composite disposal.
    /// </summary>
    internal bool TryTakeRuntimeRetirementProof(
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime,
        Switch2ProUsbRuntimeOwner expectedRuntimeOwner,
        in InputControllerRegistration expectedRuntimeRegistration,
        out Switch2ProUsbOwnedCompositeInputFacetRetirementProof proof)
    {
        proof = default;
        if (!IsValid || !ReferenceEquals(runtimeOwner,
                expectedRuntimeOwner) ||
            !runtimeRegistration.Equals(expectedRuntimeRegistration) ||
            !lifetime.Equals(expectedLifetime) ||
            !bundleAuthority.Equals(expectedAuthority) || issuer == null ||
            !issuer.TryTakeInputFacetRetirementProof(expectedAuthority,
                expectedLifetime, out proof))
        {
            return false;
        }

        return proof.IsValid && proof.Kind ==
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                RuntimeRetirement;
    }

    internal void QuarantineIssuer(
        Switch2ProUsbOwnedCompositeInputAdoptionFailure reason) =>
        issuer?.Quarantine(reason);

    internal bool Authenticates(
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer expectedIssuer,
        Switch2ProUsbOwnedCompositeInputFacetLease expectedFacetFence,
        Switch2ProUsbRuntimeOwner expectedRuntimeOwner,
        in InputControllerRegistration expectedRuntimeRegistration,
        in Switch2PhysicalInputLifetime expectedLifetime,
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        ulong expectedSequence) =>
        IsValid && ReferenceEquals(issuer, expectedIssuer) &&
        ReferenceEquals(facetFence, expectedFacetFence) &&
        ReferenceEquals(runtimeOwner, expectedRuntimeOwner) &&
        runtimeRegistration.Equals(expectedRuntimeRegistration) &&
        lifetime.Equals(expectedLifetime) &&
        bundleAuthority.Equals(expectedAuthority) &&
        Sequence == expectedSequence;

    public bool Equals(
        Switch2ProUsbOwnedCompositeInputAdoptionCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(facetFence, other.facetFence) &&
        ReferenceEquals(runtimeOwner, other.runtimeOwner) &&
        runtimeRegistration.Equals(other.runtimeRegistration) &&
        lifetime.Equals(other.lifetime) &&
        bundleAuthority.Equals(other.bundleAuthority) &&
        Sequence == other.Sequence;

    public override bool Equals(object obj) => obj is
        Switch2ProUsbOwnedCompositeInputAdoptionCredential other &&
        Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        facetFence == null ? 0 : RuntimeHelpers.GetHashCode(facetFence),
        runtimeOwner == null ? 0 : RuntimeHelpers.GetHashCode(runtimeOwner),
        runtimeRegistration, lifetime, bundleAuthority, Sequence);
}

internal enum Switch2ProUsbOwnedCompositeInputFacetRetirementKind : byte
{
    Invalid = 0,
    ConstructionRollback,
    RuntimeRetirement,
}

/// <summary>
/// One-shot evidence returned when the mediated facet observed exact native
/// input quiescence and then became permanently terminal. It does not claim
/// that command/output operations or the full composite are quiescent.
/// </summary>
internal readonly struct
    Switch2ProUsbOwnedCompositeInputFacetRetirementProof
{
    private readonly Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer;
    private readonly Switch2ProUsbOwnedCompositeInputFacetLease facet;
    private readonly Switch2ProUsbOwnedCompositeAuthority authority;
    private readonly Switch2PhysicalInputLifetime lifetime;

    internal Switch2ProUsbOwnedCompositeInputFacetRetirementProof(
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer,
        Switch2ProUsbOwnedCompositeInputFacetLease facet,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2PhysicalInputLifetime lifetime, ulong handoffSequence,
        Switch2ProUsbOwnedCompositeInputFacetRetirementKind kind)
    {
        this.issuer = issuer;
        this.facet = facet;
        this.authority = authority;
        this.lifetime = lifetime;
        HandoffSequence = handoffSequence;
        Kind = kind;
    }

    internal ulong HandoffSequence { get; }

    internal Switch2ProUsbOwnedCompositeInputFacetRetirementKind Kind { get; }

    internal bool IsValid => issuer != null && facet != null &&
        authority.IsValid && lifetime.IsValid && HandoffSequence != 0 &&
        Kind is Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                ConstructionRollback or
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                RuntimeRetirement;

    internal bool Authenticates(
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer expectedIssuer,
        Switch2ProUsbOwnedCompositeInputFacetLease expectedFacet,
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime,
        ulong expectedHandoffSequence) => IsValid &&
        ReferenceEquals(issuer, expectedIssuer) &&
        ReferenceEquals(facet, expectedFacet) &&
        authority.Equals(expectedAuthority) && lifetime.Equals(expectedLifetime) &&
        HandoffSequence == expectedHandoffSequence;
}

/// <summary>
/// Mediates only the already-admitted input facet. Runtime disposal retires
/// this logical view after native input quiescence; it deliberately does not
/// dispose the shared physical composite. A future lifecycle coordinator must
/// perform whole-composite retirement after feedback and command retirement.
/// </summary>
internal sealed class Switch2ProUsbOwnedCompositeInputFacetLease :
    ISwitch2ProUsbReadOnlyCompositeLease,
    ISwitch2ProUsbReadCompletionTarget
{
    private enum FacetState : int
    {
        Open = 0,
        QuiescenceInProgress,
        RetainedForQuiescenceRetry,
        QuiescenceProven,
        Retired,
        Quarantined,
    }

    private readonly ISwitch2ProUsbOwnedCompositeLease compositeLease;
    private readonly Switch2PhysicalInputRegistration registration;
    private readonly Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer;
    private readonly object completionGate = new();
    private ISwitch2ProUsbReadCompletionTarget completionTarget;
    private Switch2ProUsbReadClaim activeClaim;
    private int state;
    private int nativeControlOperationInProgress;
    private int retirementKind;

    internal Switch2ProUsbOwnedCompositeInputFacetLease(
        ISwitch2ProUsbOwnedCompositeLease compositeLease,
        in Switch2PhysicalInputRegistration registration,
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer)
    {
        this.compositeLease = compositeLease ??
            throw new ArgumentNullException(nameof(compositeLease));
        this.registration = registration;
        this.issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
    }

    public Switch2PhysicalInputRegistration Registration => registration;

    internal bool IsRetired => (FacetState)Volatile.Read(ref state) is
        FacetState.Retired or FacetState.Quarantined;

    internal bool IsAvailableForAdoption =>
        (FacetState)Volatile.Read(ref state) == FacetState.Open &&
        Volatile.Read(ref nativeControlOperationInProgress) == 0;

    public bool TryBeginInputRead(byte[] destination, int offset, int count,
        in Switch2ProUsbReadClaim claim,
        ISwitch2ProUsbReadCompletionTarget completionTarget)
    {
        if (completionTarget == null || !claim.IsValid ||
            !TryEnterNativeControlOperation(allowRetained: false))
        {
            return false;
        }
        try
        {
            lock (completionGate)
            {
                if ((FacetState)Volatile.Read(ref state) != FacetState.Open ||
                    this.completionTarget != null)
                {
                    return false;
                }
                this.completionTarget = completionTarget;
                activeClaim = claim;
            }

            bool started;
            try
            {
                started = compositeLease.TryBeginInputRead(destination,
                    offset, count, claim, this);
            }
            catch
            {
                // A throw may follow native submission; preserve the callback
                // target until bounded quiescence proves it safe.
                throw;
            }
            if (!started)
            {
                lock (completionGate)
                {
                    if (activeClaim.Equals(claim))
                    {
                        this.completionTarget = null;
                        activeClaim = default;
                    }
                }
            }
            return started;
        }
        finally
        {
            ExitNativeControlOperation();
        }
    }

    public bool TryCancelInputRead(in Switch2ProUsbReadClaim claim)
    {
        if (!TryEnterNativeControlOperation(allowRetained: true))
        {
            return false;
        }
        try
        {
            return compositeLease.TryCancelInputRead(claim);
        }
        finally
        {
            ExitNativeControlOperation();
        }
    }

    public bool TryRetireCompletedInputRead(
        in Switch2ProUsbReadClaim claim, int timeoutMilliseconds)
    {
        if (!TryEnterNativeControlOperation(allowRetained: true))
        {
            return false;
        }
        try
        {
            bool retiredRead = compositeLease.TryRetireCompletedInputRead(
                claim, timeoutMilliseconds);
            if (retiredRead)
            {
                ClearCompletionTarget(claim);
            }
            return retiredRead;
        }
        finally
        {
            ExitNativeControlOperation();
        }
    }

    public bool TryWaitForInputQuiescence(int timeoutMilliseconds)
    {
        if (!TryEnterWaitOperation())
        {
            return false;
        }
        try
        {
            if (!TryBindRetirementKind())
            {
                Volatile.Write(ref state, (int)FacetState.Quarantined);
                issuer.NotifyUncertainInputFacetQuiescence(this);
                return false;
            }

            bool quiescent;
            try
            {
                quiescent = compositeLease.TryWaitForInputQuiescence(
                    timeoutMilliseconds);
            }
            catch
            {
                Volatile.Write(ref state, (int)FacetState.Quarantined);
                issuer.NotifyUncertainInputFacetQuiescence(this);
                throw;
            }
            if (!quiescent)
            {
                if (Interlocked.CompareExchange(ref state,
                        (int)FacetState.RetainedForQuiescenceRetry,
                        (int)FacetState.QuiescenceInProgress) !=
                    (int)FacetState.QuiescenceInProgress)
                {
                    Volatile.Write(ref state, (int)FacetState.Quarantined);
                    issuer.NotifyUncertainInputFacetQuiescence(this);
                }
                return false;
            }

            if (Interlocked.CompareExchange(ref state,
                    (int)FacetState.QuiescenceProven,
                    (int)FacetState.QuiescenceInProgress) !=
                (int)FacetState.QuiescenceInProgress)
            {
                Volatile.Write(ref state, (int)FacetState.Quarantined);
                issuer.NotifyUncertainInputFacetQuiescence(this);
                return false;
            }
            lock (completionGate)
            {
                completionTarget = null;
                activeClaim = default;
            }
            return true;
        }
        finally
        {
            ExitNativeControlOperation();
        }
    }

    public void DisposeQuiesced()
    {
        if (Interlocked.CompareExchange(ref state, (int)FacetState.Retired,
                (int)FacetState.QuiescenceProven) !=
            (int)FacetState.QuiescenceProven)
        {
            Volatile.Write(ref state, (int)FacetState.Quarantined);
            issuer.NotifyInvalidInputFacetRetirement(this);
            throw new InvalidOperationException(
                "Input facet retirement lacks exact quiescence proof.");
        }
        issuer.NotifyInputFacetRetired(this,
            (Switch2ProUsbOwnedCompositeInputFacetRetirementKind)
                Volatile.Read(ref retirementKind));
    }

    internal void RetainOrQuarantineWithoutIo()
    {
        while (true)
        {
            int observed = Volatile.Read(ref state);
            if ((FacetState)observed is FacetState.RetainedForQuiescenceRetry
                or FacetState.QuiescenceInProgress
                or FacetState.QuiescenceProven or FacetState.Retired or
                FacetState.Quarantined)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref state,
                    (int)FacetState.RetainedForQuiescenceRetry, observed) ==
                observed)
            {
                return;
            }
        }
    }

    public Switch2ProUsbReadCompletionDisposition CompleteInputRead(
        in Switch2ProUsbReadClaim claim, int bytesTransferred,
        long completionTimestampQpc, Switch2ProUsbNativeReadStatus status)
    {
        ISwitch2ProUsbReadCompletionTarget target;
        bool callbackAfterRetirement = false;
        lock (completionGate)
        {
            FacetState observed = (FacetState)Volatile.Read(ref state);
            if (observed is FacetState.QuiescenceProven or
                FacetState.Retired or FacetState.Quarantined)
            {
                completionTarget = null;
                activeClaim = default;
                callbackAfterRetirement = true;
                target = null;
            }
            else if (!activeClaim.Equals(claim) || completionTarget == null)
            {
                return Switch2ProUsbReadCompletionDisposition.StaleClaim;
            }
            else
            {
                target = completionTarget;
            }
        }
        if (callbackAfterRetirement)
        {
            issuer.NotifyCallbackAfterInputFacetRetirement(this);
            return Switch2ProUsbReadCompletionDisposition.
                LifecycleSuppressed;
        }

        // The transport callback is an external dependency and is never
        // invoked while the facet's private completion gate is held.
        return target.CompleteInputRead(claim, bytesTransferred,
            completionTimestampQpc, status);
    }

    private void ClearCompletionTarget(in Switch2ProUsbReadClaim claim)
    {
        lock (completionGate)
        {
            if (activeClaim.Equals(claim))
            {
                completionTarget = null;
                activeClaim = default;
            }
        }
    }

    private bool TryEnterNativeControlOperation(bool allowRetained)
    {
        FacetState observed = (FacetState)Volatile.Read(ref state);
        if (!AllowsNativeControl(observed, allowRetained) ||
            Interlocked.CompareExchange(
                ref nativeControlOperationInProgress, 1, 0) != 0)
        {
            return false;
        }
        observed = (FacetState)Volatile.Read(ref state);
        if (AllowsNativeControl(observed, allowRetained))
        {
            return true;
        }
        Volatile.Write(ref nativeControlOperationInProgress, 0);
        return false;
    }

    private bool TryEnterWaitOperation()
    {
        FacetState observed = (FacetState)Volatile.Read(ref state);
        if (observed is not (FacetState.Open or
                FacetState.RetainedForQuiescenceRetry) ||
            Interlocked.CompareExchange(
                ref nativeControlOperationInProgress, 1, 0) != 0)
        {
            return false;
        }
        while (true)
        {
            int current = Volatile.Read(ref state);
            observed = (FacetState)current;
            if (observed is not (FacetState.Open or
                    FacetState.RetainedForQuiescenceRetry))
            {
                Volatile.Write(ref nativeControlOperationInProgress, 0);
                return false;
            }
            if (Interlocked.CompareExchange(ref state,
                    (int)FacetState.QuiescenceInProgress, current) == current)
            {
                return true;
            }
        }
    }

    private bool TryBindRetirementKind()
    {
        if (!issuer.TrySealInputFacetRetirementPhase(this,
                out Switch2ProUsbOwnedCompositeInputFacetRetirementKind kind))
        {
            return false;
        }
        int observed = Interlocked.CompareExchange(ref retirementKind,
            (int)kind, (int)
                Switch2ProUsbOwnedCompositeInputFacetRetirementKind.Invalid);
        return observed ==
                (int)Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                    Invalid ||
            observed == (int)kind;
    }

    private static bool AllowsNativeControl(FacetState state,
        bool allowRetained) => state == FacetState.Open ||
        allowRetained && state == FacetState.RetainedForQuiescenceRetry;

    private void ExitNativeControlOperation() =>
        Volatile.Write(ref nativeControlOperationInProgress, 0);
}

/// <summary>
/// One-shot, offline bridge from an admitted full-composite bundle to the
/// existing read-only runtime factory. It neither opens a device nor starts a
/// read. Every dependency call occurs outside <see cref="gate"/>.
/// </summary>
internal sealed class Switch2ProUsbOwnedCompositeInputAdoptionIssuer :
    ISwitch2ProUsbNativeAdapter, ISwitch2ProUsbRuntimeInputAdoptionBinder
{
    private sealed class ExactLeaseClaim
    {
        internal int Taken;
    }

    private static readonly ConditionalWeakTable<
        ISwitch2ProUsbOwnedCompositeLease, ExactLeaseClaim>
        ExactLeaseClaims = new();

    private readonly object gate = new();
    private readonly Switch2ProUsbOwnedCompositeLeaseBundle bundle;
    private readonly Switch2ProUsbOwnedCompositeAuthority authority;
    private readonly Switch2PhysicalInputLifetime lifetime;
    private readonly ISwitch2ProUsbOwnedCompositeLease compositeLease;

    private Switch2ProUsbOwnedCompositeInputAdoptionState state =
        Switch2ProUsbOwnedCompositeInputAdoptionState.Ready;
    private Switch2ProUsbOwnedCompositeInputAdoptionFailure lastFailure;
    private Switch2ProUsbRuntimeOwner runtimeOwner;
    private InputControllerRegistration runtimeRegistration;
    private Switch2ProUsbOwnedCompositeInputFacetLease inputFacet;
    private Switch2ProUsbOwnedCompositeInputAdoptionCredential credential;
    private Switch2ProUsbOwnedCompositeInputFacetRetirementProof
        retirementProof;
    private ulong sequence;
    private bool operationInProgress;
    private bool credentialConsumed;
    private bool inputFacetRetired;
    private bool retirementProofTaken;
    private bool retirementPhaseSealed;
    private Switch2ProUsbOwnedCompositeInputFacetRetirementKind
        retirementKind;

    private Switch2ProUsbOwnedCompositeInputAdoptionIssuer(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2PhysicalInputLifetime lifetime,
        ISwitch2ProUsbOwnedCompositeLease compositeLease,
        ulong initialSequence)
    {
        this.bundle = bundle;
        this.authority = authority;
        this.lifetime = lifetime;
        this.compositeLease = compositeLease;
        sequence = initialSequence;
    }

    internal Switch2ProUsbOwnedCompositeInputAdoptionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    internal Switch2ProUsbOwnedCompositeInputAdoptionFailure LastFailure
    {
        get
        {
            lock (gate)
            {
                return lastFailure;
            }
        }
    }

    internal Switch2PhysicalInputLifetime Lifetime => lifetime;

    internal bool IsInputFacetRetired
    {
        get
        {
            lock (gate)
            {
                return inputFacetRetired;
            }
        }
    }

    internal Switch2ProUsbRuntimeOwner BoundRuntimeOwner
    {
        get
        {
            lock (gate)
            {
                return runtimeOwner;
            }
        }
    }

    internal static bool TryCreate(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        out Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer,
        out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure) =>
        TryCreateCore(bundle, authority, initialSequence: 0, out issuer,
            out failure);

    internal static bool TryCreateCore(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        ulong initialSequence,
        out Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer,
        out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure)
    {
        issuer = null;
        if (bundle == null)
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                MissingDependency;
            return false;
        }
        Switch2PhysicalInputLifetime lifetime = bundle.Lifetime;
        if (!lifetime.IsValid)
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                InvalidLifetime;
            return false;
        }
        if (!authority.IsValid || authority.DeviceGeneration !=
                lifetime.SessionDescriptor.DeviceGeneration ||
            authority.TransportGeneration !=
                lifetime.SessionDescriptor.TransportGeneration)
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                InvalidAuthority;
            return false;
        }

        ISwitch2ProUsbReadOnlyCompositeLease inputLease;
        bool authenticated;
        try
        {
            authenticated = bundle.TryGetInputLease(authority,
                out inputLease);
        }
        catch
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                DependencyThrew;
            return false;
        }
        if (!authenticated || inputLease == null)
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                AuthorityRejected;
            return false;
        }
        if (inputLease is not ISwitch2ProUsbOwnedCompositeLease ownedLease)
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                FacetMismatch;
            return false;
        }

        ExactLeaseClaim exactLeaseClaim = ExactLeaseClaims.GetValue(
            ownedLease, static _ => new ExactLeaseClaim());
        if (Interlocked.CompareExchange(ref exactLeaseClaim.Taken, 1, 0) != 0)
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                CompositeAlreadyClaimed;
            return false;
        }

        issuer = new Switch2ProUsbOwnedCompositeInputAdoptionIssuer(bundle,
            authority, lifetime, ownedLease, initialSequence);
        failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.None;
        return true;
    }

    public bool TryBindRuntimeOwner(Switch2ProUsbRuntimeOwner owner,
        in InputControllerRegistration registration)
    {
        if (!AuthenticatesRuntimeOwner(owner, registration))
        {
            SetLastFailure(Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                RuntimeBindingRejected);
            return false;
        }

        lock (gate)
        {
            if (operationInProgress)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        OperationAlreadyInProgress);
            }
            if (state ==
                    Switch2ProUsbOwnedCompositeInputAdoptionState.RuntimeBound)
            {
                if (ReferenceEquals(runtimeOwner, owner) &&
                    runtimeRegistration.Equals(registration))
                {
                    lastFailure =
                        Switch2ProUsbOwnedCompositeInputAdoptionFailure.None;
                    return true;
                }
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        DifferentRuntimeAlreadyBound);
            }
            if (state != Switch2ProUsbOwnedCompositeInputAdoptionState.Ready)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        InvalidState);
            }

            runtimeOwner = owner;
            runtimeRegistration = registration;
            state = Switch2ProUsbOwnedCompositeInputAdoptionState.RuntimeBound;
            lastFailure =
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.None;
            return true;
        }
    }

    public bool TryOpenReadOnlyComposite(
        in Switch2PhysicalInputRegistration registration,
        out ISwitch2ProUsbReadOnlyCompositeLease lease)
    {
        lease = null;
        if (!registration.Equals(lifetime.Registration))
        {
            SetLastFailure(Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                RegistrationMismatch);
            return false;
        }

        lock (gate)
        {
            if (operationInProgress)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        OperationAlreadyInProgress);
            }
            if (state !=
                Switch2ProUsbOwnedCompositeInputAdoptionState.RuntimeBound)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        InvalidState);
            }
            if (sequence == ulong.MaxValue)
            {
                state = Switch2ProUsbOwnedCompositeInputAdoptionState.
                    SequenceExhausted;
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        SequenceExhausted);
            }
            operationInProgress = true;
            state =
                Switch2ProUsbOwnedCompositeInputAdoptionState.HandoffInProgress;
        }

        ISwitch2ProUsbReadOnlyCompositeLease currentFacet = null;
        bool authenticated = false;
        bool dependencyThrew = false;
        try
        {
            authenticated = bundle.TryGetInputLease(authority,
                out currentFacet);
        }
        catch
        {
            dependencyThrew = true;
        }

        Switch2ProUsbOwnedCompositeInputFacetLease candidateFacet = null;
        if (authenticated && ReferenceEquals(currentFacet, compositeLease))
        {
            candidateFacet = new Switch2ProUsbOwnedCompositeInputFacetLease(
                compositeLease, registration, this);
        }

        lock (gate)
        {
            operationInProgress = false;
            if (state ==
                Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        QuarantineRequired);
            }
            if (state != Switch2ProUsbOwnedCompositeInputAdoptionState.
                    HandoffInProgress)
            {
                state = Switch2ProUsbOwnedCompositeInputAdoptionState.
                    Quarantined;
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        InvalidState);
            }
            if (dependencyThrew)
            {
                state = Switch2ProUsbOwnedCompositeInputAdoptionState.
                    Quarantined;
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        DependencyThrew);
            }
            if (!authenticated || currentFacet == null)
            {
                state =
                    Switch2ProUsbOwnedCompositeInputAdoptionState.RuntimeBound;
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        FacetRejected);
            }
            if (!ReferenceEquals(currentFacet, compositeLease) ||
                candidateFacet == null)
            {
                state = Switch2ProUsbOwnedCompositeInputAdoptionState.
                    Quarantined;
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        FacetMismatch);
            }

            sequence++;
            inputFacet = candidateFacet;
            retirementKind =
                Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                    ConstructionRollback;
            retirementPhaseSealed = false;
            credential =
                new Switch2ProUsbOwnedCompositeInputAdoptionCredential(this,
                    candidateFacet, runtimeOwner, runtimeRegistration,
                    lifetime, authority, sequence);
            state = Switch2ProUsbOwnedCompositeInputAdoptionState.HandedOff;
            lastFailure =
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.None;
            lease = candidateFacet;
            return true;
        }
    }

    internal bool TryPublishCredential(
        Switch2ProUsbRuntimeOwner expectedRuntimeOwner,
        in InputControllerRegistration expectedRuntimeRegistration,
        in Switch2PhysicalInputLifetime runtimeLifetime,
        out Switch2ProUsbOwnedCompositeInputAdoptionCredential published,
        out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure)
    {
        published = default;
        if (!ReferenceEquals(expectedRuntimeOwner, runtimeOwner) ||
            !expectedRuntimeRegistration.Equals(runtimeRegistration) ||
            !runtimeLifetime.Equals(lifetime))
        {
            Quarantine(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    StaleCredential);
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                StaleCredential;
            return false;
        }

        lock (gate)
        {
            if (operationInProgress)
            {
                failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    OperationAlreadyInProgress;
                lastFailure = failure;
                return false;
            }
            if (state !=
                    Switch2ProUsbOwnedCompositeInputAdoptionState.HandedOff ||
                inputFacet == null || retirementPhaseSealed ||
                !inputFacet.IsAvailableForAdoption ||
                !credential.Authenticates(this, inputFacet, runtimeOwner,
                    runtimeRegistration, lifetime, authority, sequence))
            {
                state = Switch2ProUsbOwnedCompositeInputAdoptionState.
                    Quarantined;
                failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    StaleCredential;
                lastFailure = failure;
                return false;
            }

            retirementKind =
                Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                    RuntimeRetirement;
            state = Switch2ProUsbOwnedCompositeInputAdoptionState.
                CredentialPublished;
            published = credential;
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.None;
            lastFailure = failure;
            return true;
        }
    }

    internal bool TryConsumeCredential(
        in Switch2ProUsbOwnedCompositeInputAdoptionCredential candidate,
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime,
        Switch2ProUsbRuntimeOwner expectedRuntimeOwner,
        in InputControllerRegistration expectedRuntimeRegistration,
        out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure)
    {
        if (!candidate.Authenticates(this, inputFacet, expectedRuntimeOwner,
                expectedRuntimeRegistration, expectedLifetime,
                expectedAuthority, sequence) ||
            !ReferenceEquals(expectedRuntimeOwner, runtimeOwner) ||
            !expectedRuntimeRegistration.Equals(runtimeRegistration) ||
            !expectedLifetime.Equals(lifetime) ||
            !expectedAuthority.Equals(authority))
        {
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                InvalidCredential;
            return false;
        }

        lock (gate)
        {
            if (credentialConsumed || state ==
                Switch2ProUsbOwnedCompositeInputAdoptionState.Adopted)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        CredentialAlreadyConsumed, out failure);
            }
            if (state ==
                Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        QuarantineRequired, out failure);
            }
            if (inputFacetRetired || inputFacet == null ||
                !inputFacet.IsAvailableForAdoption)
            {
                state =
                    Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined;
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        StaleCredential, out failure);
            }
            if (operationInProgress)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        OperationAlreadyInProgress, out failure);
            }
            if (state != Switch2ProUsbOwnedCompositeInputAdoptionState.
                    CredentialPublished)
            {
                return FailNoLock(
                    state ==
                        Switch2ProUsbOwnedCompositeInputAdoptionState.
                            Quarantined ?
                        Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                            QuarantineRequired :
                        Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                            InvalidState,
                    out failure);
            }
            operationInProgress = true;
        }

        ISwitch2ProUsbReadOnlyCompositeLease currentFacet = null;
        bool authenticated = false;
        bool dependencyThrew = false;
        try
        {
            authenticated = bundle.TryGetInputLease(authority,
                out currentFacet);
        }
        catch
        {
            dependencyThrew = true;
        }

        lock (gate)
        {
            operationInProgress = false;
            if (state ==
                Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        QuarantineRequired, out failure);
            }
            if (dependencyThrew || !authenticated ||
                !ReferenceEquals(currentFacet, compositeLease))
            {
                state = Switch2ProUsbOwnedCompositeInputAdoptionState.
                    Quarantined;
                return FailNoLock(dependencyThrew ?
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        DependencyThrew :
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        StaleCredential, out failure);
            }
            if (credentialConsumed || state !=
                Switch2ProUsbOwnedCompositeInputAdoptionState.
                    CredentialPublished)
            {
                return FailNoLock(
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        CredentialAlreadyConsumed, out failure);
            }

            credentialConsumed = true;
            state = Switch2ProUsbOwnedCompositeInputAdoptionState.Adopted;
            failure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.None;
            lastFailure = failure;
            return true;
        }
    }

    internal void Quarantine(
        Switch2ProUsbOwnedCompositeInputAdoptionFailure reason)
    {
        Switch2ProUsbOwnedCompositeInputFacetLease facet;
        lock (gate)
        {
            state = Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined;
            lastFailure = reason ==
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.None ?
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired : reason;
            facet = inputFacet;
        }
        facet?.RetainOrQuarantineWithoutIo();
    }

    internal bool TrySealInputFacetRetirementPhase(
        Switch2ProUsbOwnedCompositeInputFacetLease exactFacet,
        out Switch2ProUsbOwnedCompositeInputFacetRetirementKind kind)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inputFacet, exactFacet) ||
                retirementKind is not
                    (Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                        ConstructionRollback or
                    Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                        RuntimeRetirement))
            {
                state =
                    Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined;
                lastFailure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired;
                kind = default;
                return false;
            }
            retirementPhaseSealed = true;
            kind = retirementKind;
            return true;
        }
    }

    internal void NotifyInputFacetRetired(
        Switch2ProUsbOwnedCompositeInputFacetLease exactFacet,
        Switch2ProUsbOwnedCompositeInputFacetRetirementKind exactKind)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inputFacet, exactFacet) ||
                !retirementPhaseSealed || exactKind != retirementKind)
            {
                state =
                    Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined;
                lastFailure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired;
                return;
            }
            inputFacetRetired = true;
            retirementProof = new(this, exactFacet, authority, lifetime,
                sequence, exactKind);
        }
    }

    internal bool TryTakeInputFacetRetirementProof(
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime,
        out Switch2ProUsbOwnedCompositeInputFacetRetirementProof proof)
    {
        lock (gate)
        {
            if (retirementProofTaken ||
                !expectedAuthority.Equals(authority) ||
                !expectedLifetime.Equals(lifetime) ||
                !retirementProof.Authenticates(this, inputFacet, authority,
                    lifetime, sequence))
            {
                proof = default;
                return false;
            }
            retirementProofTaken = true;
            proof = retirementProof;
            return true;
        }
    }

    internal void NotifyInvalidInputFacetRetirement(
        Switch2ProUsbOwnedCompositeInputFacetLease exactFacet)
    {
        lock (gate)
        {
            if (ReferenceEquals(inputFacet, exactFacet))
            {
                state =
                    Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined;
                lastFailure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired;
            }
        }
    }

    internal void NotifyCallbackAfterInputFacetRetirement(
        Switch2ProUsbOwnedCompositeInputFacetLease exactFacet)
    {
        lock (gate)
        {
            if (ReferenceEquals(inputFacet, exactFacet))
            {
                state =
                    Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined;
                lastFailure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired;
            }
        }
    }

    internal void NotifyUncertainInputFacetQuiescence(
        Switch2ProUsbOwnedCompositeInputFacetLease exactFacet)
    {
        lock (gate)
        {
            if (ReferenceEquals(inputFacet, exactFacet))
            {
                state =
                    Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined;
                lastFailure = Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired;
            }
        }
    }

    private bool AuthenticatesRuntimeOwner(
        Switch2ProUsbRuntimeOwner owner,
        in InputControllerRegistration registration)
    {
        if (owner == null ||
            registration.Generation !=
                lifetime.SessionDescriptor.DeviceGeneration ||
            registration.OwnershipKind !=
                InputControllerOwnershipKind.Switch2Runtime ||
            !ReferenceEquals(registration.Owner, owner))
        {
            return false;
        }
        try
        {
            return registration.Equals(owner.Registration) &&
                owner.Authenticates(registration.Device,
                    registration.Generation);
        }
        catch
        {
            return false;
        }
    }

    private void SetLastFailure(
        Switch2ProUsbOwnedCompositeInputAdoptionFailure failure)
    {
        lock (gate)
        {
            lastFailure = failure;
        }
    }

    private bool FailNoLock(
        Switch2ProUsbOwnedCompositeInputAdoptionFailure failure)
    {
        lastFailure = failure;
        return false;
    }

    private bool FailNoLock(
        Switch2ProUsbOwnedCompositeInputAdoptionFailure reason,
        out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure)
    {
        failure = reason;
        lastFailure = reason;
        return false;
    }
}

internal enum Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind : byte
{
    None = 0,
    InputAuthorityRejected,
    RuntimeCreationRejected,
    CredentialPublicationRejected,
    DependencyThrew,
}

/// <summary>
/// Failure evidence retains the exact bundle and, after admission, the issuer
/// and any runtime candidate. No failure path guesses that the shared physical
/// composite was disposed or safe to reacquire.
/// </summary>
internal readonly struct Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure
{
    internal Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure(
        Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind kind,
        Switch2ProUsbOwnedCompositeInputAdoptionFailure adoptionFailure,
        in Switch2ProUsbRuntimeCreateFailure runtimeFailure,
        Switch2ProUsbOwnedCompositeLeaseBundle retainedBundle,
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer retainedIssuer,
        Switch2ProUsbRuntimeOwner retainedRuntimeOwner) : this(kind,
            adoptionFailure, runtimeFailure, retainedBundle, retainedIssuer,
            retainedRuntimeOwner, default)
    {
    }

    internal Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure(
        Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind kind,
        Switch2ProUsbOwnedCompositeInputAdoptionFailure adoptionFailure,
        in Switch2ProUsbRuntimeCreateFailure runtimeFailure,
        Switch2ProUsbOwnedCompositeLeaseBundle retainedBundle,
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer retainedIssuer,
        Switch2ProUsbRuntimeOwner retainedRuntimeOwner,
        in Switch2ProUsbOwnedCompositeInputFacetRetirementProof
            inputFacetRetirementProof)
    {
        Kind = kind;
        AdoptionFailure = adoptionFailure;
        RuntimeFailure = runtimeFailure;
        RetainedBundle = retainedBundle;
        RetainedIssuer = retainedIssuer;
        RetainedRuntimeOwner = retainedRuntimeOwner;
        InputFacetRetirementProof = inputFacetRetirementProof;
    }

    internal Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind Kind
    {
        get;
    }

    internal Switch2ProUsbOwnedCompositeInputAdoptionFailure AdoptionFailure
    {
        get;
    }

    internal Switch2ProUsbRuntimeCreateFailure RuntimeFailure { get; }

    internal Switch2ProUsbOwnedCompositeLeaseBundle RetainedBundle { get; }

    internal Switch2ProUsbOwnedCompositeInputAdoptionIssuer RetainedIssuer
    {
        get;
    }

    internal Switch2ProUsbRuntimeOwner RetainedRuntimeOwner { get; }

    internal Switch2ProUsbOwnedCompositeInputFacetRetirementProof
        InputFacetRetirementProof { get; }

    internal bool RequiresRetention => RetainedBundle != null;

    internal bool IsNone => Kind ==
        Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind.None;
}

/// <summary>
/// Dormant factory which derives all identity and clock facts from the admitted
/// lifetime. It publishes a credential only after the existing runtime owner
/// has accepted the mediated input facet. It performs no activation.
/// </summary>
internal static class Switch2ProUsbOwnedCompositeRuntimeAdoptionFactory
{
    internal static bool TryCreate(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2InputCalibrationSnapshot calibration,
        int readRetirementTimeoutMilliseconds,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
        out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure) =>
        TryCreateCore(bundle, authority, calibration,
            readRetirementTimeoutMilliseconds,
            Switch2ProUsbRuntimePumpFactory.Instance,
            Switch2ProUsbRuntimeTerminalScheduler.Instance,
            initialSequence: 0, out owner, out registration, out credential,
            out failure);

    internal static bool TryCreateCore(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2InputCalibrationSnapshot calibration,
        int readRetirementTimeoutMilliseconds,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler,
        ulong initialSequence,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
        out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure)
    {
        owner = null;
        registration = default;
        credential = default;

        if (pumpFactory == null || terminalScheduler == null)
        {
            failure = new Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure(
                Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind.
                    InputAuthorityRejected,
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    MissingDependency,
                default, bundle, null, null);
            return false;
        }

        if (!Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreateCore(
                bundle, authority, initialSequence,
                out Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer,
                out Switch2ProUsbOwnedCompositeInputAdoptionFailure
                    adoptionFailure))
        {
            failure = new Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure(
                Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind.
                    InputAuthorityRejected,
                adoptionFailure, default, bundle, null, null);
            return false;
        }

        Switch2PhysicalInputLifetime lifetime = issuer.Lifetime;
        Switch2ProUsbRuntimeOwner runtimeOwner = null;
        InputControllerRegistration runtimeRegistration = default;
        Switch2ProUsbRuntimeCreateFailure runtimeFailure = default;
        bool created;
        try
        {
            created = Switch2ProUsbRuntimeOwner.
                TryCreateOwnedCompositeCore(issuer, pumpFactory,
                    terminalScheduler, lifetime, calibration,
                    readRetirementTimeoutMilliseconds,
                    out runtimeOwner, out runtimeRegistration,
                    out runtimeFailure);
        }
        catch
        {
            created = false;
            adoptionFailure =
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    DependencyThrew;
        }

        if (!created || runtimeOwner == null)
        {
            if (adoptionFailure ==
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.None)
            {
                adoptionFailure = issuer.LastFailure ==
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.None ?
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        RuntimeCreationRejected : issuer.LastFailure;
            }
            Switch2ProUsbRuntimeOwner retainedRuntimeOwner =
                runtimeOwner ?? runtimeFailure.QuarantinedOwner ??
                issuer.BoundRuntimeOwner;
            retainedRuntimeOwner?.MarkOwnedCompositeCreationQuarantined();
            issuer.Quarantine(adoptionFailure);
            issuer.TryTakeInputFacetRetirementProof(authority, lifetime,
                out Switch2ProUsbOwnedCompositeInputFacetRetirementProof
                    retirementProof);
            failure = new Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure(
                adoptionFailure ==
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        DependencyThrew ?
                    Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind.
                        DependencyThrew :
                    Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind.
                        RuntimeCreationRejected,
                adoptionFailure, runtimeFailure, bundle, issuer,
                retainedRuntimeOwner,
                retirementProof);
            return false;
        }

        Switch2PhysicalInputLifetime runtimeLifetime;
        try
        {
            runtimeLifetime = runtimeOwner.TransportOwner.Lifetime;
        }
        catch
        {
            runtimeOwner.MarkOwnedCompositeCreationQuarantined();
            issuer.Quarantine(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    DependencyThrew);
            failure = new Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure(
                Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind.
                    DependencyThrew,
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    DependencyThrew,
                runtimeFailure, bundle, issuer, runtimeOwner);
            return false;
        }

        if (!issuer.TryPublishCredential(runtimeOwner, runtimeRegistration,
                runtimeLifetime, out credential, out adoptionFailure))
        {
            runtimeOwner.MarkOwnedCompositeCreationQuarantined();
            issuer.Quarantine(adoptionFailure);
            failure = new Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure(
                Switch2ProUsbOwnedCompositeRuntimeAdoptionFailureKind.
                    CredentialPublicationRejected,
                adoptionFailure, runtimeFailure, bundle, issuer,
                runtimeOwner);
            credential = default;
            return false;
        }

        owner = runtimeOwner;
        registration = runtimeRegistration;
        failure = default;
        return true;
    }
}

internal enum Switch2ProUsbOwnedFeedbackActivationState : byte
{
    Invalid = 0,
    Dormant,
    PrepareInProgress,
    Prepared,
    CommitInProgress,
    Committed,
    AbortInProgress,
    Aborted,
    NeutralizeInProgress,
    NeutralAndQuiescent,
    DisconnectedAndQuiescent,
    Quarantined,
    SequenceExhausted,
}

internal enum Switch2ProUsbOwnedFeedbackActivationOperation : byte
{
    Invalid = 0,
    Prepare,
    Commit,
    Abort,
}

internal enum Switch2ProUsbOwnedFeedbackActivationOutcome : byte
{
    Invalid = 0,
    Succeeded,
    ProvenRejected,
    OutcomeUncertain,
}

/// <summary>
/// Exact one-shot adoption proof for a dormant feedback lifetime. A valid
/// proof certifies that the exact authority/lifetime has never opened output,
/// has no queued or in-flight physical write, and is logically neutral. The
/// issuer must hand out this proof only once and must reject copied proof
/// consumption after the first prepare linearization point.
/// </summary>
internal readonly struct Switch2ProUsbOwnedFeedbackDormantQuiescenceProof
{
    private readonly object issuer;
    private readonly object fence;
    private readonly Switch2ProUsbOwnedCompositeAuthority authority;
    private readonly Switch2PhysicalInputLifetime lifetime;

    internal Switch2ProUsbOwnedFeedbackDormantQuiescenceProof(object issuer,
        object fence,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2PhysicalInputLifetime lifetime, ulong sequence)
    {
        this.issuer = issuer;
        this.fence = fence;
        this.authority = authority;
        this.lifetime = lifetime;
        Sequence = sequence;
    }

    internal ulong Sequence { get; }

    internal bool IsValid => issuer != null && fence != null &&
        authority.IsValid && lifetime.IsValid && Sequence != 0 &&
        authority.DeviceGeneration ==
            lifetime.SessionDescriptor.DeviceGeneration &&
        authority.TransportGeneration ==
            lifetime.SessionDescriptor.TransportGeneration;

    internal bool Authenticates(object expectedIssuer,
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime) => IsValid &&
        ReferenceEquals(issuer, expectedIssuer) &&
        authority.Equals(expectedAuthority) &&
        lifetime.Equals(expectedLifetime);

    internal bool Authenticates(object expectedIssuer, object expectedFence,
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime,
        ulong expectedSequence) =>
        Authenticates(expectedIssuer, expectedAuthority, expectedLifetime) &&
        ReferenceEquals(fence, expectedFence) && Sequence == expectedSequence;
}

/// <summary>
/// Opaque activation proof for the single dormant feedback pump/sink. The issuer
/// and fence are reference-bound; implementations must consume all copies on
/// the first exact commit or abort and must never recycle a sequence.
/// </summary>
internal readonly struct Switch2ProUsbOwnedFeedbackPrepareCredential :
    IEquatable<Switch2ProUsbOwnedFeedbackPrepareCredential>
{
    private readonly object issuer;
    private readonly object fence;
    private readonly Switch2ProUsbOwnedCompositeAuthority authority;
    private readonly Switch2PhysicalInputLifetime lifetime;

    internal Switch2ProUsbOwnedFeedbackPrepareCredential(object issuer,
        object fence,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2PhysicalInputLifetime lifetime, ulong sequence)
    {
        this.issuer = issuer;
        this.fence = fence;
        this.authority = authority;
        this.lifetime = lifetime;
        Sequence = sequence;
    }

    internal ulong Sequence { get; }

    internal ulong DeviceGeneration =>
        lifetime.SessionDescriptor.DeviceGeneration;

    internal ulong TransportGeneration =>
        lifetime.SessionDescriptor.TransportGeneration;

    internal bool IsValid => issuer != null && fence != null &&
        authority.IsValid && lifetime.IsValid && Sequence != 0 &&
        authority.DeviceGeneration == DeviceGeneration &&
        authority.TransportGeneration == TransportGeneration;

    internal bool Authenticates(object expectedIssuer, object expectedFence,
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority,
        in Switch2PhysicalInputLifetime expectedLifetime,
        ulong expectedSequence) => IsValid &&
        ReferenceEquals(issuer, expectedIssuer) &&
        ReferenceEquals(fence, expectedFence) &&
        authority.Equals(expectedAuthority) &&
        lifetime.Equals(expectedLifetime) && Sequence == expectedSequence;

    internal bool AuthenticatesAuthority(
        in Switch2ProUsbOwnedCompositeAuthority expectedAuthority) =>
        IsValid && authority.Equals(expectedAuthority);

    public bool Equals(Switch2ProUsbOwnedFeedbackPrepareCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) &&
        authority.Equals(other.authority) && lifetime.Equals(other.lifetime) &&
        Sequence == other.Sequence;

    public override bool Equals(object obj) => obj is
        Switch2ProUsbOwnedFeedbackPrepareCredential other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        fence == null ? 0 : RuntimeHelpers.GetHashCode(fence), authority,
        lifetime, Sequence);
}

/// <summary>
/// Exact result shape. ProvenRejected certifies that the named operation did
/// not cross its linearization point and an exact retry is allowed.
/// OutcomeUncertain forbids retry, input commit, or physical retirement and
/// requires retaining the full composite.
/// </summary>
internal readonly struct Switch2ProUsbOwnedFeedbackActivationResult
{
    private readonly Switch2ProUsbOwnedCompositeAuthority authority;

    private Switch2ProUsbOwnedFeedbackActivationResult(
        Switch2ProUsbOwnedFeedbackActivationOperation operation,
        Switch2ProUsbOwnedFeedbackActivationOutcome outcome,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2ProUsbOwnedFeedbackPrepareCredential credential)
    {
        this.authority = authority;
        Operation = operation;
        Outcome = outcome;
        DeviceGeneration = authority.DeviceGeneration;
        TransportGeneration = authority.TransportGeneration;
        Credential = credential;
    }

    internal Switch2ProUsbOwnedFeedbackActivationOperation Operation { get; }

    internal Switch2ProUsbOwnedFeedbackActivationOutcome Outcome { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal Switch2ProUsbOwnedFeedbackPrepareCredential Credential { get; }

    internal bool HasValidInvariants() => Operation is >=
            Switch2ProUsbOwnedFeedbackActivationOperation.Prepare and <=
            Switch2ProUsbOwnedFeedbackActivationOperation.Abort &&
        Outcome is >= Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded
            and <=
            Switch2ProUsbOwnedFeedbackActivationOutcome.OutcomeUncertain &&
        DeviceGeneration != 0 && TransportGeneration != 0 &&
        authority.IsValid &&
        (Operation == Switch2ProUsbOwnedFeedbackActivationOperation.Prepare &&
            Outcome ==
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded ?
            Credential.AuthenticatesAuthority(authority) :
            !Credential.IsValid);

    internal bool Authenticates(
        in Switch2ProUsbOwnedCompositeAuthority authority) =>
        HasValidInvariants() && authority.IsValid &&
        this.authority.Equals(authority) &&
        DeviceGeneration == authority.DeviceGeneration &&
        TransportGeneration == authority.TransportGeneration;

    internal static Switch2ProUsbOwnedFeedbackActivationResult
        Prepared(
            in Switch2ProUsbOwnedCompositeAuthority authority,
            in Switch2ProUsbOwnedFeedbackPrepareCredential credential) =>
        new(Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            authority, credential);

    internal static Switch2ProUsbOwnedFeedbackActivationResult Succeeded(
        Switch2ProUsbOwnedFeedbackActivationOperation operation,
        in Switch2ProUsbOwnedCompositeAuthority authority) =>
        operation is Switch2ProUsbOwnedFeedbackActivationOperation.Commit or
            Switch2ProUsbOwnedFeedbackActivationOperation.Abort ?
            new(operation,
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
                authority, default) : default;

    internal static Switch2ProUsbOwnedFeedbackActivationResult Rejected(
        Switch2ProUsbOwnedFeedbackActivationOperation operation,
        in Switch2ProUsbOwnedCompositeAuthority authority) => new(operation,
            Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
            authority, default);

    internal static Switch2ProUsbOwnedFeedbackActivationResult Uncertain(
        Switch2ProUsbOwnedFeedbackActivationOperation operation,
        in Switch2ProUsbOwnedCompositeAuthority authority) => new(operation,
            Switch2ProUsbOwnedFeedbackActivationOutcome.OutcomeUncertain,
            authority, default);
}

/// <summary>
/// State/credential contract for the one dormant feedback pump/sink. Prepare
/// must leave physical output sealed. Commit opens that exact prepared lifetime;
/// abort proves it remained sealed and is quiescent. Timeout values bound
/// cumulative managed native-quiescence waits; synchronous native begin/cancel/
/// release calls have no hard wall-clock bound. Dependencies run outside private
/// gates, and results distinguish proven pre-linearization rejection from
/// uncertainty.
/// TryTakeDormantQuiescenceProof atomically adopts an exact Dormant lifetime:
/// success certifies neutral output, no queue or write in flight, and that no
/// other coordinator can acquire or activate it. Prepare must authenticate that
/// exact proof and consume all copied proofs at its linearization point (or
/// quarantine them when the outcome is uncertain). The take operation is a
/// nonblocking, process-local authority transfer: it performs no hardware I/O,
/// callback, worker, or report publication. A caller must retain this exact
/// lifetime once the take is attempted because an exception can occur after an
/// implementation has internally adopted the lifetime.
///
/// The one dormant internal implementation composes the existing canonical
/// pump/sink/writer/owned bridge and is manually driven. No production runtime
/// constructs it; it creates no worker, timer, cadence, registration, or second
/// mapping queue.
/// </summary>
internal interface ISwitch2ProUsbOwnedFeedbackActivationLifetime :
    ISwitch2ProUsbOwnedFeedbackLifetime
{
    Switch2ProUsbOwnedFeedbackActivationState ActivationState { get; }

    bool TryTakeDormantQuiescenceProof(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        out Switch2ProUsbOwnedFeedbackDormantQuiescenceProof proof);

    Switch2ProUsbOwnedFeedbackActivationResult TryPrepareActivation(
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2ProUsbOwnedFeedbackDormantQuiescenceProof dormantProof,
        int timeoutMilliseconds);

    Switch2ProUsbOwnedFeedbackActivationResult TryCommitPrepared(
        in Switch2ProUsbOwnedFeedbackPrepareCredential credential,
        int timeoutMilliseconds);

    Switch2ProUsbOwnedFeedbackActivationResult TryAbortPrepared(
        in Switch2ProUsbOwnedFeedbackPrepareCredential credential,
        int timeoutMilliseconds);
}
