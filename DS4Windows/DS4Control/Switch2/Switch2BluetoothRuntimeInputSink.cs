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
using System.Threading.Tasks;
using DS4Windows.InputDevices;

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothRuntimeSinkFailure : byte
{
    None = 0,
    InvalidArgument,
    DescriptorMismatch,
    RuntimeDeviceMismatch,
    LifecycleClosed,
    PublicationAlreadyInProgress,
    CanonicalFrameMismatch,
    ProfileMappingRejected,
    RuntimeFrameRejected,
    RuntimeSubscriberRejected,
    RuntimePublicationAdmissionTimedOut,
    TerminalIdentityMismatch,
    TerminalPublicationRejected,
    TerminalDeliveryTimedOut,
    TerminalDeliveryRejected,
    DescriptorNotBound,
    InvalidTerminalCredential,
    TerminalNotRequested,
    TerminalSchedulerRejected,
    DependencyThrew,
}

internal enum Switch2BluetoothRuntimeTerminalState : byte
{
    NotRequested = 0,
    Requested,
    AcceptedPending,
    Delivered,
    Rejected,
}

internal readonly struct Switch2BluetoothRuntimeSinkBindingCredential
{
    private readonly Switch2BluetoothRuntimeInputSink issuer;
    private readonly object fence;

    internal Switch2BluetoothRuntimeSinkBindingCredential(
        Switch2BluetoothRuntimeInputSink issuer, object fence,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        Model = model;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
    }

    internal Switch2ControllerModel Model { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal bool Authenticates(Switch2BluetoothRuntimeInputSink candidate,
        object expectedFence) => ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) && DeviceGeneration != 0 &&
        TransportGeneration != 0;
}

internal readonly struct Switch2BluetoothRuntimeTerminalCredential
{
    private readonly Switch2BluetoothRuntimeInputSink issuer;
    private readonly object fence;

    internal Switch2BluetoothRuntimeTerminalCredential(
        Switch2BluetoothRuntimeInputSink issuer, object fence,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        Model = model;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
    }

    internal Switch2ControllerModel Model { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal bool Authenticates(Switch2BluetoothRuntimeInputSink candidate,
        object expectedFence, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration) =>
        ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) && Model == expectedModel &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration;
}

/// <summary>
/// Dormant canonical-to-profile bridge for one Bluetooth Pro or standalone
/// Joy-Con lifetime. This is deliberately below discovery and registration:
/// it performs no WinRT/GATT work, starts no threads, and owns no controller
/// slot. The Bluetooth input owner serializes calls into this object; exact
/// model and generation checks prevent a callback from crossing lifetimes.
/// </summary>
internal sealed class Switch2BluetoothRuntimeInputSink :
    ISwitch2BluetoothCanonicalInputSink
{
    public bool IsVirtualOutputTransitionActive =>
        runtimeDevice.IsVirtualOutputTransitionActive;

    private readonly object sync = new();
    private Switch2InputSessionDescriptor descriptor;
    private readonly Switch2RuntimeInputDevice runtimeDevice;
    private readonly int runtimeOperationTimeoutMilliseconds;
    private readonly ISwitch2RuntimeTerminalScheduler terminalScheduler;
    private readonly Switch2ControllerModel expectedModel;
    private readonly ulong expectedDeviceGeneration;
    private readonly ulong expectedTransportGeneration;
    private readonly object descriptorBindingFence = new();
    private readonly object terminalFence = new();
    private Switch2JoyConProfileMapperState joyConMapper;
    private Switch2BluetoothRuntimeSinkFailure lastFailure;
    private Switch2ProProfileInputFailure lastProMappingFailure;
    private Switch2JoyConProfileInputFailure lastJoyConMappingFailure;
    private bool publicationInProgress;
    private int publicationThreadId;
    private bool descriptorBound;
    private bool terminalRequested;
    private Switch2BluetoothInputEndReason terminalReason;
    private Switch2BluetoothRuntimeTerminalState terminalState;
    private Switch2BluetoothRuntimeSinkFailure terminalFailure;
    private bool terminalScheduleInProgress;
    private Task<Switch2TerminalNeutralRequestResult> terminalTask;
    private int terminalPublicationThreadId;
    private long terminalWaitTimeoutCount;
    private long publishedCount;
    private long publicationAdmissionWaitCount;

    private Switch2BluetoothRuntimeInputSink(
        in Switch2InputSessionDescriptor descriptor,
        Switch2RuntimeInputDevice runtimeDevice,
        in Switch2JoyConProfileMapperState joyConMapper,
        int runtimeOperationTimeoutMilliseconds,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        bool descriptorBound, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration)
    {
        this.descriptor = descriptor;
        this.runtimeDevice = runtimeDevice;
        this.joyConMapper = joyConMapper;
        this.runtimeOperationTimeoutMilliseconds =
            runtimeOperationTimeoutMilliseconds;
        this.terminalScheduler = terminalScheduler;
        this.descriptorBound = descriptorBound;
        this.expectedModel = expectedModel;
        this.expectedDeviceGeneration = expectedDeviceGeneration;
        this.expectedTransportGeneration = expectedTransportGeneration;
    }

    internal Switch2RuntimeInputDevice RuntimeDevice => runtimeDevice;

    internal long PublishedCount
    {
        get
        {
            lock (sync)
            {
                return publishedCount;
            }
        }
    }

    internal Switch2BluetoothRuntimeSinkFailure LastFailure
    {
        get
        {
            lock (sync)
            {
                return lastFailure;
            }
        }
    }

    internal Switch2ProProfileInputFailure LastProMappingFailure
    {
        get
        {
            lock (sync)
            {
                return lastProMappingFailure;
            }
        }
    }

    internal Switch2JoyConProfileInputFailure LastJoyConMappingFailure
    {
        get
        {
            lock (sync)
            {
                return lastJoyConMappingFailure;
            }
        }
    }

    internal bool TerminalRequested
    {
        get
        {
            lock (sync)
            {
                return terminalRequested;
            }
        }
    }

    internal Switch2BluetoothInputEndReason TerminalReason
    {
        get
        {
            lock (sync)
            {
                return terminalReason;
            }
        }
    }

    internal Switch2BluetoothRuntimeTerminalState TerminalState
    {
        get
        {
            lock (sync)
            {
                return terminalState;
            }
        }
    }

    internal Switch2BluetoothRuntimeSinkFailure TerminalFailure
    {
        get
        {
            lock (sync)
            {
                return terminalFailure;
            }
        }
    }

    internal bool PublicationInProgress
    {
        get
        {
            lock (sync)
            {
                return publicationInProgress;
            }
        }
    }

    internal bool IsCurrentPublicationThread
    {
        get
        {
            lock (sync)
            {
                return publicationInProgress && publicationThreadId ==
                    Environment.CurrentManagedThreadId;
            }
        }
    }

    internal bool TerminalPublicationInProgress
    {
        get
        {
            lock (sync)
            {
                return terminalPublicationThreadId != 0;
            }
        }
    }

    internal bool IsCurrentTerminalPublicationThread
    {
        get
        {
            lock (sync)
            {
                return terminalPublicationThreadId ==
                    Environment.CurrentManagedThreadId;
            }
        }
    }

    internal long TerminalWaitTimeoutCount =>
        Interlocked.Read(ref terminalWaitTimeoutCount);

    internal long PublicationAdmissionWaitCount =>
        System.Threading.Interlocked.Read(ref publicationAdmissionWaitCount);

    internal static bool TryCreate(
        in Switch2InputSessionDescriptor descriptor,
        Switch2RuntimeInputDevice runtimeDevice,
        int runtimeOperationTimeoutMilliseconds,
        out Switch2BluetoothRuntimeInputSink sink,
        out Switch2BluetoothRuntimeSinkFailure failure) => TryCreateBound(
            descriptor, runtimeDevice, runtimeOperationTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out sink, out _,
            out failure);

    internal static bool TryCreateBound(
        in Switch2InputSessionDescriptor descriptor,
        Switch2RuntimeInputDevice runtimeDevice,
        int runtimeOperationTimeoutMilliseconds,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        out Switch2BluetoothRuntimeInputSink sink,
        out Switch2BluetoothRuntimeTerminalCredential terminalCredential,
        out Switch2BluetoothRuntimeSinkFailure failure)
    {
        sink = null;
        terminalCredential = default;
        if (!descriptor.IsValid || runtimeDevice == null ||
            terminalScheduler == null ||
            descriptor.Identity.Transport != Switch2Transport.BluetoothLe ||
            runtimeOperationTimeoutMilliseconds <= 0 ||
            runtimeOperationTimeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            failure = Switch2BluetoothRuntimeSinkFailure.InvalidArgument;
            return false;
        }

        const Switch2GattProperty requiredProperties =
            Switch2GattProperty.Read | Switch2GattProperty.Notify;
        if (descriptor.Identity.ProtocolRevision !=
                Switch2InputProtocolRevision.BluetoothLeCommon05V1 ||
            descriptor.Identity.ServiceUuid != Switch2InputCodec.ServiceUuid ||
            descriptor.Identity.CharacteristicUuid !=
                Switch2InputCodec.Common05CharacteristicUuid ||
            descriptor.Identity.GattProperties != requiredProperties)
        {
            failure = Switch2BluetoothRuntimeSinkFailure.DescriptorMismatch;
            return false;
        }

        if (!RuntimeMatches(runtimeDevice, descriptor.Identity.Model,
                descriptor.DeviceGeneration,
                descriptor.TransportGeneration))
        {
            failure = Switch2BluetoothRuntimeSinkFailure.
                RuntimeDeviceMismatch;
            return false;
        }

        Switch2JoyConProfileMapperState mapper = default;
        bool matches = TryCreateMapper(descriptor.Identity.Model, descriptor,
            runtimeDevice, out mapper);
        if (!matches)
        {
            failure = Switch2BluetoothRuntimeSinkFailure.
                RuntimeDeviceMismatch;
            return false;
        }

        sink = new Switch2BluetoothRuntimeInputSink(descriptor,
            runtimeDevice, mapper, runtimeOperationTimeoutMilliseconds,
            terminalScheduler, descriptorBound: true,
            descriptor.Identity.Model, descriptor.DeviceGeneration,
            descriptor.TransportGeneration);
        terminalCredential = new Switch2BluetoothRuntimeTerminalCredential(
            sink, sink.terminalFence, descriptor.Identity.Model,
            descriptor.DeviceGeneration, descriptor.TransportGeneration);
        failure = Switch2BluetoothRuntimeSinkFailure.None;
        return true;
    }

    internal static bool TryCreateUnbound(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration,
        Switch2RuntimeInputDevice runtimeDevice,
        int runtimeOperationTimeoutMilliseconds,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        out Switch2BluetoothRuntimeInputSink sink,
        out Switch2BluetoothRuntimeSinkBindingCredential bindingCredential,
        out Switch2BluetoothRuntimeTerminalCredential terminalCredential,
        out Switch2BluetoothRuntimeSinkFailure failure)
    {
        sink = null;
        bindingCredential = default;
        terminalCredential = default;
        if (runtimeDevice == null || terminalScheduler == null ||
            deviceGeneration == 0 || transportGeneration == 0 ||
            runtimeOperationTimeoutMilliseconds <= 0 ||
            runtimeOperationTimeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds ||
            !RuntimeMatches(runtimeDevice, model, deviceGeneration,
                transportGeneration))
        {
            failure = Switch2BluetoothRuntimeSinkFailure.InvalidArgument;
            return false;
        }

        sink = new Switch2BluetoothRuntimeInputSink(default, runtimeDevice,
            default, runtimeOperationTimeoutMilliseconds, terminalScheduler,
            descriptorBound: false, model, deviceGeneration,
            transportGeneration);
        bindingCredential = new Switch2BluetoothRuntimeSinkBindingCredential(
            sink, sink.descriptorBindingFence, model, deviceGeneration,
            transportGeneration);
        terminalCredential = new Switch2BluetoothRuntimeTerminalCredential(
            sink, sink.terminalFence, model, deviceGeneration,
            transportGeneration);
        failure = Switch2BluetoothRuntimeSinkFailure.None;
        return true;
    }

    internal bool TryBindDescriptor(
        in Switch2BluetoothRuntimeSinkBindingCredential credential,
        in Switch2InputSessionDescriptor exactDescriptor,
        out Switch2BluetoothRuntimeSinkFailure failure)
    {
        if (!credential.Authenticates(this, descriptorBindingFence) ||
            credential.Model != expectedModel ||
            credential.DeviceGeneration != expectedDeviceGeneration ||
            credential.TransportGeneration != expectedTransportGeneration ||
            !IsExactBluetoothDescriptor(exactDescriptor, expectedModel,
                expectedDeviceGeneration, expectedTransportGeneration))
        {
            failure = Switch2BluetoothRuntimeSinkFailure.DescriptorMismatch;
            return false;
        }

        Switch2JoyConProfileMapperState mapper;
        if (!TryCreateMapper(expectedModel, exactDescriptor, runtimeDevice,
                out mapper))
        {
            failure = Switch2BluetoothRuntimeSinkFailure.RuntimeDeviceMismatch;
            return false;
        }

        lock (sync)
        {
            if (descriptorBound || runtimeDevice.RuntimeState !=
                    Switch2RuntimeInputDeviceState.Created)
            {
                failure = Switch2BluetoothRuntimeSinkFailure.LifecycleClosed;
                return false;
            }
            descriptor = exactDescriptor;
            joyConMapper = mapper;
            descriptorBound = true;
        }
        failure = Switch2BluetoothRuntimeSinkFailure.None;
        return true;
    }

    private static bool TryCreateMapper(Switch2ControllerModel model,
        in Switch2InputSessionDescriptor exactDescriptor,
        Switch2RuntimeInputDevice runtimeDevice,
        out Switch2JoyConProfileMapperState mapper)
    {
        mapper = default;
        return model switch
        {
            Switch2ControllerModel.ProController2 =>
                runtimeDevice.DeviceType == InputDeviceType.Switch2Pro &&
                runtimeDevice.JoyConBindingMode ==
                    Switch2JoyConRuntimeBindingMode.Invalid,
            Switch2ControllerModel.JoyCon2Left =>
                runtimeDevice.DeviceType == InputDeviceType.
                    Switch2JoyConLeft &&
                runtimeDevice.JoyConBindingMode ==
                    Switch2JoyConRuntimeBindingMode.StandaloneLeft &&
                Switch2JoyConProfileInputMapper.TryCreateStandalone(
                    Switch2JoyConProfileMode.StandaloneVerticalLeft,
                    exactDescriptor, out mapper),
            Switch2ControllerModel.JoyCon2Right =>
                runtimeDevice.DeviceType == InputDeviceType.
                    Switch2JoyConRight &&
                runtimeDevice.JoyConBindingMode ==
                    Switch2JoyConRuntimeBindingMode.StandaloneRight &&
                Switch2JoyConProfileInputMapper.TryCreateStandalone(
                    Switch2JoyConProfileMode.StandaloneVerticalRight,
                    exactDescriptor, out mapper),
            _ => false,
        };
    }

    public void PublishPro(in Switch2CanonicalInputFrame frame)
    {
        BeginPublication(frame, Switch2ControllerModel.ProController2);
        bool published = false;
        Switch2BluetoothRuntimeSinkFailure failure = default;
        Switch2ProProfileInputFailure mappingFailure = default;
        try
        {
            if (!Switch2ProProfileInputMapper.TryMap(frame,
                    out Switch2ProProfileInputFrame profile,
                    out mappingFailure))
            {
                failure = Switch2BluetoothRuntimeSinkFailure.
                    ProfileMappingRejected;
            }
            else
            {
                Switch2RuntimePublicationResult result =
                    PublishProWithAdmission(profile);
                published = result == Switch2RuntimePublicationResult.Published;
                if (!published)
                {
                    failure = FailureFor(result);
                }
            }
        }
        catch
        {
            failure = Switch2BluetoothRuntimeSinkFailure.DependencyThrew;
        }
        finally
        {
            EndPublication(published, failure, mappingFailure, default);
        }

        if (!published)
        {
            throw new InvalidOperationException(
                "Bluetooth Pro runtime publication was rejected.");
        }
    }

    public void PublishJoyCon(in Switch2CanonicalInputFrame frame)
    {
        if (descriptor.Identity.Model is not
                (Switch2ControllerModel.JoyCon2Left or
                    Switch2ControllerModel.JoyCon2Right))
        {
            lock (sync)
            {
                lastFailure = Switch2BluetoothRuntimeSinkFailure.
                    CanonicalFrameMismatch;
            }
            throw new InvalidOperationException(
                "Bluetooth Joy-Con publication kind was rejected.");
        }
        BeginPublication(frame, descriptor.Identity.Model);
        bool published = false;
        Switch2BluetoothRuntimeSinkFailure failure = default;
        Switch2JoyConProfileInputFailure mappingFailure = default;
        Switch2JoyConProfileMapperState next = default;
        try
        {
            Switch2JoyConProfileMode selectedMode =
                ReadStandaloneProfileMode();
            if (selectedMode == Switch2JoyConProfileMode.Invalid ||
                !Switch2JoyConProfileInputMapper.TrySelectStandaloneMode(
                    joyConMapper, selectedMode, out var selectedMapper) ||
                !Switch2JoyConProfileInputMapper.TryMapStandalone(
                    selectedMapper, frame, out next,
                    out Switch2JoyConProfileInputFrame profile,
                    out mappingFailure))
            {
                failure = Switch2BluetoothRuntimeSinkFailure.
                    ProfileMappingRejected;
            }
            else
            {
                Switch2RuntimePublicationResult result =
                    PublishJoyConWithAdmission(profile);
                published = result == Switch2RuntimePublicationResult.Published;
                if (!published)
                {
                    failure = FailureFor(result);
                }
            }
        }
        catch
        {
            failure = Switch2BluetoothRuntimeSinkFailure.DependencyThrew;
        }
        finally
        {
            lock (sync)
            {
                if (published)
                {
                    joyConMapper = next;
                }
            }
            EndPublication(published, failure, default, mappingFailure);
        }

        if (!published)
        {
            throw new InvalidOperationException(
                "Bluetooth Joy-Con runtime publication was rejected.");
        }
    }

    private Switch2JoyConProfileMode ReadStandaloneProfileMode()
    {
        int slot = runtimeDevice.DeviceSlotNumber;
        if (slot < 0 || slot >= Global.MAX_DS4_CONTROLLER_COUNT)
        {
            // Creation/binding precedes ControlService slot installation. No
            // report can reach production mapping in that state; retain the
            // legacy horizontal mini projection for isolated boundary tests.
            return Switch2JoyConProfileInputMapper.StandaloneModeFor(
                expectedModel, Switch2JoyConHoldMode.Horizontal);
        }

        Switch2JoyConHoldMode holdMode =
            Global.Switch2JoyConStandaloneHoldMode[slot];
        if (holdMode is not (Switch2JoyConHoldMode.Vertical or
                Switch2JoyConHoldMode.Horizontal))
        {
            holdMode = Switch2JoyConHoldMode.Vertical;
        }
        holdMode = runtimeDevice.ResolveStandaloneJoyConHoldMode(holdMode);
        // The enum read is the whole presentation snapshot for this report.
        // A concurrent profile/default or per-device override change can
        // therefore produce one complete old frame or one complete new frame,
        // never mixed axes/buttons and never a transport-lifetime failure. The
        // next physical report observes the new value without a queue,
        // reconnect, or cadence source.
        return Switch2JoyConProfileInputMapper.StandaloneModeFor(
            expectedModel, holdMode);
    }

    public void ClearPro(ulong deviceGeneration, ulong transportGeneration,
        Switch2BluetoothInputEndReason reason)
    {
        if (!MatchesTerminal(expectedModel ==
                Switch2ControllerModel.ProController2,
                deviceGeneration, transportGeneration, reason))
        {
            throw new InvalidOperationException(
                "Bluetooth Pro terminal identity was rejected.");
        }

        RecordTerminalRequest(reason);
    }

    public void LoseJoyConHalf(Switch2StickSide side,
        ulong deviceGeneration, ulong transportGeneration,
        Switch2BluetoothInputEndReason reason)
    {
        Switch2StickSide expected = expectedModel ==
            Switch2ControllerModel.JoyCon2Left ? Switch2StickSide.Left :
            expectedModel == Switch2ControllerModel.JoyCon2Right ?
                Switch2StickSide.Right : default;
        bool isStandaloneJoyCon = expectedModel is
            Switch2ControllerModel.JoyCon2Left or
            Switch2ControllerModel.JoyCon2Right;
        if (!MatchesTerminal(isStandaloneJoyCon && side == expected,
                deviceGeneration, transportGeneration, reason))
        {
            throw new InvalidOperationException(
                "Bluetooth Joy-Con terminal identity was rejected.");
        }

        RecordTerminalRequest(reason);
    }

    private void BeginPublication(in Switch2CanonicalInputFrame frame,
        Switch2ControllerModel expectedModel)
    {
        Switch2BluetoothRuntimeSinkFailure failure = default;
        lock (sync)
        {
            if (!descriptorBound)
            {
                failure = Switch2BluetoothRuntimeSinkFailure.
                    DescriptorNotBound;
            }
            else if (terminalRequested || runtimeDevice.RuntimeState !=
                    Switch2RuntimeInputDeviceState.Active)
            {
                failure = Switch2BluetoothRuntimeSinkFailure.LifecycleClosed;
            }
            else if (publicationInProgress)
            {
                failure = Switch2BluetoothRuntimeSinkFailure.
                    PublicationAlreadyInProgress;
            }
            else if (expectedModel != descriptor.Identity.Model ||
                !frame.Descriptor.Equals(descriptor))
            {
                failure = Switch2BluetoothRuntimeSinkFailure.
                    CanonicalFrameMismatch;
            }
            else
            {
                publicationInProgress = true;
                publicationThreadId = Environment.CurrentManagedThreadId;
                return;
            }

            lastFailure = failure;
        }

        throw new InvalidOperationException(
            "Bluetooth canonical runtime publication was rejected.");
    }

    private void EndPublication(bool published,
        Switch2BluetoothRuntimeSinkFailure failure,
        Switch2ProProfileInputFailure proMappingFailure,
        Switch2JoyConProfileInputFailure joyConMappingFailure)
    {
        lock (sync)
        {
            publicationInProgress = false;
            publicationThreadId = 0;
            lastProMappingFailure = proMappingFailure;
            lastJoyConMappingFailure = joyConMappingFailure;
            if (published)
            {
                publishedCount++;
                lastFailure = Switch2BluetoothRuntimeSinkFailure.None;
            }
            else
            {
                lastFailure = failure == default ?
                    Switch2BluetoothRuntimeSinkFailure.DependencyThrew :
                    failure;
            }
            System.Threading.Monitor.PulseAll(sync);
        }
    }

    private bool MatchesTerminal(bool terminalKindMatches,
        ulong deviceGeneration,
        ulong transportGeneration, Switch2BluetoothInputEndReason reason)
    {
        lock (sync)
        {
            bool validReason = reason is
                Switch2BluetoothInputEndReason.Disconnected or
                Switch2BluetoothInputEndReason.Stopped or
                Switch2BluetoothInputEndReason.QueueOverflow or
                Switch2BluetoothInputEndReason.SinkFailure;
            if (!descriptorBound || !terminalKindMatches ||
                deviceGeneration != expectedDeviceGeneration ||
                transportGeneration != expectedTransportGeneration ||
                !validReason)
            {
                lastFailure = Switch2BluetoothRuntimeSinkFailure.
                    TerminalIdentityMismatch;
                return false;
            }
            return true;
        }
    }

    private void RecordTerminalRequest(Switch2BluetoothInputEndReason reason)
    {
        lock (sync)
        {
            if (terminalRequested)
            {
                if (terminalReason == reason)
                {
                    return;
                }
                SetTerminalFailureNoLock(Switch2BluetoothRuntimeSinkFailure.
                    TerminalIdentityMismatch);
                throw new InvalidOperationException(
                    "Bluetooth terminal reason changed within one lifetime.");
            }
            if (!descriptorBound || runtimeDevice.RuntimeState !=
                    Switch2RuntimeInputDeviceState.Active)
            {
                lastFailure = Switch2BluetoothRuntimeSinkFailure.
                    LifecycleClosed;
                throw new InvalidOperationException(
                    "Bluetooth runtime is not active.");
            }
            terminalRequested = true;
            terminalReason = reason;
            terminalState = Switch2BluetoothRuntimeTerminalState.Requested;
            lastFailure = Switch2BluetoothRuntimeSinkFailure.None;
            Monitor.PulseAll(sync);
        }
    }

    internal bool TryCompleteTerminalNeutral(
        in Switch2BluetoothRuntimeTerminalCredential credential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeSinkFailure failure)
    {
        if (!credential.Authenticates(this, terminalFence, expectedModel,
                expectedDeviceGeneration, expectedTransportGeneration) ||
            timeoutMilliseconds < 0 || timeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            failure = Switch2BluetoothRuntimeSinkFailure.
                InvalidTerminalCredential;
            return false;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        Task<Switch2TerminalNeutralRequestResult> exactTask = null;
        while (exactTask == null)
        {
            lock (sync)
            {
                if (!terminalRequested)
                {
                    SetTerminalFailureNoLock(
                        Switch2BluetoothRuntimeSinkFailure.
                            TerminalNotRequested);
                    failure = Switch2BluetoothRuntimeSinkFailure.
                        TerminalNotRequested;
                    return false;
                }
                if (terminalState ==
                    Switch2BluetoothRuntimeTerminalState.Delivered)
                {
                    failure = Switch2BluetoothRuntimeSinkFailure.None;
                    return true;
                }
                if (terminalState ==
                    Switch2BluetoothRuntimeTerminalState.Rejected)
                {
                    failure = terminalFailure == default ?
                        Switch2BluetoothRuntimeSinkFailure.
                            TerminalDeliveryRejected : terminalFailure;
                    return false;
                }
                if (terminalTask != null)
                {
                    exactTask = terminalTask;
                    break;
                }
                if (!terminalScheduleInProgress)
                {
                    terminalScheduleInProgress = true;
                    break;
                }

                int remaining = RemainingMilliseconds(deadline);
                if (remaining == 0 || !Monitor.Wait(sync, remaining))
                {
                    Interlocked.Increment(ref terminalWaitTimeoutCount);
                    SetTerminalFailureNoLock(
                        Switch2BluetoothRuntimeSinkFailure.
                            TerminalDeliveryTimedOut);
                    failure = Switch2BluetoothRuntimeSinkFailure.
                        TerminalDeliveryTimedOut;
                    return false;
                }
            }
        }

        if (exactTask == null)
        {
            bool scheduled;
            try
            {
                scheduled = terminalScheduler.TrySchedule(
                    PublishTerminalNeutral, out exactTask) &&
                    exactTask != null;
            }
            catch
            {
                scheduled = false;
            }
            lock (sync)
            {
                terminalScheduleInProgress = false;
                if (!scheduled)
                {
                    terminalState =
                        Switch2BluetoothRuntimeTerminalState.Rejected;
                    SetTerminalFailureNoLock(
                        Switch2BluetoothRuntimeSinkFailure.
                            TerminalSchedulerRejected);
                }
                else
                {
                    terminalTask = exactTask;
                }
                Monitor.PulseAll(sync);
            }
            if (!scheduled)
            {
                failure = Switch2BluetoothRuntimeSinkFailure.
                    TerminalSchedulerRejected;
                return false;
            }
        }

        int taskRemaining = RemainingMilliseconds(deadline);
        try
        {
            if (!exactTask.Wait(taskRemaining))
            {
                Interlocked.Increment(ref terminalWaitTimeoutCount);
                lock (sync)
                {
                    SetTerminalFailureNoLock(
                        Switch2BluetoothRuntimeSinkFailure.
                            TerminalDeliveryTimedOut);
                }
                failure = Switch2BluetoothRuntimeSinkFailure.
                    TerminalDeliveryTimedOut;
                return false;
            }
        }
        catch
        {
            lock (sync)
            {
                terminalState = Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2BluetoothRuntimeSinkFailure.DependencyThrew);
            }
            failure = Switch2BluetoothRuntimeSinkFailure.DependencyThrew;
            return false;
        }

        Switch2TerminalNeutralRequestResult result = exactTask.Result;
        if (result == Switch2TerminalNeutralRequestResult.
                RejectedAlreadyReserved)
        {
            lock (sync)
            {
                terminalState = Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2BluetoothRuntimeSinkFailure.
                        TerminalPublicationRejected);
            }
            failure = Switch2BluetoothRuntimeSinkFailure.
                TerminalPublicationRejected;
            return false;
        }

        lock (sync)
        {
            terminalState = Switch2BluetoothRuntimeTerminalState.
                AcceptedPending;
        }

        int completionRemaining = RemainingMilliseconds(deadline);
        bool completed;
        try
        {
            completed = runtimeDevice.TryWaitForTerminalNeutralCompletion(
                completionRemaining);
        }
        catch
        {
            completed = false;
            lock (sync)
            {
                terminalState = Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2BluetoothRuntimeSinkFailure.DependencyThrew);
            }
            failure = Switch2BluetoothRuntimeSinkFailure.DependencyThrew;
            return false;
        }
        if (!completed)
        {
            Interlocked.Increment(ref terminalWaitTimeoutCount);
            lock (sync)
            {
                SetTerminalFailureNoLock(
                    Switch2BluetoothRuntimeSinkFailure.
                        TerminalDeliveryTimedOut);
            }
            failure = Switch2BluetoothRuntimeSinkFailure.
                TerminalDeliveryTimedOut;
            return false;
        }
        if (!runtimeDevice.TerminalNeutralCompleted ||
            !runtimeDevice.TerminalNeutralReported)
        {
            lock (sync)
            {
                terminalState = Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2BluetoothRuntimeSinkFailure.
                        TerminalDeliveryRejected);
            }
            failure = Switch2BluetoothRuntimeSinkFailure.
                TerminalDeliveryRejected;
            return false;
        }

        lock (sync)
        {
            terminalState = Switch2BluetoothRuntimeTerminalState.Delivered;
        }
        failure = Switch2BluetoothRuntimeSinkFailure.None;
        return true;
    }

    private Switch2TerminalNeutralRequestResult PublishTerminalNeutral()
    {
        lock (sync)
        {
            terminalPublicationThreadId = Environment.CurrentManagedThreadId;
        }
        try
        {
            return runtimeDevice.RequestTerminalNeutral();
        }
        finally
        {
            lock (sync)
            {
                terminalPublicationThreadId = 0;
                Monitor.PulseAll(sync);
            }
        }
    }

    private void SetTerminalFailureNoLock(
        Switch2BluetoothRuntimeSinkFailure failure)
    {
        if (terminalFailure == Switch2BluetoothRuntimeSinkFailure.None)
        {
            terminalFailure = failure;
        }
        lastFailure = failure;
    }

    private Switch2RuntimePublicationResult PublishProWithAdmission(
        in Switch2ProProfileInputFrame frame)
    {
        long deadline = Environment.TickCount64 +
            runtimeOperationTimeoutMilliseconds;
        while (true)
        {
            Switch2RuntimePublicationResult result = runtimeDevice.
                TryPublishProDetailed(frame);
            if (result != Switch2RuntimePublicationResult.PublicationBusy)
            {
                return result;
            }

            int remaining = RemainingMilliseconds(deadline);
            if (remaining == 0)
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }

            System.Threading.Interlocked.Increment(
                ref publicationAdmissionWaitCount);

            // A false wait can mean either timeout or a lifecycle transition.
            // Retry once through the detailed admission seam so a closed
            // lifetime is never mislabeled as transient backpressure.
            runtimeDevice.TryWaitForPublicationAvailability(remaining);
        }
    }

    private Switch2RuntimePublicationResult PublishJoyConWithAdmission(
        in Switch2JoyConProfileInputFrame frame)
    {
        long deadline = Environment.TickCount64 +
            runtimeOperationTimeoutMilliseconds;
        while (true)
        {
            Switch2RuntimePublicationResult result = runtimeDevice.
                TryPublishStandaloneJoyConDetailed(frame);
            if (result != Switch2RuntimePublicationResult.PublicationBusy)
            {
                return result;
            }

            int remaining = RemainingMilliseconds(deadline);
            if (remaining == 0)
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            System.Threading.Interlocked.Increment(
                ref publicationAdmissionWaitCount);
            runtimeDevice.TryWaitForPublicationAvailability(remaining);
        }
    }

    private static bool RuntimeMatches(Switch2RuntimeInputDevice runtimeDevice,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration) => runtimeDevice.RuntimeState ==
            Switch2RuntimeInputDeviceState.Created &&
        runtimeDevice.HasExactStandaloneBluetoothBinding(model,
            deviceGeneration, transportGeneration);

    private static bool IsExactBluetoothDescriptor(
        in Switch2InputSessionDescriptor exactDescriptor,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        const Switch2GattProperty required = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        return exactDescriptor.IsValid &&
            exactDescriptor.Identity.Model == model &&
            exactDescriptor.DeviceGeneration == deviceGeneration &&
            exactDescriptor.TransportGeneration == transportGeneration &&
            exactDescriptor.Identity.Transport == Switch2Transport.BluetoothLe &&
            exactDescriptor.Identity.ProtocolRevision ==
                Switch2InputProtocolRevision.BluetoothLeCommon05V1 &&
            exactDescriptor.Identity.ServiceUuid == Switch2InputCodec.ServiceUuid &&
            exactDescriptor.Identity.CharacteristicUuid ==
                Switch2InputCodec.Common05CharacteristicUuid &&
            exactDescriptor.Identity.GattProperties == required;
    }

    private static Switch2BluetoothRuntimeSinkFailure FailureFor(
        Switch2RuntimePublicationResult result) => result switch
        {
            Switch2RuntimePublicationResult.LifecycleClosed =>
                Switch2BluetoothRuntimeSinkFailure.LifecycleClosed,
            Switch2RuntimePublicationResult.FrameRejected =>
                Switch2BluetoothRuntimeSinkFailure.RuntimeFrameRejected,
            Switch2RuntimePublicationResult.SubscriberRejected =>
                Switch2BluetoothRuntimeSinkFailure.RuntimeSubscriberRejected,
            Switch2RuntimePublicationResult.PublicationBusy =>
                Switch2BluetoothRuntimeSinkFailure.
                    RuntimePublicationAdmissionTimedOut,
            _ => Switch2BluetoothRuntimeSinkFailure.DependencyThrew,
        };

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, remaining);
    }
}
