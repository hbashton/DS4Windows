using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DS4Windows.InputDevices;

namespace DS4Windows;

internal readonly record struct LegacyJoyConProjectionInput(
    DS4State Left, DS4State Right, ulong LeftGeneration, ulong RightGeneration,
    long LeftTimestampQpc, long RightTimestampQpc, int ProfileSlot,
    ulong PairEpoch, long CompletionQpc, long QpcFrequency);

internal interface ILegacyJoyConProfileProjection
{
    bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination);
}

/// <summary>An exact physical connection, never a reusable numeric slot credential.</summary>
internal sealed class LegacyJoyConConnection
{
    internal LegacyJoyConConnection(DS4Device device, int slot, ulong generation)
    {
        Device = device; Slot = slot; Generation = generation;
        IsLeft = device.DeviceType == InputDeviceType.JoyConL;
    }

    internal DS4Device Device { get; }
    internal int Slot { get; }
    internal ulong Generation { get; }
    internal bool IsLeft { get; }
    internal bool Connected = true;
    internal bool Paused;
    // Explicit logical disconnect claims both exact halves before queuing
    // physical shutdown. A claimed survivor must not be linked to a new pad.
    internal bool DisconnectRequested;
    internal readonly DS4StateOwnedSnapshot Raw = new();
    internal long TimestampQpc;
    internal long FirstReportTimestampQpc;
    internal ReportDiagnosticsWorker.Source DiagnosticsSource;
    internal LegacyJoyConGroup Group;
}

internal sealed class LegacyJoyConGroup
{
    internal readonly object Gate = new();
    internal readonly LegacyJoyConConnection Owner;
    internal readonly LegacyJoyConConnection Left;
    internal readonly LegacyJoyConConnection Right;
    internal readonly ulong Epoch;
    internal readonly ILegacyJoyConProfileProjection Projection;
    // Fresh, never-published standalone projections are reserved on the cold
    // link path. Disconnect/unlink can then publish survivors without invoking
    // a potentially failing factory or allocating on a removal callback.
    internal readonly LegacyJoyConGroup LeftStandalone;
    internal readonly LegacyJoyConGroup RightStandalone;
    internal readonly DS4State State = new();
    internal readonly DS4StateOwnedSnapshot Previous = new();
    internal bool Active = true;
    internal long LastActivityTimestampQpc;

    internal LegacyJoyConGroup(LegacyJoyConConnection owner,
        LegacyJoyConConnection other, ulong epoch, ILegacyJoyConProfileProjection projection,
        LegacyJoyConGroup leftStandalone = null, LegacyJoyConGroup rightStandalone = null)
    {
        Owner = owner; Epoch = epoch; Projection = projection;
        Left = owner.IsLeft ? owner : other;
        Right = owner.IsLeft ? other : owner;
        LeftStandalone = leftStandalone;
        RightStandalone = rightStandalone;
        if (projection == null || (Joined && (leftStandalone == null || rightStandalone == null)))
            throw new ArgumentException("A logical Joy-Con group requires a projection and paired groups require fresh standalone reserves.");
    }

    internal bool Joined => Left != null && Right != null;
    internal LegacyJoyConConnection Other => ReferenceEquals(Owner, Left) ? Right : Left;
}

/// <summary>
/// Cold-path topology changes and one serialized publication owner per original
/// Joy-Con logical pad. Physical readers keep running during link/unlink. No
/// global mapping lock or transport restart is needed to change a pair.
/// </summary>
internal sealed class LegacyJoyConLinkCoordinator
{
    private readonly object topologyGate = new();
    private readonly Dictionary<DS4Device, LegacyJoyConConnection> connections = new(ReferenceEqualityComparer.Instance);
    private readonly Func<ILegacyJoyConProfileProjection> createProjection;
    private readonly Action<LegacyJoyConConnection, DS4State, DS4State> publish;
    private ulong nextGeneration;
    private ulong nextPairEpoch;

    internal LegacyJoyConLinkCoordinator(Func<ILegacyJoyConProfileProjection> createProjection,
        Action<LegacyJoyConConnection, DS4State, DS4State> publish)
    {
        this.createProjection = createProjection ?? throw new ArgumentNullException(nameof(createProjection));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    internal LegacyJoyConConnection Register(DS4Device device, int slot)
    {
        if (device == null || device.DeviceType is not (InputDeviceType.JoyConL or InputDeviceType.JoyConR) || slot < 0)
            throw new ArgumentException("Only an original Joy-Con connection can be registered.");
        lock (topologyGate)
        {
            if (connections.TryGetValue(device, out var existing))
            {
                if (existing.Slot != slot) throw new InvalidOperationException("This Joy-Con connection already belongs to another input slot.");
                return existing;
            }
            if (connections.Values.Any(item => item.Connected && item.Slot == slot))
                throw new InvalidOperationException("The Joy-Con input slot is still owned by another connection.");
            var entry = new LegacyJoyConConnection(device, slot, checked(++nextGeneration));
            entry.Group = NewGroup(entry, null);
            connections.Add(device, entry);
            return entry;
        }
    }

    internal LegacyJoyConConnection[] GetStandaloneConnections()
    {
        lock (topologyGate)
            return connections.Values.Where(item => item.Connected && !item.Paused && !item.DisconnectRequested && !item.Group.Joined)
                .OrderBy(item => item.Generation).ToArray();
    }

    internal LegacyJoyConGroup GetJoinedGroup(DS4Device device)
    {
        lock (topologyGate)
            return connections.TryGetValue(device, out var entry) && entry.Connected &&
                entry.Group.Active && entry.Group.Joined ? entry.Group : null;
    }

    internal bool IsCurrent(LegacyJoyConConnection entry)
    {
        lock (topologyGate) return IsCurrentNoLock(entry);
    }

    private bool IsCurrentNoLock(LegacyJoyConConnection entry) => entry != null && entry.Connected &&
        connections.TryGetValue(entry.Device, out var current) && ReferenceEquals(current, entry);

    internal bool TryPublish(LegacyJoyConConnection entry, DS4State physicalState,
        long completionQpc, long qpcFrequency)
    {
        if (entry == null || physicalState == null || completionQpc <= 0 || qpcFrequency <= 0) return false;
        // The cold path replaces groups only while holding their gates. Retry
        // against the new owner rather than publishing through a retired pair.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var group = Volatile.Read(ref entry.Group);
            if (group == null) return false;
            lock (group.Gate)
            {
                if (!ReferenceEquals(group, Volatile.Read(ref entry.Group)) || !group.Active) continue;
                if (!entry.Connected) return false;
                entry.Raw.Capture(physicalState);
                entry.TimestampQpc = completionQpc;
                if (entry.FirstReportTimestampQpc == 0) entry.FirstReportTimestampQpc = completionQpc;
                if (group.LastActivityTimestampQpc == 0 || !LegacyJoyConIdlePolicy.IsIdle(physicalState))
                    LegacyJoyConIdlePolicy.RecordActivity(group, completionQpc);
                if (entry.Paused || group.Owner.Paused || !group.Owner.Connected) return false;
                var input = new LegacyJoyConProjectionInput(
                    group.Left?.Raw.State, group.Right?.Raw.State,
                    group.Left?.Generation ?? 0, group.Right?.Generation ?? 0,
                    group.Left?.TimestampQpc ?? 0, group.Right?.TimestampQpc ?? 0,
                    group.Owner.Slot, group.Epoch, completionQpc, qpcFrequency);
                if (!group.Projection.TryApply(input, group.State)) return false;
                // The callback must not acquire topologyGate or call topology
                // APIs: cold operations hold it while draining these gates.
                publish(group.Owner, group.State, group.Previous.State);
                group.Previous.Capture(group.State);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Only the secondary pad is retired. The caller revalidates exact live
    /// slots and restores that pad on failure; no primary pad is reconstructed.
    /// </summary>
    internal bool TryLink(LegacyJoyConConnection preferred, LegacyJoyConConnection other,
        Action<LegacyJoyConConnection, LegacyJoyConConnection> prepare,
        out LegacyJoyConGroup joined)
    {
        joined = null;
        if (prepare == null) throw new ArgumentNullException(nameof(prepare));
        lock (topologyGate)
        {
            if (!IsCurrentNoLock(preferred) || !IsCurrentNoLock(other) ||
                preferred.IsLeft == other.IsLeft || preferred.Group.Joined || other.Group.Joined ||
                preferred.Paused || other.Paused || preferred.DisconnectRequested || other.DisconnectRequested) return false;
            // Factory/allocation failures happen before either output pad is
            // retired or either live report stream is paused.
            var candidate = NewGroup(preferred, other);
            var preferredGroup = preferred.Group;
            var otherGroup = other.Group;
            var first = preferred.Generation < other.Generation ? preferredGroup : otherGroup;
            var second = ReferenceEquals(first, preferredGroup) ? otherGroup : preferredGroup;
            lock (first.Gate) lock (second.Gate)
            {
                if (preferred.DisconnectRequested || other.DisconnectRequested) return false;
                preferred.Paused = other.Paused = true;
            }
            try
            {
                // No report/motion callback remains in flight at this boundary.
                // Do not hold report gates through USB/IP output retirement.
                prepare(preferred, other);
                lock (first.Gate) lock (second.Gate)
                {
                    if (!IsCurrentNoLock(preferred) || !IsCurrentNoLock(other) ||
                        !first.Active || !second.Active ||
                        !ReferenceEquals(preferred.Group, preferredGroup) || !ReferenceEquals(other.Group, otherGroup)) return false;
                    joined = candidate;
                    first.Active = second.Active = false;
                    Volatile.Write(ref preferred.Group, joined);
                    Volatile.Write(ref other.Group, joined);
                    preferred.Device.PrimaryDevice = true;
                    other.Device.PrimaryDevice = false;
                }
                return true;
            }
            finally
            {
                lock (preferred.Group.Gate) preferred.Paused = false;
                lock (other.Group.Gate) other.Paused = false;
            }
        }
    }

    internal bool TryUnlink(LegacyJoyConGroup expected,
        Action<LegacyJoyConConnection> restoreSecondary)
    {
        if (expected == null || !expected.Joined) return false;
        if (restoreSecondary == null) throw new ArgumentNullException(nameof(restoreSecondary));
        lock (topologyGate)
        {
            if (!IsCurrentNoLock(expected.Left) || !IsCurrentNoLock(expected.Right) ||
                !ReferenceEquals(expected.Left.Group, expected) || !ReferenceEquals(expected.Right.Group, expected)) return false;
            lock (expected.Gate)
            {
                if (expected.Left.DisconnectRequested || expected.Right.DisconnectRequested) return false;
                expected.Left.Paused = expected.Right.Paused = true;
            }
            try
            {
                restoreSecondary(expected.Other);
                lock (expected.Gate)
                {
                    // A synchronous lifecycle callback may have removed a
                    // member (or replaced its numeric slot) while restoring.
                    if (!expected.Active || !IsCurrentNoLock(expected.Left) || !IsCurrentNoLock(expected.Right) ||
                        !ReferenceEquals(expected.Left.Group, expected) || !ReferenceEquals(expected.Right.Group, expected)) return false;
                    expected.Active = false;
                    Volatile.Write(ref expected.Left.Group, expected.LeftStandalone);
                    Volatile.Write(ref expected.Right.Group, expected.RightStandalone);
                    expected.Left.Device.PrimaryDevice = expected.Right.Device.PrimaryDevice = true;
                }
                return true;
            }
            finally
            {
                lock (expected.Left.Group.Gate) expected.Left.Paused = false;
                lock (expected.Right.Group.Gate) expected.Right.Paused = false;
            }
        }
    }

    internal LegacyJoyConConnection Remove(DS4Device device,
        Action<LegacyJoyConConnection> releaseRetainedOwner = null)
    {
        lock (topologyGate)
        {
            if (!connections.Remove(device, out var entry)) return null;
            var old = entry.Group;
            LegacyJoyConConnection survivor;
            lock (old.Gate)
            {
                entry.Connected = false;
                old.Active = false;
                survivor = old.Joined ? (ReferenceEquals(entry, old.Left) ? old.Right : old.Left) : null;
                if (survivor == null || !survivor.Connected) return null;
                survivor.Paused = true;
                survivor.Device.PrimaryDevice = true;
                Volatile.Write(ref survivor.Group, survivor.IsLeft ? old.LeftStandalone : old.RightStandalone);
            }
            // A surviving owner keeps its existing virtual pad. Release that
            // old pair's held state now, even if no subsequent HID report
            // arrives. Topology remains serialized but no report gate is held
            // through mapper/output work or continuous-mouse draining.
            try
            {
                if (ReferenceEquals(survivor, old.Owner)) releaseRetainedOwner?.Invoke(survivor);
            }
            finally { lock (survivor.Group.Gate) survivor.Paused = false; }
            return survivor;
        }
    }

    internal void Clear()
    {
        lock (topologyGate)
        {
            foreach (var entry in connections.Values)
                lock (entry.Group.Gate) { entry.Connected = false; entry.Group.Active = false; }
            connections.Clear();
        }
    }

    private LegacyJoyConGroup NewGroup(LegacyJoyConConnection owner, LegacyJoyConConnection other)
    {
        if (other == null) return new(owner, null, 0, createProjection());
        LegacyJoyConConnection left = owner.IsLeft ? owner : other;
        LegacyJoyConConnection right = owner.IsLeft ? other : owner;
        var leftStandalone = NewGroup(left, null);
        var rightStandalone = NewGroup(right, null);
        return new(owner, other, checked(++nextPairEpoch), createProjection(), leftStandalone, rightStandalone);
    }
}
