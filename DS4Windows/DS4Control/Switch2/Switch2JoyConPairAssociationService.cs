/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

internal enum Switch2JoyConPairAssociationFailure : byte
{
    None = 0,
    InvalidArgument,
    InvalidPeer,
    InvalidPeerRoles,
    DuplicatePeer,
    NotFound,
    StaleRevision,
    RevisionExhausted,
    ConcurrentModification,
    CorruptStoreState,
    StoreFault,
}

/// <summary>
/// Side-tagged opaque peer identity accepted by explicit Joy-Con pair
/// mutations. It retains no OS identity, address, path, bond, or key material.
/// </summary>
internal readonly struct Switch2JoyConAssociationPeer
{
    private Switch2JoyConAssociationPeer(
        Switch2PersistentPeerId persistentPeerId,
        Switch2ControllerModel model, ushort productId)
    {
        PersistentPeerId = persistentPeerId;
        Model = model;
        ProductId = productId;
    }

    internal Switch2PersistentPeerId PersistentPeerId { get; }

    internal Switch2ControllerModel Model { get; }

    internal ushort ProductId { get; }

    internal bool IsValid => PersistentPeerId.IsValid &&
        IsExactJoyConIdentity(Model, ProductId);

    internal static bool TryCreate(
        Switch2PersistentPeerId persistentPeerId,
        Switch2ControllerModel model, ushort productId,
        out Switch2JoyConAssociationPeer peer)
    {
        peer = new Switch2JoyConAssociationPeer(persistentPeerId, model,
            productId);
        if (peer.IsValid)
        {
            return true;
        }
        peer = default;
        return false;
    }

    public override string ToString() => Model switch
    {
        Switch2ControllerModel.JoyCon2Left =>
            "Switch2JoyConAssociationPeer(Left)",
        Switch2ControllerModel.JoyCon2Right =>
            "Switch2JoyConAssociationPeer(Right)",
        _ => "Switch2JoyConAssociationPeer(Invalid)",
    };

    private static bool IsExactJoyConIdentity(
        Switch2ControllerModel model, ushort productId) =>
        (model, productId) switch
        {
            (Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId) => true,
            (Switch2ControllerModel.JoyCon2Right,
                Switch2AdvertisementCodec.JoyCon2RightProductId) => true,
            _ => false,
        };
}

/// <summary>
/// Dormant, explicit-user-action association boundary. It contains no
/// discovery candidates, signal strength, proximity, ordering, or automatic
/// opposite-side selection. Every mutation uses the store's compare-and-swap
/// revision contract, and store exceptions are contained at this boundary.
/// </summary>
internal sealed class Switch2JoyConPairAssociationService
{
    private const ulong InitialRevision = 1;
    private readonly ISwitch2JoyConPairStore store;

    internal Switch2JoyConPairAssociationService(
        ISwitch2JoyConPairStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal bool TryCreateExplicitPair(
        in Switch2JoyConAssociationPeer left,
        in Switch2JoyConAssociationPeer right,
        out Switch2JoyConPairRecord record,
        out Switch2JoyConPairAssociationFailure failure)
    {
        record = default;
        if (!TryValidateExactPair(left, right, out failure))
        {
            return false;
        }

        Switch2JoyConPairId pairId = Switch2JoyConPairId.CreateRandom();
        if (!Switch2JoyConPairRecord.TryCreate(InitialRevision, pairId,
                left.PersistentPeerId, right.PersistentPeerId,
                out Switch2JoyConPairRecord candidate))
        {
            failure = Switch2JoyConPairAssociationFailure.InvalidPeer;
            return false;
        }

        bool replaced;
        try
        {
            replaced = store.TryReplace(candidate, expectedPriorRevision: 0);
        }
        catch
        {
            failure = Switch2JoyConPairAssociationFailure.StoreFault;
            return false;
        }
        if (!replaced)
        {
            failure = Switch2JoyConPairAssociationFailure.
                ConcurrentModification;
            return false;
        }

        record = candidate;
        failure = Switch2JoyConPairAssociationFailure.None;
        return true;
    }

    internal bool TryReplaceExplicitPair(Switch2JoyConPairId pairId,
        ulong expectedRevision,
        in Switch2JoyConAssociationPeer left,
        in Switch2JoyConAssociationPeer right,
        out Switch2JoyConPairRecord record,
        out Switch2JoyConPairAssociationFailure failure)
    {
        record = default;
        if (!pairId.IsValid || expectedRevision == 0)
        {
            failure = Switch2JoyConPairAssociationFailure.InvalidArgument;
            return false;
        }
        if (!TryValidateExactPair(left, right, out failure) ||
            !TryLoadCurrent(pairId, out Switch2JoyConPairRecord current,
                out failure))
        {
            return false;
        }
        if (current.Revision != expectedRevision)
        {
            failure = Switch2JoyConPairAssociationFailure.StaleRevision;
            return false;
        }
        if (current.Revision == ulong.MaxValue)
        {
            failure = Switch2JoyConPairAssociationFailure.RevisionExhausted;
            return false;
        }

        ulong nextRevision = current.Revision + 1;
        if (!Switch2JoyConPairRecord.TryCreate(nextRevision, pairId,
                left.PersistentPeerId, right.PersistentPeerId,
                out Switch2JoyConPairRecord candidate))
        {
            failure = Switch2JoyConPairAssociationFailure.InvalidPeer;
            return false;
        }

        bool replaced;
        try
        {
            replaced = store.TryReplace(candidate, expectedRevision);
        }
        catch
        {
            failure = Switch2JoyConPairAssociationFailure.StoreFault;
            return false;
        }
        if (!replaced)
        {
            failure = Switch2JoyConPairAssociationFailure.
                ConcurrentModification;
            return false;
        }

        record = candidate;
        failure = Switch2JoyConPairAssociationFailure.None;
        return true;
    }

    internal bool TryDeleteExplicitPair(Switch2JoyConPairId pairId,
        ulong expectedRevision,
        out Switch2JoyConPairAssociationFailure failure)
    {
        if (!pairId.IsValid || expectedRevision == 0)
        {
            failure = Switch2JoyConPairAssociationFailure.InvalidArgument;
            return false;
        }
        if (!TryLoadCurrent(pairId, out Switch2JoyConPairRecord current,
                out failure))
        {
            return false;
        }
        if (current.Revision != expectedRevision)
        {
            failure = Switch2JoyConPairAssociationFailure.StaleRevision;
            return false;
        }

        bool deleted;
        try
        {
            deleted = store.TryDelete(pairId, expectedRevision);
        }
        catch
        {
            failure = Switch2JoyConPairAssociationFailure.StoreFault;
            return false;
        }
        if (!deleted)
        {
            failure = Switch2JoyConPairAssociationFailure.
                ConcurrentModification;
            return false;
        }

        failure = Switch2JoyConPairAssociationFailure.None;
        return true;
    }

    internal bool TryLoadExplicitPair(Switch2JoyConPairId pairId,
        ulong expectedRevision, out Switch2JoyConPairRecord record,
        out Switch2JoyConPairAssociationFailure failure)
    {
        record = default;
        if (!pairId.IsValid || expectedRevision == 0)
        {
            failure = Switch2JoyConPairAssociationFailure.InvalidArgument;
            return false;
        }
        if (!TryLoadCurrent(pairId, out Switch2JoyConPairRecord current,
                out failure))
        {
            return false;
        }
        if (current.Revision != expectedRevision)
        {
            failure = Switch2JoyConPairAssociationFailure.StaleRevision;
            return false;
        }

        record = current;
        failure = Switch2JoyConPairAssociationFailure.None;
        return true;
    }

    public override string ToString() =>
        nameof(Switch2JoyConPairAssociationService);

    private bool TryLoadCurrent(Switch2JoyConPairId pairId,
        out Switch2JoyConPairRecord record,
        out Switch2JoyConPairAssociationFailure failure)
    {
        record = default;
        bool found;
        try
        {
            found = store.TryLoad(pairId, out record);
        }
        catch
        {
            record = default;
            failure = Switch2JoyConPairAssociationFailure.StoreFault;
            return false;
        }
        if (!found)
        {
            record = default;
            failure = Switch2JoyConPairAssociationFailure.NotFound;
            return false;
        }
        if (!record.IsValid || record.PairId != pairId)
        {
            record = default;
            failure = Switch2JoyConPairAssociationFailure.CorruptStoreState;
            return false;
        }

        failure = Switch2JoyConPairAssociationFailure.None;
        return true;
    }

    private static bool TryValidateExactPair(
        in Switch2JoyConAssociationPeer left,
        in Switch2JoyConAssociationPeer right,
        out Switch2JoyConPairAssociationFailure failure)
    {
        if (!left.IsValid || !right.IsValid)
        {
            failure = Switch2JoyConPairAssociationFailure.InvalidPeer;
            return false;
        }
        if (left.Model != Switch2ControllerModel.JoyCon2Left ||
            left.ProductId != Switch2AdvertisementCodec.JoyCon2LeftProductId ||
            right.Model != Switch2ControllerModel.JoyCon2Right ||
            right.ProductId !=
                Switch2AdvertisementCodec.JoyCon2RightProductId)
        {
            failure = Switch2JoyConPairAssociationFailure.InvalidPeerRoles;
            return false;
        }
        if (left.PersistentPeerId == right.PersistentPeerId)
        {
            failure = Switch2JoyConPairAssociationFailure.DuplicatePeer;
            return false;
        }

        failure = Switch2JoyConPairAssociationFailure.None;
        return true;
    }
}
