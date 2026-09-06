/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows.Switch2;

internal sealed class Switch2ProUsbWindowsCleanupAmbiguousException :
    InvalidOperationException
{
    internal Switch2ProUsbWindowsCleanupAmbiguousException(string message,
        object retainedOwner = null, Exception innerException = null) :
        base(message, innerException)
    {
        RetainedOwner = retainedOwner;
    }

    /// <summary>
    /// Exact managed capability which strongly owns any native lifetime whose
    /// release was not proven. The adapter attaches it to the process-local
    /// container reservation before the failed acquisition frame unwinds.
    /// </summary>
    internal object RetainedOwner { get; }
}

/// <summary>
/// Failure-only strong owner for native acquisition capabilities. It has no
/// finalizer and deliberately exposes no unauthenticated cleanup path; the
/// process reservation is the terminal-attention owner.
/// </summary>
internal sealed class Switch2ProUsbWindowsAcquisitionQuarantineOwner
{
    private readonly object first;
    private readonly object second;

    internal Switch2ProUsbWindowsAcquisitionQuarantineOwner(object first,
        object second = null)
    {
        this.first = first ?? throw new ArgumentNullException(nameof(first));
        this.second = second;
    }

    internal bool Retains(object capability) =>
        ReferenceEquals(first, capability) ||
        ReferenceEquals(second, capability);
}

/// <summary>
/// Failure-only capability for an unfreed native metadata allocation which has
/// no SafeHandle wrapper. Keeping the exact value strongly attached to the
/// reservation prevents the lifetime from being silently forgotten.
/// </summary>
internal sealed class Switch2ProUsbWindowsRawAcquisitionQuarantine
{
    internal Switch2ProUsbWindowsRawAcquisitionQuarantine(IntPtr value,
        string kind)
    {
        if (value == IntPtr.Zero || value == new IntPtr(-1))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        Value = value;
        Kind = string.IsNullOrWhiteSpace(kind) ?
            throw new ArgumentException("A native lifetime kind is required.",
                nameof(kind)) : kind;
    }

    internal IntPtr Value { get; }

    internal string Kind { get; }
}

/// <summary>
/// SetupAPI/HID/WinUSB implementation behind the injectable platform seam.
/// The discovery rules mirror the separately audited verifier: complete
/// present-device enumeration, exact component markers, parent/container
/// edges, exact 057E:2069/bcd0201 HID shape, and exact MI_01 pipe topology.
/// Every failure is converted to a closed result at the adapter boundary.
/// </summary>
internal sealed class Switch2ProUsbWindowsNativePlatform :
    ISwitch2ProUsbWindowsPlatform,
    ISwitch2ProUsbWindowsOwnedCompositePlatform
{
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    public bool TryDiscoverCandidates(
        out IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates)
    {
        candidates = Array.Empty<Switch2ProUsbWindowsCandidate>();
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            HidDiscoverySnapshot hidSnapshot = DiscoverHids();
            CommandDiscoverySnapshot commandSnapshot = DiscoverCommands();
            var discovered = new List<Switch2ProUsbWindowsCandidate>();
            foreach (IGrouping<Guid, HidToken> containerGroup in
                     hidSnapshot.Tokens.GroupBy(hid => hid.ContainerId))
            {
                // Ambiguity is scoped to one physical container. Never choose
                // the first MI_00 collection, but do not let one malformed
                // container suppress a different, independently valid pad.
                HidToken[] containerHids = containerGroup.Take(2).ToArray();
                if (containerHids.Length != 1 ||
                    hidSnapshot.InvalidContainers.Contains(
                        containerGroup.Key) ||
                    commandSnapshot.InvalidContainers.Contains(
                        containerGroup.Key))
                {
                    continue;
                }

                try
                {
                    HidToken hid = containerHids[0];
                    CommandNode[] commandNodes = commandSnapshot.Nodes.Where(
                        node => node.ContainerId == hid.ContainerId).Take(2).
                        ToArray();
                    if (commandNodes.Length != 1)
                    {
                        continue;
                    }
                    CommandToken command = ResolveUniqueCommand(
                        commandNodes[0]);
                    if (!TryObserveCommandTopology(command.Path,
                            out CommandTopology topology) ||
                        !Switch2PhysicalContainerIdentity.TryCreate(
                            hid.ContainerId, out var containerIdentity))
                    {
                        continue;
                    }

                    var inputObservation =
                        new Switch2UsbHidInterfaceObservation(
                            containerIdentity,
                            Switch2PhysicalInputRegistration.
                                ProUsbInputInterfaceNumber,
                            Switch2PhysicalDeviceFactory.
                                ProUsbAlternateSetting,
                            Switch2UsbBoundDriver.HidClass,
                            hid.Caps.UsagePage, hid.Caps.Usage,
                            hid.Caps.InputReportByteLength,
                            hid.Caps.OutputReportByteLength,
                            hid.Caps.FeatureReportByteLength);
                    var commandObservation =
                        new Switch2UsbCommandInterfaceObservation(
                            containerIdentity, topology.InterfaceNumber,
                            topology.AlternateSetting,
                            Switch2UsbBoundDriver.WinUsb,
                            topology.EndpointCount, topology.Pipe0,
                            topology.Pipe1);
                    var observation = new Switch2ProUsbCompositeObservation(
                        hid.VendorId, hid.ProductId, hid.VersionNumber,
                        containerIdentity, 1, 1, inputObservation,
                        commandObservation);

                    var candidate = new Switch2ProUsbWindowsCandidate(
                        observation, hid.DevInst, hid.InstanceId,
                        hid.ParentInstanceId, hid.Path, hid.ParentService,
                        command.DevInst, command.InstanceId, command.Path,
                        command.Service);
                    if (candidate.TryGetAdmittedRegistration(out _))
                    {
                        discovered.Add(candidate);
                    }
                }
                catch (Switch2ProUsbWindowsCleanupAmbiguousException)
                {
                    throw;
                }
                catch
                {
                    // A missing, duplicated, inaccessible, or malformed MI_01
                    // fails this container closed. Other opaque containers are
                    // still independent candidates.
                }
            }

            candidates = discovered;
            return discovered.Count != 0;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException)
        {
            throw;
        }
        catch
        {
            candidates = Array.Empty<Switch2ProUsbWindowsCandidate>();
            return false;
        }
    }

    public bool TryRevalidateOwnedCandidate(
        Switch2ProUsbWindowsCandidate expected)
    {
        if (!OperatingSystem.IsWindows() || expected == null ||
            !expected.TryGetAdmittedRegistration(
                out Switch2PhysicalInputRegistration expectedRegistration))
        {
            return false;
        }

        try
        {
            HidDiscoverySnapshot hidSnapshot = DiscoverHids();
            CommandDiscoverySnapshot commandSnapshot = DiscoverCommands();
            int matches = 0;
            foreach (IGrouping<Guid, HidToken> containerGroup in
                     hidSnapshot.Tokens.GroupBy(hid => hid.ContainerId))
            {
                if (!Switch2PhysicalContainerIdentity.TryCreate(
                        containerGroup.Key,
                        out Switch2PhysicalContainerIdentity container) ||
                    !container.Equals(
                        expected.Observation.ContainerIdentity))
                {
                    continue;
                }

                HidToken[] containerHids = containerGroup.Take(2).ToArray();
                if (containerHids.Length != 1 ||
                    hidSnapshot.InvalidContainers.Contains(
                        containerGroup.Key) ||
                    commandSnapshot.InvalidContainers.Contains(
                        containerGroup.Key))
                {
                    return false;
                }

                CommandNode[] commandNodes = commandSnapshot.Nodes.Where(
                    node => node.ContainerId == containerGroup.Key).Take(2).
                    ToArray();
                if (commandNodes.Length != 1)
                {
                    return false;
                }

                HidToken hid = containerHids[0];
                CommandToken command = ResolveUniqueCommand(commandNodes[0]);
                var inputObservation =
                    new Switch2UsbHidInterfaceObservation(container,
                        Switch2PhysicalInputRegistration.
                            ProUsbInputInterfaceNumber,
                        Switch2PhysicalDeviceFactory.ProUsbAlternateSetting,
                        Switch2UsbBoundDriver.HidClass, hid.Caps.UsagePage,
                        hid.Caps.Usage, hid.Caps.InputReportByteLength,
                        hid.Caps.OutputReportByteLength,
                        hid.Caps.FeatureReportByteLength);
                var observation = new Switch2ProUsbCompositeObservation(
                    hid.VendorId, hid.ProductId, hid.VersionNumber, container,
                    1, 1, inputObservation,
                    expected.Observation.CommandInterface);
                var current = new Switch2ProUsbWindowsCandidate(observation,
                    hid.DevInst, hid.InstanceId, hid.ParentInstanceId,
                    hid.Path, hid.ParentService, command.DevInst,
                    command.InstanceId, command.Path, command.Service);
                if (current.TryGetAdmittedRegistration(
                        out Switch2PhysicalInputRegistration registration) &&
                    registration.Equals(expectedRegistration) &&
                    current.SameIdentity(expected))
                {
                    matches++;
                }
            }

            return matches == 1;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public bool TryOpenInput(Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsInputHandle input)
    {
        input = null;
        if (candidate == null)
        {
            return false;
        }

        SafeFileHandle handle = NativeMethods.CreateFileW(candidate.HidPath,
            Switch2ProUsbWindowsOpenPolicy.InputDesiredAccess,
            Switch2ProUsbWindowsOpenPolicy.InputShareMode, IntPtr.Zero,
            OpenExisting, Switch2ProUsbWindowsOpenPolicy.OverlappedFlag,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Switch2ProUsbWindowsExactHandleRelease.
                TryReleaseFileQuiesced(handle);
            return false;
        }

        Switch2ProUsbWindowsCleanupAmbiguousException pendingCleanup = null;
        try
        {
            if (!TryReadHidFacts(handle, out ushort vendorId,
                    out ushort productId, out ushort versionNumber,
                    out HidCapsFact caps) ||
                !SameHidFacts(candidate.Observation, vendorId, productId,
                    versionNumber, caps))
            {
                return false;
            }

            input = new Switch2ProUsbWindowsInputHandle(handle);
            handle = null;
            return true;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            pendingCleanup = ex;
            throw;
        }
        catch (Exception acquisitionFailure)
        {
            pendingCleanup = new
                Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Input acquisition outcome is ambiguous.",
                    retainedOwner: null,
                    innerException: acquisitionFailure);
            throw pendingCleanup;
        }
        finally
        {
            try
            {
                if (!Switch2ProUsbWindowsExactHandleRelease.
                        TryReleaseFileQuiesced(handle))
                {
                    throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                        "Input file-handle cleanup could not be proven.",
                        handle);
                }
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException cleanup)
                when (pendingCleanup != null)
            {
                throw CombineCleanupAmbiguities(pendingCleanup, cleanup);
            }
        }
    }

    public bool TryOpenPresence(Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsPresenceHandle presence)
    {
        presence = null;
        if (candidate == null)
        {
            return false;
        }
        if (!TryOpenCommandPresence(candidate.CommandPath,
                out SafeFileHandle file, out SafeWinUsbHandle winUsb,
                out CommandTopology topology))
        {
            return false;
        }

        Switch2ProUsbWindowsCleanupAmbiguousException pendingCleanup = null;
        try
        {
            if (!SameCommandTopology(candidate.Observation.CommandInterface,
                    topology))
            {
                return false;
            }

            presence = new Switch2ProUsbWindowsPresenceHandle(file, winUsb);
            file = null;
            winUsb = null;
            return true;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            pendingCleanup = ex;
            throw;
        }
        catch (Exception acquisitionFailure)
        {
            pendingCleanup = new
                Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Command-presence acquisition outcome is ambiguous.",
                    retainedOwner: null,
                    innerException: acquisitionFailure);
            throw pendingCleanup;
        }
        finally
        {
            try
            {
                CloseCommandPresence(ref file, ref winUsb);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException cleanup)
                when (pendingCleanup != null)
            {
                throw CombineCleanupAmbiguities(pendingCleanup, cleanup);
            }
        }
    }

    public bool TryOpenOwnedHid(Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsOwnedHidHandle hid)
    {
        hid = null;
        if (candidate == null)
        {
            return false;
        }

        SafeFileHandle handle = NativeMethods.CreateFileW(candidate.HidPath,
            Switch2ProUsbWindowsOpenPolicy.OwnedHidDesiredAccess,
            Switch2ProUsbWindowsOpenPolicy.OwnedHidShareMode, IntPtr.Zero,
            OpenExisting, Switch2ProUsbWindowsOpenPolicy.OverlappedFlag,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Switch2ProUsbWindowsExactHandleRelease.
                TryReleaseFileQuiesced(handle);
            return false;
        }

        Switch2ProUsbWindowsCleanupAmbiguousException pendingCleanup = null;
        try
        {
            if (!TryReadHidFacts(handle, out ushort vendorId,
                    out ushort productId, out ushort versionNumber,
                    out HidCapsFact caps) ||
                !SameHidFacts(candidate.Observation, vendorId, productId,
                    versionNumber, caps))
            {
                return false;
            }

            hid = new Switch2ProUsbWindowsOwnedHidHandle(handle);
            handle = null;
            return true;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            pendingCleanup = ex;
            throw;
        }
        catch (Exception acquisitionFailure)
        {
            pendingCleanup = new
                Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Owned HID acquisition outcome is ambiguous.",
                    retainedOwner: null,
                    innerException: acquisitionFailure);
            throw pendingCleanup;
        }
        finally
        {
            try
            {
                if (!Switch2ProUsbWindowsExactHandleRelease.
                        TryReleaseFileQuiesced(handle))
                {
                    throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                        "Owned HID file cleanup could not be proven.",
                        handle);
                }
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException cleanup)
                when (pendingCleanup != null)
            {
                throw CombineCleanupAmbiguities(pendingCleanup, cleanup);
            }
        }
    }

    public bool TryOpenOwnedCommand(
        Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsOwnedCommandHandle command)
    {
        command = null;
        if (candidate == null ||
            !TryOpenCommand(candidate.CommandPath,
                Switch2ProUsbWindowsOpenPolicy.OwnedCommandDesiredAccess,
                Switch2ProUsbWindowsOpenPolicy.OwnedCommandShareMode,
                out SafeFileHandle file, out SafeWinUsbHandle winUsb,
                out CommandTopology topology))
        {
            return false;
        }

        Switch2ProUsbWindowsCleanupAmbiguousException pendingCleanup = null;
        try
        {
            if (!SameCommandTopology(candidate.Observation.CommandInterface,
                    topology))
            {
                return false;
            }

            command = new Switch2ProUsbWindowsOwnedCommandHandle(file,
                winUsb, Switch2PhysicalDeviceFactory.CommandBulkOutEndpoint,
                Switch2PhysicalDeviceFactory.CommandBulkInEndpoint);
            file = null;
            winUsb = null;
            return true;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            pendingCleanup = ex;
            throw;
        }
        catch (Exception acquisitionFailure)
        {
            pendingCleanup = new
                Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Owned command acquisition outcome is ambiguous.",
                    retainedOwner: null,
                    innerException: acquisitionFailure);
            throw pendingCleanup;
        }
        finally
        {
            try
            {
                CloseCommandPresence(ref file, ref winUsb);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException cleanup)
                when (pendingCleanup != null)
            {
                throw CombineCleanupAmbiguities(pendingCleanup, cleanup);
            }
        }
    }

    private static HidDiscoverySnapshot DiscoverHids()
    {
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = NativeMethods.SetupDiGetClassDevsW(ref hidGuid, null,
            IntPtr.Zero, NativeConstants.DigcfPresent |
                NativeConstants.DigcfDeviceInterface);
        RequireClassSet(set);

        var matches = new List<HidToken>();
        var invalidContainers = new HashSet<Guid>();
        Switch2ProUsbWindowsCleanupAmbiguousException pendingCleanup = null;
        try
        {
            for (uint index = 0; ; index++)
            {
                SpDeviceInterfaceData interfaceData = NewInterfaceData();
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set,
                        IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    RequireEnumerationEnd();
                    break;
                }
                if (!IsActive(interfaceData.Flags))
                {
                    continue;
                }

                InterfaceDetail detail;
                SpDevinfoData detailInfo;
                string instanceId;
                string hardwareIds;
                try
                {
                    detail = GetInterfaceDetail(set, ref interfaceData);
                    detailInfo = detail.DeviceInfo;
                    instanceId = GetInstanceId(set, ref detailInfo);
                    hardwareIds = TryGetRegistryString(set,
                        ref detailInfo, NativeConstants.SpdrpHardwareId,
                        NativeConstants.RegMultiSz);
                }
                catch
                {
                    // Without identity/container attribution this active HID
                    // entry could be a duplicate of a later candidate. Exact
                    // cardinality is unproven, so reject the whole snapshot.
                    throw;
                }

                bool instanceClaimsTarget = HasExactInterfaceIdentity(
                    instanceId, NativeConstants.HidInterfaceMarker);
                bool hardwareClaimsTarget = hardwareIds != null &&
                    HasExactInterfaceIdentity(hardwareIds,
                        NativeConstants.HidInterfaceMarker);
                if (!instanceClaimsTarget && !hardwareClaimsTarget)
                {
                    continue;
                }

                // From this point failures can be attributed to one opaque
                // physical container instead of aborting otherwise valid pads.
                Guid containerId;
                try
                {
                    containerId = GetContainerId(set, ref detailInfo);
                }
                catch
                {
                    throw;
                }
                if (containerId == Guid.Empty)
                {
                    continue;
                }
                if (instanceClaimsTarget && !hardwareClaimsTarget)
                {
                    invalidContainers.Add(containerId);
                    continue;
                }

                try
                {
                    if (!TryReadHidFactsMetadataOnly(detail.Path,
                            out ushort vendorId, out ushort productId,
                            out ushort versionNumber, out HidCapsFact caps))
                    {
                        invalidContainers.Add(containerId);
                        continue;
                    }

                    SpDevinfoData parent = GetParentDeviceInfo(set,
                        ref detailInfo);
                    string parentInstanceId = GetInstanceId(set, ref parent);
                    string parentHardwareIds = GetRequiredRegistryString(set,
                        ref parent, NativeConstants.SpdrpHardwareId,
                        NativeConstants.RegMultiSz);
                    string parentService = GetRequiredRegistryString(set,
                        ref parent, NativeConstants.SpdrpService,
                        NativeConstants.RegSz);
                    Guid parentContainerId = GetContainerId(set, ref parent);

                    bool admittedShape = HasExactInterfaceIdentity(hardwareIds,
                            NativeConstants.HidInterfaceMarker) &&
                        (HasExactInterfaceIdentity(parentInstanceId,
                             NativeConstants.HidInterfaceMarker) ||
                         HasExactInterfaceIdentity(parentHardwareIds,
                             NativeConstants.HidInterfaceMarker)) &&
                        string.Equals(parentService,
                            NativeConstants.HidService,
                            StringComparison.OrdinalIgnoreCase) &&
                        parentContainerId == containerId &&
                        vendorId == Switch2InputProtocolIdentity.
                            NintendoUsbVendorId &&
                        productId == Switch2InputProtocolIdentity.
                            ProController2UsbProductId &&
                        versionNumber == Switch2InputProtocolIdentity.
                            AuditedProController2UsbBcdDevice &&
                        IsExactHidCaps(caps);
                    if (!admittedShape)
                    {
                        invalidContainers.Add(containerId);
                        continue;
                    }

                    matches.Add(new HidToken(detailInfo.DevInst, instanceId,
                        parentInstanceId, containerId, detail.Path,
                        parentService, vendorId, productId, versionNumber,
                        caps));
                }
                catch (Switch2ProUsbWindowsCleanupAmbiguousException)
                {
                    throw;
                }
                catch
                {
                    invalidContainers.Add(containerId);
                }
            }
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            pendingCleanup = ex;
            throw;
        }
        finally
        {
            try
            {
                DestroyClassSetQuiesced(set);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException cleanup)
                when (pendingCleanup != null)
            {
                throw CombineCleanupAmbiguities(pendingCleanup, cleanup);
            }
        }

        return new HidDiscoverySnapshot(matches, invalidContainers);
    }

    private static CommandDiscoverySnapshot DiscoverCommands()
    {
        IntPtr set = NativeMethods.SetupDiGetClassDevsW(IntPtr.Zero, null,
            IntPtr.Zero, NativeConstants.DigcfPresent |
                NativeConstants.DigcfAllClasses);
        RequireClassSet(set);
        var candidates = new List<CommandNode>();
        var invalidContainers = new HashSet<Guid>();
        Switch2ProUsbWindowsCleanupAmbiguousException pendingCleanup = null;
        try
        {
            for (uint index = 0; ; index++)
            {
                SpDevinfoData info = NewDeviceInfo();
                if (!NativeMethods.SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    RequireEnumerationEnd();
                    break;
                }

                string instanceId;
                string hardwareIds;
                try
                {
                    instanceId = GetInstanceId(set, ref info);
                    hardwareIds = TryGetRegistryString(set, ref info,
                        NativeConstants.SpdrpHardwareId,
                        NativeConstants.RegMultiSz) ?? string.Empty;
                }
                catch
                {
                    // An unattributable active device node could be a duplicate
                    // MI_01 for a later candidate. Fail the snapshot closed.
                    throw;
                }
                if (!HasExactInterfaceIdentity(instanceId,
                        NativeConstants.CommandInterfaceMarker) &&
                    !HasExactInterfaceIdentity(hardwareIds,
                        NativeConstants.CommandInterfaceMarker))
                {
                    continue;
                }

                Guid observedContainerId;
                try
                {
                    observedContainerId = GetContainerId(set, ref info);
                }
                catch
                {
                    throw;
                }
                if (observedContainerId == Guid.Empty)
                {
                    continue;
                }
                try
                {
                    string service = GetRequiredRegistryString(set, ref info,
                        NativeConstants.SpdrpService, NativeConstants.RegSz);
                    Guid[] interfaceGuids = ReadDeviceInterfaceGuids(set,
                        ref info);
                    if (!string.Equals(service, NativeConstants.WinUsbService,
                            StringComparison.OrdinalIgnoreCase) ||
                        interfaceGuids.Length == 0)
                    {
                        invalidContainers.Add(observedContainerId);
                        continue;
                    }
                    candidates.Add(new CommandNode(info.DevInst, instanceId,
                        observedContainerId, service, interfaceGuids));
                }
                catch
                {
                    invalidContainers.Add(observedContainerId);
                }
            }
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            pendingCleanup = ex;
            throw;
        }
        finally
        {
            try
            {
                DestroyClassSetQuiesced(set);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException cleanup)
                when (pendingCleanup != null)
            {
                throw CombineCleanupAmbiguities(pendingCleanup, cleanup);
            }
        }

        return new CommandDiscoverySnapshot(candidates, invalidContainers);
    }

    private static CommandToken ResolveUniqueCommand(CommandNode candidate)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Guid guid in candidate.InterfaceGuids)
        {
            foreach (string path in ResolveInterfacePaths(guid,
                         candidate.InstanceId, candidate.ContainerId))
            {
                paths.Add(path);
            }
        }
        if (paths.Count != 1)
        {
            throw new InvalidOperationException();
        }

        return new CommandToken(candidate.DevInst, candidate.InstanceId,
            candidate.ContainerId, paths.Single(), candidate.Service);
    }

    private static IEnumerable<string> ResolveInterfacePaths(Guid classGuid,
        string expectedInstanceId, Guid expectedContainerId)
    {
        IntPtr set = NativeMethods.SetupDiGetClassDevsW(ref classGuid, null,
            IntPtr.Zero, NativeConstants.DigcfPresent |
                NativeConstants.DigcfDeviceInterface);
        RequireClassSet(set);
        try
        {
            for (uint index = 0; ; index++)
            {
                SpDeviceInterfaceData interfaceData = NewInterfaceData();
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set,
                        IntPtr.Zero, ref classGuid, index, ref interfaceData))
                {
                    RequireEnumerationEnd();
                    yield break;
                }
                if (!IsActive(interfaceData.Flags))
                {
                    continue;
                }

                InterfaceDetail detail = GetInterfaceDetail(set,
                    ref interfaceData);
                SpDevinfoData detailInfo = detail.DeviceInfo;
                string instanceId = GetInstanceId(set, ref detailInfo);
                Guid containerId = GetContainerId(set, ref detailInfo);
                if (containerId == expectedContainerId &&
                    string.Equals(instanceId, expectedInstanceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    yield return detail.Path;
                }
            }
        }
        finally
        {
            DestroyClassSetQuiesced(set);
        }
    }

    private static bool TryReadHidFactsMetadataOnly(string path,
        out ushort vendorId, out ushort productId, out ushort versionNumber,
        out HidCapsFact caps)
    {
        vendorId = 0;
        productId = 0;
        versionNumber = 0;
        caps = default;
        SafeFileHandle handle = NativeMethods.CreateFileW(path, 0,
            Switch2ProUsbWindowsOpenPolicy.MetadataShareMode, IntPtr.Zero,
            OpenExisting, FileAttributeNormal, IntPtr.Zero);
        try
        {
            return !handle.IsInvalid && TryReadHidFacts(handle, out vendorId,
                out productId, out versionNumber, out caps);
        }
        finally
        {
            if (!Switch2ProUsbWindowsExactHandleRelease.
                    TryReleaseFileQuiesced(handle))
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Metadata HID file-handle cleanup could not be proven.",
                    handle);
            }
        }
    }

    private static bool TryReadHidFacts(SafeFileHandle handle,
        out ushort vendorId, out ushort productId, out ushort versionNumber,
        out HidCapsFact caps)
    {
        vendorId = 0;
        productId = 0;
        versionNumber = 0;
        caps = default;
        var attributes = new HiddAttributes
        {
            Size = Marshal.SizeOf<HiddAttributes>(),
        };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
        {
            return false;
        }

        IntPtr preparsed = IntPtr.Zero;
        bool acquired;
        try
        {
            acquired = NativeMethods.HidD_GetPreparsedData(handle,
                out preparsed);
        }
        catch
        {
            // The acquisition call's outcome is unprovable. Release any
            // pointer the native boundary did publish, but retain acquisition
            // ambiguity even when that cleanup succeeds.
            bool released = preparsed == IntPtr.Zero ||
                TryFreePreparsedData(preparsed);
            if (!released)
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "HID preparsed-data acquisition and cleanup outcomes are " +
                    "ambiguous.", new Switch2ProUsbWindowsRawAcquisitionQuarantine(
                        preparsed, "HID preparsed data"));
            }
            throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                "HID preparsed-data acquisition outcome is ambiguous.");
        }
        if (!acquired || preparsed == IntPtr.Zero)
        {
            // Adopt and release every nonzero pointer even when the BOOL
            // contradicts it. A false free is an outcome-uncertain metadata
            // lifetime, not an ordinary malformed-device result.
            if (preparsed != IntPtr.Zero &&
                !TryFreePreparsedData(preparsed))
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Rejected HID preparsed-data cleanup could not be proven.",
                    new Switch2ProUsbWindowsRawAcquisitionQuarantine(preparsed,
                        "HID preparsed data"));
            }
            return false;
        }

        bool succeeded = false;
        try
        {
            if (NativeMethods.HidP_GetCaps(preparsed,
                    out HidpCaps nativeCaps) == NativeConstants.HidpSuccess)
            {
                vendorId = attributes.VendorId;
                productId = attributes.ProductId;
                versionNumber = attributes.VersionNumber;
                caps = new HidCapsFact(nativeCaps.UsagePage,
                    nativeCaps.Usage, nativeCaps.InputReportByteLength,
                    nativeCaps.OutputReportByteLength,
                    nativeCaps.FeatureReportByteLength);
                succeeded = true;
            }
        }
        finally
        {
            if (!TryFreePreparsedData(preparsed))
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "HID preparsed-data cleanup could not be proven.",
                    new Switch2ProUsbWindowsRawAcquisitionQuarantine(preparsed,
                        "HID preparsed data"));
            }
        }
        return succeeded;
    }

    private static void DestroyClassSetQuiesced(IntPtr set)
    {
        if (set != IntPtr.Zero && set != new IntPtr(-1) &&
            !TryDestroyClassSet(set))
        {
            throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                "SetupAPI device-information-set cleanup could not be proven.",
                new Switch2ProUsbWindowsRawAcquisitionQuarantine(set,
                    "SetupAPI device information set"));
        }
    }

    private static bool TryFreePreparsedData(IntPtr preparsed)
    {
        try
        {
            return NativeMethods.HidD_FreePreparsedData(preparsed);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDestroyClassSet(IntPtr set)
    {
        try
        {
            return NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryObserveCommandTopology(string path,
        out CommandTopology topology)
    {
        topology = default;
        if (!TryOpenCommandPresence(path, out SafeFileHandle file,
                out SafeWinUsbHandle winUsb, out topology))
        {
            return false;
        }
        try
        {
            return true;
        }
        finally
        {
            CloseCommandPresence(ref file, ref winUsb);
        }
    }

    private static bool TryOpenCommandPresence(string path,
        out SafeFileHandle file, out SafeWinUsbHandle winUsb,
        out CommandTopology topology) => TryOpenCommand(path,
        Switch2ProUsbWindowsOpenPolicy.PresenceDesiredAccess,
        Switch2ProUsbWindowsOpenPolicy.PresenceShareMode, out file,
        out winUsb, out topology);

    private static bool TryOpenCommand(string path, uint desiredAccess,
        uint shareMode, out SafeFileHandle file,
        out SafeWinUsbHandle winUsb, out CommandTopology topology)
    {
        file = null;
        winUsb = null;
        topology = default;
        IntPtr unownedWinUsb = IntPtr.Zero;
        try
        {
            file = NativeMethods.CreateFileW(path, desiredAccess, shareMode,
                IntPtr.Zero, OpenExisting,
                Switch2ProUsbWindowsOpenPolicy.OverlappedFlag, IntPtr.Zero);
            if (file.IsInvalid)
            {
                CloseCommandPresence(ref file, ref winUsb);
                return false;
            }
            bool initialized = NativeMethods.WinUsb_Initialize(file,
                out unownedWinUsb);
            if (unownedWinUsb != IntPtr.Zero)
            {
                winUsb = new SafeWinUsbHandle(unownedWinUsb);
                unownedWinUsb = IntPtr.Zero;
            }
            if (!initialized || winUsb == null)
            {
                CloseCommandPresence(ref file, ref winUsb);
                return false;
            }
            if (!NativeMethods.WinUsb_GetCurrentAlternateSetting(winUsb,
                    out byte currentAlternate) || currentAlternate != 0 ||
                !NativeMethods.WinUsb_QueryInterfaceSettings(winUsb, 0,
                    out UsbInterfaceDescriptor descriptor) ||
                descriptor.NumEndpoints != 2)
            {
                CloseCommandPresence(ref file, ref winUsb);
                return false;
            }

            Span<Switch2UsbPipeObservation> pipes =
                stackalloc Switch2UsbPipeObservation[2];
            for (byte index = 0; index < 2; index++)
            {
                if (!NativeMethods.WinUsb_QueryPipe(winUsb, 0, index,
                        out WinUsbPipeInformation pipe))
                {
                    CloseCommandPresence(ref file, ref winUsb);
                    return false;
                }
                pipes[index] = new Switch2UsbPipeObservation(pipe.PipeId,
                    MapPipeType(pipe.PipeType), pipe.MaximumPacketSize,
                    pipe.Interval);
            }
            topology = new CommandTopology(descriptor.InterfaceNumber,
                descriptor.AlternateSetting, descriptor.NumEndpoints,
                pipes[0], pipes[1]);
            if (!IsExactCommandTopology(topology))
            {
                CloseCommandPresence(ref file, ref winUsb);
                topology = default;
                return false;
            }
            return true;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException)
        {
            topology = default;
            throw;
        }
        catch (Exception acquisitionFailure)
        {
            if (unownedWinUsb != IntPtr.Zero)
            {
                // Adopt every nonzero value before attempting cleanup. If
                // WinUsb_Initialize threw after publishing its out value, the
                // SafeHandle is the exact capability retained by a cleanup-
                // ambiguity exception and, ultimately, the reservation ledger.
                winUsb = new SafeWinUsbHandle(unownedWinUsb);
                unownedWinUsb = IntPtr.Zero;
            }
            try
            {
                CloseCommandPresence(ref file, ref winUsb);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException cleanup)
            {
                throw CreateCommandAcquisitionAmbiguity(acquisitionFailure,
                    cleanup);
            }
            topology = default;
            throw CreateCommandAcquisitionAmbiguity(acquisitionFailure,
                cleanupFailure: null);
        }
    }

    internal static Switch2ProUsbWindowsCleanupAmbiguousException
        CreateCommandAcquisitionAmbiguity(Exception acquisitionFailure,
            Switch2ProUsbWindowsCleanupAmbiguousException cleanupFailure)
    {
        if (acquisitionFailure == null)
        {
            throw new ArgumentNullException(nameof(acquisitionFailure));
        }
        return cleanupFailure == null ?
            new Switch2ProUsbWindowsCleanupAmbiguousException(
                "Command acquisition outcome is ambiguous even though its " +
                "observed cleanup completed.", retainedOwner: null,
                innerException: acquisitionFailure) :
            new Switch2ProUsbWindowsCleanupAmbiguousException(
                "Command acquisition and cleanup outcomes are ambiguous.",
                cleanupFailure.RetainedOwner, acquisitionFailure);
    }

    internal static Switch2ProUsbWindowsCleanupAmbiguousException
        CombineCleanupAmbiguities(
            Switch2ProUsbWindowsCleanupAmbiguousException first,
            Switch2ProUsbWindowsCleanupAmbiguousException second)
    {
        if (first == null)
        {
            throw new ArgumentNullException(nameof(first));
        }
        if (second == null)
        {
            throw new ArgumentNullException(nameof(second));
        }
        object retainedOwner;
        if (first.RetainedOwner == null)
        {
            retainedOwner = second.RetainedOwner;
        }
        else if (second.RetainedOwner == null ||
            ReferenceEquals(first.RetainedOwner, second.RetainedOwner))
        {
            retainedOwner = first.RetainedOwner;
        }
        else
        {
            retainedOwner = new Switch2ProUsbWindowsAcquisitionQuarantineOwner(
                first.RetainedOwner, second.RetainedOwner);
        }
        return new Switch2ProUsbWindowsCleanupAmbiguousException(
            "Multiple native cleanup outcomes are ambiguous.", retainedOwner,
            new AggregateException(first, second));
    }

    private static void CloseCommandPresence(ref SafeFileHandle file,
        ref SafeWinUsbHandle winUsb)
    {
        bool winUsbReleased = true;
        bool fileReleased = true;
        Exception winUsbFailure = null;
        try
        {
            if (winUsb != null)
            {
                winUsbReleased = winUsb.TryDisposeQuiesced();
                if (winUsbReleased)
                {
                    winUsb = null;
                }
            }
        }
        catch (Exception ex)
        {
            winUsbReleased = false;
            winUsbFailure = ex;
        }
        finally
        {
            fileReleased = Switch2ProUsbWindowsExactHandleRelease.
                TryReleaseFileQuiesced(file);
            if (fileReleased)
            {
                file = null;
            }
        }
        if (!winUsbReleased || !fileReleased)
        {
            object retainedOwner = !winUsbReleased && !fileReleased ?
                new Switch2ProUsbWindowsAcquisitionQuarantineOwner(winUsb,
                    file) : !winUsbReleased ? winUsb : file;
            throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                "WinUSB/file lifetime cleanup could not be proven.",
                retainedOwner, winUsbFailure);
        }
    }

    private static bool SameHidFacts(
        in Switch2ProUsbCompositeObservation observation,
        ushort vendorId, ushort productId, ushort versionNumber,
        in HidCapsFact caps)
    {
        Switch2UsbHidInterfaceObservation expected =
            observation.InputInterface;
        return vendorId == observation.VendorId &&
            productId == observation.ProductId &&
            versionNumber == observation.BcdDevice &&
            caps.UsagePage == expected.UsagePage &&
            caps.Usage == expected.Usage &&
            caps.InputReportByteLength == expected.InputReportByteLength &&
            caps.OutputReportByteLength == expected.OutputReportByteLength &&
            caps.FeatureReportByteLength == expected.FeatureReportByteLength &&
            IsExactHidCaps(caps);
    }

    private static bool IsExactHidCaps(in HidCapsFact caps) =>
        caps.UsagePage == Switch2PhysicalDeviceFactory.
            GenericDesktopUsagePage &&
        caps.Usage == Switch2PhysicalDeviceFactory.GamePadUsage &&
        caps.InputReportByteLength == Switch2PhysicalInputRegistration.
            ProUsbReportByteLength &&
        caps.OutputReportByteLength == Switch2PhysicalInputRegistration.
            ProUsbReportByteLength &&
        caps.FeatureReportByteLength == 0;

    private static bool SameCommandTopology(
        in Switch2UsbCommandInterfaceObservation expected,
        in CommandTopology actual) =>
        expected.InterfaceNumber == actual.InterfaceNumber &&
        expected.AlternateSetting == actual.AlternateSetting &&
        expected.EndpointCount == actual.EndpointCount &&
        expected.Pipe0.Equals(actual.Pipe0) &&
        expected.Pipe1.Equals(actual.Pipe1) &&
        IsExactCommandTopology(actual);

    private static bool IsExactCommandTopology(
        in CommandTopology topology) =>
        topology.InterfaceNumber == Switch2PhysicalInputRegistration.
            ProUsbCommandInterfaceNumber &&
        topology.AlternateSetting ==
            Switch2PhysicalDeviceFactory.ProUsbAlternateSetting &&
        topology.EndpointCount == 2 &&
        ((IsExactPipe(topology.Pipe0,
              Switch2PhysicalDeviceFactory.CommandBulkOutEndpoint) &&
          IsExactPipe(topology.Pipe1,
              Switch2PhysicalDeviceFactory.CommandBulkInEndpoint)) ||
         (IsExactPipe(topology.Pipe1,
              Switch2PhysicalDeviceFactory.CommandBulkOutEndpoint) &&
          IsExactPipe(topology.Pipe0,
              Switch2PhysicalDeviceFactory.CommandBulkInEndpoint)));

    private static bool IsExactPipe(in Switch2UsbPipeObservation pipe,
        byte address) => pipe.EndpointAddress == address &&
        pipe.TransferType == Switch2UsbPipeTransferType.Bulk &&
        pipe.MaximumPacketSize ==
            Switch2PhysicalDeviceFactory.CommandMaximumPacketSize &&
        pipe.Interval == 0;

    private static Switch2UsbPipeTransferType MapPipeType(
        NativePipeType type) => type switch
        {
            NativePipeType.Control => Switch2UsbPipeTransferType.Control,
            NativePipeType.Isochronous =>
                Switch2UsbPipeTransferType.Isochronous,
            NativePipeType.Bulk => Switch2UsbPipeTransferType.Bulk,
            NativePipeType.Interrupt => Switch2UsbPipeTransferType.Interrupt,
            _ => Switch2UsbPipeTransferType.Unknown,
        };

    private static Guid[] ReadDeviceInterfaceGuids(IntPtr set,
        ref SpDevinfoData info)
    {
        IntPtr rawKey = NativeMethods.SetupDiOpenDevRegKey(set, ref info,
            NativeConstants.DicsFlagGlobal, 0, NativeConstants.DiregDev,
            NativeConstants.KeyQueryValue);
        if (rawKey == NativeConstants.InvalidHandleValue)
        {
            throw new InvalidOperationException();
        }

        using var key = new SafeRegistryHandle(rawKey, ownsHandle: true);
        byte[] multi = TryReadRegistryValue(key, "DeviceInterfaceGUIDs",
            out uint multiType);
        byte[] single = TryReadRegistryValue(key, "DeviceInterfaceGUID",
            out uint singleType);
        if (multi == null && single == null)
        {
            throw new InvalidOperationException();
        }

        var result = new List<Guid>();
        if (multi != null)
        {
            if (!TryParseGuidMultiString(multiType, multi, out Guid[] parsed))
            {
                throw new InvalidOperationException();
            }
            result.AddRange(parsed);
        }
        if (single != null)
        {
            if (!TryParseGuidString(singleType, single, out Guid parsed) ||
                result.Contains(parsed))
            {
                throw new InvalidOperationException();
            }
            result.Add(parsed);
        }
        return result.ToArray();
    }

    private static byte[] TryReadRegistryValue(SafeRegistryHandle key,
        string name, out uint type)
    {
        uint bytes = 0;
        int result = NativeMethods.RegQueryValueExW(key, name, IntPtr.Zero,
            out type, null, ref bytes);
        if (result == NativeConstants.ErrorFileNotFound)
        {
            return null;
        }
        if (result != 0 || bytes < 4 ||
            bytes > NativeConstants.MaximumPropertyBytes || (bytes & 1) != 0)
        {
            throw new InvalidOperationException();
        }

        var buffer = new byte[checked((int)bytes)];
        uint returned = bytes;
        if (NativeMethods.RegQueryValueExW(key, name, IntPtr.Zero, out type,
                buffer, ref returned) != 0 || returned != bytes)
        {
            throw new InvalidOperationException();
        }
        return buffer;
    }

    private static bool TryParseGuidString(uint type, ReadOnlySpan<byte> bytes,
        out Guid guid)
    {
        guid = default;
        if (type != NativeConstants.RegSz || bytes.Length < 4 ||
            (bytes.Length & 1) != 0 || bytes[^1] != 0 || bytes[^2] != 0)
        {
            return false;
        }
        string decoded = Encoding.Unicode.GetString(bytes);
        return decoded.Length >= 2 && decoded[^1] == '\0' &&
            !decoded.AsSpan(0, decoded.Length - 1).Contains('\0') &&
            Guid.TryParse(decoded.AsSpan(0, decoded.Length - 1), out guid) &&
            guid != Guid.Empty;
    }

    private static bool TryParseGuidMultiString(uint type,
        ReadOnlySpan<byte> bytes, out Guid[] guids)
    {
        guids = Array.Empty<Guid>();
        if (type != NativeConstants.RegMultiSz || bytes.Length < 4 ||
            (bytes.Length & 1) != 0 || bytes[^1] != 0 || bytes[^2] != 0 ||
            bytes[^3] != 0 || bytes[^4] != 0)
        {
            return false;
        }

        string[] entries = Encoding.Unicode.GetString(bytes).Split('\0');
        if (entries.Length < 3 || entries[^1].Length != 0 ||
            entries[^2].Length != 0)
        {
            return false;
        }
        var parsed = new List<Guid>(entries.Length - 2);
        for (int index = 0; index < entries.Length - 2; index++)
        {
            if (entries[index].Length == 0 ||
                !Guid.TryParse(entries[index], out Guid guid) ||
                guid == Guid.Empty || parsed.Contains(guid))
            {
                return false;
            }
            parsed.Add(guid);
        }
        if (parsed.Count == 0)
        {
            return false;
        }
        guids = parsed.ToArray();
        return true;
    }

    private static InterfaceDetail GetInterfaceDetail(IntPtr set,
        ref SpDeviceInterfaceData interfaceData)
    {
        bool query = NativeMethods.SetupDiGetDeviceInterfaceDetailW(set,
            ref interfaceData, IntPtr.Zero, 0, out uint required,
            IntPtr.Zero);
        int queryError = Marshal.GetLastWin32Error();
        int headerBytes = IntPtr.Size == 8 ? 8 : 6;
        if (query || queryError != NativeConstants.ErrorInsufficientBuffer ||
            required < headerBytes + sizeof(char) || required > int.MaxValue)
        {
            throw new InvalidOperationException();
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            Marshal.WriteInt32(buffer, headerBytes);
            SpDevinfoData info = NewDeviceInfo();
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetailW(set,
                    ref interfaceData, buffer, required, out _, ref info))
            {
                throw new InvalidOperationException();
            }
            string path = Marshal.PtrToStringUni(buffer + 4);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException();
            }
            return new InterfaceDetail(path, info);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetInstanceId(IntPtr set, ref SpDevinfoData info)
    {
        var buffer = new char[NativeConstants.MaximumInstanceCharacters];
        if (!NativeMethods.SetupDiGetDeviceInstanceIdW(set, ref info, buffer,
                buffer.Length, out int required) || required <= 1 ||
            required > buffer.Length)
        {
            throw new InvalidOperationException();
        }
        return new string(buffer, 0, required - 1);
    }

    private static SpDevinfoData GetParentDeviceInfo(IntPtr set,
        ref SpDevinfoData child)
    {
        if (NativeMethods.CM_Get_Parent(out uint parentDevInst, child.DevInst,
                0) != 0)
        {
            throw new InvalidOperationException();
        }
        var buffer = new char[NativeConstants.MaximumInstanceCharacters];
        if (NativeMethods.CM_Get_Device_IDW(parentDevInst, buffer,
                checked((uint)buffer.Length), 0) != 0)
        {
            throw new InvalidOperationException();
        }
        int terminator = Array.IndexOf(buffer, '\0');
        if (terminator <= 0)
        {
            throw new InvalidOperationException();
        }

        string parentId = new(buffer, 0, terminator);
        SpDevinfoData parent = NewDeviceInfo();
        if (!NativeMethods.SetupDiOpenDeviceInfoW(set, parentId,
                IntPtr.Zero, 0, ref parent) || parent.DevInst != parentDevInst)
        {
            throw new InvalidOperationException();
        }
        return parent;
    }

    private static Guid GetContainerId(IntPtr set, ref SpDevinfoData info)
    {
        byte[] bytes = new byte[16];
        DevPropKey key = NativeConstants.ContainerIdKey;
        if (!NativeMethods.SetupDiGetDevicePropertyW(set, ref info, ref key,
                out uint type, bytes, bytes.Length, out uint required, 0) ||
            type != NativeConstants.DevPropTypeGuid || required != 16)
        {
            throw new InvalidOperationException();
        }
        return new Guid(bytes);
    }

    private static string GetRequiredRegistryString(IntPtr set,
        ref SpDevinfoData info, int property, uint type) =>
        TryGetRegistryString(set, ref info, property, type) ??
            throw new InvalidOperationException();

    private static string TryGetRegistryString(IntPtr set,
        ref SpDevinfoData info, int property, uint expectedType)
    {
        byte[] buffer = new byte[NativeConstants.MaximumPropertyBytes];
        if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(set, ref info,
                property, out uint actualType, buffer, buffer.Length,
                out uint required) || required < sizeof(char) ||
            required > buffer.Length || (required & 1) != 0)
        {
            return null;
        }
        return TryDecodeRegistryString(actualType, expectedType,
            buffer.AsSpan(0, checked((int)required)), out string value) ?
            value : null;
    }

    private static bool TryDecodeRegistryString(uint actualType,
        uint expectedType, ReadOnlySpan<byte> bytes, out string value)
    {
        value = string.Empty;
        if (actualType != expectedType || bytes.Length < sizeof(char) ||
            (bytes.Length & 1) != 0)
        {
            return false;
        }
        string decoded = Encoding.Unicode.GetString(bytes);
        if (expectedType == NativeConstants.RegSz)
        {
            if (decoded.Length < 1 || decoded[^1] != '\0' ||
                decoded.AsSpan(0, decoded.Length - 1).Contains('\0'))
            {
                return false;
            }
            value = decoded[..^1];
            return value.Length != 0;
        }
        if (expectedType != NativeConstants.RegMultiSz ||
            decoded.Length < 2 || decoded[^1] != '\0' ||
            decoded[^2] != '\0')
        {
            return false;
        }
        string[] entries = decoded.Split('\0');
        if (entries.Length < 3 || entries[^1].Length != 0 ||
            entries[^2].Length != 0 ||
            entries.Take(entries.Length - 2).Any(entry => entry.Length == 0))
        {
            return false;
        }
        value = string.Join('\0', entries.Take(entries.Length - 2));
        return value.Length != 0;
    }

    private static bool HasExactInterfaceIdentity(string value,
        string marker)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        char[] separators = ['\\', '&', '#'];
        foreach (string entry in value.Split('\0',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] components = entry.Split(separators,
                StringSplitOptions.RemoveEmptyEntries);
            if (components.Contains("VID_057E",
                    StringComparer.OrdinalIgnoreCase) &&
                components.Contains("PID_2069",
                    StringComparer.OrdinalIgnoreCase) &&
                components.Contains(marker,
                    StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsActive(uint flags) =>
        (flags & NativeConstants.SpintActive) != 0 &&
        (flags & NativeConstants.SpintRemoved) == 0;

    private static void RequireClassSet(IntPtr set)
    {
        if (set == NativeConstants.InvalidHandleValue)
        {
            throw new InvalidOperationException();
        }
    }

    private static void RequireEnumerationEnd()
    {
        if (Marshal.GetLastWin32Error() != NativeConstants.ErrorNoMoreItems)
        {
            throw new InvalidOperationException();
        }
    }

    private static SpDeviceInterfaceData NewInterfaceData() => new()
    {
        Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>(),
    };

    private static SpDevinfoData NewDeviceInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<SpDevinfoData>(),
    };

    private readonly record struct HidCapsFact(ushort UsagePage, ushort Usage,
        ushort InputReportByteLength, ushort OutputReportByteLength,
        ushort FeatureReportByteLength);

    private sealed record HidToken(uint DevInst, string InstanceId,
        string ParentInstanceId, Guid ContainerId, string Path,
        string ParentService, ushort VendorId, ushort ProductId,
        ushort VersionNumber, HidCapsFact Caps);

    private sealed record HidDiscoverySnapshot(IReadOnlyList<HidToken> Tokens,
        HashSet<Guid> InvalidContainers);

    private sealed record CommandNode(uint DevInst, string InstanceId,
        Guid ContainerId, string Service, Guid[] InterfaceGuids);

    private sealed record CommandDiscoverySnapshot(
        IReadOnlyList<CommandNode> Nodes,
        HashSet<Guid> InvalidContainers);

    private sealed record CommandToken(uint DevInst, string InstanceId,
        Guid ContainerId, string Path, string Service);

    private sealed record InterfaceDetail(string Path,
        SpDevinfoData DeviceInfo);

    private readonly record struct CommandTopology(byte InterfaceNumber,
        byte AlternateSetting, byte EndpointCount,
        Switch2UsbPipeObservation Pipe0,
        Switch2UsbPipeObservation Pipe1);

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        internal int Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidpCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;
        internal fixed ushort Reserved[17];
        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps;
        internal ushort NumberInputValueCaps;
        internal ushort NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps;
        internal ushort NumberOutputValueCaps;
        internal ushort NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps;
        internal ushort NumberFeatureValueCaps;
        internal ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbInterfaceDescriptor
    {
        internal byte Length;
        internal byte DescriptorType;
        internal byte InterfaceNumber;
        internal byte AlternateSetting;
        internal byte NumEndpoints;
        internal byte InterfaceClass;
        internal byte InterfaceSubClass;
        internal byte InterfaceProtocol;
        internal byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation
    {
        internal NativePipeType PipeType;
        internal byte PipeId;
        internal ushort MaximumPacketSize;
        internal byte Interval;
    }

    private enum NativePipeType
    {
        Control = 0,
        Isochronous = 1,
        Bulk = 2,
        Interrupt = 3,
    }

    internal sealed class SafeWinUsbHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly object releaseGate = new();
        private readonly Func<IntPtr, bool> releaseNative;
        private bool nativeReleased;
        private bool releaseAmbiguous;

        internal SafeWinUsbHandle(IntPtr handle,
            Func<IntPtr, bool> releaseNative = null) : base(true)
        {
            this.releaseNative = releaseNative ?? NativeMethods.WinUsb_Free;
            SetHandle(handle);
        }

        internal bool TryDisposeQuiesced()
        {
            lock (releaseGate)
            {
                if (IsClosed)
                {
                    return true;
                }
                if (releaseAmbiguous)
                {
                    return false;
                }
                if (!nativeReleased && IsInvalid)
                {
                    nativeReleased = true;
                }
                if (!nativeReleased)
                {
                    try
                    {
                        if (!releaseNative(handle))
                        {
                            return false;
                        }
                        nativeReleased = true;
                    }
                    catch
                    {
                        // The release may have reached native code. Retrying
                        // could double-free a recycled WinUSB value.
                        releaseAmbiguous = true;
                        return false;
                    }
                }
                try
                {
                    SetHandleAsInvalid();
                    Dispose();
                    return true;
                }
                catch
                {
                    // Native release is already proven and is never repeated;
                    // only managed finalization remains retryable.
                    return false;
                }
            }
        }

        internal bool IsReleaseAmbiguous
        {
            get
            {
                lock (releaseGate)
                {
                    return releaseAmbiguous;
                }
            }
        }

        protected override bool ReleaseHandle()
        {
            lock (releaseGate)
            {
                if (nativeReleased)
                {
                    return true;
                }
                if (releaseAmbiguous)
                {
                    return false;
                }
                try
                {
                    bool released = releaseNative(handle);
                    if (released)
                    {
                        nativeReleased = true;
                    }
                    return released;
                }
                catch
                {
                    releaseAmbiguous = true;
                    return false;
                }
            }
        }
    }

    private static class NativeConstants
    {
        internal const uint DigcfPresent = 0x00000002;
        internal const uint DigcfAllClasses = 0x00000004;
        internal const uint DigcfDeviceInterface = 0x00000010;
        internal const int SpdrpHardwareId = 0x00000001;
        internal const int SpdrpService = 0x00000004;
        internal const uint RegSz = 1;
        internal const uint RegMultiSz = 7;
        internal const uint DevPropTypeGuid = 0x0000000D;
        internal const int ErrorInsufficientBuffer = 122;
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorNoMoreItems = 259;
        internal const int MaximumPropertyBytes = 4096;
        internal const int MaximumInstanceCharacters = 512;
        internal const uint DicsFlagGlobal = 0x00000001;
        internal const uint DiregDev = 0x00000001;
        internal const int KeyQueryValue = 0x0001;
        internal const uint SpintActive = 0x00000001;
        internal const uint SpintRemoved = 0x00000004;
        internal const int HidpSuccess = 0x00110000;
        internal const string HidInterfaceMarker = "MI_00";
        internal const string CommandInterfaceMarker = "MI_01";
        internal const string HidService = "HidUsb";
        internal const string WinUsbService = "WinUSB";
        internal static readonly IntPtr InvalidHandleValue = new(-1);
        internal static readonly DevPropKey ContainerIdKey = new()
        {
            FormatId = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
            PropertyId = 2,
        };
    }

    private static class NativeMethods
    {
        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle,
            ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetPreparsedData(
            SafeFileHandle handle, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_FreePreparsedData(
            IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData,
            out HidpCaps capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(
            IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceSet,
            uint memberIndex, ref SpDevinfoData deviceInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceSet, IntPtr deviceInfo,
            ref Guid interfaceClassGuid, uint memberIndex,
            ref SpDeviceInterfaceData interfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr deviceSet, ref SpDeviceInterfaceData interfaceData,
            IntPtr detailData, uint detailSize, out uint requiredSize,
            IntPtr deviceInfo);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr deviceSet, ref SpDeviceInterfaceData interfaceData,
            IntPtr detailData, uint detailSize, out uint requiredSize,
            ref SpDevinfoData deviceInfo);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr deviceSet, ref SpDevinfoData deviceInfo,
            [Out] char[] instanceId, int instanceIdSize,
            out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr deviceSet, ref SpDevinfoData deviceInfo, int property,
            out uint propertyType, [Out] byte[] propertyBuffer,
            int propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceSet,
            ref SpDevinfoData deviceInfo, uint scope, uint hardwareProfile,
            uint keyType, int desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int RegQueryValueExW(SafeRegistryHandle key,
            string valueName, IntPtr reserved, out uint valueType,
            [Out] byte[] data, ref uint dataSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiOpenDeviceInfoW(IntPtr deviceSet,
            string deviceInstanceId, IntPtr parent, uint openFlags,
            ref SpDevinfoData deviceInfo);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Parent(out uint parentDevInst,
            uint childDevInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_IDW(uint devInst,
            [Out] char[] buffer, uint bufferLength, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDevicePropertyW(IntPtr set,
            ref SpDevinfoData info, ref DevPropKey propertyKey,
            out uint propertyType, [Out] byte[] propertyBuffer,
            int propertyBufferSize, out uint requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(
            IntPtr deviceSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_Initialize(
            SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_GetCurrentAlternateSetting(
            SafeWinUsbHandle interfaceHandle,
            out byte currentAlternateSetting);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_QueryInterfaceSettings(
            SafeWinUsbHandle interfaceHandle, byte alternateSetting,
            out UsbInterfaceDescriptor descriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_QueryPipe(
            SafeWinUsbHandle interfaceHandle, byte alternateSetting,
            byte pipeIndex, out WinUsbPipeInformation pipeInformation);
    }
}

internal static class Switch2ProUsbWindowsOpenPolicy
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OverlappedFlag = 0x40000000;

    // Windows' HID stack retains an output-capable MI_00 open on the production
    // Switch 2 Pro. Both read-only and full-duplex application opens fail with
    // ERROR_SHARING_VIOLATION unless write sharing is admitted. DS4Windows'
    // process reservation still permits only one mapper-owned lifetime.
    internal const uint InputDesiredAccess = GenericRead;
    internal const uint InputShareMode = FileShareRead | FileShareWrite;

    // WinUSB initialization for MI_01 requires a read/write OS handle on the
    // production Switch 2 Pro interface even when only querying topology. The
    // presence wrapper exposes no WinUSB operation and is closed immediately
    // after that read-side observation, so this access does not grant a caller
    // an output path or extend command ownership.
    internal const uint PresenceDesiredAccess = GenericRead | GenericWrite;
    internal const uint PresenceShareMode = FileShareRead | FileShareWrite;
    internal const uint MetadataShareMode = FileShareRead | FileShareWrite;

    // The full-duplex owner takes one process-reserved read/write MI_00
    // lifetime. HID requires shared write access on this device; command
    // serialization and lifetime ownership remain inside the retained owner.
    internal const uint OwnedHidDesiredAccess = GenericRead | GenericWrite;
    internal const uint OwnedHidShareMode = FileShareRead | FileShareWrite;

    // The exact MI_01 WinUSB lifetime owns both bulk pipes. Read sharing keeps
    // metadata observers possible while denying a second command writer.
    internal const uint OwnedCommandDesiredAccess =
        GenericRead | GenericWrite;
    internal const uint OwnedCommandShareMode = FileShareRead;
}

internal sealed class Switch2ProUsbWindowsPresenceHandle :
    ISwitch2ProUsbWindowsPresenceHandle
{
    private SafeFileHandle file;
    private Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle winUsb;

    internal Switch2ProUsbWindowsPresenceHandle(SafeFileHandle file,
        Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle winUsb)
    {
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        this.winUsb = winUsb ?? throw new ArgumentNullException(nameof(winUsb));
    }

    public void Dispose()
    {
        if (winUsb != null && !winUsb.TryDisposeQuiesced())
        {
            throw new InvalidOperationException(
                "WinUSB presence lifetime was not released.");
        }
        winUsb = null;
        if (!Switch2ProUsbWindowsExactHandleRelease.
                TryReleaseFileQuiesced(file))
        {
            throw new InvalidOperationException(
                "WinUSB presence file lifetime was not released.");
        }
        file = null;
    }
}
