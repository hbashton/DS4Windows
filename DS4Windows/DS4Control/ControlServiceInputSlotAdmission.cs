/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System;
using System.Threading;

namespace DS4Windows;

/// <summary>
/// Bridges table-owned runtimes and legacy devices whose worker lifecycle is
/// not table-owned. The gate protects only vacancy selection and the first
/// ownership claim: no participant, mapping, profile or transport work runs
/// here. A legacy array claim and a runtime table reservation exclude one
/// another before either path starts preparing the selected slot.
/// </summary>
internal sealed class ControlServiceInputSlotAdmission
{
    private readonly object gate = new();
    private readonly InputControllerRegistrationTable table;
    private readonly DS4Device[] controllers;
    private readonly ControllerSlotManager slots;
    private readonly int slotLimit;

    internal ControlServiceInputSlotAdmission(
        InputControllerRegistrationTable table, DS4Device[] controllers,
        ControllerSlotManager slots, int slotLimit = int.MaxValue)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
        this.controllers = controllers ?? throw new ArgumentNullException(nameof(controllers));
        this.slots = slots ?? throw new ArgumentNullException(nameof(slots));
        if (controllers.Length != table.SlotCount)
            throw new ArgumentException("The controller array and registration table must have identical cardinality.", nameof(controllers));
        if (slotLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotLimit));
        this.slotLimit = Math.Min(slotLimit, table.SlotCount);
    }

    internal InputControllerRegistrationTable Table => table;

    internal bool TryClaimLegacySlot(int slot, DS4Device device)
    {
        if (device == null || slot < 0 || slot >= slotLimit) return false;
        lock (gate)
        {
            if (!table.IsOpen || !IsExternalSlotVacant(slot)) return false;
            InputControllerSlotState state = table.GetSnapshot()[slot].State;
            if (state is not (InputControllerSlotState.Empty or InputControllerSlotState.Removed))
                return false;
            Volatile.Write(ref controllers[slot], device);
            return true;
        }
    }

    /// <summary>Called only after exact legacy per-slot cleanup is complete.</summary>
    internal bool TryReleaseLegacySlot(int slot, DS4Device device)
    {
        if (device == null || slot < 0 || slot >= slotLimit) return false;
        lock (gate)
        {
            if (!ReferenceEquals(controllers[slot], device)) return false;
            Volatile.Write(ref controllers[slot], null);
            return true;
        }
    }

    internal bool TryReserveAndBind(int exactSlot,
        InputControllerRegistration registration,
        out InputControllerSlotToken token,
        out InputControllerSetupRollbackClaim rollbackClaim,
        out InputControllerSlotTableFailure failure)
    {
        token = default;
        rollbackClaim = default;
        if (exactSlot < -1 || exactSlot >= slotLimit)
        {
            failure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }
        lock (gate)
        {
            if (!table.IsOpen)
            {
                failure = InputControllerSlotTableFailure.Closed;
                return false;
            }
            int first = exactSlot < 0 ? 0 : exactSlot;
            int end = exactSlot < 0 ? slotLimit : exactSlot + 1;
            for (int slot = first; slot < end; slot++)
            {
                if (!IsExternalSlotVacant(slot)) continue;
                if (table.TryReserveAndBindExactSlot(slot, registration,
                        out token, out rollbackClaim, out failure)) return true;
                // A busy slot has made no owner mutation. Keep looking before
                // constructing/adopting any participant; never retry an owner
                // that has already reached failed preparation and rollback.
                if (failure != InputControllerSlotTableFailure.Busy) return false;
            }
            failure = exactSlot < 0 ? InputControllerSlotTableFailure.Full :
                InputControllerSlotTableFailure.Busy;
            return false;
        }
    }

    private bool IsExternalSlotVacant(int slot)
    {
        if (Volatile.Read(ref controllers[slot]) != null) return false;
        slots.CollectionLocker.EnterReadLock();
        try
        {
            // Retained dictionary membership also keeps a partially cleaned
            // legacy slot unavailable, even if its array is inconsistent.
            return !slots.ControllerDict.ContainsKey(slot);
        }
        finally { slots.CollectionLocker.ExitReadLock(); }
    }
}
