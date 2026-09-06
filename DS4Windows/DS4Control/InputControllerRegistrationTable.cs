/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DS4Windows;

public enum InputControllerSlotState : byte
{
    Empty = 0,
    Reserved,
    Bound,
    Attached,
    Retiring,
    Quiesced,
    Removed,
    Quarantined,
}

public enum InputControllerSlotTableFailure : byte
{
    None = 0,
    InvalidArgument,
    InvalidServiceGeneration,
    ServiceGenerationNotMonotonic,
    ServiceGenerationExhausted,
    AlreadyOpen,
    Closed,
    Busy,
    InvalidRegistration,
    OwnerAuthenticationFailed,
    Full,
    DuplicateRegistration,
    SlotGenerationExhausted,
    LeaseGenerationExhausted,
    StaleCredential,
    WrongState,
    WrongSender,
    WrongLeaseKind,
    AlreadyAcknowledged,
    TerminalNeutralRequired,
    TimedOut,
    Quarantined,
    ReportLeaseLimit,
    ActivationCommitRejected,
}

public enum InputControllerSlotQuarantineReason : byte
{
    None = 0,
    OwnerAuthenticationLost,
    StopRejected,
    StopTimedOut,
    TerminalNeutralNotObserved,
    DrainTimedOut,
    RemoveRejected,
    OwnerThrew,
    ExternalLifecycleFailure,
}

/// <summary>
/// Exact, table-issued identity of one slot lifetime. Public values are
/// diagnostic only: validation also requires the private issuing-table
/// identity, which prevents replay against another table with colliding
/// service and slot generations.
/// </summary>
public readonly struct InputControllerSlotToken :
    IEquatable<InputControllerSlotToken>
{
    private readonly object issuer;

    internal InputControllerSlotToken(object issuer, int slot,
        ulong serviceGeneration, ulong slotGeneration,
        InputControllerRegistration registration)
    {
        this.issuer = issuer;
        Slot = slot;
        ServiceGeneration = serviceGeneration;
        SlotGeneration = slotGeneration;
        Registration = registration;
    }

    public int Slot { get; }

    public ulong ServiceGeneration { get; }

    public ulong SlotGeneration { get; }

    public InputControllerRegistration Registration { get; }

    public bool IsValid => issuer != null && Slot >= 0 &&
        ServiceGeneration != 0 && SlotGeneration != 0 &&
        Registration.Device != null && Registration.Generation != 0 &&
        Registration.Owner != null;

    internal object Issuer => issuer;

    public bool Equals(InputControllerSlotToken other) =>
        ReferenceEquals(issuer, other.issuer) && Slot == other.Slot &&
        ServiceGeneration == other.ServiceGeneration &&
        SlotGeneration == other.SlotGeneration &&
        Registration == other.Registration;

    public override bool Equals(object obj) =>
        obj is InputControllerSlotToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer), Slot,
        ServiceGeneration, SlotGeneration, Registration);

    public static bool operator ==(InputControllerSlotToken left,
        InputControllerSlotToken right) => left.Equals(right);

    public static bool operator !=(InputControllerSlotToken left,
        InputControllerSlotToken right) => !left.Equals(right);
}

/// <summary>
/// Revocable setup claim. Binding produces the exact token before external
/// setup, while report admission remains closed until explicit activation.
/// </summary>
public readonly struct InputControllerReservation :
    IEquatable<InputControllerReservation>
{
    internal InputControllerReservation(InputControllerSlotToken token)
    {
        Token = token;
    }

    public InputControllerSlotToken Token { get; }

    /// <summary>
    /// Exact cleanup credential retained by the setup owner. The table accepts
    /// it only after this reservation reaches Bound.
    /// </summary>
    public InputControllerSetupRollbackClaim SetupRollbackClaim =>
        new(Token);

    public bool IsValid => Token.IsValid;

    public bool Equals(InputControllerReservation other) =>
        Token == other.Token;

    public override bool Equals(object obj) =>
        obj is InputControllerReservation other && Equals(other);

    public override int GetHashCode() => Token.GetHashCode();

    public static bool operator ==(InputControllerReservation left,
        InputControllerReservation right) => left.Equals(right);

    public static bool operator !=(InputControllerReservation left,
        InputControllerReservation right) => !left.Equals(right);
}

public readonly struct InputControllerRetirementClaim :
    IEquatable<InputControllerRetirementClaim>
{
    internal InputControllerRetirementClaim(InputControllerSlotToken token)
    {
        Token = token;
    }

    public InputControllerSlotToken Token { get; }

    public bool IsValid => Token.IsValid;

    public bool Equals(InputControllerRetirementClaim other) =>
        Token == other.Token;

    public override bool Equals(object obj) =>
        obj is InputControllerRetirementClaim other && Equals(other);

    public override int GetHashCode() => Token.GetHashCode();

    public static bool operator ==(InputControllerRetirementClaim left,
        InputControllerRetirementClaim right) => left.Equals(right);

    public static bool operator !=(InputControllerRetirementClaim left,
        InputControllerRetirementClaim right) => !left.Equals(right);
}

/// <summary>
/// Exact cleanup claim for a bound setup that never became active. It keeps
/// the setup observable across service close so its exact external delegate can
/// be unsubscribed before the slot is made reusable.
/// </summary>
public readonly struct InputControllerSetupRollbackClaim :
    IEquatable<InputControllerSetupRollbackClaim>
{
    internal InputControllerSetupRollbackClaim(InputControllerSlotToken token)
    {
        Token = token;
    }

    public InputControllerSlotToken Token { get; }

    public bool IsValid => Token.IsValid;

    public bool Equals(InputControllerSetupRollbackClaim other) =>
        Token == other.Token;

    public override bool Equals(object obj) =>
        obj is InputControllerSetupRollbackClaim other && Equals(other);

    public override int GetHashCode() => Token.GetHashCode();

    public static bool operator ==(InputControllerSetupRollbackClaim left,
        InputControllerSetupRollbackClaim right) => left.Equals(right);

    public static bool operator !=(InputControllerSetupRollbackClaim left,
        InputControllerSetupRollbackClaim right) => !left.Equals(right);
}

/// <summary>
/// Ephemeral proof that one exact table has made one exact slot Attached and
/// fenced one non-blocking external activation commit. The external commit
/// runs without the table gate held. Copies expire when the exact activation
/// is completed or invalidated and cannot authorize a later or foreign
/// lifetime.
/// </summary>
public readonly struct InputControllerActivationClaim
{
    private readonly InputControllerRegistrationTable table;
    private readonly object fence;

    internal InputControllerActivationClaim(
        InputControllerRegistrationTable table, object fence,
        InputControllerSlotToken token)
    {
        this.table = table;
        this.fence = fence;
        Token = token;
    }

    public InputControllerSlotToken Token { get; }

    internal object Fence => fence;

    public bool IsValid => table != null && fence != null && Token.IsValid;

    internal bool Authenticates(in InputControllerSlotToken expectedToken) =>
        table != null && table.AuthenticatesActivation(this, fence,
            expectedToken);
}

/// <summary>
/// Single-acquisition capability for the one external commit authorized by an
/// activation claim. Unlike the copyable pre-commit claim, acquiring this
/// credential atomically closes claim completion and duplicate commit
/// acquisition for that exact table/slot epoch.
/// </summary>
internal readonly struct InputControllerActivationCommitCredential
{
    private readonly InputControllerRegistrationTable table;
    private readonly object fence;

    internal InputControllerActivationCommitCredential(
        InputControllerRegistrationTable table, object fence,
        InputControllerSlotToken token)
    {
        this.table = table;
        this.fence = fence;
        Token = token;
    }

    internal InputControllerSlotToken Token { get; }

    internal object Fence => fence;

    internal bool IsValid => table != null && fence != null && Token.IsValid;

    internal bool Authenticates(in InputControllerSlotToken expectedToken) =>
        table != null && table.AuthenticatesActivationCommit(this, fence,
            expectedToken);
}

public readonly struct InputControllerSlotSnapshot
{
    internal InputControllerSlotSnapshot(int slot,
        InputControllerSlotState state, ulong serviceGeneration,
        ulong slotGeneration, InputControllerSlotToken token,
        int activeReportLeases, bool actionPending, bool actionActive,
        bool terminalNeutralAcknowledged, bool activationPending,
        InputControllerSlotQuarantineReason quarantineReason)
    {
        Slot = slot;
        State = state;
        ServiceGeneration = serviceGeneration;
        SlotGeneration = slotGeneration;
        Token = token;
        ActiveReportLeases = activeReportLeases;
        ActionPending = actionPending;
        ActionActive = actionActive;
        TerminalNeutralAcknowledged = terminalNeutralAcknowledged;
        ActivationPending = activationPending;
        QuarantineReason = quarantineReason;
    }

    public int Slot { get; }

    public InputControllerSlotState State { get; }

    public ulong ServiceGeneration { get; }

    public ulong SlotGeneration { get; }

    public InputControllerSlotToken Token { get; }

    public InputControllerRetirementClaim RetirementClaim =>
        State is InputControllerSlotState.Retiring or
            InputControllerSlotState.Quiesced or
            InputControllerSlotState.Quarantined ?
        new InputControllerRetirementClaim(Token) : default;

    public InputControllerSetupRollbackClaim SetupRollbackClaim =>
        State == InputControllerSlotState.Bound ?
        new InputControllerSetupRollbackClaim(Token) : default;

    public int ActiveReportLeases { get; }

    public bool ActionPending { get; }

    public bool ActionActive { get; }

    public bool TerminalNeutralAcknowledged { get; }

    /// <summary>
    /// True only while the exact external activation commit is outside the
    /// table lock. Regular reports may be admitted in this state; actions,
    /// terminal reports, retirement, and close remain fenced.
    /// </summary>
    public bool ActivationPending { get; }

    public InputControllerSlotQuarantineReason QuarantineReason { get; }
}

/// <summary>
/// Allocation-free report admission lease. Struct copies are safe: release is
/// authenticated by the exact table, token, lease cell, and lease generation.
/// A stale copy therefore cannot decrement twice or release a later lease that
/// reused the same cell.
/// </summary>
public struct InputControllerReportLease : IDisposable
{
    private InputControllerRegistrationTable table;
    private readonly InputControllerSlotToken token;
    private readonly int leaseIndex;
    private readonly ulong leaseGeneration;
    private readonly bool terminal;

    internal InputControllerReportLease(
        InputControllerRegistrationTable table,
        InputControllerSlotToken token, int leaseIndex,
        ulong leaseGeneration, bool terminal)
    {
        this.table = table;
        this.token = token;
        this.leaseIndex = leaseIndex;
        this.leaseGeneration = leaseGeneration;
        this.terminal = terminal;
    }

    public bool IsValid => Volatile.Read(ref table) != null &&
        leaseGeneration != 0;

    public bool IsTerminal => IsValid && terminal;

    public bool TryAcknowledgeTerminalNeutral(
        out InputControllerSlotTableFailure failure)
    {
        InputControllerRegistrationTable current = Volatile.Read(ref table);
        if (current == null || leaseGeneration == 0)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }

        return current.TryAcknowledgeTerminalNeutral(token, leaseIndex,
            leaseGeneration, terminal, out failure);
    }

    public void Dispose()
    {
        InputControllerRegistrationTable current = Interlocked.Exchange(
            ref table, null);
        current?.ReleaseReportLease(token, leaseIndex, leaseGeneration);
    }
}

/// <summary>
/// Exclusive action admission lease. Acquiring one closes report admission and
/// boundedly drains all existing report leases. The table never invokes the
/// external action; callers execute it only after acquisition.
/// </summary>
public struct InputControllerActionLease : IDisposable
{
    private InputControllerRegistrationTable table;
    private readonly InputControllerSlotToken token;
    private readonly ulong leaseGeneration;

    internal InputControllerActionLease(
        InputControllerRegistrationTable table,
        InputControllerSlotToken token, ulong leaseGeneration)
    {
        this.table = table;
        this.token = token;
        this.leaseGeneration = leaseGeneration;
    }

    public bool IsValid => Volatile.Read(ref table) != null &&
        leaseGeneration != 0;

    public void Dispose()
    {
        InputControllerRegistrationTable current = Interlocked.Exchange(
            ref table, null);
        current?.ReleaseActionLease(token, leaseGeneration);
    }
}

/// <summary>
/// Standalone transactional registry for controller-to-service slot ownership.
/// It owns no HID, transport, profile, output, or event objects. All external
/// owner and event operations are deliberately performed by the future caller,
/// outside this table's lock, using the exact claims returned here.
/// </summary>
public sealed class InputControllerRegistrationTable
{
    public const int MaximumSlotCount = 64;

    private const int ReportLeaseCellCount = 64;
    private readonly object gate = new();
    private readonly object issuer = new();
    private readonly Entry[] entries;
    private bool open;
    private ulong currentServiceGeneration;
    private ulong lastServiceGeneration;
    private ulong lastSlotGeneration;

    public InputControllerRegistrationTable(int slotCount)
    {
        if (slotCount < 1 || slotCount > MaximumSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }

        entries = new Entry[slotCount];
        for (int index = 0; index < entries.Length; index++)
        {
            entries[index] = new Entry();
        }
    }

    internal InputControllerRegistrationTable(int slotCount,
        ulong lastServiceGeneration, ulong lastSlotGeneration) :
        this(slotCount)
    {
        this.lastServiceGeneration = lastServiceGeneration;
        this.lastSlotGeneration = lastSlotGeneration;
    }

    public int SlotCount => entries.Length;

    public bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return open;
            }
        }
    }

    public ulong CurrentServiceGeneration
    {
        get
        {
            lock (gate)
            {
                return open ? currentServiceGeneration : 0;
            }
        }
    }

    /// <summary>
    /// Atomically verifies the exact open service lifetime. This is the sole
    /// observation seam used by transaction participants which adopt a table
    /// generation opened by the ControlService-wide owner.
    /// </summary>
    public bool IsOpenForServiceGeneration(ulong exactServiceGeneration)
    {
        if (exactServiceGeneration == 0)
        {
            return false;
        }

        lock (gate)
        {
            return open && currentServiceGeneration == exactServiceGeneration;
        }
    }

    /// <summary>
    /// Opens a strictly newer service lifetime. Drained quarantined slots remain
    /// isolated and do not consume healthy slots; an undrained old-service
    /// callback still blocks reopen until it releases its exact lease.
    /// </summary>
    public bool TryOpen(ulong serviceGeneration,
        out InputControllerSlotTableFailure failure)
    {
        if (serviceGeneration == 0)
        {
            failure = InputControllerSlotTableFailure.
                InvalidServiceGeneration;
            return false;
        }

        lock (gate)
        {
            if (open)
            {
                failure = InputControllerSlotTableFailure.AlreadyOpen;
                return false;
            }
            if (lastServiceGeneration == ulong.MaxValue)
            {
                failure = InputControllerSlotTableFailure.
                    ServiceGenerationExhausted;
                return false;
            }
            if (serviceGeneration <= lastServiceGeneration)
            {
                failure = InputControllerSlotTableFailure.
                    ServiceGenerationNotMonotonic;
                return false;
            }

            foreach (Entry entry in entries)
            {
                if (entry.State == InputControllerSlotState.Quarantined &&
                    !IsDrained(entry))
                {
                    failure = InputControllerSlotTableFailure.Busy;
                    return false;
                }
                if (entry.State is not (InputControllerSlotState.Empty or
                    InputControllerSlotState.Removed or
                    InputControllerSlotState.Quarantined))
                {
                    failure = InputControllerSlotTableFailure.Busy;
                    return false;
                }
            }

            foreach (Entry entry in entries)
            {
                if (entry.State == InputControllerSlotState.Removed)
                {
                    entry.ResetToEmpty();
                }
            }

            open = true;
            currentServiceGeneration = serviceGeneration;
            lastServiceGeneration = serviceGeneration;
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Atomically closes admission, revokes raw reservations, preserves bound
    /// setups until their exact cleanup owners roll them back, and retires every
    /// attached slot. Returned snapshots contain exact setup/retirement claims.
    /// </summary>
    public bool TryClose(ulong serviceGeneration,
        out InputControllerSlotSnapshot[] snapshots,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!open || currentServiceGeneration != serviceGeneration)
            {
                snapshots = Array.Empty<InputControllerSlotSnapshot>();
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }

            foreach (Entry entry in entries)
            {
                if (entry.ActivationPending)
                {
                    snapshots = Array.Empty<InputControllerSlotSnapshot>();
                    failure = InputControllerSlotTableFailure.Busy;
                    return false;
                }
            }

            open = false;
            currentServiceGeneration = 0;
            foreach (Entry entry in entries)
            {
                if (entry.State == InputControllerSlotState.Reserved)
                {
                    entry.MarkRemoved();
                }
                else if (entry.State == InputControllerSlotState.Attached)
                {
                    entry.State = InputControllerSlotState.Retiring;
                }
            }

            Monitor.PulseAll(gate);
            snapshots = CreateSnapshotsLocked();
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    public bool TryReserve(InputControllerRegistration registration,
        out InputControllerReservation reservation,
        out InputControllerSlotTableFailure failure)
    {
        reservation = default;
        if (!registration.IsValid)
        {
            failure = InputControllerSlotTableFailure.InvalidRegistration;
            return false;
        }
        if (!registration.IsOwnerAuthenticated)
        {
            failure = InputControllerSlotTableFailure.
                OwnerAuthenticationFailed;
            return false;
        }

        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (ContainsDuplicateLocked(registration))
            {
                failure = InputControllerSlotTableFailure.
                    DuplicateRegistration;
                return false;
            }

            int slot = FindReusableSlotLocked();
            if (slot < 0)
            {
                failure = InputControllerSlotTableFailure.Full;
                return false;
            }
            if (!TryAdvance(ref lastSlotGeneration,
                out ulong slotGeneration))
            {
                failure = InputControllerSlotTableFailure.
                    SlotGenerationExhausted;
                return false;
            }

            Entry entry = entries[slot];
            entry.Reserve(currentServiceGeneration, slotGeneration,
                registration);
            var token = new InputControllerSlotToken(issuer, slot,
                currentServiceGeneration, slotGeneration, registration);
            reservation = new InputControllerReservation(token);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Atomically reserves and binds one registration. No observable raw
    /// reservation is published: a successful caller receives the exact bound
    /// token and rollback claim, while a failed caller receives no ownership
    /// credential and must not mutate the supplied owner lifetime.
    /// </summary>
    public bool TryReserveAndBind(InputControllerRegistration registration,
        out InputControllerSlotToken token,
        out InputControllerSetupRollbackClaim rollbackClaim,
        out InputControllerSlotTableFailure failure)
    {
        token = default;
        rollbackClaim = default;
        if (!registration.IsValid)
        {
            failure = InputControllerSlotTableFailure.InvalidRegistration;
            return false;
        }
        if (!registration.IsOwnerAuthenticated)
        {
            failure = InputControllerSlotTableFailure.
                OwnerAuthenticationFailed;
            return false;
        }

        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (ContainsDuplicateLocked(registration))
            {
                failure = InputControllerSlotTableFailure.
                    DuplicateRegistration;
                return false;
            }

            int slot = FindReusableSlotLocked();
            if (slot < 0)
            {
                failure = InputControllerSlotTableFailure.Full;
                return false;
            }
            if (!TryAdvance(ref lastSlotGeneration,
                    out ulong slotGeneration))
            {
                failure = InputControllerSlotTableFailure.
                    SlotGenerationExhausted;
                return false;
            }

            Entry entry = entries[slot];
            entry.Reserve(currentServiceGeneration, slotGeneration,
                registration);
            entry.State = InputControllerSlotState.Bound;
            token = new InputControllerSlotToken(issuer, slot,
                currentServiceGeneration, slotGeneration, registration);
            rollbackClaim = new InputControllerSetupRollbackClaim(token);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Atomically reserves and binds only <paramref name="exactSlot"/>. This
    /// method never falls back to another slot. It exists for a caller which
    /// already owns the external slot-array gate and must make that exact
    /// external vacancy and this table's ownership one transaction. The table
    /// does not inspect or retain the caller's external state.
    /// </summary>
    internal bool TryReserveAndBindExactSlot(int exactSlot,
        InputControllerRegistration registration,
        out InputControllerSlotToken token,
        out InputControllerSetupRollbackClaim rollbackClaim,
        out InputControllerSlotTableFailure failure)
    {
        token = default;
        rollbackClaim = default;
        if (exactSlot < 0 || exactSlot >= entries.Length)
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }
        if (!registration.IsValid)
        {
            failure = InputControllerSlotTableFailure.InvalidRegistration;
            return false;
        }
        if (!registration.IsOwnerAuthenticated)
        {
            failure = InputControllerSlotTableFailure.
                OwnerAuthenticationFailed;
            return false;
        }

        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (ContainsDuplicateLocked(registration))
            {
                failure = InputControllerSlotTableFailure.
                    DuplicateRegistration;
                return false;
            }

            Entry entry = entries[exactSlot];
            if (entry.State is not (InputControllerSlotState.Empty or
                    InputControllerSlotState.Removed))
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }
            if (!TryAdvance(ref lastSlotGeneration,
                    out ulong slotGeneration))
            {
                failure = InputControllerSlotTableFailure.
                    SlotGenerationExhausted;
                return false;
            }

            entry.Reserve(currentServiceGeneration, slotGeneration,
                registration);
            entry.State = InputControllerSlotState.Bound;
            token = new InputControllerSlotToken(issuer, exactSlot,
                currentServiceGeneration, slotGeneration, registration);
            rollbackClaim = new InputControllerSetupRollbackClaim(token);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Binds a reservation to its exact immutable token. The entry remains
    /// unable to admit reports or actions until TryActivate succeeds. The setup
    /// owner must retain reservation.SetupRollbackClaim and, after any failed
    /// activation, tear down its exact external delegate before rollback.
    /// </summary>
    public bool TryBind(InputControllerReservation reservation,
        out InputControllerSlotToken token,
        out InputControllerSlotTableFailure failure)
    {
        token = default;
        if (!reservation.IsValid)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }

        InputControllerRegistration registration =
            reservation.Token.Registration;
        if (!registration.IsOwnerAuthenticated)
        {
            failure = InputControllerSlotTableFailure.
                OwnerAuthenticationFailed;
            return false;
        }

        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (!TryGetExactEntryLocked(reservation.Token,
                out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != InputControllerSlotState.Reserved)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (entry.ServiceGeneration != currentServiceGeneration)
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }

            entry.State = InputControllerSlotState.Bound;
            token = reservation.Token;
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Publishes a bound entry to report/action admission only after all
    /// external setup succeeded. A close racing activation always wins closed.
    /// </summary>
    public bool TryActivate(InputControllerSlotToken token,
        out InputControllerSlotTableFailure failure)
    {
        if (!token.IsValid)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }
        if (!token.Registration.IsOwnerAuthenticated)
        {
            failure = InputControllerSlotTableFailure.
                OwnerAuthenticationFailed;
            return false;
        }

        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != InputControllerSlotState.Bound)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (entry.ServiceGeneration != currentServiceGeneration)
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }

            entry.State = InputControllerSlotState.Attached;
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Begins the narrow two-phase external activation transaction. The slot
    /// is Attached before the claim escapes so reports from a successfully
    /// committed owner are admissible, while close and retirement fail Busy
    /// until the same live claim completes. No external code runs under the
    /// table gate.
    /// </summary>
    public bool TryBeginActivate(InputControllerSlotToken token,
        out InputControllerActivationClaim claim,
        out InputControllerSlotTableFailure failure)
    {
        claim = default;
        if (!token.IsValid)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }
        if (!token.Registration.IsOwnerAuthenticated)
        {
            failure = InputControllerSlotTableFailure.
                OwnerAuthenticationFailed;
            return false;
        }

        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != InputControllerSlotState.Bound)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (entry.ServiceGeneration != currentServiceGeneration)
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }

            entry.State = InputControllerSlotState.Attached;
            object fence = new();
            entry.ActivationPending = true;
            entry.ActivationFence = fence;
            entry.ActivationCommitOwned = false;
            entry.ActivationCommitFence = null;
            claim = new InputControllerActivationClaim(this, fence, token);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Atomically consumes the pre-commit authority of one exact activation
    /// claim. Only the returned credential can complete that external commit;
    /// copied activation claims and duplicate acquisitions fail closed.
    /// </summary>
    internal bool TryAcquireActivationCommit(
        InputControllerActivationClaim claim,
        out InputControllerActivationCommitCredential credential,
        out InputControllerSlotTableFailure failure)
    {
        credential = default;
        if (!claim.IsValid)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }
        lock (gate)
        {
            if (!TryGetExactEntryLocked(claim.Token, out Entry entry) ||
                !entry.ActivationPending ||
                entry.State != InputControllerSlotState.Attached ||
                !ReferenceEquals(entry.ActivationFence, claim.Fence))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.ActivationCommitOwned)
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }

            object fence = new();
            entry.ActivationCommitOwned = true;
            entry.ActivationCommitFence = fence;
            credential = new InputControllerActivationCommitCredential(this,
                fence, claim.Token);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Aborts the exact activation epoch before its single-acquisition commit
    /// capability is issued. This claim is intentionally failure-only: a
    /// caller cannot assert external commit success without first acquiring
    /// and presenting <see cref="InputControllerActivationCommitCredential"/>.
    /// Rejection or misuse permanently quarantines the slot because regular
    /// report admission was already visible while activation was pending.
    /// </summary>
    public bool TryCompleteActivate(InputControllerActivationClaim claim,
        bool externalCommitSucceeded,
        out InputControllerSlotTableFailure failure)
    {
        if (!claim.IsValid)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }
        lock (gate)
        {
            if (!TryGetExactEntryLocked(claim.Token, out Entry entry) ||
                !entry.ActivationPending ||
                entry.State != InputControllerSlotState.Attached ||
                entry.ActivationCommitOwned ||
                !ReferenceEquals(entry.ActivationFence, claim.Fence))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }

            if (externalCommitSucceeded)
            {
                entry.ActivationPending = false;
                entry.ActivationFence = null;
                entry.ActivationCommitOwned = false;
                entry.ActivationCommitFence = null;
                entry.State = InputControllerSlotState.Quarantined;
                entry.QuarantineReason = InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure;
                Monitor.PulseAll(gate);
                failure = InputControllerSlotTableFailure.
                    ActivationCommitRejected;
                return false;
            }

            entry.ActivationPending = false;
            entry.ActivationFence = null;
            entry.ActivationCommitOwned = false;
            entry.ActivationCommitFence = null;
            entry.State = InputControllerSlotState.Quarantined;
            entry.QuarantineReason = InputControllerSlotQuarantineReason.
                ExternalLifecycleFailure;
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.
                ActivationCommitRejected;
            return false;
        }
    }

    /// <summary>
    /// Completes the exact single-acquisition external commit. Rejection or an
    /// uncertain result quarantines the slot; the table never restores Bound
    /// after reports became admissible.
    /// </summary>
    internal bool TryCompleteActivate(
        InputControllerActivationCommitCredential credential,
        bool externalCommitSucceeded,
        out InputControllerSlotTableFailure failure)
    {
        if (!credential.IsValid)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }
        lock (gate)
        {
            if (!TryGetExactEntryLocked(credential.Token, out Entry entry) ||
                !entry.ActivationPending ||
                entry.State != InputControllerSlotState.Attached ||
                !entry.ActivationCommitOwned ||
                !ReferenceEquals(entry.ActivationCommitFence,
                    credential.Fence))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }

            entry.ActivationPending = false;
            entry.ActivationFence = null;
            entry.ActivationCommitOwned = false;
            entry.ActivationCommitFence = null;
            if (!externalCommitSucceeded)
            {
                entry.State = InputControllerSlotState.Quarantined;
                entry.QuarantineReason = InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure;
                Monitor.PulseAll(gate);
                failure = InputControllerSlotTableFailure.
                    ActivationCommitRejected;
                return false;
            }

            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Cancels only a raw reservation, before binding grants an external setup
    /// owner. Every bound entry must use its explicit rollback claim instead.
    /// </summary>
    public bool TryCancel(InputControllerReservation reservation,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!TryGetExactEntryLocked(reservation.Token,
                out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != InputControllerSlotState.Reserved)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            entry.MarkRemoved();
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Completes cleanup of a bound setup after the caller has unsubscribed the
    /// exact delegate associated with this claim. It remains valid after close.
    /// </summary>
    public bool TryRollback(InputControllerSetupRollbackClaim claim,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!TryGetExactEntryLocked(claim.Token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State != InputControllerSlotState.Bound)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            entry.MarkRemoved();
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    public bool TryAcquireReportLease(InputControllerSlotToken token,
        DS4Device sender, out InputControllerReportLease lease,
        out InputControllerSlotTableFailure failure) =>
        TryAcquireReportLease(token, sender, terminal: false,
            out lease, out failure);

    /// <summary>
    /// Admits exactly one explicit terminal-neutral report after retirement.
    /// The caller must acknowledge that the neutral report reached the mapping
    /// callback before disposing the lease. The table does not infer this from
    /// EventArgs or mutable device state; a typed integration envelope remains
    /// a prerequisite. Ordinary reports are never admitted while retiring.
    /// </summary>
    public bool TryAcquireTerminalReportLease(
        InputControllerRetirementClaim claim, DS4Device sender,
        out InputControllerReportLease lease,
        out InputControllerSlotTableFailure failure) =>
        TryAcquireReportLease(claim.Token, sender, terminal: true,
            out lease, out failure);

    /// <summary>
    /// Captures the exact attached lifetime for a cold action before preparation.
    /// This is not a lease: acquisition must revalidate the returned token after
    /// preparation. No lifecycle lock, external owner callback or snapshot array
    /// is needed while holding the table gate.
    /// </summary>
    internal bool TryCaptureAttachedToken(int slot, DS4Device sender,
        out InputControllerSlotToken token,
        out InputControllerSlotTableFailure failure)
    {
        token = default;
        if ((uint)slot >= entries.Length || sender == null)
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            Entry entry = entries[slot];
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State != InputControllerSlotState.Attached ||
                entry.ServiceGeneration != currentServiceGeneration)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (entry.ActivationPending)
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }
            if (!ReferenceEquals(entry.Registration.Device, sender))
            {
                failure = InputControllerSlotTableFailure.WrongSender;
                return false;
            }
            token = new InputControllerSlotToken(issuer, slot,
                entry.ServiceGeneration, entry.SlotGeneration, entry.Registration);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    public bool TryAcquireActionLease(InputControllerSlotToken token,
        int timeoutMilliseconds, out InputControllerActionLease lease,
        out InputControllerSlotTableFailure failure)
    {
        lease = default;
        if (!IsValidTimeout(timeoutMilliseconds))
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State != InputControllerSlotState.Attached)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (entry.ActivationPending)
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }
            if (entry.ActionPending || entry.ActionActive)
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }

            entry.ActionPending = true;
            Monitor.PulseAll(gate);
            while (entry.ActiveReportLeaseCount != 0)
            {
                if (!open || !TryGetExactEntryLocked(token,
                    out Entry current) || !ReferenceEquals(entry, current) ||
                    entry.State != InputControllerSlotState.Attached)
                {
                    ClearActionPendingLocked(entry);
                    failure = open ?
                        InputControllerSlotTableFailure.WrongState :
                        InputControllerSlotTableFailure.Closed;
                    return false;
                }

                int remaining = GetRemainingMilliseconds(startTimestamp,
                    timeoutMilliseconds);
                if (remaining == 0)
                {
                    ClearActionPendingLocked(entry);
                    failure = InputControllerSlotTableFailure.TimedOut;
                    return false;
                }
                Monitor.Wait(gate, remaining);
            }

            if (!open || !TryGetExactEntryLocked(token,
                out Entry finalEntry) || !ReferenceEquals(entry, finalEntry) ||
                entry.State != InputControllerSlotState.Attached)
            {
                ClearActionPendingLocked(entry);
                failure = open ?
                    InputControllerSlotTableFailure.WrongState :
                    InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (!TryAdvance(ref entry.LastActionLeaseGeneration,
                out ulong leaseGeneration))
            {
                ClearActionPendingLocked(entry);
                failure = InputControllerSlotTableFailure.
                    LeaseGenerationExhausted;
                return false;
            }

            entry.ActionPending = false;
            entry.ActionActive = true;
            entry.ActiveActionLeaseGeneration = leaseGeneration;
            lease = new InputControllerActionLease(this, token,
                leaseGeneration);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    public bool TryBeginRetire(InputControllerSlotToken token,
        out InputControllerRetirementClaim claim,
        out InputControllerSlotTableFailure failure)
    {
        claim = default;
        lock (gate)
        {
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.ActivationPending)
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }
            if (entry.State != InputControllerSlotState.Attached)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            entry.State = InputControllerSlotState.Retiring;
            Monitor.PulseAll(gate);
            claim = new InputControllerRetirementClaim(token);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    public bool TryWaitForDrain(InputControllerRetirementClaim claim,
        int timeoutMilliseconds,
        out InputControllerSlotTableFailure failure)
    {
        if (!IsValidTimeout(timeoutMilliseconds))
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        lock (gate)
        {
            while (true)
            {
                if (!TryGetExactEntryLocked(claim.Token, out Entry entry))
                {
                    failure = InputControllerSlotTableFailure.StaleCredential;
                    return false;
                }
                if (entry.State is not (InputControllerSlotState.Retiring or
                    InputControllerSlotState.Quarantined))
                {
                    failure = InputControllerSlotTableFailure.WrongState;
                    return false;
                }
                if (IsDrained(entry))
                {
                    failure = InputControllerSlotTableFailure.None;
                    return true;
                }

                int remaining = GetRemainingMilliseconds(startTimestamp,
                    timeoutMilliseconds);
                if (remaining == 0)
                {
                    failure = InputControllerSlotTableFailure.TimedOut;
                    return false;
                }
                Monitor.Wait(gate, remaining);
            }
        }
    }

    /// <summary>
    /// Records successful external stop/quiescence after terminal-neutral
    /// delivery and lease drain. The caller must quarantine instead if the
    /// exact registration owner's bounded stop was rejected or uncertain.
    /// </summary>
    public bool TryMarkQuiesced(InputControllerRetirementClaim claim,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!TryGetExactEntryLocked(claim.Token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State != InputControllerSlotState.Retiring)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (!entry.TerminalNeutralAcknowledged)
            {
                failure = InputControllerSlotTableFailure.
                    TerminalNeutralRequired;
                return false;
            }
            if (!IsDrained(entry))
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }

            entry.State = InputControllerSlotState.Quiesced;
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Releases a quiesced slot only after the caller has successfully removed
    /// the same exact registration lifetime from its external owner. A rejected
    /// or uncertain external removal must be quarantined instead.
    /// </summary>
    public bool TryCompleteRemoval(InputControllerRetirementClaim claim,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!TryGetExactEntryLocked(claim.Token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State != InputControllerSlotState.Quiesced)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (!IsDrained(entry))
            {
                failure = InputControllerSlotTableFailure.Busy;
                return false;
            }

            entry.MarkRemoved();
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Permanently fail-closes an uncertain slot lifetime. Quarantine preserves
    /// its exact registration and blocks slot reuse across later service opens.
    /// </summary>
    public bool TryQuarantine(InputControllerRetirementClaim claim,
        InputControllerSlotQuarantineReason reason,
        out InputControllerSlotTableFailure failure)
    {
        if (reason == InputControllerSlotQuarantineReason.None)
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        lock (gate)
        {
            if (!TryGetExactEntryLocked(claim.Token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State is not (InputControllerSlotState.Retiring or
                InputControllerSlotState.Quiesced))
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            entry.ActivationPending = false;
            entry.ActivationFence = null;
            entry.ActivationCommitOwned = false;
            entry.ActivationCommitFence = null;
            entry.State = InputControllerSlotState.Quarantined;
            entry.QuarantineReason = reason;
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Fail-closes an Attached entry when the owner's post-table commit result
    /// is outcome-uncertain and an ordinary retirement claim could not be
    /// established. This exact-token path exists only for that narrow
    /// transaction invariant; normal teardown must use TryBeginRetire first.
    /// </summary>
    public bool TryQuarantine(InputControllerSlotToken token,
        InputControllerSlotQuarantineReason reason,
        out InputControllerSlotTableFailure failure)
    {
        if (reason == InputControllerSlotQuarantineReason.None)
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        lock (gate)
        {
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State != InputControllerSlotState.Attached)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            entry.ActivationPending = false;
            entry.ActivationFence = null;
            entry.ActivationCommitOwned = false;
            entry.ActivationCommitFence = null;
            entry.State = InputControllerSlotState.Quarantined;
            entry.QuarantineReason = reason;
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Permanently fail-closes a bound setup whose unpublished external
    /// teardown could not be proven. This is deliberately distinct from
    /// rollback: an uncertain owner, worker, or delegate lifetime must retain
    /// its exact slot identity and can never become reusable.
    /// </summary>
    public bool TryQuarantine(InputControllerSetupRollbackClaim claim,
        InputControllerSlotQuarantineReason reason,
        out InputControllerSlotTableFailure failure)
    {
        if (reason == InputControllerSlotQuarantineReason.None)
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        lock (gate)
        {
            if (!TryGetExactEntryLocked(claim.Token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            if (entry.State != InputControllerSlotState.Bound)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            entry.State = InputControllerSlotState.Quarantined;
            entry.QuarantineReason = reason;
            Monitor.PulseAll(gate);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    public InputControllerSlotSnapshot[] GetSnapshot()
    {
        lock (gate)
        {
            return CreateSnapshotsLocked();
        }
    }

    /// <summary>
    /// Allocation-free proof used by an external slot host while it owns that
    /// host's lifecycle gate.  This authenticates the private table issuer,
    /// the complete registration and slot epoch, the open service epoch, and
    /// the pre-activation Bound state.  It performs no external call and must
    /// therefore remain safe under the lock order external-lifecycle-gate then
    /// table-gate.
    /// </summary>
    internal bool TryAuthenticateBoundExternalStage(
        in InputControllerSlotToken token,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!open)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.ServiceGeneration != currentServiceGeneration)
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != InputControllerSlotState.Bound)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Allocation-free proof for exact external cleanup.  Cleanup may run
    /// after service admission closes, so this validates the retained table
    /// issuer and complete slot epoch without requiring the table to remain
    /// open.  The caller supplies the one lifecycle state in which its inverse
    /// is legal.
    /// </summary>
    internal bool TryAuthenticateExactExternalCleanup(
        in InputControllerSlotToken token,
        InputControllerSlotState expectedState,
        out InputControllerSlotTableFailure failure)
    {
        if (expectedState is not (InputControllerSlotState.Bound or
                InputControllerSlotState.Quiesced))
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        lock (gate)
        {
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != expectedState)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Allocation-free proof for the one external terminal-neutral dispatch.
    /// The regular report lease already proves Attached on the hot path; this
    /// separate retirement proof keeps the terminal path exact without adding
    /// another lock or lookup to every regular report.
    /// </summary>
    internal bool TryAuthenticateRetiringExternalTerminal(
        in InputControllerSlotToken token,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != InputControllerSlotState.Retiring)
            {
                failure = InputControllerSlotTableFailure.WrongState;
                return false;
            }

            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    internal bool TryAcknowledgeTerminalNeutral(
        InputControllerSlotToken token, int leaseIndex,
        ulong leaseGeneration, bool terminal,
        out InputControllerSlotTableFailure failure)
    {
        lock (gate)
        {
            if (!terminal)
            {
                failure = InputControllerSlotTableFailure.WrongLeaseKind;
                return false;
            }
            if (!TryGetExactEntryLocked(token, out Entry entry) ||
                leaseIndex < 0 || leaseIndex >= ReportLeaseCellCount ||
                !entry.ReportLeaseActive[leaseIndex] ||
                entry.ReportLeaseGenerations[leaseIndex] != leaseGeneration ||
                entry.TerminalReportLeaseIndex != leaseIndex ||
                entry.TerminalReportLeaseGeneration != leaseGeneration)
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State != InputControllerSlotState.Retiring)
            {
                failure = entry.State ==
                    InputControllerSlotState.Quarantined ?
                    InputControllerSlotTableFailure.Quarantined :
                    InputControllerSlotTableFailure.WrongState;
                return false;
            }
            if (entry.TerminalNeutralAcknowledged)
            {
                failure = InputControllerSlotTableFailure.
                    AlreadyAcknowledged;
                return false;
            }

            entry.TerminalNeutralAcknowledged = true;
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    internal void ReleaseReportLease(InputControllerSlotToken token,
        int leaseIndex, ulong leaseGeneration)
    {
        lock (gate)
        {
            if (!TryGetExactEntryLocked(token, out Entry entry) ||
                leaseIndex < 0 || leaseIndex >= ReportLeaseCellCount ||
                !entry.ReportLeaseActive[leaseIndex] ||
                entry.ReportLeaseGenerations[leaseIndex] != leaseGeneration)
            {
                return;
            }

            entry.ReportLeaseActive[leaseIndex] = false;
            entry.ActiveReportLeaseCount--;
            if (entry.TerminalReportLeaseIndex == leaseIndex &&
                entry.TerminalReportLeaseGeneration == leaseGeneration)
            {
                entry.TerminalReportLeaseIndex = -1;
                entry.TerminalReportLeaseGeneration = 0;
            }
            Monitor.PulseAll(gate);
        }
    }

    internal void ReleaseActionLease(InputControllerSlotToken token,
        ulong leaseGeneration)
    {
        lock (gate)
        {
            if (!TryGetExactEntryLocked(token, out Entry entry) ||
                !entry.ActionActive ||
                entry.ActiveActionLeaseGeneration != leaseGeneration)
            {
                return;
            }

            entry.ActionActive = false;
            entry.ActiveActionLeaseGeneration = 0;
            Monitor.PulseAll(gate);
        }
    }

    private bool TryAcquireReportLease(InputControllerSlotToken token,
        DS4Device sender, bool terminal,
        out InputControllerReportLease lease,
        out InputControllerSlotTableFailure failure)
    {
        lease = default;
        if (sender == null || !ReferenceEquals(sender,
            token.Registration.Device))
        {
            failure = InputControllerSlotTableFailure.WrongSender;
            return false;
        }

        lock (gate)
        {
            if (!TryGetExactEntryLocked(token, out Entry entry))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (entry.State == InputControllerSlotState.Quarantined)
            {
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }

            if (terminal)
            {
                if (entry.State != InputControllerSlotState.Retiring)
                {
                    failure = InputControllerSlotTableFailure.WrongState;
                    return false;
                }
                if (entry.TerminalNeutralAcknowledged ||
                    entry.TerminalReportLeaseIndex >= 0)
                {
                    failure = InputControllerSlotTableFailure.
                        AlreadyAcknowledged;
                    return false;
                }
                if (!IsDrained(entry))
                {
                    failure = InputControllerSlotTableFailure.Busy;
                    return false;
                }
            }
            else
            {
                if (!open || entry.ServiceGeneration !=
                    currentServiceGeneration)
                {
                    failure = InputControllerSlotTableFailure.Closed;
                    return false;
                }
                if (entry.State != InputControllerSlotState.Attached)
                {
                    failure = InputControllerSlotTableFailure.WrongState;
                    return false;
                }
                if (entry.ActionPending || entry.ActionActive)
                {
                    failure = InputControllerSlotTableFailure.Busy;
                    return false;
                }
            }

            int leaseIndex = FindFreeReportLeaseCell(entry);
            if (leaseIndex < 0)
            {
                failure = InputControllerSlotTableFailure.ReportLeaseLimit;
                return false;
            }
            if (!TryAdvance(ref entry.LastReportLeaseGeneration,
                out ulong leaseGeneration))
            {
                failure = InputControllerSlotTableFailure.
                    LeaseGenerationExhausted;
                return false;
            }

            entry.ReportLeaseActive[leaseIndex] = true;
            entry.ReportLeaseGenerations[leaseIndex] = leaseGeneration;
            entry.ActiveReportLeaseCount++;
            if (terminal)
            {
                entry.TerminalReportLeaseIndex = leaseIndex;
                entry.TerminalReportLeaseGeneration = leaseGeneration;
            }
            lease = new InputControllerReportLease(this, token, leaseIndex,
                leaseGeneration, terminal);
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    private bool TryGetExactEntryLocked(InputControllerSlotToken token,
        out Entry entry)
    {
        if (!ReferenceEquals(token.Issuer, issuer) || token.Slot < 0 ||
            token.Slot >= entries.Length || token.ServiceGeneration == 0 ||
            token.SlotGeneration == 0)
        {
            entry = null;
            return false;
        }

        entry = entries[token.Slot];
        return entry.State is not (InputControllerSlotState.Empty or
                InputControllerSlotState.Removed) &&
            entry.ServiceGeneration == token.ServiceGeneration &&
            entry.SlotGeneration == token.SlotGeneration &&
            entry.Registration == token.Registration &&
            ReferenceEquals(entry.Registration.Device,
                token.Registration.Device) &&
            ReferenceEquals(entry.Registration.Owner,
                token.Registration.Owner);
    }

    private bool ContainsDuplicateLocked(
        InputControllerRegistration registration)
    {
        foreach (Entry entry in entries)
        {
            if (entry.State is InputControllerSlotState.Empty or
                InputControllerSlotState.Removed)
            {
                continue;
            }

            InputControllerRegistration existing = entry.Registration;
            if (ReferenceEquals(existing.Device, registration.Device) ||
                ReferenceEquals(existing.Owner, registration.Owner) &&
                    existing.Generation == registration.Generation)
            {
                return true;
            }
        }

        return false;
    }

    private int FindReusableSlotLocked()
    {
        for (int index = 0; index < entries.Length; index++)
        {
            if (entries[index].State is InputControllerSlotState.Empty or
                InputControllerSlotState.Removed)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindFreeReportLeaseCell(Entry entry)
    {
        for (int index = 0; index < ReportLeaseCellCount; index++)
        {
            if (!entry.ReportLeaseActive[index])
            {
                return index;
            }
        }

        return -1;
    }

    private InputControllerSlotSnapshot[] CreateSnapshotsLocked()
    {
        var result = new InputControllerSlotSnapshot[entries.Length];
        for (int slot = 0; slot < entries.Length; slot++)
        {
            Entry entry = entries[slot];
            InputControllerSlotToken token = entry.State is
                InputControllerSlotState.Empty or
                InputControllerSlotState.Removed ? default :
                new InputControllerSlotToken(issuer, slot,
                    entry.ServiceGeneration, entry.SlotGeneration,
                    entry.Registration);
            result[slot] = new InputControllerSlotSnapshot(slot, entry.State,
                entry.ServiceGeneration, entry.SlotGeneration, token,
                entry.ActiveReportLeaseCount, entry.ActionPending,
                entry.ActionActive, entry.TerminalNeutralAcknowledged,
                entry.ActivationPending, entry.QuarantineReason);
        }

        return result;
    }

    private void ClearActionPendingLocked(Entry entry)
    {
        if (entry.ActionPending)
        {
            entry.ActionPending = false;
            Monitor.PulseAll(gate);
        }
    }

    private static bool IsDrained(Entry entry) =>
        entry.ActiveReportLeaseCount == 0 && !entry.ActionPending &&
        !entry.ActionActive;

    private static bool IsValidTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds >= 0 && timeoutMilliseconds <=
            InputControllerRegistration.MaximumStopTimeoutMilliseconds;

    internal bool AuthenticatesActivation(
        in InputControllerActivationClaim claim, object fence,
        in InputControllerSlotToken expectedToken)
    {
        lock (gate)
        {
            return claim.Token.Equals(expectedToken) &&
                TryGetExactEntryLocked(expectedToken, out Entry entry) &&
                entry.State == InputControllerSlotState.Attached &&
                entry.ActivationPending &&
                !entry.ActivationCommitOwned &&
                ReferenceEquals(entry.ActivationFence, fence);
        }
    }

    internal bool AuthenticatesActivationCommit(
        in InputControllerActivationCommitCredential credential,
        object fence, in InputControllerSlotToken expectedToken)
    {
        lock (gate)
        {
            return credential.Token.Equals(expectedToken) &&
                TryGetExactEntryLocked(expectedToken, out Entry entry) &&
                entry.State == InputControllerSlotState.Attached &&
                entry.ActivationPending && entry.ActivationCommitOwned &&
                ReferenceEquals(entry.ActivationCommitFence, fence);
        }
    }

    private static int GetRemainingMilliseconds(long startTimestamp,
        int timeoutMilliseconds)
    {
        if (timeoutMilliseconds == 0)
        {
            return 0;
        }

        long elapsedMilliseconds = Stopwatch.GetElapsedTime(startTimestamp).
            Ticks / TimeSpan.TicksPerMillisecond;
        if (elapsedMilliseconds >= timeoutMilliseconds)
        {
            return 0;
        }

        return timeoutMilliseconds - (int)elapsedMilliseconds;
    }

    private static bool TryAdvance(ref ulong value, out ulong next)
    {
        if (value == ulong.MaxValue)
        {
            next = 0;
            return false;
        }

        next = ++value;
        return true;
    }

    private sealed class Entry
    {
        public Entry()
        {
            ReportLeaseActive = new bool[ReportLeaseCellCount];
            ReportLeaseGenerations = new ulong[ReportLeaseCellCount];
            TerminalReportLeaseIndex = -1;
        }

        public InputControllerSlotState State;
        public InputControllerRegistration Registration;
        public ulong ServiceGeneration;
        public ulong SlotGeneration;
        public readonly bool[] ReportLeaseActive;
        public readonly ulong[] ReportLeaseGenerations;
        public int ActiveReportLeaseCount;
        public ulong LastReportLeaseGeneration;
        public bool ActionPending;
        public bool ActionActive;
        public ulong LastActionLeaseGeneration;
        public ulong ActiveActionLeaseGeneration;
        public int TerminalReportLeaseIndex;
        public ulong TerminalReportLeaseGeneration;
        public bool TerminalNeutralAcknowledged;
        public InputControllerSlotQuarantineReason QuarantineReason;
        public bool ActivationPending;
        public object ActivationFence;
        public bool ActivationCommitOwned;
        public object ActivationCommitFence;

        public void Reserve(ulong serviceGeneration, ulong slotGeneration,
            InputControllerRegistration registration)
        {
            Debug.Assert(ActiveReportLeaseCount == 0);
            Debug.Assert(!ActionPending && !ActionActive);
            State = InputControllerSlotState.Reserved;
            Registration = registration;
            ServiceGeneration = serviceGeneration;
            SlotGeneration = slotGeneration;
            TerminalReportLeaseIndex = -1;
            TerminalReportLeaseGeneration = 0;
            TerminalNeutralAcknowledged = false;
            QuarantineReason = InputControllerSlotQuarantineReason.None;
            ActivationPending = false;
            ActivationFence = null;
            ActivationCommitOwned = false;
            ActivationCommitFence = null;
        }

        public void MarkRemoved()
        {
            Debug.Assert(ActiveReportLeaseCount == 0);
            Debug.Assert(!ActionPending && !ActionActive);
            State = InputControllerSlotState.Removed;
            Registration = default;
            TerminalReportLeaseIndex = -1;
            TerminalReportLeaseGeneration = 0;
            TerminalNeutralAcknowledged = false;
            QuarantineReason = InputControllerSlotQuarantineReason.None;
            ActivationPending = false;
            ActivationFence = null;
            ActivationCommitOwned = false;
            ActivationCommitFence = null;
        }

        public void ResetToEmpty()
        {
            MarkRemoved();
            State = InputControllerSlotState.Empty;
            ServiceGeneration = 0;
            SlotGeneration = 0;
        }
    }
}
