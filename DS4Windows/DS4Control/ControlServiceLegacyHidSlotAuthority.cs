/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows;

internal enum ControlServiceLegacyHidSlotState : byte
{
    Invalid = 0,
    Bound,
    Activating,
    Attached,
    Retiring,
    Quiesced,
    Removed,
    Quarantined,
}

internal enum ControlServiceLegacyHidSlotFailure : byte
{
    None = 0,
    InvalidArgument,
    UnsupportedDevice,
    ServiceClosed,
    GenerationExhausted,
    SlotOccupied,
    StaleCredential,
    RegistrationRejected,
    TableRejected,
    SubscriptionRejected,
    ActivationRejected,
    WorkerStartRejected,
    TerminalNeutralRejected,
    WorkerStopRejected,
    RegistryRemovalRejected,
    DependencyThrew,
    Quarantined,
}

/// <summary>
/// Injectable seam around the exact legacy worker boundary. Production uses
/// the device-owned typed boundary; deterministic tests can inject failures
/// without opening a HID interface or starting a thread.
/// </summary>
internal interface IControlServiceLegacyHidWorkerLifecycle
{
    DS4DeviceWorkerLifecycleSupport Classify(DS4Device device);

    bool TryStart(DS4Device device,
        out DS4DeviceWorkerLifecycleLease lease,
        out DS4DeviceWorkerLifecycleResult result);

    bool TryStop(DS4Device device,
        in DS4DeviceWorkerLifecycleLease lease, int timeoutMilliseconds,
        out DS4DeviceWorkerLifecycleResult result);
}

internal sealed class ControlServiceLegacyHidDeviceWorkerLifecycle :
    IControlServiceLegacyHidWorkerLifecycle
{
    public DS4DeviceWorkerLifecycleSupport Classify(DS4Device device) =>
        device?.WorkerLifecycleSupport ??
        DS4DeviceWorkerLifecycleSupport.Invalid;

    public bool TryStart(DS4Device device,
        out DS4DeviceWorkerLifecycleLease lease,
        out DS4DeviceWorkerLifecycleResult result) =>
        device.TryStartWorkerLifecycle(out lease, out result);

    public bool TryStop(DS4Device device,
        in DS4DeviceWorkerLifecycleLease lease, int timeoutMilliseconds,
        out DS4DeviceWorkerLifecycleResult result) =>
        device.TryStopWorkerLifecycle(lease, timeoutMilliseconds, out result);
}

/// <summary>
/// Exact retained ControlService record for one typed legacy DS4/DS3 slot
/// lifetime. The issuer, device reference, service generation, connection
/// generation, table token, and exact delegates must all match before cleanup.
/// </summary>
internal sealed class ControlServiceLegacyHidSlotBinding
{
    private readonly object issuer;

    internal ControlServiceLegacyHidSlotBinding(object issuer, int slot,
        ulong serviceGeneration, ulong connectionGeneration,
        DS4Device device,
        in LegacyHidInputControllerLifetimeLease lifetimeLease)
    {
        this.issuer = issuer;
        Slot = slot;
        ServiceGeneration = serviceGeneration;
        ConnectionGeneration = connectionGeneration;
        Device = device;
        LifetimeLease = lifetimeLease;
        State = ControlServiceLegacyHidSlotState.Bound;
    }

    internal object Gate { get; } = new();

    internal object HandlerMutationGate { get; } = new();

    internal int Slot { get; }

    internal ulong ServiceGeneration { get; }

    internal ulong ConnectionGeneration { get; }

    internal DS4Device Device { get; }

    internal LegacyHidInputControllerLifetimeLease LifetimeLease { get; }

    internal LegacyHidInputControllerRegistrationOwner Owner { get; set; }

    internal InputControllerSlotToken Token { get; set; }

    internal InputControllerSetupRollbackClaim RollbackClaim { get; set; }

    internal InputControllerRetirementClaim RetirementClaim { get; set; }

    internal DS4DeviceWorkerLifecycleLease WorkerLease { get; set; }

    internal bool WorkerStartAttempted { get; set; }

    internal DS4DeviceWorkerLifecycleResult WorkerStartResult { get; set; }

    internal ControlServiceLegacyHidSlotState State { get; set; }

    internal EventHandler<EventArgs> RemovalHandler { get; set; }

    internal EventHandler<EventArgs> SyncHandler { get; set; }

    internal EventHandler<EventArgs> RegistrySyncHandler { get; set; }

    internal EventHandler<EventArgs> SerialHandler { get; set; }

    internal EventHandler ChargingHandler { get; set; }

    internal DS4Device.ReportHandler<EventArgs> ReportHandler { get; set; }

    internal ReportDiagnosticsWorker.Source DiagnosticsSource { get; set; }

    internal DS4Device.ReportHandler<EventArgs> MotionHandler { get; set; }

    internal bool RemovalSubscribed { get; set; }

    internal bool SyncSubscribed { get; set; }

    internal bool RegistrySyncSubscribed { get; set; }

    internal bool SerialSubscribed { get; set; }

    internal bool ChargingSubscribed { get; set; }

    internal bool ReportSubscribed { get; set; }

    internal bool MotionSubscribed { get; set; }

    internal bool RemovalQueued { get; set; }

    internal bool RegistryRemoved { get; set; }

    internal bool QuarantinedCleanupProven { get; set; }

    internal bool QuarantinedCleanupInProgress { get; set; }

    internal bool QuarantinedWorkerStopProven { get; set; }

    internal bool TerminalNeutralPublished { get; set; }

    internal bool Authenticates(object expectedIssuer,
        DS4Device expectedDevice, ulong expectedServiceGeneration,
        ulong expectedConnectionGeneration) =>
        ReferenceEquals(issuer, expectedIssuer) &&
        ReferenceEquals(Device, expectedDevice) &&
        ServiceGeneration == expectedServiceGeneration &&
        ConnectionGeneration == expectedConnectionGeneration;
}

/// <summary>
/// Shared table authority for the exact DS4Device and DS3Device worker path.
/// It deliberately rejects every other subtype before registration. It owns no
/// discovery, profile, mapping, output, HidHide, or Switch 2 transport work.
/// </summary>
internal sealed class ControlServiceLegacyHidSlotAuthority :
    ILegacyHidInputControllerLifecycleHost
{
    private readonly object gate = new();
    private readonly object bindingIssuer = new();
    private readonly object lifetimeIssuer = new();
    private readonly ControlServiceLegacyHidSlotBinding[] bindings;
    private readonly IControlServiceLegacyHidWorkerLifecycle workers;
    private readonly Action<DS4Device> removeFromRegistry;
    private readonly InputControllerRegistrationTable table;

    private ulong lastServiceGeneration;
    private ulong currentServiceGeneration;
    private ulong lastConnectionGeneration;

    internal ControlServiceLegacyHidSlotAuthority(int slotCount,
        Action<DS4Device> removeFromRegistry) : this(slotCount,
        removeFromRegistry,
        new ControlServiceLegacyHidDeviceWorkerLifecycle())
    {
    }

    internal ControlServiceLegacyHidSlotAuthority(int slotCount,
        Action<DS4Device> removeFromRegistry,
        IControlServiceLegacyHidWorkerLifecycle workers)
        : this(new InputControllerRegistrationTable(slotCount),
            removeFromRegistry, workers)
    {
    }

    internal ControlServiceLegacyHidSlotAuthority(
        InputControllerRegistrationTable table,
        Action<DS4Device> removeFromRegistry,
        IControlServiceLegacyHidWorkerLifecycle workers)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
        this.removeFromRegistry = removeFromRegistry ??
            throw new ArgumentNullException(nameof(removeFromRegistry));
        this.workers = workers ??
            throw new ArgumentNullException(nameof(workers));
        bindings = new ControlServiceLegacyHidSlotBinding[table.SlotCount];
    }

    internal InputControllerRegistrationTable Table => table;

    internal ulong CurrentServiceGeneration
    {
        get { lock (gate) { return currentServiceGeneration; } }
    }

    internal bool TryOpenNext(out ulong serviceGeneration,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        serviceGeneration = 0;
        lock (gate)
        {
            if (currentServiceGeneration != 0)
            {
                failure = ControlServiceLegacyHidSlotFailure.ServiceClosed;
                tableFailure = InputControllerSlotTableFailure.AlreadyOpen;
                return false;
            }
            if (lastServiceGeneration == ulong.MaxValue)
            {
                failure = ControlServiceLegacyHidSlotFailure.
                    GenerationExhausted;
                tableFailure = InputControllerSlotTableFailure.
                    ServiceGenerationExhausted;
                return false;
            }

            serviceGeneration = lastServiceGeneration + 1;
            if (!table.TryOpen(serviceGeneration, out tableFailure))
            {
                serviceGeneration = 0;
                failure = ControlServiceLegacyHidSlotFailure.TableRejected;
                return false;
            }
            lastServiceGeneration = serviceGeneration;
            currentServiceGeneration = serviceGeneration;
            failure = ControlServiceLegacyHidSlotFailure.None;
            return true;
        }
    }

    internal bool TryClose(out InputControllerSlotSnapshot[] snapshots,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        ulong serviceGeneration;
        lock (gate)
        {
            serviceGeneration = currentServiceGeneration;
            if (serviceGeneration == 0)
            {
                snapshots = Array.Empty<InputControllerSlotSnapshot>();
                failure = ControlServiceLegacyHidSlotFailure.ServiceClosed;
                tableFailure = InputControllerSlotTableFailure.Closed;
                return false;
            }
        }

        if (!table.TryClose(serviceGeneration, out snapshots,
                out tableFailure))
        {
            failure = ControlServiceLegacyHidSlotFailure.TableRejected;
            return false;
        }

        return TryObserveExternalTableClose(serviceGeneration, snapshots,
            out failure, out tableFailure);
    }

    /// <summary>
    /// Completes this authority's local close bookkeeping from the immutable
    /// snapshots produced by the one shared table closer.
    /// </summary>
    internal bool TryObserveExternalTableClose(ulong serviceGeneration,
        InputControllerSlotSnapshot[] snapshots,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        if (serviceGeneration == 0 || snapshots == null || table.IsOpen)
        {
            failure = ControlServiceLegacyHidSlotFailure.InvalidArgument;
            tableFailure = InputControllerSlotTableFailure.InvalidArgument;
            return false;
        }

        lock (gate)
        {
            if (currentServiceGeneration != serviceGeneration)
            {
                failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
                tableFailure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            var seenSlots = new bool[bindings.Length];
            foreach (InputControllerSlotSnapshot snapshot in snapshots)
            {
                bool requiresExactToken = snapshot.State is
                    InputControllerSlotState.Bound or
                    InputControllerSlotState.Retiring or
                    InputControllerSlotState.Quiesced;
                if (snapshot.Slot < 0 || snapshot.Slot >= bindings.Length ||
                    seenSlots[snapshot.Slot] ||
                    requiresExactToken &&
                    (snapshot.ServiceGeneration != serviceGeneration ||
                     snapshot.Token.ServiceGeneration != serviceGeneration ||
                     snapshot.Token.Slot != snapshot.Slot) ||
                    snapshot.Token.IsValid &&
                    snapshot.Token.Slot != snapshot.Slot)
                {
                    failure = ControlServiceLegacyHidSlotFailure.
                        StaleCredential;
                    tableFailure = InputControllerSlotTableFailure.
                        StaleCredential;
                    return false;
                }
                seenSlots[snapshot.Slot] = true;
            }

            currentServiceGeneration = 0;
            foreach (InputControllerSlotSnapshot snapshot in snapshots)
            {
                if (snapshot.Slot < 0 || snapshot.Slot >= bindings.Length)
                {
                    continue;
                }
                ControlServiceLegacyHidSlotBinding binding =
                    bindings[snapshot.Slot];
                if (!IsExactBindingNoLock(binding) ||
                    binding.Token != snapshot.Token)
                {
                    continue;
                }
                lock (binding.Gate)
                {
                    if (snapshot.State == InputControllerSlotState.Retiring)
                    {
                        binding.RetirementClaim = snapshot.RetirementClaim;
                        binding.State =
                            ControlServiceLegacyHidSlotState.Retiring;
                    }
                }
            }
            failure = ControlServiceLegacyHidSlotFailure.None;
            tableFailure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    internal bool TryBindExactSlot(int exactSlot, DS4Device device,
        bool hasPersistentIdentity,
        out ControlServiceLegacyHidSlotBinding binding,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        binding = null;
        tableFailure = InputControllerSlotTableFailure.None;
        if (device == null || exactSlot < 0 || exactSlot >= bindings.Length)
        {
            failure = ControlServiceLegacyHidSlotFailure.InvalidArgument;
            return false;
        }
        if (workers.Classify(device) !=
            DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid)
        {
            failure = ControlServiceLegacyHidSlotFailure.UnsupportedDevice;
            return false;
        }

        ulong serviceGeneration;
        ulong connectionGeneration;
        ControlServiceLegacyHidSlotBinding candidate;
        lock (gate)
        {
            serviceGeneration = currentServiceGeneration;
            if (serviceGeneration == 0)
            {
                failure = ControlServiceLegacyHidSlotFailure.ServiceClosed;
                return false;
            }
            if (bindings[exactSlot] != null)
            {
                failure = ControlServiceLegacyHidSlotFailure.SlotOccupied;
                return false;
            }
            if (lastConnectionGeneration == ulong.MaxValue)
            {
                failure = ControlServiceLegacyHidSlotFailure.
                    GenerationExhausted;
                return false;
            }
            connectionGeneration = ++lastConnectionGeneration;
            var lifetimeLease = new LegacyHidInputControllerLifetimeLease(
                lifetimeIssuer, device, connectionGeneration,
                hasPersistentIdentity);
            candidate = new ControlServiceLegacyHidSlotBinding(bindingIssuer,
                exactSlot, serviceGeneration, connectionGeneration, device,
                lifetimeLease);
            bindings[exactSlot] = candidate;
        }

        if (!LegacyHidInputControllerRegistrationOwner.TryCreate(
                candidate.LifetimeLease, this, out var owner,
                out _, out _))
        {
            RemoveProvisional(candidate);
            failure = ControlServiceLegacyHidSlotFailure.
                RegistrationRejected;
            return false;
        }
        if (!table.TryReserveAndBindExactSlot(exactSlot, owner.Registration,
                out InputControllerSlotToken token,
                out InputControllerSetupRollbackClaim rollbackClaim,
                out tableFailure))
        {
            RemoveProvisional(candidate);
            failure = tableFailure == InputControllerSlotTableFailure.Busy ?
                ControlServiceLegacyHidSlotFailure.SlotOccupied :
                ControlServiceLegacyHidSlotFailure.TableRejected;
            return false;
        }

        lock (candidate.Gate)
        {
            candidate.Owner = owner;
            candidate.Token = token;
            candidate.RollbackClaim = rollbackClaim;
        }
        binding = candidate;
        failure = ControlServiceLegacyHidSlotFailure.None;
        return true;
    }

    internal bool TryGetExactBinding(int slot, DS4Device device,
        out ControlServiceLegacyHidSlotBinding binding)
    {
        binding = null;
        if (device == null || slot < 0 || slot >= bindings.Length)
        {
            return false;
        }
        lock (gate)
        {
            ControlServiceLegacyHidSlotBinding candidate = bindings[slot];
            if (!IsExactBindingNoLock(candidate) ||
                !ReferenceEquals(candidate.Device, device))
            {
                return false;
            }
            binding = candidate;
            return true;
        }
    }

    internal bool TryClaimRemovalQueue(
        ControlServiceLegacyHidSlotBinding binding)
    {
        if (!AuthenticatesBinding(binding))
        {
            return false;
        }
        lock (binding.Gate)
        {
            if (binding.RemovalQueued || binding.State is
                    ControlServiceLegacyHidSlotState.Removed or
                    ControlServiceLegacyHidSlotState.Quarantined)
            {
                return false;
            }
            binding.RemovalQueued = true;
            return true;
        }
    }

    internal bool TrySubscribeLegacyLifecycle(
        ControlServiceLegacyHidSlotBinding binding,
        EventHandler<EventArgs> removalHandler,
        EventHandler<EventArgs> syncHandler,
        EventHandler<EventArgs> registrySyncHandler,
        EventHandler<EventArgs> serialHandler,
        EventHandler chargingHandler,
        out ControlServiceLegacyHidSlotFailure failure)
    {
        if (!AuthenticatesBinding(binding) || removalHandler == null ||
            syncHandler == null || registrySyncHandler == null ||
            serialHandler == null || chargingHandler == null)
        {
            failure = ControlServiceLegacyHidSlotFailure.InvalidArgument;
            return false;
        }

        lock (binding.HandlerMutationGate)
        {
            lock (binding.Gate)
            {
                if (binding.State != ControlServiceLegacyHidSlotState.Bound ||
                    binding.RemovalHandler != null)
                {
                    failure = ControlServiceLegacyHidSlotFailure.
                        StaleCredential;
                    return false;
                }
                binding.RemovalHandler = removalHandler;
                binding.SyncHandler = syncHandler;
                binding.RegistrySyncHandler = registrySyncHandler;
                binding.SerialHandler = serialHandler;
                binding.ChargingHandler = chargingHandler;
            }
            try
            {
                // Mark every delegate as possibly installed before invoking
                // its external event accessor. If an accessor installs and
                // then throws, exact cleanup retains the only safe inverse.
                lock (binding.Gate) { binding.RemovalSubscribed = true; }
                binding.Device.Removal += removalHandler;
                lock (binding.Gate) { binding.SyncSubscribed = true; }
                binding.Device.SyncChange += syncHandler;
                lock (binding.Gate)
                {
                    binding.RegistrySyncSubscribed = true;
                }
                binding.Device.SyncChange += registrySyncHandler;
                lock (binding.Gate) { binding.SerialSubscribed = true; }
                binding.Device.SerialChange += serialHandler;
                lock (binding.Gate) { binding.ChargingSubscribed = true; }
                binding.Device.ChargingChanged += chargingHandler;
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            catch
            {
                failure = ControlServiceLegacyHidSlotFailure.DependencyThrew;
                return false;
            }
        }
    }

    internal bool TrySubscribeReport(
        ControlServiceLegacyHidSlotBinding binding,
        DS4Device.ReportHandler<EventArgs> reportHandler,
        out ControlServiceLegacyHidSlotFailure failure)
    {
        if (!AuthenticatesBinding(binding) || reportHandler == null)
        {
            failure = ControlServiceLegacyHidSlotFailure.InvalidArgument;
            return false;
        }
        lock (binding.HandlerMutationGate)
        {
            lock (binding.Gate)
            {
                if (binding.State !=
                        ControlServiceLegacyHidSlotState.Bound ||
                    binding.ReportHandler != null)
                {
                    failure = ControlServiceLegacyHidSlotFailure.
                        StaleCredential;
                    return false;
                }
                binding.ReportHandler = reportHandler;
            }
            try
            {
                lock (binding.Gate) { binding.ReportSubscribed = true; }
                binding.Device.Report += reportHandler;
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            catch
            {
                failure = ControlServiceLegacyHidSlotFailure.DependencyThrew;
                return false;
            }
        }
    }

    internal bool TryReplaceMotionHandler(
        ControlServiceLegacyHidSlotBinding binding,
        DS4Device.ReportHandler<EventArgs> motionHandler,
        bool subscribe,
        out ControlServiceLegacyHidSlotFailure failure)
    {
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }

        lock (binding.HandlerMutationGate)
        {
            DS4Device.ReportHandler<EventArgs> oldHandler;
            bool oldSubscribed;
            lock (binding.Gate)
            {
                if (binding.State is not
                    (ControlServiceLegacyHidSlotState.Bound or
                        ControlServiceLegacyHidSlotState.Activating or
                        ControlServiceLegacyHidSlotState.Attached))
                {
                    failure = ControlServiceLegacyHidSlotFailure.
                        StaleCredential;
                    return false;
                }
                oldHandler = binding.MotionHandler;
                oldSubscribed = binding.MotionSubscribed;
            }
            try
            {
                if (oldHandler != null && oldSubscribed)
                {
                    // Do not overwrite the old inverse until its removal is
                    // proven. A throwing accessor leaves it retained as
                    // possibly installed.
                    binding.Device.Report -= oldHandler;
                    lock (binding.Gate)
                    {
                        binding.MotionSubscribed = false;
                    }
                }
                if (ReferenceEquals(binding.Device.MotionEvent, oldHandler))
                {
                    binding.Device.MotionEvent = null;
                }
                lock (binding.Gate)
                {
                    binding.MotionHandler = motionHandler;
                    binding.MotionSubscribed = motionHandler != null &&
                        subscribe;
                }
                if (motionHandler != null)
                {
                    binding.Device.MotionEvent = motionHandler;
                    if (subscribe)
                    {
                        binding.Device.Report += motionHandler;
                    }
                }
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            catch
            {
                failure = ControlServiceLegacyHidSlotFailure.DependencyThrew;
                return false;
            }
        }
    }

    internal bool TrySetMotionSubscription(
        ControlServiceLegacyHidSlotBinding binding, bool subscribe,
        out ControlServiceLegacyHidSlotFailure failure)
    {
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        lock (binding.HandlerMutationGate)
        {
            DS4Device.ReportHandler<EventArgs> handler;
            bool current;
            lock (binding.Gate)
            {
                if (binding.State is not
                    (ControlServiceLegacyHidSlotState.Activating or
                        ControlServiceLegacyHidSlotState.Attached))
                {
                    failure = ControlServiceLegacyHidSlotFailure.
                        StaleCredential;
                    return false;
                }
                handler = binding.MotionHandler;
                current = binding.MotionSubscribed;
            }
            if (handler == null || current == subscribe)
            {
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            try
            {
                if (subscribe)
                {
                    // Retain possible installation before the add accessor.
                    lock (binding.Gate)
                    {
                        binding.MotionSubscribed = true;
                    }
                    binding.Device.Report += handler;
                }
                else
                {
                    binding.Device.Report -= handler;
                    lock (binding.Gate)
                    {
                        binding.MotionSubscribed = false;
                    }
                }
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            catch
            {
                failure = ControlServiceLegacyHidSlotFailure.DependencyThrew;
                return false;
            }
        }
    }

    internal bool TryStartAndActivate(
        ControlServiceLegacyHidSlotBinding binding,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure,
        out DS4DeviceWorkerLifecycleResult workerResult)
    {
        tableFailure = InputControllerSlotTableFailure.None;
        workerResult = default;
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        lock (binding.Gate)
        {
            if (binding.State != ControlServiceLegacyHidSlotState.Bound ||
                !binding.ReportSubscribed)
            {
                failure = ControlServiceLegacyHidSlotFailure.
                    SubscriptionRejected;
                return false;
            }
            binding.State = ControlServiceLegacyHidSlotState.Activating;
        }

        if (!table.TryBeginActivate(binding.Token, out var activationClaim,
                out tableFailure))
        {
            table.TryQuarantine(binding.RollbackClaim,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
                out _);
            lock (binding.Gate)
            {
                binding.State = ControlServiceLegacyHidSlotState.Quarantined;
            }
            failure = ControlServiceLegacyHidSlotFailure.ActivationRejected;
            return false;
        }
        if (!table.TryAcquireActivationCommit(activationClaim,
                out var commitCredential, out tableFailure))
        {
            table.TryCompleteActivate(activationClaim,
                externalCommitSucceeded: false, out _);
            lock (binding.Gate)
            {
                binding.State = ControlServiceLegacyHidSlotState.Quarantined;
            }
            failure = ControlServiceLegacyHidSlotFailure.ActivationRejected;
            return false;
        }

        bool started;
        DS4DeviceWorkerLifecycleLease workerLease;
        lock (binding.Gate)
        {
            // Publish the attempt before crossing the external worker
            // boundary. A throw without a cleanup lease is therefore never
            // misclassified as "start was not called" during recovery.
            binding.WorkerStartAttempted = true;
        }
        try
        {
            started = workers.TryStart(binding.Device, out workerLease,
                out workerResult);
        }
        catch
        {
            started = false;
            workerLease = default;
            workerResult = DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.StartDependencyThrew);
        }
        lock (binding.Gate)
        {
            binding.WorkerLease = workerLease;
            binding.WorkerStartResult = workerResult;
        }
        bool tableCompleted = table.TryCompleteActivate(commitCredential,
            started && workerResult.Succeeded, out tableFailure);
        if (!tableCompleted)
        {
            table.TryQuarantine(binding.Token,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
                out _);
        }
        lock (binding.Gate)
        {
            binding.State = tableCompleted && started &&
                    workerResult.Succeeded ?
                ControlServiceLegacyHidSlotState.Attached :
                ControlServiceLegacyHidSlotState.Quarantined;
        }
        if (!tableCompleted || !started || !workerResult.Succeeded)
        {
            failure = started ?
                ControlServiceLegacyHidSlotFailure.ActivationRejected :
                ControlServiceLegacyHidSlotFailure.WorkerStartRejected;
            return false;
        }
        failure = ControlServiceLegacyHidSlotFailure.None;
        return true;
    }

    internal bool TryAcquireReport(
        ControlServiceLegacyHidSlotBinding binding, DS4Device sender,
        out InputControllerReportLease lease,
        out InputControllerSlotTableFailure failure)
    {
        lease = default;
        if (!AuthenticatesBinding(binding) ||
            !ReferenceEquals(sender, binding.Device))
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }
        return table.TryAcquireReportLease(binding.Token, sender, out lease,
            out failure);
    }

    internal bool TryBeginRetirement(
        ControlServiceLegacyHidSlotBinding binding,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        tableFailure = InputControllerSlotTableFailure.None;
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        lock (binding.Gate)
        {
            if (binding.State == ControlServiceLegacyHidSlotState.Retiring &&
                binding.RetirementClaim.IsValid)
            {
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            if (binding.State != ControlServiceLegacyHidSlotState.Attached)
            {
                failure = binding.State ==
                        ControlServiceLegacyHidSlotState.Quarantined ?
                    ControlServiceLegacyHidSlotFailure.Quarantined :
                    ControlServiceLegacyHidSlotFailure.StaleCredential;
                return false;
            }
        }
        if (!table.TryBeginRetire(binding.Token, out var claim,
                out tableFailure))
        {
            failure = ControlServiceLegacyHidSlotFailure.TableRejected;
            return false;
        }
        lock (binding.Gate)
        {
            binding.RetirementClaim = claim;
            binding.State = ControlServiceLegacyHidSlotState.Retiring;
        }
        failure = ControlServiceLegacyHidSlotFailure.None;
        return true;
    }

    internal bool TryPublishTerminalNeutral(
        ControlServiceLegacyHidSlotBinding binding, Action publishNeutral,
        int timeoutMilliseconds,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        tableFailure = InputControllerSlotTableFailure.None;
        if (!AuthenticatesBinding(binding) || publishNeutral == null)
        {
            failure = ControlServiceLegacyHidSlotFailure.InvalidArgument;
            return false;
        }
        InputControllerRetirementClaim claim;
        lock (binding.Gate)
        {
            claim = binding.RetirementClaim;
            if (binding.State != ControlServiceLegacyHidSlotState.Retiring ||
                !claim.IsValid)
            {
                failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
                return false;
            }
            if (binding.TerminalNeutralPublished)
            {
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
        }
        if (!table.TryWaitForDrain(claim, timeoutMilliseconds,
                out tableFailure) ||
            !table.TryAcquireTerminalReportLease(claim, binding.Device,
                out InputControllerReportLease terminalLease,
                out tableFailure))
        {
            failure = ControlServiceLegacyHidSlotFailure.
                TerminalNeutralRejected;
            return false;
        }
        using (terminalLease)
        {
            try
            {
                publishNeutral();
            }
            catch
            {
                table.TryQuarantine(claim,
                    InputControllerSlotQuarantineReason.
                        TerminalNeutralNotObserved, out tableFailure);
                lock (binding.Gate)
                {
                    binding.State =
                        ControlServiceLegacyHidSlotState.Quarantined;
                }
                failure = ControlServiceLegacyHidSlotFailure.DependencyThrew;
                return false;
            }
            if (!terminalLease.TryAcknowledgeTerminalNeutral(
                    out tableFailure))
            {
                failure = ControlServiceLegacyHidSlotFailure.
                    TerminalNeutralRejected;
                return false;
            }
            lock (binding.Gate)
            {
                binding.TerminalNeutralPublished = true;
            }
        }
        failure = ControlServiceLegacyHidSlotFailure.None;
        return true;
    }

    internal bool TryFinalizeRetirement(
        ControlServiceLegacyHidSlotBinding binding, int timeoutMilliseconds,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure,
        Action<DS4Device> afterStopBeforeRemove = null)
    {
        tableFailure = InputControllerSlotTableFailure.None;
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        InputControllerRetirementClaim claim;
        LegacyHidInputControllerRegistrationOwner owner;
        lock (binding.Gate)
        {
            claim = binding.RetirementClaim;
            owner = binding.Owner;
            if (binding.State != ControlServiceLegacyHidSlotState.Retiring ||
                !claim.IsValid || owner == null)
            {
                failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
                return false;
            }
        }

        if (!TryDetachExactHandlers(binding, out failure))
        {
            table.TryQuarantine(claim,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
                out tableFailure);
            MarkQuarantined(binding);
            return false;
        }
        if (!owner.Registration.TryStopAndQuiesce(timeoutMilliseconds,
                out InputControllerOwnerOperationFailure stopFailure))
        {
            table.TryQuarantine(claim,
                stopFailure == InputControllerOwnerOperationFailure.OwnerThrew ?
                    InputControllerSlotQuarantineReason.OwnerThrew :
                    InputControllerSlotQuarantineReason.StopRejected,
                out tableFailure);
            MarkQuarantined(binding);
            failure = ControlServiceLegacyHidSlotFailure.WorkerStopRejected;
            return false;
        }
        if (afterStopBeforeRemove != null)
        {
            try
            {
                afterStopBeforeRemove(binding.Device);
            }
            catch
            {
                table.TryQuarantine(claim,
                    InputControllerSlotQuarantineReason.OwnerThrew,
                    out tableFailure);
                MarkQuarantined(binding);
                failure = ControlServiceLegacyHidSlotFailure.DependencyThrew;
                return false;
            }
        }
        if (!table.TryMarkQuiesced(claim, out tableFailure))
        {
            table.TryQuarantine(claim,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
                out _);
            MarkQuarantined(binding);
            failure = ControlServiceLegacyHidSlotFailure.TableRejected;
            return false;
        }
        lock (binding.Gate)
        {
            binding.State = ControlServiceLegacyHidSlotState.Quiesced;
        }
        if (!owner.Registration.TryRemove(
                out InputControllerOwnerOperationFailure removeFailure))
        {
            table.TryQuarantine(claim,
                removeFailure == InputControllerOwnerOperationFailure.OwnerThrew ?
                    InputControllerSlotQuarantineReason.OwnerThrew :
                    InputControllerSlotQuarantineReason.RemoveRejected,
                out tableFailure);
            MarkQuarantined(binding);
            failure = ControlServiceLegacyHidSlotFailure.
                RegistryRemovalRejected;
            return false;
        }
        if (!table.TryCompleteRemoval(claim, out tableFailure))
        {
            MarkQuarantined(binding);
            failure = ControlServiceLegacyHidSlotFailure.TableRejected;
            return false;
        }
        lock (binding.Gate)
        {
            binding.State = ControlServiceLegacyHidSlotState.Removed;
        }
        lock (gate)
        {
            if (ReferenceEquals(bindings[binding.Slot], binding))
            {
                bindings[binding.Slot] = null;
            }
        }
        failure = ControlServiceLegacyHidSlotFailure.None;
        return true;
    }

    internal bool TryRollbackPrepared(
        ControlServiceLegacyHidSlotBinding binding,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        tableFailure = InputControllerSlotTableFailure.None;
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        lock (binding.Gate)
        {
            if (binding.State != ControlServiceLegacyHidSlotState.Bound)
            {
                failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
                return false;
            }
        }
        if (!TryDetachExactHandlers(binding, out failure))
        {
            table.TryQuarantine(binding.RollbackClaim,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
                out tableFailure);
            MarkQuarantined(binding);
            return false;
        }
        if (!table.TryRollback(binding.RollbackClaim, out tableFailure))
        {
            failure = ControlServiceLegacyHidSlotFailure.TableRejected;
            return false;
        }
        lock (binding.Gate)
        {
            binding.State = ControlServiceLegacyHidSlotState.Removed;
        }
        lock (gate)
        {
            if (ReferenceEquals(bindings[binding.Slot], binding))
            {
                bindings[binding.Slot] = null;
            }
        }
        failure = ControlServiceLegacyHidSlotFailure.None;
        return true;
    }

    internal bool TryQuarantinePrepared(
        ControlServiceLegacyHidSlotBinding binding,
        out ControlServiceLegacyHidSlotFailure failure,
        out InputControllerSlotTableFailure tableFailure)
    {
        tableFailure = InputControllerSlotTableFailure.None;
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        lock (binding.Gate)
        {
            if (binding.State ==
                ControlServiceLegacyHidSlotState.Quarantined)
            {
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            if (binding.State != ControlServiceLegacyHidSlotState.Bound)
            {
                failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
                return false;
            }
        }
        TryDetachExactHandlers(binding, out _);
        if (!table.TryQuarantine(binding.RollbackClaim,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
                out tableFailure))
        {
            failure = ControlServiceLegacyHidSlotFailure.TableRejected;
            return false;
        }
        MarkQuarantined(binding);
        failure = ControlServiceLegacyHidSlotFailure.None;
        return true;
    }

    /// <summary>
    /// Bounded recovery for an activation which is permanently quarantined by
    /// the table after external start became visible. The table slot remains
    /// quarantined, but its exact possibly-live worker and registry lifetime
    /// are not leaked. No later binding can adopt or reuse this credential.
    /// </summary>
    internal bool TryRecoverQuarantinedActivation(
        ControlServiceLegacyHidSlotBinding binding, int timeoutMilliseconds,
        out ControlServiceLegacyHidSlotFailure failure)
    {
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            failure = ControlServiceLegacyHidSlotFailure.InvalidArgument;
            return false;
        }
        LegacyHidInputControllerRegistrationOwner owner;
        DS4DeviceWorkerLifecycleLease workerLease;
        DS4DeviceWorkerLifecycleResult workerStartResult;
        bool workerStartAttempted;
        lock (binding.Gate)
        {
            if (binding.State !=
                    ControlServiceLegacyHidSlotState.Quarantined)
            {
                failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
                return false;
            }
            if (binding.QuarantinedCleanupProven)
            {
                failure = ControlServiceLegacyHidSlotFailure.None;
                return true;
            }
            if (binding.QuarantinedCleanupInProgress)
            {
                failure = ControlServiceLegacyHidSlotFailure.Quarantined;
                return false;
            }
            binding.QuarantinedCleanupInProgress = true;
            owner = binding.Owner;
            workerLease = binding.WorkerLease;
            workerStartAttempted = binding.WorkerStartAttempted;
            workerStartResult = binding.WorkerStartResult;
        }
        bool cleanupProven = false;
        try
        {
            if (!TryDetachExactHandlers(binding, out failure))
            {
                return false;
            }

            LegacyHidInputControllerOwnerState ownerState = owner?.State ??
                LegacyHidInputControllerOwnerState.Created;
            if (ownerState is
                    LegacyHidInputControllerOwnerState.StopInProgress or
                    LegacyHidInputControllerOwnerState.RemoveInProgress)
            {
                failure = ControlServiceLegacyHidSlotFailure.Quarantined;
                return false;
            }

            bool stopProven;
            lock (binding.Gate)
            {
                stopProven = binding.QuarantinedWorkerStopProven;
            }
            if (ownerState is LegacyHidInputControllerOwnerState.Quiesced or
                    LegacyHidInputControllerOwnerState.Removed)
            {
                stopProven = true;
            }
            if (!stopProven && workerLease.IsValid)
            {
                bool stopped;
                DS4DeviceWorkerLifecycleResult stopResult;
                try
                {
                    stopped = workers.TryStop(binding.Device, workerLease,
                        timeoutMilliseconds, out stopResult);
                }
                catch
                {
                    stopped = false;
                    stopResult = DS4DeviceWorkerLifecycleResult.Uncertain(
                        DS4DeviceWorkerLifecycleOperation.Stop,
                        DS4DeviceWorkerLifecycleFailureKind.
                            StopDependencyThrew);
                }
                if (!stopped || !stopResult.Succeeded)
                {
                    failure = ControlServiceLegacyHidSlotFailure.
                        WorkerStopRejected;
                    return false;
                }
                stopProven = true;
                lock (binding.Gate)
                {
                    binding.QuarantinedWorkerStopProven = true;
                }
            }
            else if (!stopProven && workerStartAttempted &&
                (!workerStartResult.IsValid ||
                    workerStartResult.RequiresQuarantine ||
                    workerStartResult.Succeeded))
            {
                // External start may have become visible, but no exact worker
                // lease exists. Registry removal cannot prove worker quiescence
                // and must not be attempted.
                failure = ControlServiceLegacyHidSlotFailure.
                    WorkerStopRejected;
                return false;
            }

            bool registryRemoved;
            lock (binding.Gate)
            {
                registryRemoved = binding.RegistryRemoved;
            }
            if (!registryRemoved)
            {
                try
                {
                    removeFromRegistry(binding.Device);
                }
                catch
                {
                    failure = ControlServiceLegacyHidSlotFailure.
                        DependencyThrew;
                    return false;
                }
                lock (binding.Gate)
                {
                    binding.RegistryRemoved = true;
                }
            }

            lock (binding.Gate)
            {
                binding.QuarantinedCleanupProven = true;
            }
            cleanupProven = true;
            failure = ControlServiceLegacyHidSlotFailure.None;
            return true;
        }
        finally
        {
            lock (binding.Gate)
            {
                binding.QuarantinedCleanupInProgress = false;
                if (!cleanupProven)
                {
                    binding.QuarantinedCleanupProven = false;
                }
            }
        }
    }

    internal bool TryDetachExactHandlers(
        ControlServiceLegacyHidSlotBinding binding,
        out ControlServiceLegacyHidSlotFailure failure)
    {
        if (!AuthenticatesBinding(binding))
        {
            failure = ControlServiceLegacyHidSlotFailure.StaleCredential;
            return false;
        }
        bool uncertain = false;
        lock (binding.HandlerMutationGate)
        {
            binding.DiagnosticsSource?.Retire();
            DS4Device.ReportHandler<EventArgs> motionHandler;
            DS4Device.ReportHandler<EventArgs> reportHandler;
            EventHandler chargingHandler;
            EventHandler<EventArgs> serialHandler;
            EventHandler<EventArgs> registrySyncHandler;
            EventHandler<EventArgs> syncHandler;
            EventHandler<EventArgs> removalHandler;
            bool motionSubscribed;
            bool reportSubscribed;
            bool chargingSubscribed;
            bool serialSubscribed;
            bool registrySyncSubscribed;
            bool syncSubscribed;
            bool removalSubscribed;
            lock (binding.Gate)
            {
                motionHandler = binding.MotionHandler;
                reportHandler = binding.ReportHandler;
                chargingHandler = binding.ChargingHandler;
                serialHandler = binding.SerialHandler;
                registrySyncHandler = binding.RegistrySyncHandler;
                syncHandler = binding.SyncHandler;
                removalHandler = binding.RemovalHandler;
                motionSubscribed = binding.MotionSubscribed;
                reportSubscribed = binding.ReportSubscribed;
                chargingSubscribed = binding.ChargingSubscribed;
                serialSubscribed = binding.SerialSubscribed;
                registrySyncSubscribed = binding.RegistrySyncSubscribed;
                syncSubscribed = binding.SyncSubscribed;
                removalSubscribed = binding.RemovalSubscribed;
            }

            if (motionSubscribed && motionHandler != null)
            {
                try
                {
                    binding.Device.Report -= motionHandler;
                    lock (binding.Gate)
                    {
                        binding.MotionSubscribed = false;
                    }
                }
                catch { uncertain = true; }
            }
            if (ReferenceEquals(binding.Device.MotionEvent, motionHandler))
            {
                binding.Device.MotionEvent = null;
            }
            if (reportSubscribed && reportHandler != null)
            {
                try
                {
                    binding.Device.Report -= reportHandler;
                    lock (binding.Gate)
                    {
                        binding.ReportSubscribed = false;
                    }
                }
                catch { uncertain = true; }
            }
            if (chargingSubscribed && chargingHandler != null)
            {
                try
                {
                    binding.Device.ChargingChanged -= chargingHandler;
                    lock (binding.Gate)
                    {
                        binding.ChargingSubscribed = false;
                    }
                }
                catch { uncertain = true; }
            }
            if (serialSubscribed && serialHandler != null)
            {
                try
                {
                    binding.Device.SerialChange -= serialHandler;
                    lock (binding.Gate)
                    {
                        binding.SerialSubscribed = false;
                    }
                }
                catch { uncertain = true; }
            }
            if (registrySyncSubscribed && registrySyncHandler != null)
            {
                try
                {
                    binding.Device.SyncChange -= registrySyncHandler;
                    lock (binding.Gate)
                    {
                        binding.RegistrySyncSubscribed = false;
                    }
                }
                catch { uncertain = true; }
            }
            if (syncSubscribed && syncHandler != null)
            {
                try
                {
                    binding.Device.SyncChange -= syncHandler;
                    lock (binding.Gate)
                    {
                        binding.SyncSubscribed = false;
                    }
                }
                catch { uncertain = true; }
            }
            if (removalSubscribed && removalHandler != null)
            {
                try
                {
                    binding.Device.Removal -= removalHandler;
                    lock (binding.Gate)
                    {
                        binding.RemovalSubscribed = false;
                    }
                }
                catch { uncertain = true; }
            }
        }
        failure = uncertain ?
            ControlServiceLegacyHidSlotFailure.DependencyThrew :
            ControlServiceLegacyHidSlotFailure.None;
        return !uncertain;
    }

    public bool Authenticates(
        in LegacyHidInputControllerLifetimeLease lease)
    {
        if (!lease.IsValid)
        {
            return false;
        }
        lock (gate)
        {
            foreach (ControlServiceLegacyHidSlotBinding binding in bindings)
            {
                if (binding == null ||
                    !ReferenceEquals(binding.Device, lease.Device) ||
                    binding.ConnectionGeneration != lease.Generation ||
                    binding.LifetimeLease != lease ||
                    !lease.Authenticates(lifetimeIssuer, binding.Device,
                        binding.ConnectionGeneration))
                {
                    continue;
                }
                lock (binding.Gate)
                {
                    return binding.State !=
                        ControlServiceLegacyHidSlotState.Removed;
                }
            }
            return false;
        }
    }

    public LegacyHidInputControllerLifecycleResult TryStopAndQuiesce(
        in LegacyHidInputControllerLifetimeLease lease,
        int timeoutMilliseconds)
    {
        if (!TryFindByLease(lease, out var binding))
        {
            return LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                LegacyHidInputControllerLifecycleFailureKind.
                    InvalidCredential);
        }
        DS4DeviceWorkerLifecycleLease workerLease;
        lock (binding.Gate)
        {
            workerLease = binding.WorkerLease;
        }
        bool stopped = workers.TryStop(binding.Device, workerLease,
            timeoutMilliseconds, out DS4DeviceWorkerLifecycleResult result);
        if (stopped && result.Succeeded)
        {
            return LegacyHidInputControllerLifecycleResult.Success(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce);
        }
        if (result.IsValid && !result.RequiresQuarantine)
        {
            return LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                LegacyHidInputControllerLifecycleFailureKind.StopRejected);
        }
        return LegacyHidInputControllerLifecycleResult.Uncertain(
            LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
            result.IsValid && result.FailureKind ==
                    DS4DeviceWorkerLifecycleFailureKind.StopTimedOut ?
                LegacyHidInputControllerLifecycleFailureKind.StopTimedOut :
                LegacyHidInputControllerLifecycleFailureKind.DependencyThrew);
    }

    public LegacyHidInputControllerLifecycleResult TryRemove(
        in LegacyHidInputControllerLifetimeLease lease)
    {
        if (!TryFindByLease(lease, out var binding))
        {
            return LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.Remove,
                LegacyHidInputControllerLifecycleFailureKind.
                    InvalidCredential);
        }
        lock (binding.Gate)
        {
            if (binding.RegistryRemoved)
            {
                return LegacyHidInputControllerLifecycleResult.Success(
                    LegacyHidInputControllerLifecycleOperation.Remove);
            }
        }
        try
        {
            removeFromRegistry(binding.Device);
        }
        catch
        {
            return LegacyHidInputControllerLifecycleResult.Uncertain(
                LegacyHidInputControllerLifecycleOperation.Remove,
                LegacyHidInputControllerLifecycleFailureKind.DependencyThrew);
        }
        lock (binding.Gate)
        {
            binding.RegistryRemoved = true;
        }
        return LegacyHidInputControllerLifecycleResult.Success(
            LegacyHidInputControllerLifecycleOperation.Remove);
    }

    private bool AuthenticatesBinding(
        ControlServiceLegacyHidSlotBinding binding)
    {
        if (binding == null || binding.Slot < 0 ||
            binding.Slot >= bindings.Length)
        {
            return false;
        }
        lock (gate)
        {
            return IsExactBindingNoLock(binding);
        }
    }

    private bool IsExactBindingNoLock(
        ControlServiceLegacyHidSlotBinding binding) =>
        binding != null &&
        ReferenceEquals(bindings[binding.Slot], binding) &&
        binding.Authenticates(bindingIssuer, binding.Device,
            binding.ServiceGeneration, binding.ConnectionGeneration);

    private bool TryFindByLease(
        in LegacyHidInputControllerLifetimeLease lease,
        out ControlServiceLegacyHidSlotBinding binding)
    {
        binding = null;
        if (!lease.IsValid)
        {
            return false;
        }
        lock (gate)
        {
            foreach (ControlServiceLegacyHidSlotBinding candidate in bindings)
            {
                if (candidate != null &&
                    candidate.LifetimeLease == lease &&
                    lease.Authenticates(lifetimeIssuer, candidate.Device,
                        candidate.ConnectionGeneration))
                {
                    binding = candidate;
                    return true;
                }
            }
            return false;
        }
    }

    private void RemoveProvisional(
        ControlServiceLegacyHidSlotBinding binding)
    {
        lock (gate)
        {
            if (ReferenceEquals(bindings[binding.Slot], binding))
            {
                bindings[binding.Slot] = null;
            }
        }
    }

    private static void MarkQuarantined(
        ControlServiceLegacyHidSlotBinding binding)
    {
        lock (binding.Gate)
        {
            binding.State = ControlServiceLegacyHidSlotState.Quarantined;
        }
    }
}
