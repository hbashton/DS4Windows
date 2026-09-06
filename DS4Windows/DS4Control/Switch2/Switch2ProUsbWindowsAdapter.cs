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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows.Switch2;

/// <summary>
/// Windows implementation of the read-only Switch 2 Pro USB discovery and
/// input lease seams. Discovery considers admitted composites independently by
/// opaque Windows container. A successful observation is a one-shot capability:
/// open consumes it, atomically reserves that container within this process,
/// re-enumerates the exact MI_00+MI_01 composite before acquiring handles,
/// validates both open handles, and re-enumerates once more before a lease can
/// escape. No path, instance ID, serial, or native handle is exposed.
/// </summary>
public sealed class Switch2ProUsbWindowsAdapter :
    ISwitch2ProUsbOsDiscoveryAdapter, ISwitch2ProUsbNativeAdapter
{
    private readonly object gate = new();
    private readonly ISwitch2ProUsbWindowsPlatform platform;
    private readonly Switch2ProUsbWindowsReservationRegistry reservations;
    private Switch2ProUsbWindowsCandidate observedCandidate;
    private Switch2PhysicalContainerIdentity selectionCursor;

    public Switch2ProUsbWindowsAdapter()
        : this(new Switch2ProUsbWindowsNativePlatform(),
            Switch2ProUsbWindowsReservationRegistry.ProcessWide)
    {
    }

    internal Switch2ProUsbWindowsAdapter(
        ISwitch2ProUsbWindowsPlatform platform)
        : this(platform, Switch2ProUsbWindowsReservationRegistry.ProcessWide)
    {
    }

    internal Switch2ProUsbWindowsAdapter(
        ISwitch2ProUsbWindowsPlatform platform,
        Switch2ProUsbWindowsReservationRegistry reservations)
    {
        this.platform = platform ?? throw new ArgumentNullException(
            nameof(platform));
        this.reservations = reservations ?? throw new ArgumentNullException(
            nameof(reservations));
    }

    public bool TryObserveComposite(
        out Switch2ProUsbCompositeObservation observation)
    {
        lock (gate)
        {
            observation = default;
            if (reservations.HasUnattributedAcquisitionQuarantine)
            {
                observedCandidate = null;
                return false;
            }
            try
            {
                if (!platform.TryDiscoverCandidates(out var candidates) ||
                    !TrySelectUnreservedCandidate(candidates,
                        out var candidate))
                {
                    observedCandidate = null;
                    return false;
                }
                observedCandidate = candidate;
                selectionCursor = candidate.Observation.ContainerIdentity;
                observation = candidate.Observation;
                return true;
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservations.RetainUnattributedAcquisitionQuarantine(
                    ex.RetainedOwner ?? ex);
                observedCandidate = null;
                return false;
            }
            catch
            {
                observedCandidate = null;
                return false;
            }
        }
    }

    public bool TryOpenReadOnlyComposite(
        in Switch2PhysicalInputRegistration registration,
        out ISwitch2ProUsbReadOnlyCompositeLease lease)
    {
        lease = null;
        if (!registration.IsValid)
        {
            ClearObservation();
            return false;
        }

        // An observation authorizes at most one open attempt. This prevents a
        // caller from replaying stale discovery state to acquire another
        // physical lifetime.
        Switch2ProUsbWindowsCandidate expected;
        lock (gate)
        {
            expected = observedCandidate;
            observedCandidate = null;
        }
        if (expected == null ||
            !expected.TryGetAdmittedRegistration(out var expectedRegistration) ||
            !expectedRegistration.Equals(registration))
        {
            return false;
        }

        ISwitch2ProUsbWindowsInputHandle input = null;
        ISwitch2ProUsbWindowsPresenceHandle presence = null;
        Switch2ProUsbWindowsReservationRegistry.
            Switch2ProUsbWindowsReservation reservation = null;
        bool inputReleased = true;
        bool presenceReleased = true;
        bool acquisitionAmbiguous = false;
        try
        {
            // Fence the physical container before the revalidation snapshot:
            // native discovery itself opens metadata/topology handles.
            if (!reservations.TryAcquire(registration.ContainerIdentity,
                    out reservation))
            {
                return false;
            }
            IReadOnlyList<Switch2ProUsbWindowsCandidate> currentCandidates;
            try
            {
                if (!platform.TryDiscoverCandidates(out currentCandidates))
                {
                    return false;
                }
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            if (
                !TryFindExactCandidate(currentCandidates, expected,
                    registration, out var current))
            {
                return false;
            }
            bool inputOpened;
            try
            {
                inputOpened = platform.TryOpenInput(current, out input);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            if (!inputOpened || input == null)
            {
                acquisitionAmbiguous = inputOpened && input == null;
                return false;
            }
            inputReleased = false;
            bool presenceOpened;
            try
            {
                presenceOpened = platform.TryOpenPresence(current,
                    out presence);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            if (!presenceOpened || presence == null)
            {
                acquisitionAmbiguous = presenceOpened && presence == null;
                return false;
            }
            presenceReleased = false;

            // Validate the complete device-tree identity again while both
            // handles are retained. A path reuse, device removal/rearrival,
            // descriptor change, or topology change fails the open.
            IReadOnlyList<Switch2ProUsbWindowsCandidate> reopenedCandidates;
            try
            {
                if (!platform.TryDiscoverCandidates(out reopenedCandidates))
                {
                    return false;
                }
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            if (!TryFindExactCandidate(reopenedCandidates, current,
                    registration, out var reopened) ||
                !reopened.SameIdentity(expected))
            {
                return false;
            }

            lease = new Switch2ProUsbWindowsReadOnlyCompositeLease(
                registration, input, presence, reservation);
            input = null;
            presence = null;
            reservation = null;
            return true;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            reservation?.RetainAcquisitionQuarantine(ex.RetainedOwner);
            acquisitionAmbiguous = true;
            return false;
        }
        catch
        {
            // Conservatively retain the container fence for any unexpected
            // failure after reservation. Future helper/constructor changes
            // must not silently convert an unproven acquisition into a clean
            // aborted-open release.
            acquisitionAmbiguous = true;
            return false;
        }
        finally
        {
            // No read can have started before the composite lease escapes, so
            // these partial-open resources are already quiescent.
            if (input != null)
            {
                inputReleased = false;
                try
                {
                    input.DisposeQuiesced();
                    inputReleased = true;
                }
                catch
                {
                    reservation?.RetainAcquisitionQuarantine(input);
                }
            }
            if (presence != null)
            {
                presenceReleased = false;
                try
                {
                    presence.Dispose();
                    presenceReleased = true;
                }
                catch
                {
                    reservation?.RetainAcquisitionQuarantine(presence);
                }
            }

            // A failed partial-handle release deliberately retains the
            // reservation for the rest of the process. Allowing a second
            // owner while an unproven handle may still be alive is unsafe.
            if (inputReleased && presenceReleased && !acquisitionAmbiguous)
            {
                reservation?.ReleaseAfterAbortedOpen();
            }
        }
    }

    private bool TrySelectUnreservedCandidate(
        IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates,
        out Switch2ProUsbWindowsCandidate selected)
    {
        selected = null;
        if (candidates == null || candidates.Count == 0)
        {
            return false;
        }

        var multiplicity = new Dictionary<
            Switch2PhysicalContainerIdentity, int>();
        for (int index = 0; index < candidates.Count; index++)
        {
            Switch2ProUsbWindowsCandidate candidate = candidates[index];
            if (candidate == null ||
                !candidate.Observation.ContainerIdentity.IsValid)
            {
                continue;
            }
            Switch2PhysicalContainerIdentity container =
                candidate.Observation.ContainerIdentity;
            multiplicity.TryGetValue(container, out int count);
            multiplicity[container] = count + 1;
        }

        int cursorIndex = -1;
        if (selectionCursor.IsValid)
        {
            for (int index = 0; index < candidates.Count; index++)
            {
                if (candidates[index] != null &&
                    candidates[index].Observation.ContainerIdentity.Equals(
                        selectionCursor))
                {
                    cursorIndex = index;
                    break;
                }
            }
        }

        // Round-robin from the previous opaque container, preserving platform
        // order without permanently starving a valid later controller when an
        // earlier candidate repeatedly fails to open.
        for (int offset = 1; offset <= candidates.Count; offset++)
        {
            int index = (cursorIndex + offset) % candidates.Count;
            Switch2ProUsbWindowsCandidate candidate = candidates[index];
            if (candidate == null ||
                !candidate.Observation.ContainerIdentity.IsValid ||
                multiplicity[candidate.Observation.ContainerIdentity] != 1 ||
                !candidate.TryGetAdmittedRegistration(out _) ||
                reservations.IsReserved(
                    candidate.Observation.ContainerIdentity))
            {
                continue;
            }
            selected = candidate;
            return true;
        }
        return false;
    }

    private static bool TryFindExactCandidate(
        IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates,
        Switch2ProUsbWindowsCandidate expected,
        in Switch2PhysicalInputRegistration registration,
        out Switch2ProUsbWindowsCandidate exact)
    {
        exact = null;
        if (candidates == null || expected == null)
        {
            return false;
        }

        int containerMatches = 0;
        for (int index = 0; index < candidates.Count; index++)
        {
            Switch2ProUsbWindowsCandidate candidate = candidates[index];
            if (candidate == null ||
                !candidate.Observation.ContainerIdentity.Equals(
                    registration.ContainerIdentity))
            {
                continue;
            }
            containerMatches++;
            exact = candidate;
        }

        return containerMatches == 1 && exact.SameIdentity(expected) &&
            exact.TryGetAdmittedRegistration(out var exactRegistration) &&
            exactRegistration.Equals(registration);
    }

    private void ClearObservation()
    {
        lock (gate)
        {
            observedCandidate = null;
        }
    }
}

internal interface ISwitch2ProUsbWindowsPlatform
{
    bool TryDiscoverCandidates(
        out IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates);

    bool TryOpenInput(Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsInputHandle input);

    bool TryOpenPresence(Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsPresenceHandle presence);
}

/// <summary>
/// Process-local sole-owner fence keyed only by the opaque Windows container.
/// The default adapter shares <see cref="ProcessWide"/>; tests inject isolated
/// registries. This does not claim, hide, open, or otherwise mutate a device.
/// </summary>
internal sealed class Switch2ProUsbWindowsReservationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<Switch2PhysicalContainerIdentity,
        ReservationEntry> reserved = [];
    private readonly List<object> unattributedQuarantineOwners = [];
    private readonly Action beforeRelease;

    internal static Switch2ProUsbWindowsReservationRegistry ProcessWide
        { get; } = new();

    internal Switch2ProUsbWindowsReservationRegistry(
        Action beforeRelease = null)
    {
        this.beforeRelease = beforeRelease;
    }

    internal bool IsReserved(
        in Switch2PhysicalContainerIdentity containerIdentity)
    {
        if (!containerIdentity.IsValid)
        {
            return true;
        }
        lock (gate)
        {
            return unattributedQuarantineOwners.Count != 0 ||
                reserved.ContainsKey(containerIdentity);
        }
    }

    internal bool HasUnattributedAcquisitionQuarantine
    {
        get
        {
            lock (gate)
            {
                return unattributedQuarantineOwners.Count != 0;
            }
        }
    }

    internal bool TryAcquire(
        in Switch2PhysicalContainerIdentity containerIdentity,
        out Switch2ProUsbWindowsReservation reservation)
    {
        reservation = null;
        if (!containerIdentity.IsValid)
        {
            return false;
        }
        lock (gate)
        {
            if (unattributedQuarantineOwners.Count != 0)
            {
                return false;
            }
            if (reserved.ContainsKey(containerIdentity))
            {
                return false;
            }
            var entry = new ReservationEntry();
            reserved.Add(containerIdentity, entry);
            reservation = new Switch2ProUsbWindowsReservation(this,
                containerIdentity, entry);
            return true;
        }
    }

    private void Release(
        in Switch2PhysicalContainerIdentity containerIdentity,
        ReservationEntry expectedEntry,
        Switch2ProUsbWindowsCompositeTerminalFence terminalFence,
        object expectedTerminalLifetimeOwner)
    {
        lock (gate)
        {
            if (!reserved.TryGetValue(containerIdentity,
                    out ReservationEntry current) ||
                !ReferenceEquals(current, expectedEntry))
            {
                throw new InvalidOperationException(
                    "The reservation release capability is stale.");
            }
            if (current.QuarantineOwners.Count != 0)
            {
                throw new InvalidOperationException(
                    "An acquisition quarantine still owns native resources.");
            }
            if (!ReferenceEquals(current.TerminalLifetimeOwner,
                    expectedTerminalLifetimeOwner))
            {
                throw new InvalidOperationException(
                    "The terminal lifetime release capability is stale.");
            }
        }
        beforeRelease?.Invoke();
        Switch2PhysicalContainerIdentity exactContainer = containerIdentity;

        void PublishRegistryRemoval()
        {
            lock (gate)
            {
                if (!reserved.TryGetValue(exactContainer,
                        out ReservationEntry current) ||
                    !ReferenceEquals(current, expectedEntry) ||
                    current.QuarantineOwners.Count != 0 ||
                    !ReferenceEquals(current.TerminalLifetimeOwner,
                        expectedTerminalLifetimeOwner))
                {
                    throw new InvalidOperationException(
                        "The reservation changed while release was in progress.");
                }
                reserved.Remove(exactContainer);
            }
        }

        if (terminalFence == null)
        {
            PublishRegistryRemoval();
        }
        else
        {
            // The release hook above is intentionally outside the terminal
            // gate. A stale callback may therefore latch the shared fence.
            // Final removal is then committed under that same fence, so the
            // callback and reservation publication have one exact order.
            terminalFence.PublishTerminalRelease(PublishRegistryRemoval);
        }
    }

    private void AdoptTerminalLifetime(
        in Switch2PhysicalContainerIdentity containerIdentity,
        ReservationEntry expectedEntry, object exactLifetimeOwner)
    {
        if (exactLifetimeOwner == null)
        {
            throw new ArgumentNullException(nameof(exactLifetimeOwner));
        }
        lock (gate)
        {
            if (!reserved.TryGetValue(containerIdentity,
                    out ReservationEntry current) ||
                !ReferenceEquals(current, expectedEntry) ||
                current.QuarantineOwners.Count != 0 ||
                current.TerminalLifetimeOwner != null)
            {
                throw new InvalidOperationException(
                    "The reservation cannot adopt this terminal lifetime.");
            }

            // Root the exact escaped lease for the complete reservation
            // lifetime. A caller dropping its last reference after a failed
            // terminal cleanup therefore cannot hand ambiguous SafeHandles to
            // GC/finalization while the registry merely retains a key.
            current.TerminalLifetimeOwner = exactLifetimeOwner;
        }
    }

    private void RetainAcquisitionQuarantine(
        in Switch2PhysicalContainerIdentity containerIdentity,
        ReservationEntry expectedEntry, object retainedOwner)
    {
        if (retainedOwner == null)
        {
            return;
        }
        lock (gate)
        {
            if (!reserved.TryGetValue(containerIdentity,
                    out ReservationEntry current) ||
                !ReferenceEquals(current, expectedEntry))
            {
                throw new InvalidOperationException(
                    "The acquisition quarantine capability is stale.");
            }
            current.QuarantineOwners.Add(retainedOwner);
        }
    }

    internal bool RetainsAcquisitionQuarantine(
        in Switch2PhysicalContainerIdentity containerIdentity,
        object retainedOwner)
    {
        if (retainedOwner == null)
        {
            return false;
        }
        lock (gate)
        {
            return reserved.TryGetValue(containerIdentity,
                    out ReservationEntry entry) &&
                entry.QuarantineOwners.Exists(owner =>
                    ReferenceEquals(owner, retainedOwner));
        }
    }

    internal bool RetainsTerminalLifetime(
        in Switch2PhysicalContainerIdentity containerIdentity,
        object exactLifetimeOwner)
    {
        if (exactLifetimeOwner == null)
        {
            return false;
        }
        lock (gate)
        {
            return reserved.TryGetValue(containerIdentity,
                    out ReservationEntry entry) &&
                ReferenceEquals(entry.TerminalLifetimeOwner,
                    exactLifetimeOwner);
        }
    }

    internal void RetainUnattributedAcquisitionQuarantine(
        object retainedOwner)
    {
        lock (gate)
        {
            // Even a dependency which did not expose its native capability is
            // a terminal-attention fact. Keep an exact marker strongly rooted
            // so later discovery/reservation cannot silently proceed.
            unattributedQuarantineOwners.Add(retainedOwner ?? new object());
        }
    }

    internal bool RetainsUnattributedAcquisitionQuarantine(
        object retainedOwner)
    {
        lock (gate)
        {
            return retainedOwner != null &&
                unattributedQuarantineOwners.Exists(owner =>
                    ReferenceEquals(owner, retainedOwner));
        }
    }

    internal sealed class ReservationEntry
    {
        internal List<object> QuarantineOwners { get; } = [];

        internal object TerminalLifetimeOwner { get; set; }
    }

    internal sealed class Switch2ProUsbWindowsReservation
    {
        private readonly object releaseGate = new();
        private Switch2ProUsbWindowsReservationRegistry owner;
        private readonly Switch2PhysicalContainerIdentity containerIdentity;
        private readonly ReservationEntry entry;

        internal Switch2ProUsbWindowsReservation(
            Switch2ProUsbWindowsReservationRegistry owner,
            in Switch2PhysicalContainerIdentity containerIdentity,
            ReservationEntry entry)
        {
            this.owner = owner ?? throw new ArgumentNullException(
                nameof(owner));
            this.containerIdentity = containerIdentity;
            this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        internal void ReleaseAfterAbortedOpen() => Release(terminalFence: null);

        internal void AdoptTerminalLifetime(object exactLifetimeOwner)
        {
            lock (releaseGate)
            {
                if (owner == null)
                {
                    throw new InvalidOperationException(
                        "The reservation was already released.");
                }
                owner.AdoptTerminalLifetime(containerIdentity, entry,
                    exactLifetimeOwner);
            }
        }

        internal void ReleaseAfterTerminalDisposal(
            Switch2ProUsbWindowsCompositeTerminalFence terminalFence,
            object exactLifetimeOwner)
        {
            if (terminalFence == null)
            {
                throw new ArgumentNullException(nameof(terminalFence));
            }
            if (exactLifetimeOwner == null)
            {
                throw new ArgumentNullException(nameof(exactLifetimeOwner));
            }
            Release(terminalFence, exactLifetimeOwner);
        }

        internal void RetainAcquisitionQuarantine(object retainedOwner)
        {
            if (retainedOwner == null)
            {
                return;
            }
            lock (releaseGate)
            {
                if (owner == null)
                {
                    throw new InvalidOperationException(
                        "The reservation was already released.");
                }
                owner.RetainAcquisitionQuarantine(containerIdentity, entry,
                    retainedOwner);
            }
        }

        private void Release(
            Switch2ProUsbWindowsCompositeTerminalFence terminalFence,
            object exactTerminalLifetimeOwner = null)
        {
            lock (releaseGate)
            {
                if (owner == null)
                {
                    return;
                }

                // Keep the exact release capability until both the registry
                // hook and removal succeed. A failed release can then retry
                // without stranding an ownerless reserved container.
                if (terminalFence == null)
                {
                    owner.Release(containerIdentity, entry,
                        terminalFence: null,
                        expectedTerminalLifetimeOwner: null);
                    owner = null;
                    return;
                }

                terminalFence.BeginTerminalRelease();
                try
                {
                    owner.Release(containerIdentity, entry, terminalFence,
                        exactTerminalLifetimeOwner);
                    owner = null;
                }
                catch
                {
                    terminalFence.AbandonTerminalRelease();
                    throw;
                }
            }
        }
    }
}

internal interface ISwitch2ProUsbWindowsInputHandle
{
    /// <summary>
    /// A false result guarantees that no callback has run or can later run.
    /// A true result may invoke the callback before returning.
    /// </summary>
    bool TryBeginRead(byte[] destination, int offset, int count,
        Action<Switch2ProUsbWindowsReadCompletion> callback,
        out ISwitch2ProUsbWindowsReadOperation operation);

    void DisposeQuiesced();
}

internal interface ISwitch2ProUsbWindowsReadOperation
{
    bool TryCancelExact();

    /// <summary>
    /// True means the native completion has been observed and no future native
    /// callback can start. The lease separately drains a callback already in
    /// progress before exposing public quiescence.
    /// </summary>
    bool TryWaitForNativeQuiescence(int timeoutMilliseconds);

    void ReleaseSubmissionQuiesced();
}

internal interface ISwitch2ProUsbWindowsPresenceHandle : IDisposable
{
}

internal readonly struct Switch2ProUsbWindowsReadCompletion
{
    internal Switch2ProUsbWindowsReadCompletion(int bytesTransferred,
        long timestampQpc, Switch2ProUsbNativeReadStatus status)
    {
        BytesTransferred = bytesTransferred;
        TimestampQpc = timestampQpc;
        Status = status;
    }

    internal int BytesTransferred { get; }

    internal long TimestampQpc { get; }

    internal Switch2ProUsbNativeReadStatus Status { get; }
}

/// <summary>
/// Raw Windows identity is intentionally confined to this internal class. It
/// has no formatting override, serialization surface, or public accessor.
/// </summary>
internal sealed class Switch2ProUsbWindowsCandidate
{
    internal Switch2ProUsbWindowsCandidate(
        Switch2ProUsbCompositeObservation observation,
        uint hidDevInst, string hidInstanceId, string hidParentInstanceId,
        string hidPath, string hidParentService,
        uint commandDevInst, string commandInstanceId, string commandPath,
        string commandService)
    {
        Observation = observation;
        HidDevInst = hidDevInst;
        HidInstanceId = RequirePrivateIdentity(hidInstanceId);
        HidParentInstanceId = RequirePrivateIdentity(hidParentInstanceId);
        HidPath = RequirePrivateIdentity(hidPath);
        HidParentService = RequirePrivateIdentity(hidParentService);
        CommandDevInst = commandDevInst;
        CommandInstanceId = RequirePrivateIdentity(commandInstanceId);
        CommandPath = RequirePrivateIdentity(commandPath);
        CommandService = RequirePrivateIdentity(commandService);
    }

    internal Switch2ProUsbCompositeObservation Observation { get; }
    internal uint HidDevInst { get; }
    internal string HidInstanceId { get; }
    internal string HidParentInstanceId { get; }
    internal string HidPath { get; }
    internal string HidParentService { get; }
    internal uint CommandDevInst { get; }
    internal string CommandInstanceId { get; }
    internal string CommandPath { get; }
    internal string CommandService { get; }

    internal bool TryGetAdmittedRegistration(
        out Switch2PhysicalInputRegistration registration) =>
        Switch2PhysicalDeviceFactory.TryAdmitProUsb(Observation,
            out registration, out _);

    internal bool SameIdentity(Switch2ProUsbWindowsCandidate other) =>
        other != null && HidDevInst == other.HidDevInst &&
        CommandDevInst == other.CommandDevInst &&
        PrivateEquals(HidInstanceId, other.HidInstanceId) &&
        PrivateEquals(HidParentInstanceId, other.HidParentInstanceId) &&
        PrivateEquals(HidPath, other.HidPath) &&
        PrivateEquals(HidParentService, other.HidParentService) &&
        PrivateEquals(CommandInstanceId, other.CommandInstanceId) &&
        PrivateEquals(CommandPath, other.CommandPath) &&
        PrivateEquals(CommandService, other.CommandService) &&
        SameObservation(Observation, other.Observation);

    private static bool SameObservation(
        in Switch2ProUsbCompositeObservation left,
        in Switch2ProUsbCompositeObservation right) =>
        left.VendorId == right.VendorId &&
        left.ProductId == right.ProductId &&
        left.BcdDevice == right.BcdDevice &&
        left.ContainerIdentity.Equals(right.ContainerIdentity) &&
        left.MatchingInputInterfaceCount ==
            right.MatchingInputInterfaceCount &&
        left.MatchingCommandInterfaceCount ==
            right.MatchingCommandInterfaceCount &&
        SameHid(left.InputInterface, right.InputInterface) &&
        SameCommand(left.CommandInterface, right.CommandInterface);

    private static bool SameHid(in Switch2UsbHidInterfaceObservation left,
        in Switch2UsbHidInterfaceObservation right) =>
        left.ContainerIdentity.Equals(right.ContainerIdentity) &&
        left.InterfaceNumber == right.InterfaceNumber &&
        left.AlternateSetting == right.AlternateSetting &&
        left.BoundDriver == right.BoundDriver &&
        left.UsagePage == right.UsagePage && left.Usage == right.Usage &&
        left.InputReportByteLength == right.InputReportByteLength &&
        left.OutputReportByteLength == right.OutputReportByteLength &&
        left.FeatureReportByteLength == right.FeatureReportByteLength;

    private static bool SameCommand(
        in Switch2UsbCommandInterfaceObservation left,
        in Switch2UsbCommandInterfaceObservation right) =>
        left.ContainerIdentity.Equals(right.ContainerIdentity) &&
        left.InterfaceNumber == right.InterfaceNumber &&
        left.AlternateSetting == right.AlternateSetting &&
        left.BoundDriver == right.BoundDriver &&
        left.EndpointCount == right.EndpointCount &&
        left.Pipe0.Equals(right.Pipe0) && left.Pipe1.Equals(right.Pipe1);

    private static bool PrivateEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string RequirePrivateIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) ? value :
            throw new ArgumentException("Invalid private Windows identity.");
}

internal sealed class Switch2ProUsbWindowsReadOnlyCompositeLease :
    ISwitch2ProUsbReadOnlyCompositeLease
{
    private readonly object gate = new();
    private readonly ISwitch2ProUsbWindowsInputHandle input;
    private readonly ISwitch2ProUsbWindowsPresenceHandle presence;
    private readonly Switch2ProUsbWindowsCompositeTerminalFence terminalFence;
    private readonly Switch2ProUsbWindowsReservationRegistry.
        Switch2ProUsbWindowsReservation reservation;
    private readonly ReadContext reusableContext = new();
    private readonly Action<Switch2ProUsbWindowsReadCompletion>
        completionCallback;
    private ReadContext active;
    private ulong readEpoch;
    private bool controlOperationInProgress;
    private bool controlReleaseSealed;
    private bool cancellationOperationInProgress;
    private bool readQuarantined;
    private ISwitch2ProUsbWindowsReadOperation quarantinedReadOperation;
    private bool quiescent;
    private bool disposalRequested;
    private bool disposalInProgress;
    private bool inputDisposed;
    private bool presenceDisposed;
    private bool reservationReleased;
    private bool disposed;

    internal Switch2ProUsbWindowsReadOnlyCompositeLease(
        in Switch2PhysicalInputRegistration registration,
        ISwitch2ProUsbWindowsInputHandle input,
        ISwitch2ProUsbWindowsPresenceHandle presence)
        : this(registration, input, presence, null,
            new Switch2ProUsbWindowsCompositeTerminalFence())
    {
    }

    internal Switch2ProUsbWindowsReadOnlyCompositeLease(
        in Switch2PhysicalInputRegistration registration,
        ISwitch2ProUsbWindowsInputHandle input,
        ISwitch2ProUsbWindowsPresenceHandle presence,
        Switch2ProUsbWindowsReservationRegistry.
            Switch2ProUsbWindowsReservation reservation)
        : this(registration, input, presence, reservation,
            new Switch2ProUsbWindowsCompositeTerminalFence())
    {
    }

    internal Switch2ProUsbWindowsReadOnlyCompositeLease(
        in Switch2PhysicalInputRegistration registration,
        ISwitch2ProUsbWindowsInputHandle input,
        ISwitch2ProUsbWindowsPresenceHandle presence,
        Switch2ProUsbWindowsReservationRegistry.
            Switch2ProUsbWindowsReservation reservation,
        Switch2ProUsbWindowsCompositeTerminalFence terminalFence)
    {
        if (!registration.IsValid)
        {
            throw new ArgumentException("Invalid registration.",
                nameof(registration));
        }
        Registration = registration;
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.presence = presence ?? throw new ArgumentNullException(
            nameof(presence));
        this.terminalFence = terminalFence ?? throw new ArgumentNullException(
            nameof(terminalFence));
        this.reservation = reservation;
        reservationReleased = reservation == null;
        completionCallback = CompleteCurrent;
        reservation?.AdoptTerminalLifetime(this);
    }

    public Switch2PhysicalInputRegistration Registration { get; }

    internal bool IsQuarantined
    {
        get
        {
            if (terminalFence.IsLatched)
            {
                return true;
            }
            lock (gate)
            {
                return readQuarantined;
            }
        }
    }

    public bool TryBeginInputRead(byte[] destination, int offset, int count,
        in Switch2ProUsbReadClaim claim,
        ISwitch2ProUsbReadCompletionTarget completionTarget)
    {
        if (destination == null || completionTarget == null ||
            offset < 0 || count != Registration.InputReportByteLength ||
            offset > destination.Length - count || !claim.IsValid)
        {
            return false;
        }

        bool ambiguous = false;
        bool started = false;
        lock (terminalFence.Gate)
        {
            if (!terminalFence.TryBeginSubmissionNoLock())
            {
                return false;
            }
            try
            {
                ReadContext context;
                ulong epoch;
                lock (gate)
                {
                    if (disposalRequested || readQuarantined || quiescent ||
                        active != null || controlOperationInProgress ||
                        readEpoch == ulong.MaxValue)
                    {
                        if (readQuarantined)
                        {
                            terminalFence.LatchNoLock();
                        }
                        return false;
                    }
                    epoch = ++readEpoch;
                    reusableContext.Reset(epoch, claim, completionTarget);
                    active = reusableContext;
                    context = active;
                }

                ISwitch2ProUsbWindowsReadOperation operation = null;
                bool dependencyThrew = false;
                try
                {
                    started = input.TryBeginRead(destination, offset, count,
                        completionCallback, out operation);
                }
                catch
                {
                    started = false;
                    dependencyThrew = true;
                }

                lock (gate)
                {
                    if (!started || operation == null || dependencyThrew)
                    {
                        if (MatchesSubmissionNoLock(context, epoch, claim,
                                null))
                        {
                            bool cleanRejection = !started &&
                                operation == null && !dependencyThrew &&
                                !context.CallbackStarted;
                            if (cleanRejection)
                            {
                                active = null;
                                context.Release();
                            }
                            else
                            {
                                // Contradictory start shapes retain every
                                // published capability and atomically fence all
                                // three composite facets.
                                context.Operation = operation;
                                quarantinedReadOperation = operation;
                                readQuarantined = true;
                                ambiguous = true;
                            }
                        }
                        else
                        {
                            quarantinedReadOperation = operation;
                            readQuarantined = true;
                            ambiguous = true;
                        }
                    }
                    else if (MatchesSubmissionNoLock(context, epoch, claim,
                                 null))
                    {
                        context.Operation = operation;
                        if (terminalFence.IsLatchedNoLock || readQuarantined)
                        {
                            // An inline callback may have exposed a duplicate
                            // or stale completion while the dependency was
                            // still inside native-start admission. Retain the
                            // just-published exact operation and do not let a
                            // success escape across that terminal fact.
                            quarantinedReadOperation = operation;
                            readQuarantined = true;
                            ambiguous = true;
                        }
                    }
                    else
                    {
                        // A started operation whose exact context disappeared
                        // remains owned and permanently fenced.
                        quarantinedReadOperation = operation;
                        readQuarantined = true;
                        ambiguous = true;
                    }
                    if (ambiguous)
                    {
                        terminalFence.LatchNoLock();
                    }
                    Monitor.PulseAll(gate);
                }

                if (ambiguous)
                {
                    throw new Switch2ProUsbWindowsReadStartAmbiguousException(
                        "Input start ownership could not be proven.");
                }
                return started && operation != null;
            }
            finally
            {
                terminalFence.EndSubmissionNoLock();
            }
        }
    }

    public bool TryRetireCompletedInputRead(
        in Switch2ProUsbReadClaim claim, int timeoutMilliseconds)
    {
        if (!claim.IsValid || timeoutMilliseconds < 0)
        {
            return false;
        }

        ReadContext completed;
        ulong epoch;
        ISwitch2ProUsbWindowsReadOperation operation;
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (gate)
        {
            completed = active;
            if (disposed || readQuarantined || quiescent || completed == null ||
                !completed.Claim.Equals(claim) ||
                completed.Operation == null || controlOperationInProgress)
            {
                return false;
            }
            epoch = completed.Epoch;
            operation = completed.Operation;
            controlOperationInProgress = true;
        }

        bool nativeQuiescent;
        try
        {
            nativeQuiescent = operation.
                TryWaitForNativeQuiescence(timeoutMilliseconds);
        }
        catch
        {
            nativeQuiescent = false;
        }
        if (!nativeQuiescent)
        {
            ReleaseControlOperation();
            return false;
        }

        lock (gate)
        {
            if (!MatchesSubmissionNoLock(completed, epoch, claim, operation))
            {
                controlOperationInProgress = false;
                Monitor.PulseAll(gate);
                return false;
            }
            // Once native quiescence is proven, seal this epoch against a new
            // cancellation capture. A cancellation already in flight remains
            // allowed, but exact storage release waits for that call to return.
            controlReleaseSealed = true;
            while (cancellationOperationInProgress)
            {
                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    controlReleaseSealed = false;
                    controlOperationInProgress = false;
                    Monitor.PulseAll(gate);
                    return false;
                }
                if (!MatchesSubmissionNoLock(completed, epoch, claim,
                        operation))
                {
                    controlReleaseSealed = false;
                    controlOperationInProgress = false;
                    Monitor.PulseAll(gate);
                    return false;
                }
            }
            // A cancellation may reach native quiescence without a managed
            // completion. Native quiescence proves no callback can begin
            // later, so that exact callback slot may be retired here.
            if (!completed.CallbackStarted)
            {
                completed.CallbackFinished = true;
            }
            if (!completed.CallbackFinished)
            {
                controlReleaseSealed = false;
                controlOperationInProgress = false;
                Monitor.PulseAll(gate);
                return false;
            }
        }
        try
        {
            operation.ReleaseSubmissionQuiesced();
        }
        catch
        {
            ReleaseControlOperation();
            return false;
        }

        lock (gate)
        {
            if (!MatchesSubmissionNoLock(completed, epoch, claim, operation))
            {
                controlReleaseSealed = false;
                controlOperationInProgress = false;
                Monitor.PulseAll(gate);
                return false;
            }
            active = null;
            completed.Release();
            controlReleaseSealed = false;
            controlOperationInProgress = false;
            Monitor.PulseAll(gate);
        }
        return true;
    }

    public bool TryCancelInputRead(in Switch2ProUsbReadClaim claim)
    {
        ReadContext context;
        ulong epoch;
        ISwitch2ProUsbWindowsReadOperation operation;
        lock (gate)
        {
            context = active;
            if (disposalRequested || readQuarantined || context == null ||
                !active.Claim.Equals(claim) || active.CallbackStarted ||
                active.Operation == null || active.CancellationIssued ||
                controlReleaseSealed || cancellationOperationInProgress)
            {
                return false;
            }
            epoch = context.Epoch;
            operation = active.Operation;
            cancellationOperationInProgress = true;
        }

        bool cancelled;
        try
        {
            cancelled = operation.TryCancelExact();
        }
        catch
        {
            cancelled = false;
        }
        lock (gate)
        {
            bool submissionUnchanged = MatchesSubmissionNoLock(context,
                epoch, claim, operation);
            if (cancelled && submissionUnchanged)
            {
                context.CancellationIssued = true;
            }
            cancellationOperationInProgress = false;
            Monitor.PulseAll(gate);
            return cancelled && submissionUnchanged;
        }
    }

    public bool TryWaitForInputQuiescence(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0)
        {
            return false;
        }

        ReadContext context;
        ulong epoch = 0;
        Switch2ProUsbReadClaim claim = default;
        ISwitch2ProUsbWindowsReadOperation operation = null;
        lock (gate)
        {
            if (disposed)
            {
                return true;
            }
            if (readQuarantined)
            {
                return false;
            }
            context = active;
            if (context == null)
            {
                if (controlOperationInProgress)
                {
                    return false;
                }
                quiescent = true;
                return true;
            }
            if (context.Operation == null || controlOperationInProgress)
            {
                return false;
            }
            epoch = context.Epoch;
            claim = context.Claim;
            operation = context.Operation;
            controlOperationInProgress = true;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool nativeQuiescent;
        try
        {
            nativeQuiescent = operation.
                TryWaitForNativeQuiescence(timeoutMilliseconds);
        }
        catch
        {
            nativeQuiescent = false;
        }
        if (!nativeQuiescent)
        {
            ReleaseControlOperation();
            return false;
        }

        lock (gate)
        {
            if (!MatchesSubmissionNoLock(context, epoch, claim, operation))
            {
                controlOperationInProgress = false;
                Monitor.PulseAll(gate);
                return false;
            }
            controlReleaseSealed = true;
            while (cancellationOperationInProgress)
            {
                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    controlReleaseSealed = false;
                    controlOperationInProgress = false;
                    Monitor.PulseAll(gate);
                    return false;
                }
                if (!MatchesSubmissionNoLock(context, epoch, claim,
                        operation))
                {
                    controlReleaseSealed = false;
                    controlOperationInProgress = false;
                    Monitor.PulseAll(gate);
                    return false;
                }
            }
            // A native implementation may report quiescence for a cancelled
            // operation without delivering a cancellation callback. Because
            // its true result forbids any callback from starting later, an
            // as-yet-unstarted callback can be retired here.
            if (!context.CallbackStarted)
            {
                context.CallbackFinished = true;
            }
            while (!context.CallbackFinished)
            {
                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    controlReleaseSealed = false;
                    controlOperationInProgress = false;
                    Monitor.PulseAll(gate);
                    return false;
                }
            }
        }

        try
        {
            operation.ReleaseSubmissionQuiesced();
        }
        catch
        {
            ReleaseControlOperation();
            return false;
        }

        lock (gate)
        {
            if (!MatchesSubmissionNoLock(context, epoch, claim, operation))
            {
                controlReleaseSealed = false;
                controlOperationInProgress = false;
                Monitor.PulseAll(gate);
                return false;
            }
            active = null;
            context.Release();
            quiescent = true;
            controlReleaseSealed = false;
            controlOperationInProgress = false;
            Monitor.PulseAll(gate);
        }
        return true;
    }

    public void DisposeQuiesced()
    {
        bool releaseInput;
        bool releasePresence;
        lock (gate)
        {
            if (disposalInProgress)
            {
                throw new InvalidOperationException(
                    "Windows input lease disposal is already in progress.");
            }
        }
        lock (terminalFence.Gate)
        {
            if (terminalFence.IsLatchedNoLock)
            {
                throw new InvalidOperationException(
                    "The Windows input lease is terminally quarantined.");
            }
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                if (readQuarantined || !quiescent || active != null ||
                    controlOperationInProgress ||
                    cancellationOperationInProgress)
                {
                    throw new InvalidOperationException(
                        "The Windows input lease is not exactly quiescent.");
                }
                if (disposalInProgress)
                {
                    throw new InvalidOperationException(
                        "Windows input lease disposal is already in progress.");
                }
                disposalRequested = true;
                disposalInProgress = true;
                releaseInput = !inputDisposed;
                releasePresence = !presenceDisposed;
            }
        }

        Exception inputFailure = null;
        Exception presenceFailure = null;
        if (releaseInput)
        {
            try
            {
                input.DisposeQuiesced();
                lock (gate)
                {
                    inputDisposed = true;
                }
            }
            catch (Exception ex)
            {
                inputFailure = ex;
            }
        }
        if (releasePresence)
        {
            try
            {
                presence.Dispose();
                lock (gate)
                {
                    presenceDisposed = true;
                }
            }
            catch (Exception ex)
            {
                presenceFailure = ex;
            }
        }

        bool terminalDisposed;
        lock (gate)
        {
            terminalDisposed = inputDisposed && presenceDisposed;
        }

        // The reservation cannot be released merely because quiescence was
        // proven. Both retained OS resources must have reached terminal,
        // successful disposal first. A failed partial disposal keeps the same
        // physical container fenced until a retry succeeds.
        Exception reservationFailure = null;
        if (terminalDisposed && !reservationReleased)
        {
            try
            {
                reservation.ReleaseAfterTerminalDisposal(terminalFence,
                    this);
                lock (gate)
                {
                    reservationReleased = true;
                }
            }
            catch (Exception ex)
            {
                reservationFailure = ex;
            }
        }

        lock (gate)
        {
            disposed = terminalDisposed && reservationReleased;
            disposalInProgress = false;
            Monitor.PulseAll(gate);
        }
        var failures = new List<Exception>(3);
        if (inputFailure != null)
        {
            failures.Add(inputFailure);
        }
        if (presenceFailure != null)
        {
            failures.Add(presenceFailure);
        }
        if (reservationFailure != null)
        {
            failures.Add(reservationFailure);
        }
        if (failures.Count == 1)
        {
            throw failures[0];
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }

    private void CompleteCurrent(Switch2ProUsbWindowsReadCompletion completion)
    {
        Complete(reusableContext, completion);
    }

    private void Complete(ReadContext context,
        Switch2ProUsbWindowsReadCompletion completion)
    {
        lock (terminalFence.Gate)
        {
            lock (gate)
            {
                if (!ReferenceEquals(active, context) ||
                    context.CallbackStarted || quiescent || disposed)
                {
                    // A callback that cannot authenticate the exact retained
                    // context is an asynchronous ownership contradiction. Latch
                    // the shared fence while native-start admission is excluded.
                    readQuarantined = true;
                    terminalFence.LatchNoLock();
                    Monitor.PulseAll(gate);
                    return;
                }
                if (readQuarantined || terminalFence.IsLatchedNoLock)
                {
                    // A completion after a start/peer ambiguity is retained
                    // only to make the native callback quiescent. It must not
                    // escape to the controller-input consumer after the shared
                    // whole-composite terminal fact was published.
                    readQuarantined = true;
                    terminalFence.LatchNoLock();
                    context.CallbackStarted = true;
                    context.CallbackFinished = true;
                    Monitor.PulseAll(gate);
                    return;
                }
                context.CallbackStarted = true;
            }
        }

        try
        {
            context.Target.CompleteInputRead(context.Claim,
                completion.BytesTransferred, completion.TimestampQpc,
                completion.Status);
        }
        catch
        {
            // The transport owner owns downstream failure policy. Native
            // callback quiescence must still be published if an unexpected
            // target implementation throws.
        }
        finally
        {
            lock (gate)
            {
                context.CallbackFinished = true;
                Monitor.PulseAll(gate);
            }
        }
    }

    private static int RemainingMilliseconds(long deadline,
        int originalTimeout)
    {
        if (originalTimeout == 0)
        {
            return 0;
        }
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 : (int)Math.Min(remaining, int.MaxValue);
    }

    private bool MatchesSubmissionNoLock(ReadContext context, ulong epoch,
        in Switch2ProUsbReadClaim claim,
        ISwitch2ProUsbWindowsReadOperation operation) =>
        ReferenceEquals(active, context) && context.Epoch == epoch &&
        context.Claim.Equals(claim) &&
        (operation == null || ReferenceEquals(context.Operation, operation));

    private void ReleaseControlOperation()
    {
        lock (gate)
        {
            controlReleaseSealed = false;
            controlOperationInProgress = false;
            Monitor.PulseAll(gate);
        }
    }

    private sealed class ReadContext
    {
        internal void Reset(ulong epoch, in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget target)
        {
            Epoch = epoch;
            Claim = claim;
            Target = target;
            Operation = null;
            CancellationIssued = false;
            CallbackStarted = false;
            CallbackFinished = false;
        }

        internal void Release()
        {
            Epoch = 0;
            Claim = default;
            Target = null;
            Operation = null;
            CancellationIssued = false;
            CallbackStarted = false;
            CallbackFinished = false;
        }

        internal ulong Epoch;
        internal Switch2ProUsbReadClaim Claim;
        internal ISwitch2ProUsbReadCompletionTarget Target;
        internal ISwitch2ProUsbWindowsReadOperation Operation;
        internal bool CancellationIssued;
        internal bool CallbackStarted;
        internal bool CallbackFinished;
    }
}

internal sealed unsafe class Switch2ProUsbWindowsInputHandle :
    ISwitch2ProUsbWindowsInputHandle
{
    private readonly object ownerGate = new();
    private readonly SafeFileHandle handle;
    private readonly ThreadPoolBoundHandle boundHandle;
    private readonly Switch2ProUsbWindowsReadOperation operation;
    private bool disposalRequested;
    private bool disposalInProgress;
    private bool operationDisposed;
    private bool boundHandleDisposed;
    private bool fileHandleDisposed;
    private bool disposed;

    internal Switch2ProUsbWindowsInputHandle(SafeFileHandle handle)
    {
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException("Invalid input handle.", nameof(handle));
        }
        ThreadPoolBoundHandle bound = null;
        try
        {
            bound = ThreadPoolBoundHandle.BindHandle(handle);
            operation = new Switch2ProUsbWindowsReadOperation(handle, bound);
            boundHandle = bound;
        }
        catch
        {
            if (!Switch2ProUsbWindowsExactHandleRelease.
                    TryDisposeBoundHandleQuiesced(bound))
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Partial input IOCP binding cleanup is ambiguous.", bound);
            }
            throw;
        }
    }

    public bool TryBeginRead(byte[] destination, int offset, int count,
        Action<Switch2ProUsbWindowsReadCompletion> callback,
        out ISwitch2ProUsbWindowsReadOperation operation)
    {
        operation = null;
        if (destination == null || callback == null || offset < 0 ||
            count <= 0 || offset > destination.Length - count)
        {
            return false;
        }

        lock (ownerGate)
        {
            if (disposalRequested || disposed)
            {
                return false;
            }

            Switch2ProUsbWindowsReadStartOutcome start = this.operation.
                TryStart(destination, offset, count, callback);
            if (start == Switch2ProUsbWindowsReadStartOutcome.
                    RejectedSubmissionFenced)
            {
                throw new InvalidOperationException(
                    "Rejected input-start storage remains retained.");
            }
            if (start != Switch2ProUsbWindowsReadStartOutcome.Started)
            {
                // TryStart owns cleanup of a newly rejected submission. In
                // particular, RejectedNoSubmission may describe an older
                // completed-but-unretired submission and must not release it.
                return false;
            }
            operation = this.operation;
            return true;
        }
    }

    public void DisposeQuiesced()
    {
        lock (ownerGate)
        {
            if (disposed)
            {
                return;
            }
            if (disposalInProgress)
            {
                throw new InvalidOperationException(
                    "Windows input handle disposal is already in progress.");
            }
            disposalRequested = true;
            disposalInProgress = true;
        }

        Exception failure = null;
        try
        {
            if (!operationDisposed)
            {
                operation.DisposeOwnerQuiesced();
                lock (ownerGate)
                {
                    operationDisposed = true;
                }
            }
            if (!boundHandleDisposed)
            {
                boundHandle.Dispose();
                lock (ownerGate)
                {
                    boundHandleDisposed = true;
                }
            }
            if (!fileHandleDisposed)
            {
                if (!Switch2ProUsbWindowsExactHandleRelease.
                        TryReleaseFileQuiesced(handle))
                {
                    throw new InvalidOperationException(
                        "Windows input file handle was not released.");
                }
                lock (ownerGate)
                {
                    fileHandleDisposed = true;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (ownerGate)
            {
                disposalInProgress = false;
                disposed = operationDisposed && boundHandleDisposed &&
                    fileHandleDisposed;
                Monitor.PulseAll(ownerGate);
            }
        }
        if (failure != null)
        {
            throw failure;
        }
    }
}

internal sealed unsafe class Switch2ProUsbWindowsReadOperation :
    ISwitch2ProUsbWindowsReadOperation
{
    private const int ErrorIoPending = 997;
    private readonly object gate = new();
    private readonly SafeFileHandle handle;
    private readonly ThreadPoolBoundHandle boundHandle;
    private readonly ManualResetEventSlim nativeCompleted = new(true);
    private byte[] destination;
    private GCHandle bufferPin;
    private byte* buffer;
    private int bufferOffset;
    private int bufferCount;
    private PreAllocatedOverlapped preAllocated;
    private Action<Switch2ProUsbWindowsReadCompletion> callback;
    private NativeOverlapped* overlapped;
    private bool submissionActive;
    private bool terminal;
    private bool ownerDisposed;

    // Sticky evidence from this exact handle, not discovery absence or a timeout.
    private int deviceDisconnected;
    internal bool HasObservedDeviceDisconnection =>
        Volatile.Read(ref deviceDisconnected) != 0;

    internal Switch2ProUsbWindowsReadOperation(SafeFileHandle handle,
        ThreadPoolBoundHandle boundHandle)
    {
        this.handle = handle;
        this.boundHandle = boundHandle;
    }

    internal Switch2ProUsbWindowsReadStartOutcome TryStart(byte[] destination,
        int offset, int count,
        Action<Switch2ProUsbWindowsReadCompletion> callback)
    {
        lock (gate)
        {
            if (ownerDisposed || submissionActive || callback == null ||
                !TryBindBufferNoLock(destination, offset, count))
            {
                return Switch2ProUsbWindowsReadStartOutcome.
                    RejectedNoSubmission;
            }
            submissionActive = true;
            terminal = false;
            this.callback = callback;
            nativeCompleted.Reset();
            try
            {
                overlapped = boundHandle.AllocateNativeOverlapped(
                    preAllocated);
                bool read = NativeMethods.ReadFile(handle, buffer,
                    checked((uint)count), null, overlapped);
                if (read)
                {
                    // IOCP completion is still delivered for synchronous
                    // success because this handle never enables
                    // skip-on-success mode.
                    return Switch2ProUsbWindowsReadStartOutcome.Started;
                }

                int error = Marshal.GetLastWin32Error();
                if (Switch2ProUsbWindowsReadStatusMap.IsDefiniteDeviceRemoval((uint)error))
                {
                    Volatile.Write(ref deviceDisconnected, 1);
                }
                if (error == ErrorIoPending)
                {
                    return Switch2ProUsbWindowsReadStartOutcome.Started;
                }
            }
            catch (Exception ex)
            {
                // A thrown native begin has no accepted/not-accepted fact.
                // Retain the exact operation, pin, and OVERLAPPED so a late
                // completion cannot target freed storage. The outer lease
                // converts this throw into whole-composite quarantine.
                throw new InvalidOperationException(
                    "Native input-start outcome is ambiguous.", ex);
            }
            terminal = true;
            if (!TryReleaseNativeNoLock())
            {
                // The native call rejected this submission, but failure to
                // release its exact OVERLAPPED storage is not quiescence. Keep
                // the operation permanently active/fenced and never wake a
                // waiter with a false proof.
                return Switch2ProUsbWindowsReadStartOutcome.
                    RejectedSubmissionFenced;
            }
            TrySignalNativeCompletion();
            submissionActive = false;
            terminal = false;
            this.callback = null;
            return Switch2ProUsbWindowsReadStartOutcome.
                RejectedSubmissionQuiescent;
        }
    }

    public bool TryCancelExact()
    {
        lock (gate)
        {
            if (ownerDisposed || !submissionActive || terminal ||
                overlapped == null)
            {
                return false;
            }
            // The exact OVERLAPPED pointer is held stable under this lock until
            // CancelIoEx returns; a handle-wide cancellation is never issued.
            return NativeMethods.CancelIoEx(handle, overlapped);
        }
    }

    public bool TryWaitForNativeQuiescence(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0 || ownerDisposed)
        {
            return false;
        }
        return nativeCompleted.Wait(timeoutMilliseconds);
    }

    public void ReleaseSubmissionQuiesced()
    {
        lock (gate)
        {
            if (!submissionActive)
            {
                return;
            }
            if (!nativeCompleted.IsSet || overlapped != null || !terminal)
            {
                throw new InvalidOperationException(
                    "Native read operation is not quiescent.");
            }
            submissionActive = false;
            terminal = false;
            callback = null;
        }
    }

    internal void DisposeOwnerQuiesced()
    {
        lock (gate)
        {
            if (ownerDisposed)
            {
                return;
            }
            if (submissionActive || overlapped != null)
            {
                throw new InvalidOperationException(
                    "Native read owner is not quiescent.");
            }
            if (preAllocated != null)
            {
                preAllocated.Dispose();
                preAllocated = null;
            }
            if (bufferPin.IsAllocated)
            {
                bufferPin.Free();
            }
            destination = null;
            buffer = null;
            nativeCompleted.Dispose();
            ownerDisposed = true;
        }
    }

    private static void CompletionCallback(uint errorCode,
        uint numBytes, NativeOverlapped* nativeOverlapped)
    {
        Switch2ProUsbWindowsReadOperation operation = null;
        try
        {
            operation = ThreadPoolBoundHandle.GetNativeOverlappedState(
                nativeOverlapped) as Switch2ProUsbWindowsReadOperation;
            operation?.Finish(errorCode, numBytes, nativeOverlapped);
        }
        catch
        {
            // An exception must never escape an IOCP callback. If state lookup
            // failed, no exact operation can be authenticated and nothing is
            // signalled. If lookup succeeded, attempt a fail-closed terminal
            // transition for this exact pointer.
            operation?.FinishFaulted(nativeOverlapped);
        }
    }

    private void Finish(uint errorCode, uint numBytes,
        NativeOverlapped* completedOverlapped)
    {
        bool exactTransition = false;
        bool nativeStorageReleased = false;
        Action<Switch2ProUsbWindowsReadCompletion> completion = null;
        Switch2ProUsbWindowsReadCompletion result = default;
        try
        {
            lock (gate)
            {
                if (terminal || completedOverlapped != overlapped)
                {
                    return;
                }
                terminal = true;
                exactTransition = true;
                if (Switch2ProUsbWindowsReadStatusMap.IsDefiniteDeviceRemoval(errorCode))
                {
                    Volatile.Write(ref deviceDisconnected, 1);
                }
                bool lengthValid = numBytes <= (uint)bufferCount;
                int boundedBytes = lengthValid ? (int)numBytes : bufferCount;
                Switch2ProUsbNativeReadStatus status = lengthValid ?
                    Switch2ProUsbWindowsReadStatusMap.FromNativeError(
                        errorCode) : Switch2ProUsbNativeReadStatus.Failed;
                result = new Switch2ProUsbWindowsReadCompletion(boundedBytes,
                    Stopwatch.GetTimestamp(), status);
                nativeStorageReleased = TryReleaseNativeNoLock();
                if (!nativeStorageReleased)
                {
                    result = new Switch2ProUsbWindowsReadCompletion(0,
                        Stopwatch.GetTimestamp(),
                        Switch2ProUsbNativeReadStatus.Failed);
                }
                completion = callback;
            }
        }
        catch
        {
            FinishFaulted(completedOverlapped);
            return;
        }
        try
        {
            completion?.Invoke(result);
        }
        catch
        {
            // Consumer failures cannot escape the IOCP callback. Completion
            // has nevertheless returned before any quiescence publication.
        }
        finally
        {
            // A waiter is released only after the managed callback has
            // returned and the exact native OVERLAPPED storage was freed.
            if (Switch2ProUsbWindowsOwnedCompletionPublication.
                    CanPublishQuiescence(exactTransition,
                        nativeStorageReleased))
            {
                TrySignalNativeCompletion();
            }
        }
    }

    private void FinishFaulted(NativeOverlapped* completedOverlapped)
    {
        bool exactTransition = false;
        bool nativeStorageReleased = false;
        Action<Switch2ProUsbWindowsReadCompletion> completion = null;
        try
        {
            lock (gate)
            {
                if (terminal || completedOverlapped != overlapped)
                {
                    return;
                }
                terminal = true;
                exactTransition = true;
                nativeStorageReleased = TryReleaseNativeNoLock();
                completion = callback;
            }
        }
        catch
        {
        }
        try
        {
            completion?.Invoke(new Switch2ProUsbWindowsReadCompletion(0,
                Stopwatch.GetTimestamp(),
                Switch2ProUsbNativeReadStatus.Failed));
        }
        catch
        {
            // Consumer exceptions are contained just as on the normal path.
        }
        finally
        {
            // Even the faulted path publishes only after the managed callback
            // has returned and exact native storage release was proven.
            if (Switch2ProUsbWindowsOwnedCompletionPublication.
                    CanPublishQuiescence(exactTransition,
                        nativeStorageReleased))
            {
                TrySignalNativeCompletion();
            }
        }
    }

    private bool TryReleaseNativeNoLock()
    {
        if (overlapped == null)
        {
            return true;
        }
        try
        {
            boundHandle.FreeNativeOverlapped(overlapped);
            overlapped = null;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TrySignalNativeCompletion()
    {
        try
        {
            nativeCompleted.Set();
        }
        catch
        {
        }
    }

    private bool TryBindBufferNoLock(byte[] candidate, int offset, int count)
    {
        if (candidate == null || offset < 0 || count <= 0 ||
            offset > candidate.Length - count)
        {
            return false;
        }
        if (destination != null)
        {
            return ReferenceEquals(destination, candidate) &&
                bufferOffset == offset && bufferCount == count;
        }

        try
        {
            bufferPin = GCHandle.Alloc(candidate, GCHandleType.Pinned);
            buffer = (byte*)bufferPin.AddrOfPinnedObject() + offset;
            preAllocated = new PreAllocatedOverlapped(CompletionCallback,
                this, null);
            destination = candidate;
            bufferOffset = offset;
            bufferCount = count;
            return true;
        }
        catch
        {
            preAllocated?.Dispose();
            preAllocated = null;
            if (bufferPin.IsAllocated)
            {
                bufferPin.Free();
            }
            buffer = null;
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(SafeFileHandle file,
            byte* buffer, uint bytesToRead, uint* bytesRead,
            NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(SafeFileHandle file,
            NativeOverlapped* overlapped);
    }
}

internal enum Switch2ProUsbWindowsReadStartOutcome
{
    RejectedNoSubmission = 0,
    Started = 1,
    RejectedSubmissionQuiescent = 2,
    RejectedSubmissionFenced = 3,
}

internal sealed class Switch2ProUsbWindowsReadStartAmbiguousException :
    InvalidOperationException
{
    internal Switch2ProUsbWindowsReadStartAmbiguousException(string message) :
        base(message)
    {
    }
}

internal static class Switch2ProUsbWindowsReadStatusMap
{
    // ERROR_NO_SUCH_DEVICE / ERROR_DEVICE_NOT_CONNECTED. ERROR_GEN_FAILURE,
    // cancellation, timeout and an absent discovery entry are not this proof.
    internal static bool IsDefiniteDeviceRemoval(uint errorCode) =>
        errorCode is 433 or 1167;

    internal static Switch2ProUsbNativeReadStatus FromNativeError(
        uint errorCode) => errorCode switch
        {
            0 => Switch2ProUsbNativeReadStatus.Completed,
            995 => Switch2ProUsbNativeReadStatus.Cancelled,
            31 or 433 or 1167 =>
                Switch2ProUsbNativeReadStatus.DeviceRemoved,
            _ => Switch2ProUsbNativeReadStatus.Failed,
        };
}
