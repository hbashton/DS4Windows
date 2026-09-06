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
/// Dormant mixed-transport service over exactly one transport-neutral
/// registration transaction core. Typed overloads select only the participant
/// adapter for an already-created exact owner; discovery, association,
/// ControlService integration, hardware I/O, mapping policy, and virtual output
/// remain outside this boundary.
/// </summary>
internal sealed class Switch2RuntimeRegistrationService
{
    private readonly Switch2RuntimeRegistrationTransactionCore core;
    private readonly Func<Switch2ProUsbRuntimeOwner,
        ISwitch2RuntimeRegistrationParticipant> usbParticipantFactory;
    private readonly Func<Switch2BluetoothRuntimeOwner,
        ISwitch2RuntimeRegistrationParticipant> bluetoothParticipantFactory;
    private readonly Func<Switch2JoyConJoinedRuntimeOwner,
        ISwitch2RuntimeRegistrationParticipant> joinedParticipantFactory;

    internal Switch2RuntimeRegistrationService(
        InputControllerRegistrationTable table,
        int lifecycleAttentionTimeoutMilliseconds = 5_000,
        ControlServiceInputSlotAdmission slotAdmission = null) : this(table,
        lifecycleAttentionTimeoutMilliseconds,
        static owner =>
            new Switch2ProUsbRuntimeRegistrationParticipant(owner),
        static owner =>
            new Switch2BluetoothRuntimeRegistrationParticipant(owner),
        static owner =>
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner),
        slotAdmission)
    {
    }

    internal Switch2RuntimeRegistrationService(
        InputControllerRegistrationTable table,
        int lifecycleAttentionTimeoutMilliseconds,
        Func<Switch2ProUsbRuntimeOwner,
            ISwitch2RuntimeRegistrationParticipant> usbParticipantFactory,
        Func<Switch2BluetoothRuntimeOwner,
            ISwitch2RuntimeRegistrationParticipant> bluetoothParticipantFactory,
        Func<Switch2JoyConJoinedRuntimeOwner,
            ISwitch2RuntimeRegistrationParticipant> joinedParticipantFactory,
        ControlServiceInputSlotAdmission slotAdmission = null)
    {
        this.usbParticipantFactory = usbParticipantFactory ??
            throw new ArgumentNullException(nameof(usbParticipantFactory));
        this.bluetoothParticipantFactory = bluetoothParticipantFactory ??
            throw new ArgumentNullException(
                nameof(bluetoothParticipantFactory));
        this.joinedParticipantFactory = joinedParticipantFactory ??
            throw new ArgumentNullException(nameof(joinedParticipantFactory));
        core = new Switch2RuntimeRegistrationTransactionCore(table,
            lifecycleAttentionTimeoutMilliseconds, slotAdmission);
    }

    internal InputControllerRegistrationTable Table => core.Table;

    /// <summary>
    /// Test-visible identity of the sole core gate. The service deliberately
    /// owns no additional lifecycle gate.
    /// </summary>
    internal object LifecycleGate => core.LifecycleGate;

    internal event Action<InputControllerSlotToken> RuntimeRemoved
    {
        add => core.RuntimeRemoved += value;
        remove => core.RuntimeRemoved -= value;
    }

    internal bool TryOpen(ulong exactServiceGeneration,
        out Switch2RuntimeRegistrationTransactionFailure failure) =>
        core.TryOpen(exactServiceGeneration, out failure);

    internal bool TryAdoptOpen(ulong exactServiceGeneration,
        out Switch2RuntimeRegistrationTransactionFailure failure) =>
        core.TryAdoptOpen(exactServiceGeneration, out failure);

    internal bool TryObserveExternalTableClose(
        ulong exactServiceGeneration,
        InputControllerSlotSnapshot[] snapshots,
        out Switch2RuntimeRegistrationTransactionFailure failure) =>
        core.TryObserveExternalTableClose(exactServiceGeneration, snapshots,
            out failure);

    internal bool TryAttach(Switch2ProUsbRuntimeOwner owner,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            mappingCallback == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return core.TryAttach(registration,
            () => usbParticipantFactory(owner), mappingCallback,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttachToHost(Switch2ProUsbRuntimeOwner owner,
        ISwitch2ControlServiceSlotHost host, int timeoutMilliseconds,
        out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            host == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return TryAttachThroughHost(registration,
            () => usbParticipantFactory(owner), host, timeoutMilliseconds,
            out token, out failure);
    }

    internal bool TryAttachExactSlot(int exactSlot,
        Switch2ProUsbRuntimeOwner owner,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            mappingCallback == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return core.TryAttachExactSlot(exactSlot, registration,
            () => usbParticipantFactory(owner), mappingCallback,
            timeoutMilliseconds, out token, out failure);
    }

    /// <summary>
    /// Exact-slot ControlService integration seam. The relay is constructed on
    /// the control path, while the decorated participant is still created only
    /// after the table has accepted the exact slot. Its stable method-group
    /// callback adds no per-report allocation, queue, or cadence source.
    /// </summary>
    internal bool TryAttachExactSlot(int exactSlot,
        Switch2ProUsbRuntimeOwner owner,
        ISwitch2ControlServiceSlotHost host,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            host == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return TryAttachExactSlotThroughHost(exactSlot, registration,
            () => usbParticipantFactory(owner), host, timeoutMilliseconds,
            out token, out failure);
    }

    /// <summary>
    /// Production USB entry point for the already-composed one-handle
    /// participant. It reuses the same transaction core and ControlService
    /// slot decorator as BLE; the participant remains the sole owner of USB
    /// startup, input, output, and whole-composite retirement.
    /// </summary>
    internal bool TryAttachOwnedUsb(
        Switch2ProUsbOwnedCompositeRegistrationParticipant participant,
        ISwitch2ControlServiceSlotHost host, int timeoutMilliseconds,
        out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (participant == null || host == null ||
            participant.State !=
                Switch2ProUsbOwnedCompositeParticipantState.Dormant)
        {
            failure = InvalidArgument();
            return false;
        }

        InputControllerRegistration registration = participant.Registration;
        Switch2ProUsbRuntimeOwner owner = participant.RuntimeOwner;
        if (owner == null || !ReferenceEquals(registration.Owner, owner) ||
            !ReferenceEquals(registration.Device,
                owner.RuntimeInputDevice) ||
            registration.Generation !=
                owner.RuntimeInputDevice.RuntimeGeneration ||
            registration.OwnershipKind !=
                InputControllerOwnershipKind.Switch2Runtime ||
            !registration.IsOwnerAuthenticated)
        {
            failure = InvalidArgument();
            return false;
        }

        return TryAttachThroughHost(registration, () => participant, host,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttach(Switch2BluetoothRuntimeOwner owner,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            mappingCallback == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return core.TryAttach(registration,
            () => bluetoothParticipantFactory(owner), mappingCallback,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttachToHost(Switch2BluetoothRuntimeOwner owner,
        ISwitch2ControlServiceSlotHost host, int timeoutMilliseconds,
        out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            host == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return TryAttachThroughHost(registration,
            () => bluetoothParticipantFactory(owner), host,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttachExactSlot(int exactSlot,
        Switch2BluetoothRuntimeOwner owner,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            mappingCallback == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return core.TryAttachExactSlot(exactSlot, registration,
            () => bluetoothParticipantFactory(owner), mappingCallback,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttachExactSlot(int exactSlot,
        Switch2BluetoothRuntimeOwner owner,
        ISwitch2ControlServiceSlotHost host,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            host == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return TryAttachExactSlotThroughHost(exactSlot, registration,
            () => bluetoothParticipantFactory(owner), host,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttach(Switch2JoyConJoinedRuntimeOwner owner,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            mappingCallback == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return core.TryAttach(registration,
            () => joinedParticipantFactory(owner), mappingCallback,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttachToHost(Switch2JoyConJoinedRuntimeOwner owner,
        ISwitch2ControlServiceSlotHost host, int timeoutMilliseconds,
        out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            host == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return TryAttachThroughHost(registration,
            () => joinedParticipantFactory(owner), host,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttachExactSlot(int exactSlot,
        Switch2JoyConJoinedRuntimeOwner owner,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            mappingCallback == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return core.TryAttachExactSlot(exactSlot, registration,
            () => joinedParticipantFactory(owner), mappingCallback,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryAttachExactSlot(int exactSlot,
        Switch2JoyConJoinedRuntimeOwner owner,
        ISwitch2ControlServiceSlotHost host,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (!TryGetExactRegistration(owner, out var registration) ||
            host == null)
        {
            failure = InvalidArgument();
            return false;
        }
        return TryAttachExactSlotThroughHost(exactSlot, registration,
            () => joinedParticipantFactory(owner), host,
            timeoutMilliseconds, out token, out failure);
    }

    internal bool TryRemove(in InputControllerSlotToken token,
        int timeoutMilliseconds,
        out Switch2RuntimeRegistrationTransactionFailure failure) =>
        core.TryRemove(token, timeoutMilliseconds, out failure);

    internal bool TryClose(ulong exactServiceGeneration,
        int timeoutMilliseconds,
        out Switch2RuntimeRegistrationTransactionFailure failure) =>
        core.TryClose(exactServiceGeneration, timeoutMilliseconds,
            out failure);

    private bool TryAttachExactSlotThroughHost(int exactSlot,
        in InputControllerRegistration registration,
        Func<ISwitch2RuntimeRegistrationParticipant> innerFactory,
        ISwitch2ControlServiceSlotHost host, int timeoutMilliseconds,
        out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        var relay = new ControlServiceParticipantRelay(innerFactory, host);
        return core.TryAttachExactSlot(exactSlot, registration,
            relay.CreateParticipant, relay.Dispatch, timeoutMilliseconds,
            out token, out failure);
    }

    private bool TryAttachThroughHost(
        in InputControllerRegistration registration,
        Func<ISwitch2RuntimeRegistrationParticipant> innerFactory,
        ISwitch2ControlServiceSlotHost host, int timeoutMilliseconds,
        out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        var relay = new ControlServiceParticipantRelay(innerFactory, host);
        return core.TryAttach(registration, relay.CreateParticipant,
            relay.Dispatch, timeoutMilliseconds, out token, out failure);
    }

    private static bool TryGetExactRegistration(
        Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration)
    {
        registration = default;
        if (owner == null)
        {
            return false;
        }
        registration = owner.Registration;
        return ReferenceEquals(registration.Owner, owner) &&
            ReferenceEquals(registration.Device, owner.RuntimeInputDevice) &&
            registration.Generation ==
                owner.RuntimeInputDevice.RuntimeGeneration &&
            registration.OwnershipKind ==
                InputControllerOwnershipKind.Switch2Runtime &&
            registration.IsOwnerAuthenticated;
    }

    private static bool TryGetExactRegistration(
        Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration)
    {
        registration = default;
        if (owner == null)
        {
            return false;
        }
        registration = owner.Registration;
        return ReferenceEquals(registration.Owner, owner) &&
            ReferenceEquals(registration.Device, owner.RuntimeDevice) &&
            registration.Generation == owner.RuntimeDevice.RuntimeGeneration &&
            registration.OwnershipKind ==
                InputControllerOwnershipKind.Switch2Runtime &&
            owner.DependenciesComplete && registration.IsOwnerAuthenticated;
    }

    private static bool TryGetExactRegistration(
        Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration)
    {
        registration = default;
        if (owner == null)
        {
            return false;
        }
        registration = owner.Registration;
        return ReferenceEquals(registration.Owner, owner) &&
            ReferenceEquals(registration.Device, owner.RuntimeDevice) &&
            registration.Generation == owner.RuntimeGeneration &&
            owner.RuntimeGeneration == owner.RuntimeDevice.RuntimeGeneration &&
            registration.OwnershipKind ==
                InputControllerOwnershipKind.Switch2Runtime &&
            owner.DependenciesComplete && registration.IsOwnerAuthenticated;
    }

    private static Switch2RuntimeRegistrationTransactionFailure
        InvalidArgument() => new(
            Switch2RuntimeRegistrationTransactionFailureKind.InvalidArgument);

    /// <summary>
    /// Per-attachment control-path relay. Construction has no participant or
    /// host side effect. The transaction core calls CreateParticipant only
    /// after exact-slot binding, and cannot publish a report until that method
    /// has stored the decorator. Dispatch is a synchronous forwarding call.
    /// </summary>
    private sealed class ControlServiceParticipantRelay
    {
        private readonly Func<ISwitch2RuntimeRegistrationParticipant>
            innerFactory;
        private readonly ISwitch2ControlServiceSlotHost host;
        private Switch2ControlServiceSlotRegistrationParticipant participant;
        private int creationClaimed;

        internal ControlServiceParticipantRelay(
            Func<ISwitch2RuntimeRegistrationParticipant> innerFactory,
            ISwitch2ControlServiceSlotHost host)
        {
            this.innerFactory = innerFactory ??
                throw new ArgumentNullException(nameof(innerFactory));
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        internal ISwitch2RuntimeRegistrationParticipant CreateParticipant()
        {
            if (Interlocked.CompareExchange(ref creationClaimed, 1, 0) != 0)
            {
                return null;
            }

            ISwitch2RuntimeRegistrationParticipant inner = innerFactory();
            if (inner == null)
            {
                return null;
            }
            var decorated =
                new Switch2ControlServiceSlotRegistrationParticipant(inner,
                    host);
            Volatile.Write(ref participant, decorated);
            return decorated;
        }

        internal void Dispatch(int slot, DS4Device sender,
            Switch2RuntimeReportEventArgs report)
        {
            Switch2ControlServiceSlotRegistrationParticipant decorated =
                Volatile.Read(ref participant);
            if (decorated == null)
            {
                throw new InvalidOperationException(
                    "The ControlService participant was not published.");
            }
            decorated.MappingCallback(slot, sender, report);
        }
    }
}
