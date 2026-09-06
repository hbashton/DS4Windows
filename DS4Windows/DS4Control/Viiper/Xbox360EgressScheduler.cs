/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows
{
    /// <summary>
    /// Value-owned state contract shared by the VIIPER ordered-egress
    /// foundation. Implementations decide which transitions are discrete and
    /// serialize one complete canonical payload without retaining aliases.
    /// </summary>
    internal interface IOrderedEgressState<TState> : IEquatable<TState>
        where TState : struct
    {
        bool HasOrderedTransitionTo(in TState current);
        void BuildInto(Span<byte> destination);
    }

    /// <summary>
    /// Allocation-free-after-construction VIIPER presentation foundation.
    /// Discrete boundaries use a fixed ordered journal while replaceable
    /// values use one latest snapshot. A zero maximum age disables only the
    /// stale-history deadline. Capacity overflow always enters the strict
    /// neutral-and-resynchronize fault contract so a rejected edge cannot
    /// silently change the baseline used to classify its release.
    /// </summary>
    internal class OrderedEgressScheduler<TState>
        where TState : struct, IOrderedEgressState<TState>
    {
        internal const int DefaultOrderedCapacity = 64;

        private readonly object stateLock = new();
        private readonly Entry[] ordered;
        private readonly TState neutralState;
        private int orderedHead;
        private int orderedCount;
        private Entry latest;
        private bool hasLatest;
        private Entry retry;
        private bool hasRetry;
        private Entry last;
        private Entry claimed;
        private ClaimSource claimSource;
        private bool claimRequiresOrderedRecovery;
        private bool claimSurvivedHistoryFault;
        private bool claimAdmitted;
        private long claimSelectedTimestamp;
        private long claimAdmittedTimestamp;
        private bool hasClaim;
        private ulong claimToken;
        private ulong claimGeneration;
        private Entry mandatoryNeutral;
        private bool mandatoryNeutralPending;
        private bool resynchronizationRequired;
        private long minimumResynchronizationTimestamp;
        private TState previous;
        private bool previousKnown;
        private ulong nextOrdinal;
        private ulong nextToken;
        private ulong presentationGeneration = 1;
        private ulong producerEpoch = 1;
        private readonly long maximumOrderedAge;
        private long lastProducerTimestamp;
        private bool hasProducerTimestamp;
        private long acceptedPublications;
        private long rejectedPublications;
        private long continuousReplacements;
        private long continuousPromotions;
        private long retryCount;
        private long overflowFaults;
        private long orderedAgeFaults;
        private long lifecycleResetFaults;
        private long staleProducerRejections;
        private long mandatoryNeutralCommits;
        private long resynchronizationCount;
        private long invalidTimestampCount;
        private long lastOldestAgeAtFault;
        private int orderedHighWater;

        internal OrderedEgressScheduler(TState neutralState,
            long maximumOrderedAge,
            int orderedCapacity = DefaultOrderedCapacity)
        {
            if (maximumOrderedAge < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumOrderedAge),
                    "The monotonic age limit cannot be negative.");
            }
            if (orderedCapacity < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(orderedCapacity),
                    "Atomic continuous promotion plus an edge needs at least two slots.");
            }

            this.maximumOrderedAge = maximumOrderedAge;
            this.neutralState = neutralState;
            ordered = new Entry[orderedCapacity];
            previous = neutralState;
            previousKnown = true;
            last = NewEntry(neutralState, 0,
                isOrdered: false);
        }

        /// <summary>
        /// The limit is supplied by the integration from its declared
        /// monotonic clock and workload policy. The scheduler deliberately has
        /// no universal time-unit or default threshold.
        /// </summary>
        internal OrderedEgressAgePolicy OrderedAgePolicy =>
            maximumOrderedAge > 0 ?
                OrderedEgressAgePolicy.CallerSuppliedMonotonicLimit :
                OrderedEgressAgePolicy.CompatibilityNoAgeLimit;

        internal OrderedEgressProducerEpoch CurrentProducerEpoch
        {
            get
            {
                lock (stateLock)
                {
                    return new OrderedEgressProducerEpoch(producerEpoch);
                }
            }
        }

        internal ulong PresentationGeneration
        {
            get
            {
                lock (stateLock)
                {
                    return presentationGeneration;
                }
            }
        }

        /// <summary>
        /// Captures the producer epoch only when the caller still owns the
        /// exact presentation generation. This is the callback admission
        /// boundary; callers must not replace the returned epoch later.
        /// </summary>
        internal bool TryCaptureProducerEpoch(
            ulong expectedPresentationGeneration,
            out OrderedEgressProducerEpoch epoch)
        {
            lock (stateLock)
            {
                if (expectedPresentationGeneration == 0 ||
                    expectedPresentationGeneration != presentationGeneration)
                {
                    epoch = default;
                    return false;
                }

                epoch = new OrderedEgressProducerEpoch(producerEpoch);
                return true;
            }
        }

        /// <summary>
        /// Publishes one complete canonical state. The state contract decides
        /// whether a transition is discrete; all other values are continuous.
        /// Before an edge, the newest pending continuous snapshot and the edge
        /// are reserved and journaled as one atomic operation.
        /// </summary>
        internal OrderedEgressPublishDisposition Publish(
            OrderedEgressProducerEpoch epoch,
            in TState state, long receivedTimestamp)
        {
            lock (stateLock)
            {
                OrderedEgressPublishDisposition rejection =
                    ValidatePublication(epoch);
                if (rejection != OrderedEgressPublishDisposition.None)
                {
                    rejectedPublications++;
                    return rejection;
                }
                if (!ObserveProducerTimestamp(receivedTimestamp))
                {
                    rejectedPublications++;
                    return OrderedEgressPublishDisposition.
                        RejectedInvalidTimestamp;
                }

                bool isOrdered = previousKnown &&
                    previous.HasOrderedTransitionTo(state);
                int requiredSlots = isOrdered ? 1 + (hasLatest ? 1 : 0) : 0;
                if (isOrdered && ordered.Length - orderedCount < requiredSlots)
                {
                    rejectedPublications++;
                    EnterOverflowFault(receivedTimestamp);
                    return OrderedEgressPublishDisposition.FaultedOverflow;
                }

                Entry entry = NewEntry(state, receivedTimestamp, isOrdered);
                if (isOrdered)
                {
                    // A latest-state claim already in flight is the truthful
                    // predecessor only when there is no newer pending latest
                    // sample to promote. A downstream defer must then recover
                    // that exact claim before this edge.
                    if (!hasLatest && hasClaim &&
                        claimSource == ClaimSource.Latest &&
                        !claimed.IsOrdered)
                    {
                        claimRequiresOrderedRecovery = true;
                    }

                    if (hasLatest)
                    {
                        Entry promoted = latest;
                        promoted.IsOrdered = true;
                        PushOrdered(promoted);
                        latest = default;
                        hasLatest = false;
                        continuousPromotions++;
                    }
                    PushOrdered(entry);
                }
                else
                {
                    if (hasLatest)
                    {
                        continuousReplacements++;
                    }
                    latest = entry;
                    hasLatest = true;
                }

                previous = state;
                previousKnown = true;
                acceptedPublications++;
                return isOrdered ?
                    OrderedEgressPublishDisposition.AcceptedOrdered :
                    OrderedEgressPublishDisposition.AcceptedContinuous;
            }
        }

        /// <summary>
        /// Supplies the one complete fresh producer snapshot required after a
        /// fault neutral commits. It is current state, not an invented edge.
        /// </summary>
        internal OrderedEgressPublishDisposition Resynchronize(
            OrderedEgressProducerEpoch epoch,
            in TState state, long receivedTimestamp)
        {
            lock (stateLock)
            {
                if (!epoch.IsValid || epoch.Value != producerEpoch)
                {
                    staleProducerRejections++;
                    rejectedPublications++;
                    return OrderedEgressPublishDisposition.
                        RejectedStaleProducerEpoch;
                }
                if (mandatoryNeutralPending)
                {
                    rejectedPublications++;
                    return OrderedEgressPublishDisposition.
                        RejectedFaultNeutralPending;
                }
                if (!resynchronizationRequired)
                {
                    rejectedPublications++;
                    return OrderedEgressPublishDisposition.
                        RejectedResynchronizationNotRequired;
                }
                if (receivedTimestamp < minimumResynchronizationTimestamp)
                {
                    invalidTimestampCount++;
                    rejectedPublications++;
                    return OrderedEgressPublishDisposition.
                        RejectedInvalidTimestamp;
                }
                if (!ObserveProducerTimestamp(receivedTimestamp))
                {
                    rejectedPublications++;
                    return OrderedEgressPublishDisposition.
                        RejectedInvalidTimestamp;
                }

                Entry entry = NewEntry(state, receivedTimestamp,
                    isOrdered: false);
                latest = entry;
                hasLatest = true;
                previous = state;
                previousKnown = true;
                resynchronizationRequired = false;
                minimumResynchronizationTimestamp = 0;
                acceptedPublications++;
                resynchronizationCount++;
                return OrderedEgressPublishDisposition.
                    AcceptedResynchronization;
            }
        }

        /// <summary>
        /// Starts a new producer epoch at an explicit reset boundary. An
        /// admitted write may finish before the mandatory neutral, but no
        /// state captured in the retired epoch can publish into the successor.
        /// </summary>
        internal bool BeginLifecycleReset(
            ulong expectedPresentationGeneration, long resetTimestamp,
            out OrderedEgressProducerEpoch successorEpoch)
        {
            lock (stateLock)
            {
                if (expectedPresentationGeneration == 0 ||
                    expectedPresentationGeneration !=
                        presentationGeneration ||
                    resetTimestamp < 0)
                {
                    if (resetTimestamp < 0)
                    {
                        invalidTimestampCount++;
                    }
                    successorEpoch = default;
                    return false;
                }

                EnterHistoryFault(resetTimestamp,
                    OrderedEgressHistoryFaultKind.LifecycleReset, 0);
                successorEpoch = new OrderedEgressProducerEpoch(
                    producerEpoch);
                return true;
            }
        }

        /// <summary>
        /// Claims one immutable value. Selection priority is exact retry,
        /// oldest ordered state, continuous latest, then the last committed
        /// idle state. A fault neutral supersedes all non-active history.
        /// </summary>
        internal bool TryClaim(long selectedTimestamp,
            out OrderedEgressClaim<TState> claim, bool includeIdle = true)
        {
            lock (stateLock)
            {
                if (hasClaim || selectedTimestamp < 0)
                {
                    if (selectedTimestamp < 0)
                    {
                        invalidTimestampCount++;
                    }
                    claim = default;
                    return false;
                }

                EnterPendingPresentationFaultIfRequired(selectedTimestamp);

                OrderedEgressClaimKind kind;
                if (mandatoryNeutralPending)
                {
                    claimed = mandatoryNeutral;
                    claimSource = ClaimSource.MandatoryNeutral;
                    kind = OrderedEgressClaimKind.MandatoryNeutral;
                }
                else if (hasRetry)
                {
                    claimed = retry;
                    retry = default;
                    hasRetry = false;
                    claimSource = ClaimSource.Retry;
                    kind = OrderedEgressClaimKind.Retry;
                }
                else if (orderedCount > 0)
                {
                    claimed = PopOrdered();
                    claimSource = ClaimSource.Ordered;
                    kind = OrderedEgressClaimKind.Ordered;
                }
                else if (hasLatest)
                {
                    claimed = latest;
                    latest = default;
                    hasLatest = false;
                    claimSource = ClaimSource.Latest;
                    kind = OrderedEgressClaimKind.Continuous;
                }
                else if (!includeIdle)
                {
                    claim = default;
                    return false;
                }
                else
                {
                    claimed = last;
                    claimed.IsOrdered = false;
                    claimSource = ClaimSource.Idle;
                    kind = OrderedEgressClaimKind.Idle;
                }

                nextToken = AdvanceNonzero(nextToken);
                claimToken = nextToken;
                claimGeneration = presentationGeneration;
                claimRequiresOrderedRecovery = false;
                claimSurvivedHistoryFault = false;
                claimAdmitted = false;
                claimSelectedTimestamp = selectedTimestamp;
                claimAdmittedTimestamp = 0;
                hasClaim = true;
                claim = new OrderedEgressClaim<TState>(claimed.State, claimToken,
                    claimGeneration, claimed.ProducerEpoch, claimed.Ordinal,
                    claimed.ReceivedTimestamp, claimed.IsOrdered, kind);
                return true;
            }
        }

        /// <summary>
        /// Revalidates one exact claim immediately before a nonblocking writer
        /// accepts its bytes. Retirement, a history fault, a wrong token, a
        /// second admission, or an aged ordered dependency fails closed. A
        /// successful admission is the scheduler's terminal safety boundary;
        /// lifecycle code requiring no writes after retirement returns must
        /// separately cancel and join an already-admitted writer.
        /// </summary>
        internal bool TryAdmit(in OrderedEgressClaim<TState> claim,
            long admittedTimestamp)
        {
            lock (stateLock)
            {
                if (!ClaimMatches(claim) || claimAdmitted)
                {
                    return false;
                }
                if (admittedTimestamp < claimSelectedTimestamp)
                {
                    invalidTimestampCount++;
                    return false;
                }

                if (maximumOrderedAge > 0 &&
                    claimSource != ClaimSource.MandatoryNeutral &&
                    (claimed.IsOrdered || claimRequiresOrderedRecovery) &&
                    OrderedAgeAtLeastLimit(claimed, admittedTimestamp,
                        out long age))
                {
                    EnterHistoryFault(admittedTimestamp,
                        OrderedEgressHistoryFaultKind.OrderedAge, age);
                    return false;
                }

                claimAdmitted = true;
                claimAdmittedTimestamp = admittedTimestamp;
                return true;
            }
        }

        /// <summary>
        /// Resolves exactly one claim. Ordered defers are retried state- and
        /// byte-exactly. Defer is also the legal pre-admission backpressure
        /// path; Commit requires a successful TryAdmit. A claim which was
        /// already admitted when history faulted may commit once, but a defer
        /// can never resurrect it ahead of the mandatory neutral.
        /// </summary>
        internal bool Complete(in OrderedEgressClaim<TState> claim,
            OrderedEgressCompletion completion)
        {
            if (completion != OrderedEgressCompletion.Commit &&
                completion != OrderedEgressCompletion.Defer)
            {
                return false;
            }

            lock (stateLock)
            {
                if (!ClaimMatches(claim) ||
                    completion == OrderedEgressCompletion.Commit &&
                    !claimAdmitted)
                {
                    return false;
                }

                if (completion == OrderedEgressCompletion.Commit)
                {
                    if (!claimSurvivedHistoryFault)
                    {
                        last = claimed;
                        last.IsOrdered = false;
                    }
                    if (claimSource == ClaimSource.MandatoryNeutral)
                    {
                        mandatoryNeutralCommits++;
                        if (claimed.Ordinal == mandatoryNeutral.Ordinal)
                        {
                            mandatoryNeutralPending = false;
                            resynchronizationRequired = true;
                        }
                    }
                }
                else if (claimSource == ClaimSource.MandatoryNeutral)
                {
                    // The fixed neutral entry remains pending unchanged.
                }
                else if (!claimSurvivedHistoryFault &&
                    (claimed.IsOrdered || claimRequiresOrderedRecovery))
                {
                    retry = claimed;
                    retry.IsOrdered = true;
                    hasRetry = true;
                    retryCount++;
                }
                else if (!claimSurvivedHistoryFault &&
                    claimSource != ClaimSource.Idle && !hasRetry &&
                    orderedCount == 0 && !hasLatest)
                {
                    latest = claimed;
                    latest.IsOrdered = false;
                    hasLatest = true;
                }

                ClearClaim();
                return true;
            }
        }

        /// <summary>
        /// Retires presentation ownership without allowing a completion from
        /// the old generation to mutate the successor. The producer epoch is
        /// advanced independently so already-captured producer callbacks are
        /// rejected as stale.
        /// </summary>
        internal bool RetirePresentationGeneration(
            ulong expectedGeneration, long retiredTimestamp)
        {
            if (expectedGeneration == 0)
            {
                return false;
            }

            lock (stateLock)
            {
                if (expectedGeneration != presentationGeneration)
                {
                    return false;
                }

                // A lifecycle boundary may be captured before a producer
                // callback which later wins this lock. Matching retirement is
                // terminal. Diagnose that ordering, but retain the real
                // lifecycle timestamp; carrying a later old-epoch timestamp
                // into the successor would poison its fresh publications.
                long effectiveRetiredTimestamp = retiredTimestamp;
                bool timestampAdjusted = effectiveRetiredTimestamp < 0;
                if (effectiveRetiredTimestamp < 0)
                {
                    effectiveRetiredTimestamp = 0;
                }
                if (hasProducerTimestamp &&
                    effectiveRetiredTimestamp < lastProducerTimestamp)
                {
                    timestampAdjusted = true;
                }
                if (hasClaim &&
                    effectiveRetiredTimestamp < claimSelectedTimestamp)
                {
                    timestampAdjusted = true;
                }
                if (hasClaim && claimAdmitted &&
                    effectiveRetiredTimestamp < claimAdmittedTimestamp)
                {
                    timestampAdjusted = true;
                }
                if (timestampAdjusted)
                {
                    invalidTimestampCount++;
                }

                presentationGeneration = AdvanceNonzero(
                    presentationGeneration);
                AdvanceProducerEpoch();
                ClearNonActiveHistory();
                ClearClaim();
                mandatoryNeutralPending = false;
                resynchronizationRequired = false;
                minimumResynchronizationTimestamp = 0;
                previous = neutralState;
                previousKnown = true;
                last = NewEntry(neutralState,
                    effectiveRetiredTimestamp,
                    isOrdered: false);
                return true;
            }
        }

        internal OrderedEgressSchedulerSnapshot Snapshot()
        {
            lock (stateLock)
            {
                bool hasOrderedDependency = false;
                long oldestOrderedTimestamp = 0;
                if (hasClaim && !claimSurvivedHistoryFault &&
                    claimSource != ClaimSource.MandatoryNeutral &&
                    (claimed.IsOrdered ||
                    claimRequiresOrderedRecovery))
                {
                    hasOrderedDependency = true;
                    oldestOrderedTimestamp = claimed.ReceivedTimestamp;
                }
                else if (hasRetry)
                {
                    hasOrderedDependency = true;
                    oldestOrderedTimestamp = retry.ReceivedTimestamp;
                }
                else if (orderedCount > 0)
                {
                    hasOrderedDependency = true;
                    oldestOrderedTimestamp =
                        ordered[orderedHead].ReceivedTimestamp;
                }

                return new OrderedEgressSchedulerSnapshot(
                    presentationGeneration, producerEpoch, orderedCount,
                    orderedHighWater, hasLatest, hasRetry, hasClaim,
                    claimAdmitted,
                    mandatoryNeutralPending, resynchronizationRequired,
                    acceptedPublications, rejectedPublications,
                    continuousReplacements, continuousPromotions, retryCount,
                    overflowFaults, orderedAgeFaults, lifecycleResetFaults,
                    staleProducerRejections, mandatoryNeutralCommits,
                    resynchronizationCount,
                    invalidTimestampCount, maximumOrderedAge,
                    lastOldestAgeAtFault, hasOrderedDependency,
                    oldestOrderedTimestamp, OrderedAgePolicy);
            }
        }

        private OrderedEgressPublishDisposition ValidatePublication(
            OrderedEgressProducerEpoch epoch)
        {
            if (!epoch.IsValid || epoch.Value != producerEpoch)
            {
                staleProducerRejections++;
                return OrderedEgressPublishDisposition.
                    RejectedStaleProducerEpoch;
            }
            if (mandatoryNeutralPending)
            {
                return OrderedEgressPublishDisposition.
                    RejectedFaultNeutralPending;
            }
            if (resynchronizationRequired)
            {
                return OrderedEgressPublishDisposition.
                    RejectedResynchronizationRequired;
            }
            return OrderedEgressPublishDisposition.None;
        }

        private Entry NewEntry(in TState state,
            long receivedTimestamp, bool isOrdered)
        {
            nextOrdinal = AdvanceNonzero(nextOrdinal);
            return new Entry
            {
                State = state,
                ReceivedTimestamp = receivedTimestamp,
                ProducerEpoch = producerEpoch,
                Ordinal = nextOrdinal,
                IsOrdered = isOrdered,
            };
        }

        private void PushOrdered(in Entry entry)
        {
            int tail = (orderedHead + orderedCount) % ordered.Length;
            ordered[tail] = entry;
            orderedCount++;
            if (orderedCount > orderedHighWater)
            {
                orderedHighWater = orderedCount;
            }
        }

        private Entry PopOrdered()
        {
            Entry entry = ordered[orderedHead];
            ordered[orderedHead] = default;
            orderedHead = (orderedHead + 1) % ordered.Length;
            orderedCount--;
            return entry;
        }

        private void EnterOverflowFault(long faultTimestamp)
        {
            EnterHistoryFault(faultTimestamp,
                OrderedEgressHistoryFaultKind.Overflow, 0);
        }

        private void EnterPendingPresentationFaultIfRequired(
            long nowTimestamp)
        {
            if (maximumOrderedAge <= 0 || mandatoryNeutralPending ||
                resynchronizationRequired)
            {
                return;
            }

            Entry dependency;
            if (hasRetry)
            {
                dependency = retry;
            }
            else if (orderedCount > 0)
            {
                dependency = ordered[orderedHead];
            }
            else if (hasLatest)
            {
                // Continuous state is replaceable and not age-limited, but a
                // future-dated selected sample still makes freshness
                // unprovable. Purge the complete history and present neutral.
                if (nowTimestamp < latest.ReceivedTimestamp)
                {
                    invalidTimestampCount++;
                    EnterHistoryFault(nowTimestamp,
                        OrderedEgressHistoryFaultKind.OrderedAge, 0);
                }
                return;
            }
            else
            {
                return;
            }

            if (OrderedAgeAtLeastLimit(dependency, nowTimestamp,
                out long age))
            {
                EnterHistoryFault(nowTimestamp,
                    OrderedEgressHistoryFaultKind.OrderedAge, age);
            }
        }

        private bool OrderedAgeAtLeastLimit(in Entry entry,
            long nowTimestamp, out long age)
        {
            if (nowTimestamp < entry.ReceivedTimestamp)
            {
                invalidTimestampCount++;
                age = 0;
                return true;
            }

            age = nowTimestamp - entry.ReceivedTimestamp;
            return age >= maximumOrderedAge;
        }

        private void EnterHistoryFault(long faultTimestamp,
            OrderedEgressHistoryFaultKind kind, long oldestAge)
        {
            if (kind == OrderedEgressHistoryFaultKind.Overflow)
            {
                overflowFaults++;
            }
            else if (kind == OrderedEgressHistoryFaultKind.OrderedAge)
            {
                orderedAgeFaults++;
                lastOldestAgeAtFault = oldestAge;
            }
            else
            {
                lifecycleResetFaults++;
            }
            AdvanceProducerEpoch();
            ClearNonActiveHistory();
            previous = default;
            previousKnown = false;
            mandatoryNeutral = NewEntry(neutralState,
                faultTimestamp, isOrdered: true);
            mandatoryNeutralPending = true;
            resynchronizationRequired = false;
            minimumResynchronizationTimestamp = faultTimestamp;
            last = mandatoryNeutral;
            last.IsOrdered = false;
            if (hasClaim)
            {
                if (claimAdmitted)
                {
                    claimSurvivedHistoryFault = true;
                    claimRequiresOrderedRecovery = false;
                }
                else
                {
                    ClearClaim();
                }
            }
        }

        private void ClearNonActiveHistory()
        {
            Array.Clear(ordered, 0, ordered.Length);
            orderedHead = 0;
            orderedCount = 0;
            latest = default;
            hasLatest = false;
            retry = default;
            hasRetry = false;
        }

        private bool ClaimMatches(in OrderedEgressClaim<TState> claim) =>
            hasClaim && claim.IsValid && claim.Token == claimToken &&
            claim.PresentationGeneration == claimGeneration &&
            claim.PresentationGeneration == presentationGeneration &&
            claim.ProducerEpoch == claimed.ProducerEpoch &&
            claim.Ordinal == claimed.Ordinal &&
            claim.State.Equals(claimed.State);

        private void ClearClaim()
        {
            claimed = default;
            claimSource = ClaimSource.None;
            claimRequiresOrderedRecovery = false;
            claimSurvivedHistoryFault = false;
            claimAdmitted = false;
            claimSelectedTimestamp = 0;
            claimAdmittedTimestamp = 0;
            claimToken = 0;
            claimGeneration = 0;
            hasClaim = false;
        }

        private bool ObserveProducerTimestamp(long timestamp)
        {
            if (timestamp < 0 || hasProducerTimestamp &&
                timestamp < lastProducerTimestamp)
            {
                invalidTimestampCount++;
                return false;
            }

            lastProducerTimestamp = timestamp;
            hasProducerTimestamp = true;
            return true;
        }

        private void AdvanceProducerEpoch()
        {
            producerEpoch = AdvanceNonzero(producerEpoch);
            // Producer timestamp monotonicity is epoch-local. A future sample
            // which ended an epoch cannot poison immediate resynchronization
            // or a successor generation.
            lastProducerTimestamp = 0;
            hasProducerTimestamp = false;
        }

        private static ulong AdvanceNonzero(ulong value)
        {
            value++;
            return value == 0 ? 1 : value;
        }

        private enum ClaimSource : byte
        {
            None = 0,
            Retry,
            Ordered,
            Latest,
            Idle,
            MandatoryNeutral,
        }

        private struct Entry
        {
            internal TState State;
            internal long ReceivedTimestamp;
            internal ulong ProducerEpoch;
            internal ulong Ordinal;
            internal bool IsOrdered;
        }
    }

    /// <summary>
    /// Xbox 360 specialization retained as the integration-facing type while
    /// Switch 2 and future VIIPER payloads share the same scheduler contract.
    /// </summary>
    internal sealed class Xbox360EgressScheduler :
        OrderedEgressScheduler<Xbox360EgressState>
    {
        internal Xbox360EgressScheduler(long maximumOrderedAge,
            int orderedCapacity = DefaultOrderedCapacity)
            : base(Xbox360EgressState.Neutral, maximumOrderedAge,
                orderedCapacity)
        {
        }
    }

    internal sealed class Switch2EgressScheduler :
        OrderedEgressScheduler<Switch2EgressState>
    {
        internal Switch2EgressScheduler(long maximumOrderedAge,
            int orderedCapacity = DefaultOrderedCapacity)
            : base(Switch2EgressState.Neutral, maximumOrderedAge,
                orderedCapacity)
        {
        }
    }

    internal enum OrderedEgressAgePolicy : byte
    {
        CompatibilityNoAgeLimit = 1,
        CallerSuppliedMonotonicLimit,
    }

    internal enum OrderedEgressHistoryFaultKind : byte
    {
        Overflow = 1,
        OrderedAge,
        LifecycleReset,
    }

    internal enum OrderedEgressPublishDisposition : byte
    {
        None = 0,
        AcceptedContinuous,
        AcceptedOrdered,
        AcceptedResynchronization,
        RejectedStaleProducerEpoch,
        RejectedFaultNeutralPending,
        RejectedResynchronizationRequired,
        RejectedResynchronizationNotRequired,
        RejectedInvalidTimestamp,
        FaultedOverflow,
    }

    internal enum OrderedEgressCompletion : byte
    {
        Commit = 1,
        Defer = 2,
    }

    internal enum OrderedEgressClaimKind : byte
    {
        Ordered = 1,
        Retry,
        Continuous,
        Idle,
        MandatoryNeutral,
    }

    internal readonly struct OrderedEgressProducerEpoch :
        IEquatable<OrderedEgressProducerEpoch>
    {
        internal OrderedEgressProducerEpoch(ulong value)
        {
            Value = value;
        }

        internal ulong Value { get; }
        internal bool IsValid => Value != 0;

        public bool Equals(OrderedEgressProducerEpoch other) =>
            Value == other.Value;

        public override bool Equals(object obj) =>
            obj is OrderedEgressProducerEpoch other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public static bool operator ==(OrderedEgressProducerEpoch left,
            OrderedEgressProducerEpoch right) => left.Equals(right);

        public static bool operator !=(OrderedEgressProducerEpoch left,
            OrderedEgressProducerEpoch right) => !left.Equals(right);
    }

    /// <summary>
    /// Exact outer lifecycle captured with a scheduler claim. The scheduler's
    /// own presentation generation is necessary but insufficient while
    /// Disconnect defers scheduler retirement until the state writer joins.
    /// </summary>
    internal readonly struct OrderedEgressWriterAdmissionLease
    {
        internal OrderedEgressWriterAdmissionLease(long writerGeneration,
            long presentationGenerationBits, long admissionGeneration)
        {
            WriterGeneration = writerGeneration;
            PresentationGenerationBits = presentationGenerationBits;
            AdmissionGeneration = admissionGeneration;
        }

        internal long WriterGeneration { get; }
        internal long PresentationGenerationBits { get; }
        internal ulong PresentationGeneration => unchecked((ulong)
            PresentationGenerationBits);
        internal long AdmissionGeneration { get; }
        internal bool IsValid => WriterGeneration != 0 &&
            PresentationGenerationBits != 0 && AdmissionGeneration != 0;
    }

    /// <summary>
    /// Serializes outer lifecycle retirement with final scheduler admission.
    /// Socket I/O is deliberately outside this gate: once admission wins,
    /// lifecycle teardown may cancel/join that admitted writer, while a writer
    /// paused before admission can never enter a retired lifecycle.
    /// </summary>
    internal sealed class OrderedEgressWriterAdmissionGate
    {
        private readonly object stateLock = new();
        private long writerGeneration;
        private long presentationGenerationBits;
        private long admissionGeneration;
        private bool active;

        internal void Activate(long writerGeneration,
            long presentationGenerationBits, long admissionGeneration)
        {
            if (writerGeneration == 0 || presentationGenerationBits == 0 ||
                admissionGeneration == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(writerGeneration),
                    "Ordered egress lifecycle generations must be nonzero.");
            }

            lock (stateLock)
            {
                this.writerGeneration = writerGeneration;
                this.presentationGenerationBits =
                    presentationGenerationBits;
                this.admissionGeneration = admissionGeneration;
                active = true;
            }
        }

        internal void Invalidate()
        {
            lock (stateLock)
            {
                active = false;
                writerGeneration = 0;
                presentationGenerationBits = 0;
                admissionGeneration = 0;
            }
        }

        internal bool BeginLifecycleReset<TState>(long writerGeneration,
            long presentationGenerationBits, long admissionGeneration,
            OrderedEgressScheduler<TState> scheduler, long resetTimestamp,
            out OrderedEgressProducerEpoch successorEpoch)
            where TState : struct, IOrderedEgressState<TState>
        {
            ArgumentNullException.ThrowIfNull(scheduler);
            if (writerGeneration == 0 || presentationGenerationBits == 0 ||
                admissionGeneration == 0)
            {
                successorEpoch = default;
                return false;
            }

            lock (stateLock)
            {
                // No writer may claim or finally admit old history between
                // the outer admission-generation change and the scheduler's
                // mandatory-neutral boundary.
                active = false;
                this.writerGeneration = 0;
                this.presentationGenerationBits = 0;
                this.admissionGeneration = 0;
                if (!scheduler.BeginLifecycleReset(
                        unchecked((ulong)presentationGenerationBits),
                        resetTimestamp, out successorEpoch))
                {
                    return false;
                }

                this.writerGeneration = writerGeneration;
                this.presentationGenerationBits =
                    presentationGenerationBits;
                this.admissionGeneration = admissionGeneration;
                active = true;
                return true;
            }
        }

        internal bool TryClaim<TState>(long expectedWriterGeneration,
            OrderedEgressScheduler<TState> scheduler, long selectedTimestamp,
            out OrderedEgressClaim<TState> claim,
            out OrderedEgressWriterAdmissionLease lease,
            bool includeIdle = true)
            where TState : struct, IOrderedEgressState<TState>
        {
            ArgumentNullException.ThrowIfNull(scheduler);
            lock (stateLock)
            {
                if (!active || expectedWriterGeneration == 0 ||
                    expectedWriterGeneration != writerGeneration ||
                    !scheduler.TryClaim(selectedTimestamp, out claim,
                        includeIdle))
                {
                    claim = default;
                    lease = default;
                    return false;
                }

                lease = new OrderedEgressWriterAdmissionLease(
                    writerGeneration, presentationGenerationBits,
                    admissionGeneration);
                if (claim.PresentationGeneration ==
                        lease.PresentationGeneration)
                {
                    return true;
                }

                scheduler.Complete(claim, OrderedEgressCompletion.Defer);
                claim = default;
                lease = default;
                return false;
            }
        }

        internal bool TryAdmit<TState>(
            in OrderedEgressWriterAdmissionLease lease,
            OrderedEgressScheduler<TState> scheduler,
            in OrderedEgressClaim<TState> claim, long admittedTimestamp)
            where TState : struct, IOrderedEgressState<TState>
        {
            ArgumentNullException.ThrowIfNull(scheduler);
            lock (stateLock)
            {
                if (!active || !lease.IsValid ||
                    lease.WriterGeneration != writerGeneration ||
                    lease.PresentationGenerationBits !=
                        presentationGenerationBits ||
                    lease.AdmissionGeneration != admissionGeneration ||
                    claim.PresentationGeneration !=
                        lease.PresentationGeneration)
                {
                    return false;
                }

                return scheduler.TryAdmit(claim, admittedTimestamp);
            }
        }
    }

    internal readonly struct OrderedEgressClaim<TState>
        where TState : struct, IOrderedEgressState<TState>
    {
        internal OrderedEgressClaim(in TState state,
            ulong token, ulong presentationGeneration, ulong producerEpoch,
            ulong ordinal, long receivedTimestamp, bool isOrdered,
            OrderedEgressClaimKind kind)
        {
            State = state;
            Token = token;
            PresentationGeneration = presentationGeneration;
            ProducerEpoch = producerEpoch;
            Ordinal = ordinal;
            ReceivedTimestamp = receivedTimestamp;
            IsOrdered = isOrdered;
            Kind = kind;
        }

        internal TState State { get; }
        internal ulong Token { get; }
        internal ulong PresentationGeneration { get; }
        internal ulong ProducerEpoch { get; }
        internal ulong Ordinal { get; }
        internal long ReceivedTimestamp { get; }
        internal bool IsOrdered { get; }
        internal OrderedEgressClaimKind Kind { get; }
        internal bool IsValid => Token != 0 && PresentationGeneration != 0 &&
            ProducerEpoch != 0 && Ordinal != 0;

        internal void BuildInto(Span<byte> destination) =>
            State.BuildInto(destination);
    }

    internal readonly struct OrderedEgressSchedulerSnapshot
    {
        internal OrderedEgressSchedulerSnapshot(
            ulong presentationGeneration, ulong producerEpoch,
            int orderedDepth, int orderedHighWater, bool continuousPending,
            bool retryPending, bool claimPending, bool claimAdmitted,
            bool mandatoryNeutralPending, bool resynchronizationRequired,
            long acceptedPublications, long rejectedPublications,
            long continuousReplacements, long continuousPromotions,
            long retryCount, long overflowFaults, long orderedAgeFaults,
            long lifecycleResetFaults, long staleProducerRejections,
            long mandatoryNeutralCommits, long resynchronizationCount,
            long invalidTimestampCount, long maximumOrderedAge,
            long lastOldestAgeAtFault, bool hasOrderedDependency,
            long oldestOrderedTimestamp,
            OrderedEgressAgePolicy orderedAgePolicy)
        {
            PresentationGeneration = presentationGeneration;
            ProducerEpoch = producerEpoch;
            OrderedDepth = orderedDepth;
            OrderedHighWater = orderedHighWater;
            ContinuousPending = continuousPending;
            RetryPending = retryPending;
            ClaimPending = claimPending;
            ClaimAdmitted = claimAdmitted;
            MandatoryNeutralPending = mandatoryNeutralPending;
            ResynchronizationRequired = resynchronizationRequired;
            AcceptedPublications = acceptedPublications;
            RejectedPublications = rejectedPublications;
            ContinuousReplacements = continuousReplacements;
            ContinuousPromotions = continuousPromotions;
            RetryCount = retryCount;
            OverflowFaults = overflowFaults;
            OrderedAgeFaults = orderedAgeFaults;
            LifecycleResetFaults = lifecycleResetFaults;
            StaleProducerRejections = staleProducerRejections;
            MandatoryNeutralCommits = mandatoryNeutralCommits;
            ResynchronizationCount = resynchronizationCount;
            InvalidTimestampCount = invalidTimestampCount;
            MaximumOrderedAge = maximumOrderedAge;
            LastOldestAgeAtFault = lastOldestAgeAtFault;
            HasOrderedDependency = hasOrderedDependency;
            OldestOrderedTimestamp = oldestOrderedTimestamp;
            OrderedAgePolicy = orderedAgePolicy;
        }

        internal ulong PresentationGeneration { get; }
        internal ulong ProducerEpoch { get; }
        internal int OrderedDepth { get; }
        internal int OrderedHighWater { get; }
        internal bool ContinuousPending { get; }
        internal bool RetryPending { get; }
        internal bool ClaimPending { get; }
        internal bool ClaimAdmitted { get; }
        internal bool MandatoryNeutralPending { get; }
        internal bool ResynchronizationRequired { get; }
        internal long AcceptedPublications { get; }
        internal long RejectedPublications { get; }
        internal long ContinuousReplacements { get; }
        internal long ContinuousPromotions { get; }
        internal long RetryCount { get; }
        internal long OverflowFaults { get; }
        internal long OrderedAgeFaults { get; }
        internal long LifecycleResetFaults { get; }
        internal long StaleProducerRejections { get; }
        internal long MandatoryNeutralCommits { get; }
        internal long ResynchronizationCount { get; }
        internal long InvalidTimestampCount { get; }
        internal long MaximumOrderedAge { get; }
        internal long LastOldestAgeAtFault { get; }
        internal bool HasOrderedDependency { get; }
        internal long OldestOrderedTimestamp { get; }
        internal OrderedEgressAgePolicy OrderedAgePolicy { get; }
    }
}
