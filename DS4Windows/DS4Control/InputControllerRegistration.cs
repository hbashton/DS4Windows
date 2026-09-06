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

public enum InputControllerOwnershipKind : byte
{
    Invalid = 0,
    LegacyHid,
    Switch2Runtime,
}

public enum InputControllerRegistrationFailure : byte
{
    None = 0,
    InvalidArgument,
    InvalidGeneration,
    OwnershipKindMismatch,
    CapabilityMismatch,
    PersistentIdentityNotAllowed,
    OwnerAuthenticationFailed,
    OwnerThrew,
}

public enum InputControllerOwnerOperationFailure : byte
{
    None = 0,
    InvalidRegistration,
    InvalidTimeout,
    OwnerAuthenticationFailed,
    StopRejected,
    RemoveRejected,
    OwnerThrew,
}

/// <summary>
/// Exact owner of one controller registration lifetime. Authentication must be
/// reference-and-generation based and must perform no discovery or I/O.
/// Stop is explicitly bounded; remove must be non-blocking and may succeed only
/// for the same authenticated lifetime.
/// </summary>
public interface IInputControllerRegistrationOwner
{
    InputControllerOwnershipKind Kind { get; }

    bool Authenticates(DS4Device device, ulong generation);

    bool TryStopAndQuiesce(DS4Device device, ulong generation,
        int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure);

    bool TryRemove(DS4Device device, ulong generation,
        out InputControllerOwnerOperationFailure failure);
}

/// <summary>
/// Immutable, owner-authenticated slot candidate. This value does not register
/// a controller or start a transport; it is the dormant ownership token that a
/// later ControlService integration can carry through attach, report, stop, and
/// exact-generation removal.
/// </summary>
public readonly struct InputControllerRegistration :
    IEquatable<InputControllerRegistration>
{
    public const int MaximumStopTimeoutMilliseconds = 30_000;

    private readonly IInputControllerRegistrationOwner owner;

    private InputControllerRegistration(DS4Device device, ulong generation,
        InputControllerOwnershipKind ownershipKind, bool hasHidInterface,
        bool hasPersistentIdentity,
        IInputControllerRegistrationOwner owner)
    {
        Device = device;
        Generation = generation;
        OwnershipKind = ownershipKind;
        HasHidInterface = hasHidInterface;
        HasPersistentIdentity = hasPersistentIdentity;
        this.owner = owner;
    }

    public DS4Device Device { get; }

    public ulong Generation { get; }

    public InputControllerOwnershipKind OwnershipKind { get; }

    public bool HasHidInterface { get; }

    public bool HasPersistentIdentity { get; }

    public IInputControllerRegistrationOwner Owner => owner;

    public bool IsValid => TryValidate(out _);

    public bool IsOwnerAuthenticated
    {
        get
        {
            if (!TryValidate(out _))
            {
                return false;
            }

            return TryAuthenticateOwner(out _);
        }
    }

    public static bool TryCreate(DS4Device device, ulong generation,
        InputControllerOwnershipKind ownershipKind, bool hasHidInterface,
        bool hasPersistentIdentity,
        IInputControllerRegistrationOwner owner,
        out InputControllerRegistration registration,
        out InputControllerRegistrationFailure failure)
    {
        registration = default;
        if (device == null || owner == null || ownershipKind is not
            (InputControllerOwnershipKind.LegacyHid or
                InputControllerOwnershipKind.Switch2Runtime))
        {
            failure = InputControllerRegistrationFailure.InvalidArgument;
            return false;
        }
        if (generation == 0)
        {
            failure = InputControllerRegistrationFailure.InvalidGeneration;
            return false;
        }
        if (!TryGetOwnerKind(owner,
                out InputControllerOwnershipKind ownerKind))
        {
            failure = InputControllerRegistrationFailure.OwnerThrew;
            return false;
        }
        if (ownerKind != ownershipKind)
        {
            failure = InputControllerRegistrationFailure.
                OwnershipKindMismatch;
            return false;
        }
        if (device.HasHidInterface != hasHidInterface ||
            ownershipKind == InputControllerOwnershipKind.LegacyHid &&
                !hasHidInterface ||
            ownershipKind == InputControllerOwnershipKind.Switch2Runtime &&
                hasHidInterface)
        {
            failure = InputControllerRegistrationFailure.CapabilityMismatch;
            return false;
        }
        if (hasPersistentIdentity && !device.AllowsPersistentIdentity)
        {
            failure = InputControllerRegistrationFailure.
                PersistentIdentityNotAllowed;
            return false;
        }

        bool authenticated;
        try
        {
            authenticated = owner.Authenticates(device, generation);
        }
        catch
        {
            failure = InputControllerRegistrationFailure.OwnerThrew;
            return false;
        }
        if (!authenticated)
        {
            failure = InputControllerRegistrationFailure.
                OwnerAuthenticationFailed;
            return false;
        }

        registration = new InputControllerRegistration(device, generation,
            ownershipKind, hasHidInterface, hasPersistentIdentity, owner);
        failure = InputControllerRegistrationFailure.None;
        return true;
    }

    public bool TryStopAndQuiesce(int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure)
    {
        if (!TryValidate(out bool ownerThrew))
        {
            failure = ownerThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.InvalidRegistration;
            return false;
        }
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            MaximumStopTimeoutMilliseconds)
        {
            failure = InputControllerOwnerOperationFailure.InvalidTimeout;
            return false;
        }
        if (!TryAuthenticateOwner(out ownerThrew))
        {
            failure = ownerThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed;
            return false;
        }

        try
        {
            bool stopped = owner.TryStopAndQuiesce(Device, Generation,
                timeoutMilliseconds, out failure);
            if (stopped && failure == InputControllerOwnerOperationFailure.None)
            {
                return true;
            }
            if (stopped || !IsAllowedStopFailure(failure))
            {
                failure = InputControllerOwnerOperationFailure.StopRejected;
            }
            return false;
        }
        catch
        {
            failure = InputControllerOwnerOperationFailure.OwnerThrew;
            return false;
        }
    }

    public bool TryRemove(out InputControllerOwnerOperationFailure failure)
    {
        if (!TryValidate(out bool ownerThrew))
        {
            failure = ownerThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.InvalidRegistration;
            return false;
        }
        if (!TryAuthenticateOwner(out ownerThrew))
        {
            failure = ownerThrew ?
                InputControllerOwnerOperationFailure.OwnerThrew :
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed;
            return false;
        }

        try
        {
            bool removed = owner.TryRemove(Device, Generation, out failure);
            if (removed && failure == InputControllerOwnerOperationFailure.None)
            {
                return true;
            }
            if (removed || !IsAllowedRemoveFailure(failure))
            {
                failure = InputControllerOwnerOperationFailure.RemoveRejected;
            }
            return false;
        }
        catch
        {
            failure = InputControllerOwnerOperationFailure.OwnerThrew;
            return false;
        }
    }

    public bool Equals(InputControllerRegistration other) =>
        ReferenceEquals(Device, other.Device) && Generation == other.Generation &&
        OwnershipKind == other.OwnershipKind &&
        HasHidInterface == other.HasHidInterface &&
        HasPersistentIdentity == other.HasPersistentIdentity &&
        ReferenceEquals(owner, other.owner);

    public override bool Equals(object obj) =>
        obj is InputControllerRegistration other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        Device == null ? 0 : RuntimeHelpers.GetHashCode(Device), Generation,
        OwnershipKind, HasHidInterface, HasPersistentIdentity,
        owner == null ? 0 : RuntimeHelpers.GetHashCode(owner));

    public static bool operator ==(InputControllerRegistration left,
        InputControllerRegistration right) => left.Equals(right);

    public static bool operator !=(InputControllerRegistration left,
        InputControllerRegistration right) => !left.Equals(right);

    private static bool TryGetOwnerKind(
        IInputControllerRegistrationOwner candidate,
        out InputControllerOwnershipKind kind)
    {
        try
        {
            kind = candidate.Kind;
            return true;
        }
        catch
        {
            kind = InputControllerOwnershipKind.Invalid;
            return false;
        }
    }

    private bool TryValidate(out bool ownerThrew)
    {
        ownerThrew = false;
        if (Device == null || Generation == 0 || owner == null ||
            OwnershipKind is not (InputControllerOwnershipKind.LegacyHid or
                InputControllerOwnershipKind.Switch2Runtime))
        {
            return false;
        }

        if (!TryGetOwnerKind(owner,
                out InputControllerOwnershipKind ownerKind))
        {
            ownerThrew = true;
            return false;
        }

        return OwnershipKind == ownerKind;
    }

    private bool TryAuthenticateOwner(out bool ownerThrew)
    {
        try
        {
            ownerThrew = false;
            return owner.Authenticates(Device, Generation);
        }
        catch
        {
            ownerThrew = true;
            return false;
        }
    }

    private static bool IsAllowedStopFailure(
        InputControllerOwnerOperationFailure failure) => failure is
        InputControllerOwnerOperationFailure.OwnerAuthenticationFailed or
        InputControllerOwnerOperationFailure.StopRejected or
        InputControllerOwnerOperationFailure.OwnerThrew;

    private static bool IsAllowedRemoveFailure(
        InputControllerOwnerOperationFailure failure) => failure is
        InputControllerOwnerOperationFailure.OwnerAuthenticationFailed or
        InputControllerOwnerOperationFailure.RemoveRejected or
        InputControllerOwnerOperationFailure.OwnerThrew;
}
