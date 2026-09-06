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

namespace DS4Windows;

internal enum LegacyHidInputControllerOwnerState : byte
{
    Created = 0,
    StopInProgress,
    Quiesced,
    RemoveInProgress,
    Removed,
    Quarantined,
}

internal enum LegacyHidInputControllerCreateFailure : byte
{
    None = 0,
    InvalidArgument,
    InvalidGeneration,
    MissingHidInterface,
    InvalidDeviceState,
    PersistentIdentityNotProven,
    HostAuthenticationRejected,
    HostThrew,
    RegistrationRejected,
}

internal enum LegacyHidInputControllerLifecycleOperation : byte
{
    Invalid = 0,
    StopAndQuiesce,
    Remove,
}

internal enum LegacyHidInputControllerLifecycleOutcome : byte
{
    Invalid = 0,
    Succeeded,
    ProvenRejected,
    OutcomeUncertain,
}

internal enum LegacyHidInputControllerLifecycleFailureKind : byte
{
    None = 0,
    InvalidCredential,
    StaleGeneration,
    InvalidState,
    StopRejected,
    StopTimedOut,
    RemoveRejected,
    DependencyThrew,
}

/// <summary>
/// Strict proof returned by the existing legacy lifecycle owner. A successful
/// result means the exact synchronous operation completed. Timeout and thrown
/// dependency outcomes are necessarily uncertain; they can never be reported
/// as a clean rejection.
/// </summary>
internal readonly struct LegacyHidInputControllerLifecycleResult
{
    private LegacyHidInputControllerLifecycleResult(
        LegacyHidInputControllerLifecycleOperation operation,
        LegacyHidInputControllerLifecycleOutcome outcome,
        LegacyHidInputControllerLifecycleFailureKind failureKind)
    {
        Operation = operation;
        Outcome = outcome;
        FailureKind = failureKind;
    }

    internal LegacyHidInputControllerLifecycleOperation Operation { get; }

    internal LegacyHidInputControllerLifecycleOutcome Outcome { get; }

    internal LegacyHidInputControllerLifecycleFailureKind FailureKind { get; }

    internal bool IsValid => IsDefined(Operation) && IsDefined(Outcome) &&
        IsDefined(FailureKind) && IsAllowedShape(Operation, Outcome,
            FailureKind);

    internal bool Succeeded => IsValid && Outcome ==
        LegacyHidInputControllerLifecycleOutcome.Succeeded;

    internal bool RequiresQuarantine => !IsValid || Outcome ==
            LegacyHidInputControllerLifecycleOutcome.OutcomeUncertain ||
        FailureKind is
            LegacyHidInputControllerLifecycleFailureKind.InvalidCredential or
            LegacyHidInputControllerLifecycleFailureKind.StaleGeneration or
            LegacyHidInputControllerLifecycleFailureKind.InvalidState;

    internal static LegacyHidInputControllerLifecycleResult Success(
        LegacyHidInputControllerLifecycleOperation operation)
    {
        if (!IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
        return new LegacyHidInputControllerLifecycleResult(operation,
            LegacyHidInputControllerLifecycleOutcome.Succeeded,
            LegacyHidInputControllerLifecycleFailureKind.None);
    }

    internal static LegacyHidInputControllerLifecycleResult Reject(
        LegacyHidInputControllerLifecycleOperation operation,
        LegacyHidInputControllerLifecycleFailureKind failureKind) =>
        CreateFailure(operation,
            LegacyHidInputControllerLifecycleOutcome.ProvenRejected,
            failureKind);

    internal static LegacyHidInputControllerLifecycleResult Uncertain(
        LegacyHidInputControllerLifecycleOperation operation,
        LegacyHidInputControllerLifecycleFailureKind failureKind) =>
        CreateFailure(operation,
            LegacyHidInputControllerLifecycleOutcome.OutcomeUncertain,
            failureKind);

    private static LegacyHidInputControllerLifecycleResult CreateFailure(
        LegacyHidInputControllerLifecycleOperation operation,
        LegacyHidInputControllerLifecycleOutcome outcome,
        LegacyHidInputControllerLifecycleFailureKind failureKind)
    {
        if (!IsDefined(operation) || !IsDefined(outcome) ||
            !IsDefined(failureKind) || !IsAllowedShape(operation, outcome,
                failureKind))
        {
            throw new ArgumentException(
                "The legacy HID lifecycle result is malformed.");
        }
        return new LegacyHidInputControllerLifecycleResult(operation, outcome,
            failureKind);
    }

    private static bool IsAllowedShape(
        LegacyHidInputControllerLifecycleOperation operation,
        LegacyHidInputControllerLifecycleOutcome outcome,
        LegacyHidInputControllerLifecycleFailureKind failureKind)
    {
        if (outcome == LegacyHidInputControllerLifecycleOutcome.Succeeded)
        {
            return failureKind ==
                LegacyHidInputControllerLifecycleFailureKind.None;
        }
        if (failureKind == LegacyHidInputControllerLifecycleFailureKind.None)
        {
            return false;
        }
        if (failureKind is
            LegacyHidInputControllerLifecycleFailureKind.StopTimedOut or
            LegacyHidInputControllerLifecycleFailureKind.DependencyThrew)
        {
            return outcome ==
                    LegacyHidInputControllerLifecycleOutcome.OutcomeUncertain &&
                (failureKind == LegacyHidInputControllerLifecycleFailureKind.
                        DependencyThrew ||
                    operation == LegacyHidInputControllerLifecycleOperation.
                        StopAndQuiesce);
        }
        if (outcome !=
            LegacyHidInputControllerLifecycleOutcome.ProvenRejected)
        {
            return false;
        }
        return failureKind switch
        {
            LegacyHidInputControllerLifecycleFailureKind.InvalidCredential or
            LegacyHidInputControllerLifecycleFailureKind.StaleGeneration or
            LegacyHidInputControllerLifecycleFailureKind.InvalidState => true,
            LegacyHidInputControllerLifecycleFailureKind.StopRejected =>
                operation == LegacyHidInputControllerLifecycleOperation.
                    StopAndQuiesce,
            LegacyHidInputControllerLifecycleFailureKind.RemoveRejected =>
                operation == LegacyHidInputControllerLifecycleOperation.Remove,
            _ => false,
        };
    }

    private static bool IsDefined(
        LegacyHidInputControllerLifecycleOperation value) => value is
            LegacyHidInputControllerLifecycleOperation.StopAndQuiesce or
            LegacyHidInputControllerLifecycleOperation.Remove;

    private static bool IsDefined(
        LegacyHidInputControllerLifecycleOutcome value) => value is
            LegacyHidInputControllerLifecycleOutcome.Succeeded or
            LegacyHidInputControllerLifecycleOutcome.ProvenRejected or
            LegacyHidInputControllerLifecycleOutcome.OutcomeUncertain;

    private static bool IsDefined(
        LegacyHidInputControllerLifecycleFailureKind value) => value is >=
            LegacyHidInputControllerLifecycleFailureKind.None and <=
            LegacyHidInputControllerLifecycleFailureKind.DependencyThrew;
}

/// <summary>
/// Opaque, host-issued exact identity of one already-discovered legacy HID
/// connection. It grants no direct HID handle access and cannot be recreated
/// from a MAC address, device path, slot number, or service generation.
/// </summary>
internal readonly struct LegacyHidInputControllerLifetimeLease :
    IEquatable<LegacyHidInputControllerLifetimeLease>
{
    private readonly object issuer;

    internal LegacyHidInputControllerLifetimeLease(object issuer,
        DS4Device device, ulong generation, bool hasPersistentIdentity)
    {
        this.issuer = issuer;
        Device = device;
        Generation = generation;
        HasPersistentIdentity = hasPersistentIdentity;
    }

    internal DS4Device Device { get; }

    internal ulong Generation { get; }

    internal bool HasPersistentIdentity { get; }

    internal bool IsValid => issuer != null && Device != null &&
        Generation != 0 && Device.HasHidInterface && Device.HidDevice != null;

    internal bool IsIssued => issuer != null;

    internal bool Authenticates(object expectedIssuer, DS4Device expectedDevice,
        ulong expectedGeneration) => IsValid &&
        ReferenceEquals(issuer, expectedIssuer) &&
        ReferenceEquals(Device, expectedDevice) &&
        Generation == expectedGeneration;

    public bool Equals(LegacyHidInputControllerLifetimeLease other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(Device, other.Device) &&
        Generation == other.Generation &&
        HasPersistentIdentity == other.HasPersistentIdentity;

    public override bool Equals(object obj) => obj is
        LegacyHidInputControllerLifetimeLease other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        Device == null ? 0 : RuntimeHelpers.GetHashCode(Device), Generation,
        HasPersistentIdentity);

    public static bool operator ==(
        LegacyHidInputControllerLifetimeLease left,
        LegacyHidInputControllerLifetimeLease right) => left.Equals(right);

    public static bool operator !=(
        LegacyHidInputControllerLifetimeLease left,
        LegacyHidInputControllerLifetimeLease right) => !left.Equals(right);
}

/// <summary>
/// Adapter implemented by the existing ControlService lifecycle owner. Before
/// registration, that owner must issue the lease with a private issuer which
/// it alone can authenticate. The authentication method must then be pure and
/// perform no I/O. Stop and Remove must call the existing legacy operations
/// exactly once and return success only when that exact connection generation
/// is synchronously proven quiesced or removed. This interface must not be
/// implemented by a second HID owner.
/// </summary>
internal interface ILegacyHidInputControllerLifecycleHost
{
    bool Authenticates(in LegacyHidInputControllerLifetimeLease lease);

    LegacyHidInputControllerLifecycleResult TryStopAndQuiesce(
        in LegacyHidInputControllerLifetimeLease lease,
        int timeoutMilliseconds);

    LegacyHidInputControllerLifecycleResult TryRemove(
        in LegacyHidInputControllerLifetimeLease lease);
}

/// <summary>
/// Dormant exact-generation registration owner for an already-created legacy
/// DS4Device. It performs no discovery, HID I/O, report subscription, profile
/// setup, slot mutation, StartUpdate, StopUpdate, DS4Devices mutation, or
/// removal callback. Those operations remain owned by ControlService through
/// <see cref="ILegacyHidInputControllerLifecycleHost"/>.
/// </summary>
internal sealed class LegacyHidInputControllerRegistrationOwner :
    IInputControllerRegistrationOwner
{
    private readonly object gate = new();
    private readonly DS4Device device;
    private readonly ulong generation;
    private readonly ILegacyHidInputControllerLifecycleHost host;
    private readonly LegacyHidInputControllerLifetimeLease lease;
    private readonly InputControllerRegistration registration;

    private LegacyHidInputControllerOwnerState state;
    private LegacyHidInputControllerLifecycleResult lastLifecycleResult;

    private LegacyHidInputControllerRegistrationOwner(
        in LegacyHidInputControllerLifetimeLease lease,
        ILegacyHidInputControllerLifecycleHost host,
        out InputControllerRegistration registration,
        out InputControllerRegistrationFailure registrationFailure)
    {
        device = lease.Device;
        generation = lease.Generation;
        this.host = host;
        this.lease = lease;
        state = LegacyHidInputControllerOwnerState.Created;

        if (!InputControllerRegistration.TryCreate(device, generation,
                InputControllerOwnershipKind.LegacyHid,
                hasHidInterface: true, lease.HasPersistentIdentity, this,
                out registration, out registrationFailure))
        {
            this.registration = default;
            return;
        }
        this.registration = registration;
    }

    public InputControllerOwnershipKind Kind =>
        InputControllerOwnershipKind.LegacyHid;

    internal InputControllerRegistration Registration => registration;

    internal LegacyHidInputControllerLifetimeLease LifetimeLease => lease;

    internal LegacyHidInputControllerOwnerState State
    {
        get { lock (gate) { return state; } }
    }

    internal LegacyHidInputControllerLifecycleResult LastLifecycleResult
    {
        get { lock (gate) { return lastLifecycleResult; } }
    }

    internal bool RequiresQuarantine
    {
        get { lock (gate) { return state ==
            LegacyHidInputControllerOwnerState.Quarantined; } }
    }

    /// <summary>Test-visible identity of the owner's only private gate.</summary>
    internal object LifecycleGate => gate;

    internal static bool TryCreate(
        in LegacyHidInputControllerLifetimeLease lifetimeLease,
        ILegacyHidInputControllerLifecycleHost host,
        out LegacyHidInputControllerRegistrationOwner owner,
        out LegacyHidInputControllerCreateFailure failure,
        out InputControllerRegistrationFailure registrationFailure)
    {
        owner = null;
        registrationFailure = InputControllerRegistrationFailure.None;
        DS4Device device = lifetimeLease.Device;
        if (device == null || host == null || !lifetimeLease.IsIssued)
        {
            failure = LegacyHidInputControllerCreateFailure.InvalidArgument;
            return false;
        }
        ulong generation = lifetimeLease.Generation;
        if (generation == 0)
        {
            failure = LegacyHidInputControllerCreateFailure.InvalidGeneration;
            return false;
        }
        if (!device.HasHidInterface || device.HidDevice == null)
        {
            failure =
                LegacyHidInputControllerCreateFailure.MissingHidInterface;
            return false;
        }
        if (device.IsRemoving || device.IsRemoved ||
            device.isDisconnectingStatus())
        {
            failure = LegacyHidInputControllerCreateFailure.
                InvalidDeviceState;
            return false;
        }
        string persistentIdentity = lifetimeLease.HasPersistentIdentity ?
            device.getMacAddress() : null;
        if (lifetimeLease.HasPersistentIdentity &&
            (!device.AllowsPersistentIdentity ||
                string.IsNullOrWhiteSpace(persistentIdentity) ||
                string.Equals(persistentIdentity, DS4Device.BLANK_SERIAL,
                    StringComparison.OrdinalIgnoreCase)))
        {
            failure = LegacyHidInputControllerCreateFailure.
                PersistentIdentityNotProven;
            return false;
        }

        var candidate = new LegacyHidInputControllerRegistrationOwner(
            lifetimeLease, host,
            out InputControllerRegistration registration,
            out registrationFailure);
        if (registrationFailure != InputControllerRegistrationFailure.None)
        {
            failure = registrationFailure ==
                    InputControllerRegistrationFailure.OwnerThrew ?
                LegacyHidInputControllerCreateFailure.HostThrew :
                registrationFailure == InputControllerRegistrationFailure.
                        OwnerAuthenticationFailed ?
                    LegacyHidInputControllerCreateFailure.
                        HostAuthenticationRejected :
                    LegacyHidInputControllerCreateFailure.RegistrationRejected;
            return false;
        }

        owner = candidate;
        failure = LegacyHidInputControllerCreateFailure.None;
        return true;
    }

    public bool Authenticates(DS4Device candidate, ulong candidateGeneration)
    {
        LegacyHidInputControllerLifetimeLease exactLease;
        lock (gate)
        {
            if (!ReferenceEquals(candidate, device) ||
                candidateGeneration != generation || state is
                    LegacyHidInputControllerOwnerState.Removed or
                    LegacyHidInputControllerOwnerState.Quarantined)
            {
                return false;
            }
            exactLease = lease;
        }

        return host.Authenticates(exactLease);
    }

    public bool TryStopAndQuiesce(DS4Device candidate,
        ulong candidateGeneration, int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure)
    {
        if (!IsExact(candidate, candidateGeneration))
        {
            failure = InputControllerOwnerOperationFailure.
                OwnerAuthenticationFailed;
            return false;
        }
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            failure = InputControllerOwnerOperationFailure.InvalidTimeout;
            return false;
        }

        lock (gate)
        {
            if (state == LegacyHidInputControllerOwnerState.Quiesced)
            {
                failure = InputControllerOwnerOperationFailure.None;
                return true;
            }
            if (state != LegacyHidInputControllerOwnerState.Created)
            {
                failure = InputControllerOwnerOperationFailure.StopRejected;
                return false;
            }
            state = LegacyHidInputControllerOwnerState.StopInProgress;
        }

        if (!TryAuthenticateHost(out bool authenticationThrew))
        {
            lock (gate)
            {
                state = LegacyHidInputControllerOwnerState.Quarantined;
            }
            failure = authenticationThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed;
            return false;
        }

        LegacyHidInputControllerLifecycleResult result = InvokeHost(
            LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
            () => host.TryStopAndQuiesce(lease, timeoutMilliseconds));
        lock (gate)
        {
            lastLifecycleResult = result;
            state = result.Succeeded ?
                LegacyHidInputControllerOwnerState.Quiesced :
                result.RequiresQuarantine ?
                    LegacyHidInputControllerOwnerState.Quarantined :
                    LegacyHidInputControllerOwnerState.Created;
        }
        failure = result.Succeeded ?
            InputControllerOwnerOperationFailure.None :
            result.FailureKind ==
                    LegacyHidInputControllerLifecycleFailureKind.
                        InvalidCredential ||
                result.FailureKind ==
                    LegacyHidInputControllerLifecycleFailureKind.
                        StaleGeneration ?
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed :
            result.FailureKind ==
                    LegacyHidInputControllerLifecycleFailureKind.DependencyThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.StopRejected;
        return result.Succeeded;
    }

    public bool TryRemove(DS4Device candidate, ulong candidateGeneration,
        out InputControllerOwnerOperationFailure failure)
    {
        if (!IsExact(candidate, candidateGeneration))
        {
            failure = InputControllerOwnerOperationFailure.
                OwnerAuthenticationFailed;
            return false;
        }
        lock (gate)
        {
            if (state != LegacyHidInputControllerOwnerState.Quiesced)
            {
                failure = InputControllerOwnerOperationFailure.RemoveRejected;
                return false;
            }
            state = LegacyHidInputControllerOwnerState.RemoveInProgress;
        }

        if (!TryAuthenticateHost(out bool authenticationThrew))
        {
            lock (gate)
            {
                state = LegacyHidInputControllerOwnerState.Quarantined;
            }
            failure = authenticationThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed;
            return false;
        }

        LegacyHidInputControllerLifecycleResult result = InvokeHost(
            LegacyHidInputControllerLifecycleOperation.Remove,
            () => host.TryRemove(lease));
        lock (gate)
        {
            lastLifecycleResult = result;
            state = result.Succeeded ?
                LegacyHidInputControllerOwnerState.Removed :
                result.RequiresQuarantine ?
                    LegacyHidInputControllerOwnerState.Quarantined :
                    LegacyHidInputControllerOwnerState.Quiesced;
        }
        failure = result.Succeeded ?
            InputControllerOwnerOperationFailure.None :
            result.FailureKind ==
                    LegacyHidInputControllerLifecycleFailureKind.
                        InvalidCredential ||
                result.FailureKind ==
                    LegacyHidInputControllerLifecycleFailureKind.
                        StaleGeneration ?
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed :
            result.FailureKind ==
                    LegacyHidInputControllerLifecycleFailureKind.DependencyThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.RemoveRejected;
        return result.Succeeded;
    }

    private bool IsExact(DS4Device candidate, ulong candidateGeneration) =>
        ReferenceEquals(candidate, device) && candidateGeneration == generation;

    private bool TryAuthenticateHost(out bool threw)
    {
        try
        {
            threw = false;
            return host.Authenticates(lease);
        }
        catch
        {
            threw = true;
            return false;
        }
    }

    private static LegacyHidInputControllerLifecycleResult InvokeHost(
        LegacyHidInputControllerLifecycleOperation operation,
        Func<LegacyHidInputControllerLifecycleResult> call)
    {
        LegacyHidInputControllerLifecycleResult result;
        try
        {
            result = call();
        }
        catch
        {
            return LegacyHidInputControllerLifecycleResult.Uncertain(operation,
                LegacyHidInputControllerLifecycleFailureKind.DependencyThrew);
        }
        return result.IsValid && result.Operation == operation ? result :
            LegacyHidInputControllerLifecycleResult.Uncertain(operation,
                LegacyHidInputControllerLifecycleFailureKind.DependencyThrew);
    }
}
