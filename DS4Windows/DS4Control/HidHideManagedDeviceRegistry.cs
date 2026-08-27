using System;
using System.Collections.Generic;
using System.Linq;

namespace DS4Windows
{
    internal readonly record struct HidHideConnectionClaim<TDevice>(
        TDevice Device, long Generation, long LifecycleGeneration,
        IReadOnlyList<string> InstanceIds,
        IReadOnlyList<string> SupersededPersistentReleaseIds)
        where TDevice : class;

    internal readonly record struct HidHideDisconnectPlan(
        IReadOnlyList<string> BoundInstanceIds,
        IReadOnlyList<string> PersistentReleaseIds)
    {
        public static HidHideDisconnectPlan Empty { get; } = new(
            Array.Empty<string>(), Array.Empty<string>());
    }

    internal readonly record struct HidHideServiceReleasePlan(
        IReadOnlyList<string> SessionIds,
        IReadOnlyList<string> PersistentIds);

    /// <summary>
    /// Tracks only HidHide entries inserted by this process.  Device bindings
    /// include pending connections, which lets disconnect/reconnect races
    /// transfer ownership without momentarily treating a user entry as ours.
    /// </summary>
    internal sealed class HidHideManagedDeviceRegistry<TDevice>
        where TDevice : class
    {
        private sealed class Binding
        {
            public long Generation;
            public HashSet<string> InstanceIds;
        }

        private readonly object syncRoot = new object();
        private readonly Dictionary<TDevice, Binding> bindings =
            new Dictionary<TDevice, Binding>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> sessionOwnedIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> persistentOwnedIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long nextGeneration;
        private long lifecycleGeneration;
        private bool acceptingConnections;

        public HidHideManagedDeviceRegistry(
            bool initiallyAcceptingConnections = true)
        {
            acceptingConnections = initiallyAcceptingConnections;
        }

        /// <summary>
        /// Opens a new service generation. Claims from an earlier generation
        /// can never become current again after Stop followed by Start.
        /// </summary>
        public void OpenLifecycle()
        {
            lock (syncRoot)
            {
                lifecycleGeneration++;
                acceptingConnections = true;
            }
        }

        /// <summary>
        /// Stops new HotPlug claims immediately. Existing claims are retained
        /// until the driver-mutation owner takes its atomic release snapshot.
        /// </summary>
        public void CloseLifecycle()
        {
            lock (syncRoot)
            {
                CloseLifecycleNoLock();
            }
        }

        public bool TryBeginConnection(TDevice device,
            IEnumerable<string> instanceIds,
            out HidHideConnectionClaim<TDevice> claim)
        {
            ArgumentNullException.ThrowIfNull(device);
            HashSet<string> ids = Normalize(instanceIds);
            lock (syncRoot)
            {
                if (!acceptingConnections)
                {
                    claim = default;
                    return false;
                }

                claim = BeginConnectionNoLock(device, ids);
                return true;
            }
        }

        public HidHideConnectionClaim<TDevice> BeginConnection(TDevice device,
            IEnumerable<string> instanceIds)
        {
            if (!TryBeginConnection(device, instanceIds, out var claim))
            {
                throw new InvalidOperationException(
                    "The HidHide service lifecycle is not accepting connections.");
            }

            return claim;
        }

        public bool IsCurrent(HidHideConnectionClaim<TDevice> claim)
        {
            lock (syncRoot)
            {
                return bindings.TryGetValue(claim.Device, out Binding binding) &&
                    binding.Generation == claim.Generation &&
                    acceptingConnections &&
                    claim.LifecycleGeneration == lifecycleGeneration;
            }
        }

        public void CancelConnection(HidHideConnectionClaim<TDevice> claim)
        {
            lock (syncRoot)
            {
                if (bindings.TryGetValue(claim.Device, out Binding binding) &&
                    binding.Generation == claim.Generation)
                {
                    bindings.Remove(claim.Device);
                }
            }
        }

        public IReadOnlyList<string> SessionOwnedIds
        {
            get
            {
                lock (syncRoot) return sessionOwnedIds.ToArray();
            }
        }

        public IReadOnlyList<string> PersistentOwnedIds
        {
            get
            {
                lock (syncRoot) return persistentOwnedIds.ToArray();
            }
        }

        public bool HasOwnedIds
        {
            get
            {
                lock (syncRoot)
                {
                    return sessionOwnedIds.Count != 0 ||
                        persistentOwnedIds.Count != 0;
                }
            }
        }

        public bool HasConnections
        {
            get
            {
                lock (syncRoot) return bindings.Count != 0;
            }
        }

        public IReadOnlyList<string> GetUncoveredIds(
            HidHideConnectionClaim<TDevice> claim,
            IEnumerable<string> persistentBlacklist)
        {
            HashSet<string> persistent = Normalize(persistentBlacklist);
            lock (syncRoot)
            {
                if (!bindings.TryGetValue(claim.Device, out Binding binding) ||
                    binding.Generation != claim.Generation ||
                    !acceptingConnections ||
                    claim.LifecycleGeneration != lifecycleGeneration)
                {
                    return Array.Empty<string>();
                }

                return binding.InstanceIds.Where(id =>
                    !persistent.Contains(id) && !sessionOwnedIds.Contains(id))
                    .ToArray();
            }
        }

        /// <summary>
        /// Records entries that the caller proved it added.  If the connection
        /// vanished during the IOCTL, persistent IDs with no replacement
        /// binding are returned for immediate rollback.
        /// </summary>
        public IReadOnlyList<string> CompleteConnection(
            HidHideConnectionClaim<TDevice> claim,
            IEnumerable<string> addedSessionIds,
            IEnumerable<string> addedPersistentIds)
        {
            HashSet<string> sessions = Normalize(addedSessionIds);
            HashSet<string> persistents = Normalize(addedPersistentIds);
            lock (syncRoot)
            {
                sessionOwnedIds.UnionWith(sessions);
                persistentOwnedIds.UnionWith(persistents);

                bool current = bindings.TryGetValue(claim.Device,
                    out Binding binding) &&
                    binding.Generation == claim.Generation &&
                    acceptingConnections &&
                    claim.LifecycleGeneration == lifecycleGeneration;
                if (current)
                {
                    return Array.Empty<string>();
                }

                return persistents.Where(id => !IsReferencedNoLock(id))
                    .ToArray();
            }
        }

        public HidHideDisconnectPlan Disconnect(TDevice device)
        {
            if (device == null) return HidHideDisconnectPlan.Empty;
            lock (syncRoot)
            {
                if (!bindings.Remove(device, out Binding binding))
                {
                    return HidHideDisconnectPlan.Empty;
                }

                string[] releases = binding.InstanceIds.Where(id =>
                    persistentOwnedIds.Contains(id) &&
                    !IsReferencedNoLock(id)).ToArray();
                return new HidHideDisconnectPlan(binding.InstanceIds.ToArray(),
                    releases);
            }
        }

        public IReadOnlyList<string> RevalidatePersistentRelease(
            IEnumerable<string> candidates)
        {
            HashSet<string> ids = Normalize(candidates);
            lock (syncRoot)
            {
                return ids.Where(id => persistentOwnedIds.Contains(id) &&
                    !IsReferencedNoLock(id)).ToArray();
            }
        }

        /// <summary>
        /// Finalizes a successful driver removal.  IDs referenced by a new
        /// generation are returned so the caller can reassert them before
        /// releasing its driver-mutation lock.
        /// </summary>
        public IReadOnlyList<string> CompletePersistentRelease(
            IEnumerable<string> removedIds)
        {
            HashSet<string> ids = Normalize(removedIds);
            lock (syncRoot)
            {
                List<string> reassert = new List<string>();
                foreach (string id in ids)
                {
                    if (!persistentOwnedIds.Contains(id)) continue;
                    if (IsReferencedNoLock(id))
                    {
                        reassert.Add(id);
                    }
                    else
                    {
                        persistentOwnedIds.Remove(id);
                    }
                }
                return reassert;
            }
        }

        public void CompletePersistentReassert(IEnumerable<string> instanceIds,
            bool success)
        {
            if (success) return;
            HashSet<string> ids = Normalize(instanceIds);
            lock (syncRoot)
            {
                persistentOwnedIds.ExceptWith(ids);
            }
        }

        public void CompleteSessionRelease(IEnumerable<string> instanceIds,
            bool success)
        {
            if (!success) return;
            HashSet<string> ids = Normalize(instanceIds);
            lock (syncRoot)
            {
                sessionOwnedIds.ExceptWith(ids);
            }
        }

        public void ForgetPersistentOwnership(IEnumerable<string> instanceIds)
        {
            HashSet<string> ids = Normalize(instanceIds);
            lock (syncRoot)
            {
                persistentOwnedIds.ExceptWith(ids);
            }
        }

        /// <summary>
        /// Closes admission and captures the complete process-owned set in one
        /// registry critical section. The caller must hold its driver mutation
        /// lock from before this snapshot through the process-wide session
        /// clear, so no accepted generation can be omitted from the IOCTL.
        /// </summary>
        public HidHideServiceReleasePlan BeginServiceRelease()
        {
            lock (syncRoot)
            {
                CloseLifecycleNoLock();
                HidHideServiceReleasePlan plan = new(
                    sessionOwnedIds.ToArray(), persistentOwnedIds.ToArray());
                bindings.Clear();
                return plan;
            }
        }

        private HidHideConnectionClaim<TDevice> BeginConnectionNoLock(
            TDevice device, HashSet<string> ids)
        {
            bindings.TryGetValue(device, out Binding previousBinding);
            long generation = ++nextGeneration;
            bindings[device] = new Binding
            {
                Generation = generation,
                InstanceIds = ids,
            };

            string[] superseded = previousBinding == null ?
                Array.Empty<string>() :
                previousBinding.InstanceIds.Where(id =>
                    !ids.Contains(id) &&
                    persistentOwnedIds.Contains(id) &&
                    !IsReferencedNoLock(id)).ToArray();
            return new HidHideConnectionClaim<TDevice>(device, generation,
                lifecycleGeneration, ids.ToArray(), superseded);
        }

        private void CloseLifecycleNoLock()
        {
            if (!acceptingConnections) return;
            acceptingConnections = false;
            lifecycleGeneration++;
        }

        private bool IsReferencedNoLock(string instanceId)
        {
            return bindings.Values.Any(binding =>
                binding.InstanceIds.Contains(instanceId));
        }

        private static HashSet<string> Normalize(IEnumerable<string> values)
        {
            return new HashSet<string>((values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
