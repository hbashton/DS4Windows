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

internal enum Switch2ControlServiceReversibleStageOutcome : byte
{
    Invalid = 0,
    Succeeded,
    ProvenRejected,
    OutcomeUncertain,
}

internal enum Switch2ControlServiceReversibleStageFailureKind : byte
{
    None = 0,
    InvalidCredential,
    SlotOccupied,
    SlotChanged,
    ProfileSetupRejected,
    CleanupRejected,
    DependencyThrew,
}

/// <summary>
/// Strict result for one reversible staging facet.  A proven rejection means
/// that facet made no mutation.  Success and uncertain outcomes which may have
/// mutated must return an authenticated retained inverse to their caller.
/// </summary>
internal readonly struct Switch2ControlServiceReversibleStageResult
{
    private Switch2ControlServiceReversibleStageResult(
        Switch2ControlServiceReversibleStageOutcome outcome,
        Switch2ControlServiceReversibleStageFailureKind failureKind)
    {
        Outcome = outcome;
        FailureKind = failureKind;
    }

    internal Switch2ControlServiceReversibleStageOutcome Outcome { get; }

    internal Switch2ControlServiceReversibleStageFailureKind FailureKind
    {
        get;
    }

    internal bool IsValid => Outcome is
            Switch2ControlServiceReversibleStageOutcome.Succeeded or
            Switch2ControlServiceReversibleStageOutcome.ProvenRejected or
            Switch2ControlServiceReversibleStageOutcome.OutcomeUncertain &&
        (Outcome == Switch2ControlServiceReversibleStageOutcome.Succeeded ?
            FailureKind ==
                Switch2ControlServiceReversibleStageFailureKind.None :
            FailureKind !=
                Switch2ControlServiceReversibleStageFailureKind.None);

    internal bool Succeeded => IsValid && Outcome ==
        Switch2ControlServiceReversibleStageOutcome.Succeeded;

    internal static Switch2ControlServiceReversibleStageResult Success() =>
        new(Switch2ControlServiceReversibleStageOutcome.Succeeded,
            Switch2ControlServiceReversibleStageFailureKind.None);

    internal static Switch2ControlServiceReversibleStageResult Reject(
        Switch2ControlServiceReversibleStageFailureKind failureKind) =>
        Failure(Switch2ControlServiceReversibleStageOutcome.ProvenRejected,
            failureKind);

    internal static Switch2ControlServiceReversibleStageResult Uncertain(
        Switch2ControlServiceReversibleStageFailureKind failureKind) =>
        Failure(Switch2ControlServiceReversibleStageOutcome.OutcomeUncertain,
            failureKind);

    private static Switch2ControlServiceReversibleStageResult Failure(
        Switch2ControlServiceReversibleStageOutcome outcome,
        Switch2ControlServiceReversibleStageFailureKind failureKind)
    {
        if (failureKind is <=
                Switch2ControlServiceReversibleStageFailureKind.None or >
                Switch2ControlServiceReversibleStageFailureKind.
                    DependencyThrew)
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        return new Switch2ControlServiceReversibleStageResult(outcome,
            failureKind);
    }
}

/// <summary>
/// Exact non-discovery request handed to a future reversible profile facet.
/// The private issuer and complete table token prevent a slot number, runtime
/// generation, or device reference from being promoted into authority.
/// </summary>
internal readonly struct Switch2ControlServiceProfileStageRequest
{
    private readonly object issuer;

    internal Switch2ControlServiceProfileStageRequest(object issuer,
        in InputControllerSlotToken token)
    {
        this.issuer = issuer;
        Token = token;
    }

    internal InputControllerSlotToken Token { get; }

    internal int Slot => Token.Slot;

    internal DS4Device Device => Token.Registration.Device;

    internal ulong ServiceGeneration => Token.ServiceGeneration;

    internal ulong SlotGeneration => Token.SlotGeneration;

    internal ulong RuntimeGeneration => Token.Registration.Generation;

    internal bool IsValid => issuer != null && Token.IsValid &&
        Token.Registration.OwnershipKind ==
            InputControllerOwnershipKind.Switch2Runtime &&
        Device is Switch2RuntimeInputDevice runtime &&
        runtime.RuntimeGeneration == RuntimeGeneration;

    internal bool Authenticates(object expectedIssuer,
        in InputControllerSlotToken expectedToken) => IsValid &&
        ReferenceEquals(issuer, expectedIssuer) && Token == expectedToken;
}

/// <summary>
/// One-shot exact inverse returned by a future profile/touch/output staging
/// facet.  A successful inverse must prove that only this request's mutations
/// were undone; a copied or stale request must be rejected without mutation.
/// </summary>
internal interface ISwitch2ControlServiceReversibleProfileStageInverse
{
    bool Authenticates(in Switch2ControlServiceProfileStageRequest request);

    Switch2ControlServiceReversibleStageResult TryUndo(
        in Switch2ControlServiceProfileStageRequest request);
}

/// <summary>
/// Missing production facet for profile, touch, hooks, and virtual output.
/// Implementations run under the ControlService lifecycle gate and must return
/// a retained exact inverse for every outcome which may have mutated state.
/// ControlService deliberately does not implement this contract yet.
/// </summary>
internal interface ISwitch2ControlServiceReversibleProfileStage
{
    Switch2ControlServiceReversibleStageResult TryPrepare(
        in Switch2ControlServiceProfileStageRequest request,
        out ISwitch2ControlServiceReversibleProfileStageInverse inverse);
}

/// <summary>
/// Optional control-path diagnostic implemented by the production profile
/// stage. Test hosts and alternate stages do not need to implement it.
/// </summary>
internal interface ISwitch2ControlServiceProfileStageDiagnostics
{
    string LastPrepareDiagnostic { get; }
}

/// <summary>
/// Pre-bound method group for ControlService.On_Report.  It is invoked
/// synchronously and receives the existing runtime envelope unchanged.
/// </summary>
internal delegate void Switch2ControlServiceExistingReportPipeline(
    DS4Device device, EventArgs report, int slot);

// Same canonical mapper, with the exact cold-stage observation handle. The
// report must never reacquire observation authority from a mutable slot lookup.
internal delegate void Switch2ControlServiceObservedReportPipeline(
    DS4Device device, EventArgs report, int slot,
    ReportDiagnosticsWorker.Source diagnosticsSource);

/// <summary>
/// Retained exact inverse for the small slot-array/slot-manager subset.  The
/// flags are advanced only after each individual mutation is proven, allowing
/// cleanup to resume after a partial failure without touching a newer value.
/// </summary>
internal sealed class Switch2ControlServiceExactSlotInverse
{
    private readonly object issuer;

    internal Switch2ControlServiceExactSlotInverse(object issuer,
        in Switch2ControlServiceProfileStageRequest request,
        int previousDeviceSlotNumber)
    {
        this.issuer = issuer;
        RequestToken = request.Token;
        Device = request.Device;
        Slot = request.Slot;
        PreviousDeviceSlotNumber = previousDeviceSlotNumber;
    }

    internal InputControllerSlotToken RequestToken { get; }

    internal DS4Device Device { get; }

    internal int Slot { get; }

    internal int PreviousDeviceSlotNumber { get; }

    internal bool ArrayInstalled { get; set; }

    internal bool DeviceSlotNumberInstalled { get; set; }

    internal bool CollectionInstalled { get; set; }

    internal bool SlotDictionaryInstalled { get; set; }

    internal bool ReverseDictionaryInstalled { get; set; }

    internal bool Consumed { get; set; }

    internal bool Authenticates(object expectedIssuer,
        in Switch2ControlServiceProfileStageRequest request) => !Consumed &&
        ReferenceEquals(issuer, expectedIssuer) &&
        RequestToken == request.Token && ReferenceEquals(Device,
            request.Device) && Slot == request.Slot;
}

/// <summary>
/// Exact tokenized transaction for the only currently reversible
/// ControlService subset: DS4Controllers[slot], DeviceSlotNumber, and the
/// three ControllerSlotManager indexes.  It never calls the legacy
/// AddController/RemoveController pair because those methods are neither
/// failure-atomic nor exact-generation aware.
/// </summary>
internal sealed class Switch2ControlServiceExactSlotCollectionsStage
{
    private readonly object issuer;
    private readonly object lifecycleGate;
    private readonly DS4Device[] controllers;
    private readonly ControllerSlotManager slotManager;

    internal Switch2ControlServiceExactSlotCollectionsStage(object issuer,
        object lifecycleGate, DS4Device[] controllers,
        ControllerSlotManager slotManager)
    {
        this.issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
        this.lifecycleGate = lifecycleGate ??
            throw new ArgumentNullException(nameof(lifecycleGate));
        this.controllers = controllers ??
            throw new ArgumentNullException(nameof(controllers));
        this.slotManager = slotManager ??
            throw new ArgumentNullException(nameof(slotManager));
    }

    internal Switch2ControlServiceReversibleStageResult TryPrepare(
        in Switch2ControlServiceProfileStageRequest request,
        out Switch2ControlServiceExactSlotInverse inverse)
    {
        inverse = null;
        if (!Monitor.IsEntered(lifecycleGate) || !request.IsValid ||
            request.Slot < 0 || request.Slot >= controllers.Length)
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.
                    InvalidCredential);
        }

        int slot = request.Slot;
        DS4Device device = request.Device;
        bool lockTaken = false;
        try
        {
            slotManager.CollectionLocker.EnterWriteLock();
            lockTaken = true;
            if (controllers[slot] != null ||
                slotManager.ControllerDict.ContainsKey(slot) ||
                slotManager.ReverseControllerDict.ContainsKey(device) ||
                ContainsExact(slotManager.ControllerColl, device))
            {
                return Switch2ControlServiceReversibleStageResult.Reject(
                    Switch2ControlServiceReversibleStageFailureKind.
                        SlotOccupied);
            }

            inverse = new Switch2ControlServiceExactSlotInverse(issuer,
                request, device.DeviceSlotNumber);
            try
            {
                controllers[slot] = device;
                inverse.ArrayInstalled = true;

                if (device.DeviceSlotNumber != slot)
                {
                    int previous = inverse.PreviousDeviceSlotNumber;
                    try
                    {
                        device.DeviceSlotNumber = slot;
                    }
                    finally
                    {
                        inverse.DeviceSlotNumberInstalled =
                            previous != slot &&
                            device.DeviceSlotNumber == slot;
                    }
                }

                slotManager.ControllerColl.Add(device);
                inverse.CollectionInstalled = true;
                slotManager.ControllerDict.Add(slot, device);
                inverse.SlotDictionaryInstalled = true;
                slotManager.ReverseControllerDict.Add(device, slot);
                inverse.ReverseDictionaryInstalled = true;
                return Switch2ControlServiceReversibleStageResult.Success();
            }
            catch
            {
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.
                        DependencyThrew);
            }
        }
        catch
        {
            return Switch2ControlServiceReversibleStageResult.Uncertain(
                Switch2ControlServiceReversibleStageFailureKind.
                    DependencyThrew);
        }
        finally
        {
            if (lockTaken)
            {
                slotManager.CollectionLocker.ExitWriteLock();
            }
        }
    }

    internal Switch2ControlServiceReversibleStageResult TryUndo(
        in Switch2ControlServiceProfileStageRequest request,
        Switch2ControlServiceExactSlotInverse inverse)
    {
        if (!Monitor.IsEntered(lifecycleGate) || inverse == null ||
            !inverse.Authenticates(issuer, request))
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.
                    InvalidCredential);
        }

        bool lockTaken = false;
        try
        {
            slotManager.CollectionLocker.EnterWriteLock();
            lockTaken = true;
            if (!CanUndoExactNoLock(inverse))
            {
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.
                        SlotChanged);
            }

            try
            {
                if (inverse.ReverseDictionaryInstalled)
                {
                    if (!slotManager.ReverseControllerDict.Remove(
                            inverse.Device))
                    {
                        return CleanupRejected();
                    }
                    inverse.ReverseDictionaryInstalled = false;
                }
                if (inverse.SlotDictionaryInstalled)
                {
                    if (!slotManager.ControllerDict.Remove(inverse.Slot))
                    {
                        return CleanupRejected();
                    }
                    inverse.SlotDictionaryInstalled = false;
                }
                if (inverse.CollectionInstalled)
                {
                    int exactIndex = FindExact(slotManager.ControllerColl,
                        inverse.Device);
                    if (exactIndex < 0)
                    {
                        return CleanupRejected();
                    }
                    slotManager.ControllerColl.RemoveAt(exactIndex);
                    inverse.CollectionInstalled = false;
                }
                if (inverse.DeviceSlotNumberInstalled)
                {
                    try
                    {
                        inverse.Device.DeviceSlotNumber =
                            inverse.PreviousDeviceSlotNumber;
                    }
                    finally
                    {
                        if (inverse.Device.DeviceSlotNumber ==
                            inverse.PreviousDeviceSlotNumber)
                        {
                            inverse.DeviceSlotNumberInstalled = false;
                        }
                    }
                }
                if (inverse.ArrayInstalled)
                {
                    controllers[inverse.Slot] = null;
                    inverse.ArrayInstalled = false;
                }

                inverse.Consumed = true;
                return Switch2ControlServiceReversibleStageResult.Success();
            }
            catch
            {
                return CleanupRejected();
            }
        }
        finally
        {
            if (lockTaken)
            {
                slotManager.CollectionLocker.ExitWriteLock();
            }
        }
    }

    private bool CanUndoExactNoLock(
        Switch2ControlServiceExactSlotInverse inverse)
    {
        if (inverse.ArrayInstalled && !ReferenceEquals(
                controllers[inverse.Slot], inverse.Device) ||
            inverse.DeviceSlotNumberInstalled &&
                inverse.Device.DeviceSlotNumber != inverse.Slot)
        {
            return false;
        }
        if (inverse.CollectionInstalled && CountExact(
                slotManager.ControllerColl, inverse.Device) != 1)
        {
            return false;
        }
        if (inverse.SlotDictionaryInstalled &&
            (!slotManager.ControllerDict.TryGetValue(inverse.Slot,
                    out DS4Device slotDevice) ||
                !ReferenceEquals(slotDevice, inverse.Device)))
        {
            return false;
        }
        if (inverse.ReverseDictionaryInstalled &&
            (!slotManager.ReverseControllerDict.TryGetValue(inverse.Device,
                    out int reverseSlot) || reverseSlot != inverse.Slot))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsExact(
        System.Collections.Generic.List<DS4Device> devices,
        DS4Device expected) => FindExact(devices, expected) >= 0;

    private static int FindExact(
        System.Collections.Generic.List<DS4Device> devices,
        DS4Device expected)
    {
        for (int index = 0; index < devices.Count; index++)
        {
            if (ReferenceEquals(devices[index], expected))
            {
                return index;
            }
        }
        return -1;
    }

    private static int CountExact(
        System.Collections.Generic.List<DS4Device> devices,
        DS4Device expected)
    {
        int count = 0;
        for (int index = 0; index < devices.Count; index++)
        {
            if (ReferenceEquals(devices[index], expected))
            {
                count++;
            }
        }
        return count;
    }

    private static Switch2ControlServiceReversibleStageResult
        CleanupRejected() =>
        Switch2ControlServiceReversibleStageResult.Uncertain(
            Switch2ControlServiceReversibleStageFailureKind.CleanupRejected);
}

/// <summary>
/// Retained exact inverse for the per-slot Mouse object used by the existing
/// mapping pipeline. The instance itself is the authority: cleanup never
/// clears a different Mouse installed by a newer lifetime.
/// </summary>
internal sealed class Switch2ControlServiceExactMouseSlotInverse
{
    private readonly InputControllerSlotToken token;
    private readonly DS4Device device;
    private readonly Mouse mouse;
    private readonly int slot;
    private bool consumed;

    internal Switch2ControlServiceExactMouseSlotInverse(
        in Switch2ControlServiceProfileStageRequest request, Mouse mouse)
    {
        token = request.Token;
        device = request.Device;
        slot = request.Slot;
        this.mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
    }

    internal Mouse Mouse => mouse;

    internal bool Authenticates(
        in Switch2ControlServiceProfileStageRequest request) => !consumed &&
        request.IsValid && request.Token == token && request.Slot == slot &&
        ReferenceEquals(request.Device, device);

    internal void Consume() => consumed = true;
}

/// <summary>
/// Exact reversible subset for ControlService.touchPad[slot]. Construction of
/// the Mouse happens before the array mutation. Prepare and undo both require
/// the exact table epoch, lifecycle gate, runtime device, slot number, and
/// current array occupant. This is a composable profile prerequisite; it does
/// not load a profile, install hooks, or create a virtual output device.
/// </summary>
internal sealed class Switch2ControlServiceExactMouseSlotStage
{
    private readonly InputControllerRegistrationTable table;
    private readonly object lifecycleGate;
    private readonly DS4Device[] controllers;
    private readonly Mouse[] touchPads;
    private readonly Func<int, DS4Device, Mouse> mouseFactory;

    internal Switch2ControlServiceExactMouseSlotStage(
        InputControllerRegistrationTable table, object lifecycleGate,
        DS4Device[] controllers, Mouse[] touchPads)
        : this(table, lifecycleGate, controllers, touchPads,
            static (slot, device) => new Mouse(slot, device))
    {
    }

    internal Switch2ControlServiceExactMouseSlotStage(
        InputControllerRegistrationTable table, object lifecycleGate,
        DS4Device[] controllers, Mouse[] touchPads,
        Func<int, DS4Device, Mouse> mouseFactory)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
        this.lifecycleGate = lifecycleGate ??
            throw new ArgumentNullException(nameof(lifecycleGate));
        this.controllers = controllers ??
            throw new ArgumentNullException(nameof(controllers));
        this.touchPads = touchPads ??
            throw new ArgumentNullException(nameof(touchPads));
        this.mouseFactory = mouseFactory ??
            throw new ArgumentNullException(nameof(mouseFactory));
    }

    internal Switch2ControlServiceReversibleStageResult TryPrepare(
        in Switch2ControlServiceProfileStageRequest request,
        out Switch2ControlServiceExactMouseSlotInverse inverse)
    {
        inverse = null;
        if (!Monitor.IsEntered(lifecycleGate) || !request.IsValid ||
            request.Slot < 0 || request.Slot >= controllers.Length ||
            request.Slot >= touchPads.Length ||
            !table.TryAuthenticateBoundExternalStage(request.Token, out _))
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.
                    InvalidCredential);
        }

        int slot = request.Slot;
        DS4Device device = request.Device;
        if (!ReferenceEquals(controllers[slot], device) ||
            device.DeviceSlotNumber != slot)
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.SlotChanged);
        }
        if (touchPads[slot] != null)
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.SlotOccupied);
        }

        Mouse mouse;
        try
        {
            mouse = mouseFactory(slot, device);
        }
        catch
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.
                    DependencyThrew);
        }
        if (mouse == null)
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.
                    DependencyThrew);
        }

        inverse = new Switch2ControlServiceExactMouseSlotInverse(request,
            mouse);
        touchPads[slot] = mouse;
        return Switch2ControlServiceReversibleStageResult.Success();
    }

    internal Switch2ControlServiceReversibleStageResult TryUndo(
        in Switch2ControlServiceProfileStageRequest request,
        Switch2ControlServiceExactMouseSlotInverse inverse)
    {
        if (!Monitor.IsEntered(lifecycleGate) || inverse == null ||
            !inverse.Authenticates(request) ||
            !TryAuthenticateCleanup(request.Token))
        {
            return Switch2ControlServiceReversibleStageResult.Reject(
                Switch2ControlServiceReversibleStageFailureKind.
                    InvalidCredential);
        }

        int slot = request.Slot;
        if (slot < 0 || slot >= controllers.Length ||
            slot >= touchPads.Length ||
            !ReferenceEquals(controllers[slot], request.Device) ||
            request.Device.DeviceSlotNumber != slot ||
            !ReferenceEquals(touchPads[slot], inverse.Mouse))
        {
            return Switch2ControlServiceReversibleStageResult.Uncertain(
                Switch2ControlServiceReversibleStageFailureKind.SlotChanged);
        }

        touchPads[slot] = null;
        inverse.Consume();
        return Switch2ControlServiceReversibleStageResult.Success();
    }

    private bool TryAuthenticateCleanup(in InputControllerSlotToken token)
    {
        if (table.TryAuthenticateExactExternalCleanup(token,
                InputControllerSlotState.Bound, out _))
        {
            return true;
        }
        return table.TryAuthenticateExactExternalCleanup(token,
            InputControllerSlotState.Quiesced, out _);
    }
}

/// <summary>
/// Production ControlService host. Composes exact reversible slot, Mouse and
/// profile stages, then runs projected gyro and the existing On_Report mapper
/// under one already-admitted registration report. It never subscribes Mouse
/// to the raw SixAxis event, which runs before registration report admission.
/// </summary>
internal sealed class Switch2ControlServiceReversibleProfileSlotHost :
    ISwitch2ControlServiceSlotHost
{
    private sealed class StageRecord
    {
        internal StageRecord(in Switch2ControlServiceProfileStageRequest request)
        {
            Request = request;
        }

        internal Switch2ControlServiceProfileStageRequest Request { get; }
        internal Switch2ControlServiceExactSlotInverse ExactInverse { get; set; }
        internal Switch2ControlServiceExactMouseSlotInverse MouseInverse
        {
            get;
            set;
        }
        internal ISwitch2ControlServiceReversibleProfileStageInverse
            ProfileInverse { get; set; }
        internal bool ExactCleanupRequired { get; set; }
        internal bool MouseCleanupRequired { get; set; }
        internal bool ProfileCleanupRequired { get; set; }
        internal bool UnownedCleanupRisk { get; set; }
        internal bool Prepared { get; set; }
        internal bool TerminalAccepted { get; set; }
        internal bool OperationActive { get; set; }
        internal bool DispatchActive { get; set; }
        internal ControlServiceMouseCallbackSubscription GyroOwner { get; set; }
        internal SixAxisEventArgs GyroEnvelope { get; } = new(default, null);
        internal bool GyroTerminalPrepared { get; set; }
        internal UdpMotionObservationWorker.Source UdpObservation { get; set; }
        internal ReportDiagnosticsWorker.Source DiagnosticsSource { get; set; }
    }

    private readonly object lifecycleGate;
    private readonly object stageIssuer = new();
    private readonly InputControllerRegistrationTable table;
    private readonly DS4Device[] controllers;
    private readonly Mouse[] touchPads;
    private readonly ISwitch2ControlServiceReversibleProfileStage profileStage;
    private readonly Switch2ControlServiceExistingReportPipeline pipeline;
    private readonly Switch2ControlServiceExactSlotCollectionsStage exactStage;
    private readonly Switch2ControlServiceExactMouseSlotStage mouseStage;
    private readonly StageRecord[] records;
    private readonly Switch2UdpMotionObserver udpObserver;
    private readonly ReportDiagnosticsWorker diagnosticsWorker;
    private readonly Switch2ControlServiceObservedReportPipeline observedPipeline;
    private long udpObservationFailures;
    private long diagnosticsRegistrationFailures;
    private string lastPreparePhase = "never-entered";

    internal Switch2ControlServiceReversibleProfileSlotHost(
        InputControllerRegistrationTable table, object lifecycleGate,
        DS4Device[] controllers, Mouse[] touchPads,
        ControllerSlotManager slotManager,
        ISwitch2ControlServiceReversibleProfileStage profileStage,
        Switch2ControlServiceExistingReportPipeline pipeline,
        Switch2UdpMotionObserver udpObserver = null,
        ReportDiagnosticsWorker diagnosticsWorker = null,
        Switch2ControlServiceObservedReportPipeline observedPipeline = null)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
        this.lifecycleGate = lifecycleGate ??
            throw new ArgumentNullException(nameof(lifecycleGate));
        this.controllers = controllers ??
            throw new ArgumentNullException(nameof(controllers));
        this.touchPads = touchPads ??
            throw new ArgumentNullException(nameof(touchPads));
        this.profileStage = profileStage ??
            throw new ArgumentNullException(nameof(profileStage));
        this.pipeline = pipeline ??
            throw new ArgumentNullException(nameof(pipeline));
        this.udpObserver = udpObserver;
        if ((diagnosticsWorker == null) != (observedPipeline == null))
            throw new ArgumentException("Diagnostics require both the worker and the observed canonical callback.");
        this.diagnosticsWorker = diagnosticsWorker;
        this.observedPipeline = observedPipeline;
        if (slotManager == null)
        {
            throw new ArgumentNullException(nameof(slotManager));
        }
        if (controllers.Length != table.SlotCount ||
            touchPads.Length != controllers.Length)
        {
            throw new ArgumentException(
                "The ControlService slot arrays and registration table must have identical cardinality.",
                nameof(controllers));
        }

        records = new StageRecord[controllers.Length];
        exactStage = new Switch2ControlServiceExactSlotCollectionsStage(
            stageIssuer, lifecycleGate, controllers, slotManager);
        mouseStage = new Switch2ControlServiceExactMouseSlotStage(table,
            lifecycleGate, controllers, touchPads);
    }

    public Switch2ControlServiceSlotHostResult TryPrepare(
        in Switch2ControlServiceSlotLease lease)
    {
        lastPreparePhase = "entered";
        const Switch2ControlServiceSlotHostOperation operation =
            Switch2ControlServiceSlotHostOperation.Prepare;
        if (!TryValidateLease(lease, out InputControllerSlotToken token))
        {
            lastPreparePhase = "invalid-lease";
            return Reject(operation,
                Switch2ControlServiceSlotHostFailureKind.InvalidCredential);
        }

        lock (lifecycleGate)
        {
            if (!table.TryAuthenticateBoundExternalStage(token,
                    out InputControllerSlotTableFailure tableFailure))
            {
                lastPreparePhase = $"table-rejected:{tableFailure}";
                return Reject(operation, MapTableFailure(tableFailure));
            }

            StageRecord current = records[token.Slot];
            if (current != null)
            {
                if (current.Request.Token != token)
                {
                    return Reject(operation,
                        Switch2ControlServiceSlotHostFailureKind.SlotOccupied);
                }
                if (current.OperationActive || current.DispatchActive)
                {
                    return Uncertain(operation,
                        Switch2ControlServiceSlotHostFailureKind.
                            DependencyThrew);
                }
                return current.Prepared ? Success(operation) :
                    Uncertain(operation,
                        Switch2ControlServiceSlotHostFailureKind.
                            CleanupRejected);
            }

            var request = new Switch2ControlServiceProfileStageRequest(
                stageIssuer, token);
            var record = new StageRecord(request)
            {
                OperationActive = true,
            };
            records[token.Slot] = record;

            Switch2ControlServiceReversibleStageResult exactResult =
                exactStage.TryPrepare(request, out var exactInverse);
            lastPreparePhase = $"exact:{exactResult.Outcome}/" +
                $"{exactResult.FailureKind}";
            if (exactResult.IsValid && exactResult.Outcome ==
                    Switch2ControlServiceReversibleStageOutcome.
                        ProvenRejected && exactInverse != null)
            {
                // Proven rejection and retained mutation authority are
                // contradictory evidence.  Calling that alleged inverse could
                // mutate unrelated state, so retain the slot and quarantine.
                record.UnownedCleanupRisk = true;
                record.OperationActive = false;
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
            }
            if (exactInverse != null && exactInverse.Authenticates(
                    stageIssuer, request))
            {
                record.ExactInverse = exactInverse;
                record.ExactCleanupRequired = true;
            }
            if (!exactResult.IsValid || exactResult.Succeeded &&
                    !record.ExactCleanupRequired ||
                !exactResult.Succeeded && exactResult.Outcome !=
                    Switch2ControlServiceReversibleStageOutcome.
                        ProvenRejected &&
                    !record.ExactCleanupRequired)
            {
                record.UnownedCleanupRisk = true;
                record.OperationActive = false;
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
            }
            if (!exactResult.Succeeded)
            {
                return CompleteFailedPrepareNoLock(record, operation,
                    exactResult, exactResult.FailureKind ==
                        Switch2ControlServiceReversibleStageFailureKind.
                            SlotOccupied ?
                        Switch2ControlServiceSlotHostFailureKind.SlotOccupied :
                        Switch2ControlServiceSlotHostFailureKind.
                            DependencyThrew);
            }

            Switch2ControlServiceReversibleStageResult mouseResult =
                mouseStage.TryPrepare(request, out var mouseInverse);
            lastPreparePhase = $"mouse:{mouseResult.Outcome}/" +
                $"{mouseResult.FailureKind}";
            if (mouseResult.IsValid && mouseResult.Outcome ==
                    Switch2ControlServiceReversibleStageOutcome.
                        ProvenRejected && mouseInverse != null)
            {
                record.UnownedCleanupRisk = true;
                record.OperationActive = false;
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
            }
            if (mouseInverse != null && mouseInverse.Authenticates(request))
            {
                record.MouseInverse = mouseInverse;
                record.MouseCleanupRequired = true;
            }
            if (!mouseResult.IsValid || mouseResult.Succeeded &&
                    !record.MouseCleanupRequired ||
                !mouseResult.Succeeded && mouseResult.Outcome !=
                    Switch2ControlServiceReversibleStageOutcome.
                        ProvenRejected && !record.MouseCleanupRequired)
            {
                record.UnownedCleanupRisk = true;
                record.OperationActive = false;
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
            }
            if (!mouseResult.Succeeded)
            {
                return CompleteFailedPrepareNoLock(record, operation,
                    mouseResult, mouseResult.FailureKind ==
                        Switch2ControlServiceReversibleStageFailureKind.
                            SlotOccupied ?
                        Switch2ControlServiceSlotHostFailureKind.SlotOccupied :
                        Switch2ControlServiceSlotHostFailureKind.
                            ProfileSetupRejected);
            }

            Switch2ControlServiceReversibleStageResult profileResult;
            ISwitch2ControlServiceReversibleProfileStageInverse
                profileInverse;
            try
            {
                profileResult = profileStage.TryPrepare(request,
                    out profileInverse);
            }
            catch
            {
                profileResult =
                    Switch2ControlServiceReversibleStageResult.Uncertain(
                        Switch2ControlServiceReversibleStageFailureKind.
                            DependencyThrew);
                profileInverse = null;
            }
            string profileDiagnostic = profileStage is
                    ISwitch2ControlServiceProfileStageDiagnostics diagnostics ?
                diagnostics.LastPrepareDiagnostic : "unavailable";
            lastPreparePhase = $"profile:{profileResult.Outcome}/" +
                $"{profileResult.FailureKind}/{profileDiagnostic}";

            bool inverseAuthenticated = false;
            if (profileResult.IsValid && profileResult.Outcome ==
                    Switch2ControlServiceReversibleStageOutcome.
                        ProvenRejected && profileInverse != null)
            {
                // A clean rejection cannot also confer cleanup authority.
                // Treat the contradiction as unknown mutation ownership and
                // never invoke the alleged inverse.
                record.UnownedCleanupRisk = true;
                record.OperationActive = false;
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
            }
            if (profileInverse != null)
            {
                try
                {
                    inverseAuthenticated =
                        profileInverse.Authenticates(request);
                }
                catch
                {
                }
            }
            if (inverseAuthenticated)
            {
                record.ProfileInverse = profileInverse;
                record.ProfileCleanupRequired = true;
            }
            if (!profileResult.IsValid || profileResult.Succeeded &&
                    !record.ProfileCleanupRequired ||
                !profileResult.Succeeded && profileResult.Outcome !=
                    Switch2ControlServiceReversibleStageOutcome.
                        ProvenRejected &&
                    !record.ProfileCleanupRequired)
            {
                record.UnownedCleanupRisk = true;
                record.OperationActive = false;
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
            }
            if (!profileResult.Succeeded)
            {
                return CompleteFailedPrepareNoLock(record, operation,
                    profileResult,
                    Switch2ControlServiceSlotHostFailureKind.
                        ProfileSetupRejected);
            }

            try
            {
                Mouse mouse = record.MouseInverse.Mouse;
                if (!ReferenceEquals(controllers[token.Slot], request.Device) ||
                    !ReferenceEquals(touchPads[token.Slot], mouse) ||
                    !ReferenceEquals(mouse.BoundDevice, request.Device))
                    throw new InvalidOperationException("The gyro mapping owner changed during profile preparation.");
                // Keep the inverse before activation so every partial setup
                // has exact cleanup authority. Direct mode adds no raw event
                // callbacks: Binding.HandleReport already owns our table lease.
                record.GyroOwner = new ControlServiceMouseCallbackSubscription(
                    mouse, request.Device, token.Slot);
                record.GyroOwner.ActivateDirectPublication();
            }
            catch
            {
                lastPreparePhase = "gyro-owner:rejected";
                return CompleteFailedPrepareNoLock(record, operation,
                    Switch2ControlServiceReversibleStageResult.Reject(
                        Switch2ControlServiceReversibleStageFailureKind.DependencyThrew),
                    Switch2ControlServiceSlotHostFailureKind.ProfileSetupRejected);
            }

            // Optional DSU failure must never reject a valid gameplay source.
            // No controller state is borrowed until the first admitted report.
            try
            {
                record.UdpObservation = udpObserver?.Register(token);
                if (udpObserver != null && token.Slot < UdpServer.NUMBER_SLOTS && record.UdpObservation == null)
                    Interlocked.Increment(ref udpObservationFailures);
            }
            catch { Interlocked.Increment(ref udpObservationFailures); }
            try
            {
                record.DiagnosticsSource = diagnosticsWorker?.Register(token.Slot, request.Device);
                if (diagnosticsWorker != null && record.DiagnosticsSource == null)
                    Interlocked.Increment(ref diagnosticsRegistrationFailures);
            }
            catch { Interlocked.Increment(ref diagnosticsRegistrationFailures); }
            record.Prepared = true;
            record.OperationActive = false;
            lastPreparePhase = "succeeded";
            return Success(operation);
        }
    }

    internal string LastPreparePhase => lastPreparePhase;
    internal long UdpObservationFailureCount => Interlocked.Read(ref udpObservationFailures);
    internal long DiagnosticsRegistrationFailureCount => Interlocked.Read(ref diagnosticsRegistrationFailures);

    public Switch2ControlServiceSlotHostResult TryDispatch(
        in Switch2ControlServiceSlotLease lease, DS4Device sender,
        Switch2RuntimeReportEventArgs report)
    {
        Switch2ControlServiceSlotHostOperation operation = report?.Kind ==
                Switch2RuntimeReportKind.TerminalNeutral ?
            Switch2ControlServiceSlotHostOperation.DispatchTerminalNeutral :
            Switch2ControlServiceSlotHostOperation.DispatchRegular;
        if (!TryValidateLease(lease, out InputControllerSlotToken token) ||
            report == null || !ReferenceEquals(sender,
                token.Registration.Device) ||
            report.RuntimeGeneration != token.Registration.Generation ||
            report.Kind is not (Switch2RuntimeReportKind.Regular or
                Switch2RuntimeReportKind.TerminalNeutral))
        {
            return Reject(operation,
                Switch2ControlServiceSlotHostFailureKind.InvalidCredential);
        }

        StageRecord record;
        lock (lifecycleGate)
        {
            record = records[token.Slot];
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral &&
                !table.TryAuthenticateRetiringExternalTerminal(token,
                    out _))
            {
                return Reject(operation,
                    Switch2ControlServiceSlotHostFailureKind.
                        TerminalNeutralRejected);
            }
            if (record == null || record.Request.Token != token ||
                !record.Prepared || record.OperationActive ||
                record.DispatchActive ||
                !ReferenceEquals(controllers[token.Slot], sender) ||
                record.MouseInverse == null || record.GyroOwner == null ||
                !ReferenceEquals(touchPads[token.Slot],
                    record.MouseInverse.Mouse) ||
                sender.DeviceSlotNumber != token.Slot ||
                report.Kind == Switch2RuntimeReportKind.Regular &&
                    (record.TerminalAccepted || record.GyroTerminalPrepared) ||
                report.Kind == Switch2RuntimeReportKind.TerminalNeutral &&
                    record.TerminalAccepted)
            {
                return Reject(operation,
                    Switch2ControlServiceSlotHostFailureKind.CallbackRejected);
            }
            record.DispatchActive = true;
        }

        bool succeeded = false;
        try
        {
            var runtime = (Switch2RuntimeInputDevice)sender;
            if (!runtime.TryBorrowCurrentPublication(report,
                    out DS4State rawState, out bool hasMotion))
                return Reject(operation, Switch2ControlServiceSlotHostFailureKind.CallbackRejected);

            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
            {
                record.UdpObservation?.Retire();
                record.DiagnosticsSource?.Retire();
                // Terminal publication is serialized after regular callbacks.
                // Never wait for a callback while holding a lifecycle gate.
                if (!record.GyroOwner.TryRetire(0))
                    return Uncertain(operation, Switch2ControlServiceSlotHostFailureKind.CleanupRejected);
                if (!record.GyroTerminalPrepared)
                {
                    record.MouseInverse.Mouse.PrepareGyroNeutralReport(terminal: true);
                    record.GyroTerminalPrepared = true;
                }
            }
            else if (hasMotion)
            {
                record.GyroEnvelope.Reset(rawState.ReportTimeStamp, rawState.Motion);
                if (!record.GyroOwner.TryInvokeProjectedMotion(sender.SixAxis, record.GyroEnvelope))
                    return Reject(operation, Switch2ControlServiceSlotHostFailureKind.CallbackRejected);
            }
            else
            {
                // A valid report without motion is different from an invalid
                // publication. Release prior transient contributions without
                // fabricating a sample or changing the user's toggle latch.
                record.MouseInverse.Mouse.PrepareGyroNeutralReport(terminal: false);
            }
            if (observedPipeline != null)
                observedPipeline(sender, report, token.Slot, record.DiagnosticsSource);
            else
                pipeline(sender, report, token.Slot);
            succeeded = true;
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                // Capture only after canonical submission, while this exact
                // runtime publication is still borrowed. Optional observer
                // refusal/exception cannot undo successful gameplay delivery.
                try { udpObserver?.Observe(record.UdpObservation, rawState, hasMotion); }
                catch { Interlocked.Increment(ref udpObservationFailures); }
            }
        }
        catch
        {
        }
        finally
        {
            lock (lifecycleGate)
            {
                record.DispatchActive = false;
                if (succeeded && report.Kind ==
                        Switch2RuntimeReportKind.TerminalNeutral)
                {
                    record.TerminalAccepted = true;
                }
            }
        }

        return succeeded ? Success(operation) : Uncertain(operation,
            Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
    }

    public Switch2ControlServiceSlotHostResult TryAbort(
        in Switch2ControlServiceSlotLease lease) =>
        TryCleanup(lease, Switch2ControlServiceSlotHostOperation.Abort,
            InputControllerSlotState.Bound, requireTerminal: false);

    public Switch2ControlServiceSlotHostResult TryRemove(
        in Switch2ControlServiceSlotLease lease) =>
        TryCleanup(lease, Switch2ControlServiceSlotHostOperation.Remove,
            InputControllerSlotState.Quiesced, requireTerminal: true);

    private Switch2ControlServiceSlotHostResult TryCleanup(
        in Switch2ControlServiceSlotLease lease,
        Switch2ControlServiceSlotHostOperation operation,
        InputControllerSlotState expectedTableState, bool requireTerminal)
    {
        if (!TryValidateLease(lease, out InputControllerSlotToken token))
        {
            return Reject(operation,
                Switch2ControlServiceSlotHostFailureKind.InvalidCredential);
        }

        lock (lifecycleGate)
        {
            if (!table.TryAuthenticateExactExternalCleanup(token,
                    expectedTableState,
                    out InputControllerSlotTableFailure tableFailure))
            {
                return Reject(operation, MapTableFailure(tableFailure));
            }

            StageRecord record = records[token.Slot];
            if (record == null)
            {
                return Success(operation);
            }
            if (record.Request.Token != token)
            {
                return Reject(operation,
                    Switch2ControlServiceSlotHostFailureKind.SlotChanged);
            }
            if (record.OperationActive || record.DispatchActive)
            {
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.CleanupRejected);
            }
            if (requireTerminal && !record.TerminalAccepted)
            {
                return Reject(operation,
                    Switch2ControlServiceSlotHostFailureKind.
                        TerminalNeutralRejected);
            }

            record.OperationActive = true;
            Switch2ControlServiceReversibleStageResult cleanupResult =
                TryCleanupRecordNoLock(record);
            record.OperationActive = false;
            if (!cleanupResult.Succeeded)
            {
                return Uncertain(operation,
                    Switch2ControlServiceSlotHostFailureKind.CleanupRejected);
            }

            records[token.Slot] = null;
            return Success(operation);
        }
    }

    private Switch2ControlServiceSlotHostResult CompleteFailedPrepareNoLock(
        StageRecord record, Switch2ControlServiceSlotHostOperation operation,
        in Switch2ControlServiceReversibleStageResult failedResult,
        Switch2ControlServiceSlotHostFailureKind failureKind)
    {
        Switch2ControlServiceReversibleStageResult cleanup =
            TryCleanupRecordNoLock(record);
        record.OperationActive = false;
        if (cleanup.Succeeded)
        {
            records[record.Request.Slot] = null;
            return failedResult.Outcome ==
                    Switch2ControlServiceReversibleStageOutcome.
                        ProvenRejected ?
                Reject(operation, failureKind) :
                Uncertain(operation, failureKind);
        }

        return Uncertain(operation,
            Switch2ControlServiceSlotHostFailureKind.CleanupRejected);
    }

    private Switch2ControlServiceReversibleStageResult
        TryCleanupRecordNoLock(StageRecord record)
    {
        if (record.UnownedCleanupRisk)
        {
            return Switch2ControlServiceReversibleStageResult.Uncertain(
                Switch2ControlServiceReversibleStageFailureKind.
                    CleanupRejected);
        }

        // Once undo begins this is no longer an activatable prepared host,
        // even if a later inverse needs retry after earlier facets retired.
        record.Prepared = false;
        record.UdpObservation?.Retire();
        record.UdpObservation = null;
        record.DiagnosticsSource?.Retire();
        record.DiagnosticsSource = null;

        if (record.GyroOwner != null)
        {
            // Retire the exact direct callback before profile Undo can reset
            // Mouse/output state. Unexpected admission fails closed, retaining
            // every inverse for retry rather than waiting under lifecycleGate.
            if (!record.GyroOwner.TryRetire(0))
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.CleanupRejected);

            int slot = record.Request.Slot;
            if (!ReferenceEquals(controllers[slot], record.Request.Device) ||
                record.MouseInverse == null ||
                !ReferenceEquals(touchPads[slot], record.MouseInverse.Mouse))
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.SlotChanged);

            Mapping.RequestPostMapStickReset(slot);
            record.GyroOwner = null;
        }

        if (record.ProfileCleanupRequired)
        {
            // An earlier attempt may have retired GyroOwner before profile
            // Undo failed. Revalidate on every retry: the production inverse
            // resets the slot's Mouse and must never touch a replacement.
            if (!ReferenceEquals(controllers[record.Request.Slot], record.Request.Device) ||
                record.MouseInverse == null ||
                !ReferenceEquals(touchPads[record.Request.Slot], record.MouseInverse.Mouse))
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.SlotChanged);

            if (record.ProfileInverse == null)
            {
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.
                        CleanupRejected);
            }

            Switch2ControlServiceReversibleStageResult profileCleanup;
            try
            {
                profileCleanup = record.ProfileInverse.TryUndo(
                    record.Request);
            }
            catch
            {
                profileCleanup =
                    Switch2ControlServiceReversibleStageResult.Uncertain(
                        Switch2ControlServiceReversibleStageFailureKind.
                            DependencyThrew);
            }
            if (!profileCleanup.IsValid || !profileCleanup.Succeeded)
            {
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.
                        CleanupRejected);
            }
            record.ProfileCleanupRequired = false;
            record.ProfileInverse = null;
        }

        if (record.MouseCleanupRequired)
        {
            Switch2ControlServiceReversibleStageResult mouseCleanup =
                mouseStage.TryUndo(record.Request, record.MouseInverse);
            if (!mouseCleanup.IsValid || !mouseCleanup.Succeeded)
            {
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.
                        CleanupRejected);
            }
            record.MouseCleanupRequired = false;
            record.MouseInverse = null;
        }

        if (record.ExactCleanupRequired)
        {
            Switch2ControlServiceReversibleStageResult exactCleanup =
                exactStage.TryUndo(record.Request, record.ExactInverse);
            if (!exactCleanup.IsValid || !exactCleanup.Succeeded)
            {
                return Switch2ControlServiceReversibleStageResult.Uncertain(
                    Switch2ControlServiceReversibleStageFailureKind.
                        CleanupRejected);
            }
            record.ExactCleanupRequired = false;
            record.ExactInverse = null;
        }

        record.Prepared = false;
        return Switch2ControlServiceReversibleStageResult.Success();
    }

    private bool TryValidateLease(in Switch2ControlServiceSlotLease lease,
        out InputControllerSlotToken token)
    {
        token = lease.Token;
        return lease.IsValid && token.Slot >= 0 &&
            token.Slot < records.Length &&
            token.Registration.OwnershipKind ==
                InputControllerOwnershipKind.Switch2Runtime &&
            token.Registration.Device is Switch2RuntimeInputDevice runtime &&
            runtime.RuntimeGeneration == token.Registration.Generation;
    }

    private static Switch2ControlServiceSlotHostFailureKind MapTableFailure(
        InputControllerSlotTableFailure failure) => failure switch
        {
            InputControllerSlotTableFailure.WrongState =>
                Switch2ControlServiceSlotHostFailureKind.SlotChanged,
            InputControllerSlotTableFailure.Busy =>
                Switch2ControlServiceSlotHostFailureKind.SlotOccupied,
            _ => Switch2ControlServiceSlotHostFailureKind.InvalidCredential,
        };

    private static Switch2ControlServiceSlotHostResult Success(
        Switch2ControlServiceSlotHostOperation operation) =>
        Switch2ControlServiceSlotHostResult.Success(operation);

    private static Switch2ControlServiceSlotHostResult Reject(
        Switch2ControlServiceSlotHostOperation operation,
        Switch2ControlServiceSlotHostFailureKind failureKind) =>
        Switch2ControlServiceSlotHostResult.Reject(operation, failureKind);

    private static Switch2ControlServiceSlotHostResult Uncertain(
        Switch2ControlServiceSlotHostOperation operation,
        Switch2ControlServiceSlotHostFailureKind failureKind) =>
        Switch2ControlServiceSlotHostResult.Uncertain(operation, failureKind);
}
