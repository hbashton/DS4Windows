/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;

namespace DS4Windows
{
    /// <summary>
    /// Separates ordered control transitions from replaceable continuous
    /// state. All storage is allocated once with the output device. The single
    /// transport writer claims work under this object's short lock and performs
    /// framing and network I/O only after the lock has been released.
    /// </summary>
    internal sealed class ViiperInputScheduler
    {
        internal const int DefaultTransitionCapacity = 64;

        private readonly object stateLock = new();
        private readonly ViiperInputEnvelope[] transitions;
        private int transitionHead;
        private int transitionCount;
        private bool continuousPending;
        private ViiperInputEnvelope continuous;
        private bool retryPending;
        private ViiperInputEnvelope retry;
        private bool claimPending;
        private ViiperInputEnvelope claimed;
        private bool claimedTransition;
        private bool claimedPeakBackupRequired;
        private bool previousReceivedKnown;
        private ViiperMappedInputState previousReceived;
        private bool lastClaimedKnown;
        private ViiperMappedInputState lastClaimed;
        private bool lastTransportedKnown;
        private ViiperInputEnvelope lastTransported;
        private TriggerEpoch l2Epoch;
        private TriggerEpoch r2Epoch;
        private long nextPublicationId;
        private long nextEpochId;
        private long generation;
        private long receivedCount;
        private long transitionCountTotal;
        private long continuousReplacementCount;
        private long peakUpgradeCount;
        private long overflowCount;
        private int transitionHighWater;
        private long maximumQueueAgeTicks;

        internal ViiperInputScheduler(
            int transitionCapacity = DefaultTransitionCapacity)
        {
            if (transitionCapacity < 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transitionCapacity));
            }

            transitions = new ViiperInputEnvelope[transitionCapacity];
        }

        internal long Generation
        {
            get
            {
                lock (stateLock)
                {
                    return generation;
                }
            }
        }

        internal void Reset(long newGeneration)
        {
            lock (stateLock)
            {
                Array.Clear(transitions, 0, transitions.Length);
                transitionHead = 0;
                transitionCount = 0;
                continuousPending = false;
                retryPending = false;
                claimPending = false;
                claimedPeakBackupRequired = false;
                previousReceivedKnown = false;
                lastClaimedKnown = false;
                lastTransportedKnown = false;
                l2Epoch = default;
                r2Epoch = default;
                nextPublicationId = 0;
                nextEpochId = 0;
                generation = newGeneration;
                receivedCount = 0;
                transitionCountTotal = 0;
                continuousReplacementCount = 0;
                peakUpgradeCount = 0;
                overflowCount = 0;
                transitionHighWater = 0;
                maximumQueueAgeTicks = 0;
            }
        }

        /// <summary>
        /// Publishes one complete mapped state without allocating. The caller
        /// never waits for transport ownership or network I/O.
        /// </summary>
        internal ViiperInputPublication Publish(
            in ViiperMappedInputState state, long queuedTimestamp = 0)
        {
            long timestamp = queuedTimestamp > 0 ? queuedTimestamp :
                Stopwatch.GetTimestamp();
            lock (stateLock)
            {
                receivedCount++;
                ViiperMappedInputState prior = previousReceived;
                bool baselineKnown = previousReceivedKnown;
                bool l2Began = state.L2 != 0 &&
                    (!baselineKnown || prior.L2 == 0);
                bool r2Began = state.R2 != 0 &&
                    (!baselineKnown || prior.R2 == 0);
                bool l2Released = baselineKnown && prior.L2 != 0 && state.L2 == 0;
                bool r2Released = baselineKnown && prior.R2 != 0 && state.R2 == 0;

                if (l2Began)
                {
                    BeginEpoch(ref l2Epoch, state.L2, state, timestamp,
                        receivedCount);
                }
                else if (state.L2 != 0 && l2Epoch.Active &&
                    state.L2 > l2Epoch.PeakValue)
                {
                    RecordPeak(ref l2Epoch, state.L2, state, timestamp,
                        receivedCount);
                }
                else if (state.L2 != 0 && l2Epoch.Active &&
                    state.L2 == l2Epoch.PeakValue)
                {
                    RefreshEqualPeakTriggerStatus(ref l2Epoch, state,
                        left: true, timestamp: timestamp,
                        receiveId: receivedCount);
                }

                if (r2Began)
                {
                    BeginEpoch(ref r2Epoch, state.R2, state, timestamp,
                        receivedCount);
                }
                else if (state.R2 != 0 && r2Epoch.Active &&
                    state.R2 > r2Epoch.PeakValue)
                {
                    RecordPeak(ref r2Epoch, state.R2, state, timestamp,
                        receivedCount);
                }
                else if (state.R2 != 0 && r2Epoch.Active &&
                    state.R2 == r2Epoch.PeakValue)
                {
                    RefreshEqualPeakTriggerStatus(ref r2Epoch, state,
                        left: false, timestamp: timestamp,
                        receiveId: receivedCount);
                }

                bool transition = IsTransition(baselineKnown, prior, state);
                ViiperInputEnvelope envelope = new()
                {
                    State = state,
                    Generation = generation,
                    QueuedTimestamp = timestamp,
                    L2EpochId = state.L2 != 0 && l2Epoch.Active ?
                        l2Epoch.Id : 0,
                    R2EpochId = state.R2 != 0 && r2Epoch.Active ?
                        r2Epoch.Id : 0,
                };

                bool accepted;
                if (transition)
                {
                    // This ordered boundary makes every older replaceable
                    // sample stale. Preserve only still-unrepresented trigger
                    // peaks, and only now: ordinary analog rise/fall remains
                    // one latest continuous state. When both triggers need a
                    // historical snapshot, use receive order rather than QPC
                    // timestamp because deterministic/rapid samples may share
                    // a timestamp.
                    if (l2Epoch.Active && r2Epoch.Active &&
                        l2Epoch.PeakReceiveId > r2Epoch.PeakReceiveId)
                    {
                        PreservePeakBeforeTransition(ref r2Epoch,
                            left: false, upcoming: envelope);
                        PreservePeakBeforeTransition(ref l2Epoch,
                            left: true, upcoming: envelope);
                    }
                    else
                    {
                        if (l2Epoch.Active)
                        {
                            PreservePeakBeforeTransition(ref l2Epoch,
                                left: true, upcoming: envelope);
                        }
                        if (r2Epoch.Active)
                        {
                            PreservePeakBeforeTransition(ref r2Epoch,
                                left: false, upcoming: envelope);
                        }
                    }

                    long publicationId = ++nextPublicationId;
                    envelope.PublicationId = publicationId;
                    // A continuous sample observed before this control edge is
                    // now stale and must never be replayed after the edge.
                    if (continuousPending)
                    {
                        continuousPending = false;
                        continuousReplacementCount++;
                    }
                    bool ordered = EnqueueTransition(envelope);
                    if (ordered)
                    {
                        transitionCountTotal++;
                    }
                    else
                    {
                        // Overflow is exceptional, but it must not strand the
                        // virtual pad in an old pressed state after the
                        // ordered ring drains. Retain only the newest rejected
                        // complete state as a recovery snapshot.
                        if (continuousPending)
                        {
                            continuousReplacementCount++;
                        }
                        continuous = envelope;
                        continuousPending = true;
                    }
                    accepted = true;

                    if (l2Began)
                    {
                        l2Epoch.InitialPublicationId = publicationId;
                    }
                    if (r2Began)
                    {
                        r2Epoch.InitialPublicationId = publicationId;
                    }
                }
                else
                {
                    // A falling in-epoch sample proves that an earlier peak
                    // will no longer be the latest continuous snapshot. It
                    // may strengthen an existing unclaimed initial edge, as
                    // required by the trigger-epoch contract, but must never
                    // create a new ordered item. Thus arbitrary analog/motion
                    // rise and fall still occupies exactly one replaceable
                    // continuous slot.
                    StrengthenPendingInitialForFallenPeaks(state);

                    long publicationId = ++nextPublicationId;
                    envelope.PublicationId = publicationId;
                    if (continuousPending)
                    {
                        continuousReplacementCount++;
                    }
                    continuous = envelope;
                    continuousPending = true;
                    accepted = true;

                    if (l2Began)
                    {
                        l2Epoch.InitialPublicationId = publicationId;
                    }
                    if (r2Began)
                    {
                        r2Epoch.InitialPublicationId = publicationId;
                    }
                }

                previousReceived = state;
                previousReceivedKnown = true;
                if (l2Released)
                {
                    l2Epoch = default;
                }
                if (r2Released)
                {
                    r2Epoch = default;
                }

                return new ViiperInputPublication(accepted, transition,
                    envelope.PublicationId, transitionCount,
                    continuousPending);
            }
        }

        internal bool HasPendingInput
        {
            get
            {
                lock (stateLock)
                {
                    return !claimPending &&
                        (retryPending || transitionCount > 0 ||
                            continuousPending);
                }
            }
        }

        internal bool HasPendingTransition
        {
            get
            {
                lock (stateLock)
                {
                    return !claimPending &&
                        (retryPending || transitionCount > 0);
                }
            }
        }

        internal bool TryClaim(out ViiperInputClaim claim)
        {
            lock (stateLock)
            {
                if (claimPending)
                {
                    claim = default;
                    return false;
                }

                ViiperInputEnvelope envelope;
                bool transition;
                if (retryPending)
                {
                    envelope = retry;
                    transition = true;
                    retryPending = false;
                }
                else if (transitionCount > 0)
                {
                    envelope = transitions[transitionHead];
                    transitions[transitionHead] = default;
                    transitionHead = (transitionHead + 1) %
                        transitions.Length;
                    transitionCount--;
                    transition = true;
                }
                else if (continuousPending)
                {
                    envelope = continuous;
                    continuousPending = false;
                    transition = false;
                }
                else
                {
                    claim = default;
                    return false;
                }

                claimPending = true;
                claimed = envelope;
                claimedTransition = transition;
                claimedPeakBackupRequired = false;
                lastClaimed = envelope.State;
                lastClaimedKnown = true;
                claim = new ViiperInputClaim(envelope, transition);
                return true;
            }
        }

        internal void CompleteSuccess(in ViiperInputClaim claim,
            long transportedTimestamp = 0)
        {
            lock (stateLock)
            {
                if (!ClaimMatches(claim))
                {
                    return;
                }

                ViiperInputEnvelope transported = claimed;
                transported.TransportedTimestamp = transportedTimestamp > 0 ?
                    transportedTimestamp : Stopwatch.GetTimestamp();
                if (transported.QueuedTimestamp > 0)
                {
                    RecordMaximum(ref maximumQueueAgeTicks,
                        transported.TransportedTimestamp -
                            transported.QueuedTimestamp);
                }
                lastTransported = transported;
                lastTransportedKnown = true;
                if (l2Epoch.Active &&
                    transported.L2EpochId == l2Epoch.Id)
                {
                    RecordPresentedPeak(ref l2Epoch, transported.State,
                        left: true);
                }
                if (r2Epoch.Active &&
                    transported.R2EpochId == r2Epoch.Id)
                {
                    RecordPresentedPeak(ref r2Epoch, transported.State,
                        left: false);
                }
                claimPending = false;
                claimedPeakBackupRequired = false;
                claimed = default;
            }
        }

        internal void CompleteFailure(in ViiperInputClaim claim)
        {
            lock (stateLock)
            {
                if (!ClaimMatches(claim))
                {
                    return;
                }

                ViiperInputEnvelope failed = claimed;
                bool transition = claimedTransition;
                bool peakBackup = claimedPeakBackupRequired;
                claimPending = false;
                claimedPeakBackupRequired = false;
                claimed = default;
                if (transition || peakBackup)
                {
                    // Retry storage is distinct from the transition ring, so a
                    // failed oldest edge remains ahead of every state produced
                    // while the socket was failing.
                    retry = failed;
                    retryPending = true;
                }
                else if (!retryPending && transitionCount == 0 &&
                    !continuousPending)
                {
                    continuous = failed;
                    continuousPending = true;
                }
                else
                {
                    continuousReplacementCount++;
                }
            }
        }

        internal ViiperInputSchedulerSnapshot Snapshot()
        {
            lock (stateLock)
            {
                return new ViiperInputSchedulerSnapshot(generation,
                    transitionCount + (retryPending ? 1 : 0),
                    transitionHighWater, continuousPending,
                    receivedCount, transitionCountTotal,
                    continuousReplacementCount, peakUpgradeCount,
                    overflowCount, maximumQueueAgeTicks,
                    previousReceivedKnown, previousReceived,
                    lastClaimedKnown, lastClaimed,
                    lastTransportedKnown, lastTransported.State);
            }
        }

        private bool ClaimMatches(in ViiperInputClaim claim) =>
            claimPending && claimed.PublicationId == claim.PublicationId &&
            claimed.Generation == claim.Generation;

        private void BeginEpoch(ref TriggerEpoch epoch, byte value,
            in ViiperMappedInputState state, long timestamp, long receiveId)
        {
            epoch = new TriggerEpoch
            {
                Active = true,
                Id = ++nextEpochId,
                PeakValue = value,
                PeakState = state,
                PeakTimestamp = timestamp,
                PeakReceiveId = receiveId,
            };
        }

        private static void RecordPeak(ref TriggerEpoch epoch, byte value,
            in ViiperMappedInputState state, long timestamp, long receiveId)
        {
            epoch.PeakValue = value;
            epoch.PeakState = state;
            epoch.PeakTimestamp = timestamp;
            epoch.PeakReceiveId = receiveId;
        }

        private void RefreshEqualPeakTriggerStatus(
            ref TriggerEpoch epoch, in ViiperMappedInputState state,
            bool left, long timestamp, long receiveId)
        {
            // Firmware can advance trigger feedback status one report after
            // the analog value first reaches its maximum (for example 0x28 to
            // 0x29 while L2 remains 255). Refresh only that trigger-coupled
            // metadata. The peak's receive order, clock fields, other trigger,
            // and other controls must continue to describe the saved peak
            // report rather than a synthetic merged state.
            if (!epoch.PeakState.RawInputStatus.CanCoupleTriggerFrom(
                    state.RawInputStatus) ||
                IsTransition(baselineKnown: true, epoch.PeakState, state))
            {
                // Base and Edge raw49..52 layouts cannot be combined. Nor may
                // a later button/D-pad/touch-contact boundary be folded back
                // into a saved state with different controls. Retain the newer
                // complete state as a separate truthful peak; any older
                // pending/presented snapshot will fail the representation
                // check and be followed by this one before release.
                epoch.PeakState = state;
                epoch.PeakTimestamp = timestamp;
                epoch.PeakReceiveId = receiveId;
                return;
            }

            epoch.PeakState.RawInputStatus.CoupleTriggerFrom(
                state.RawInputStatus, left);
            RefreshUnclaimedPeakTriggerStatus(epoch, left);
        }

        private static void RecordPresentedPeak(ref TriggerEpoch epoch,
            in ViiperMappedInputState state, bool left)
        {
            byte value = left ? state.L2 : state.R2;
            byte presentedValue = epoch.PresentedPeakKnown ?
                (left ? epoch.PresentedPeakState.L2 :
                    epoch.PresentedPeakState.R2) : (byte)0;
            if (value == 0 || epoch.PresentedPeakKnown &&
                value < presentedValue)
            {
                return;
            }

            if (!epoch.PresentedPeakKnown || value > presentedValue ||
                !epoch.PresentedPeakState.RawInputStatus.
                    CanCoupleTriggerFrom(state.RawInputStatus))
            {
                epoch.PresentedPeakState = state;
                epoch.PresentedPeakKnown = true;
                return;
            }

            // Equal analog maxima can carry a later physical feedback state.
            // Refresh only the coupled trigger fields so the recorded report
            // is never a cross-layout or unrelated-field synthesis.
            epoch.PresentedPeakState.RawInputStatus.CoupleTriggerFrom(
                state.RawInputStatus, left);
        }

        private void RefreshUnclaimedPeakTriggerStatus(
            in TriggerEpoch epoch, bool left)
        {
            // Only the still-unclaimed initial press may absorb a peak
            // upgrade. Rewriting every same-epoch button transition would
            // invent a status/control pairing that never arrived together.
            // Retry storage is also excluded: a failed write must retry the
            // exact same logical state that was claimed.
            for (int index = 0; index < transitionCount; index++)
            {
                int slot = (transitionHead + index) % transitions.Length;
                if (transitions[slot].PublicationId ==
                        epoch.InitialPublicationId &&
                    index == transitionCount - 1 &&
                    EnvelopeHasPeakValue(transitions[slot], epoch, left) &&
                    transitions[slot].State.RawInputStatus.
                        CanCoupleTriggerFrom(
                            epoch.PeakState.RawInputStatus))
                {
                    transitions[slot].State.RawInputStatus.CoupleTriggerFrom(
                        epoch.PeakState.RawInputStatus, left);
                    return;
                }
            }
        }

        private void StrengthenPendingInitialForFallenPeaks(
            in ViiperMappedInputState state)
        {
            bool leftFell = l2Epoch.Active && state.L2 != 0 &&
                state.L2 < l2Epoch.PeakValue;
            bool rightFell = r2Epoch.Active && state.R2 != 0 &&
                state.R2 < r2Epoch.PeakValue;

            if (leftFell && rightFell &&
                l2Epoch.PeakReceiveId > r2Epoch.PeakReceiveId)
            {
                TryStrengthenPendingInitial(ref r2Epoch, left: false);
                TryStrengthenPendingInitial(ref l2Epoch, left: true);
                return;
            }

            if (leftFell)
            {
                TryStrengthenPendingInitial(ref l2Epoch, left: true);
            }
            if (rightFell)
            {
                TryStrengthenPendingInitial(ref r2Epoch, left: false);
            }
        }

        private void TryStrengthenPendingInitial(ref TriggerEpoch epoch,
            bool left)
        {
            if (!EpochPeakRepresented(epoch, left) &&
                TryStrengthenUnclaimedInitial(epoch.InitialPublicationId,
                    left, epoch))
            {
                peakUpgradeCount++;
            }
        }

        private void PreservePeakBeforeTransition(ref TriggerEpoch epoch,
            bool left, in ViiperInputEnvelope upcoming)
        {
            if (!epoch.Active || epoch.PeakValue == 0 ||
                EnvelopeRepresentsPeak(upcoming, epoch, left) ||
                EpochPeakRepresented(epoch, left))
            {
                return;
            }

            // A socket write in progress is not a durable presentation. Mark
            // it for ordered retry if it is the sole peak representation;
            // success clears the mark, while failure restores it ahead of the
            // contradictory release already in the ring.
            if (claimPending && EnvelopeRepresentsPeak(claimed, epoch, left))
            {
                claimedPeakBackupRequired = true;
                return;
            }

            if (TryStrengthenUnclaimedInitial(epoch.InitialPublicationId,
                left, epoch))
            {
                peakUpgradeCount++;
                return;
            }

            ViiperInputEnvelope peak = new()
            {
                State = epoch.PeakState,
                PublicationId = ++nextPublicationId,
                Generation = generation,
                QueuedTimestamp = epoch.PeakTimestamp,
                L2EpochId = epoch.PeakState.L2 != 0 && l2Epoch.Active ?
                    l2Epoch.Id : 0,
                R2EpochId = epoch.PeakState.R2 != 0 && r2Epoch.Active ?
                    r2Epoch.Id : 0,
            };
            if (EnqueueTransition(peak))
            {
                transitionCountTotal++;
                peakUpgradeCount++;
            }
        }

        private bool EpochPeakRepresented(in TriggerEpoch epoch, bool left)
        {
            if (epoch.PresentedPeakKnown && StateRepresentsPeak(
                    epoch.PresentedPeakState, epoch, left) ||
                retryPending && EnvelopeRepresentsPeak(retry, epoch, left) ||
                lastTransportedKnown && EnvelopeRepresentsPeak(
                    lastTransported, epoch, left))
            {
                return true;
            }

            for (int index = 0; index < transitionCount; index++)
            {
                ViiperInputEnvelope item = transitions[
                    (transitionHead + index) % transitions.Length];
                if (EnvelopeRepresentsPeak(item, epoch, left))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool EnvelopeRepresentsPeak(
            in ViiperInputEnvelope envelope, in TriggerEpoch epoch, bool left)
        {
            return EnvelopeHasPeakValue(envelope, epoch, left) &&
                envelope.State.RawInputStatus.TriggerCoupledEquals(
                    epoch.PeakState.RawInputStatus, left);
        }

        private static bool EnvelopeHasPeakValue(
            in ViiperInputEnvelope envelope, in TriggerEpoch epoch, bool left)
        {
            return left ?
                envelope.L2EpochId == epoch.Id &&
                    envelope.State.L2 >= epoch.PeakValue :
                envelope.R2EpochId == epoch.Id &&
                    envelope.State.R2 >= epoch.PeakValue;
        }

        private static bool StateRepresentsPeak(
            in ViiperMappedInputState state, in TriggerEpoch epoch, bool left)
        {
            byte value = left ? state.L2 : state.R2;
            return value >= epoch.PeakValue &&
                state.RawInputStatus.TriggerCoupledEquals(
                    epoch.PeakState.RawInputStatus, left);
        }

        private bool TryStrengthenUnclaimedInitial(long publicationId,
            bool left, in TriggerEpoch epoch)
        {
            if (publicationId <= 0)
            {
                return false;
            }

            for (int index = 0; index < transitionCount; index++)
            {
                int slot = (transitionHead + index) % transitions.Length;
                if (transitions[slot].PublicationId == publicationId)
                {
                    if (index != transitionCount - 1 &&
                        !transitions[slot].State.RawInputStatus.
                            TriggerCoupledEquals(
                                epoch.PeakState.RawInputStatus, left))
                    {
                        // A later physical report changed the coupled raw
                        // trigger state after other ordered controls. Do not
                        // pull either its analog peak or settled status ahead
                        // of those controls; promote the complete saved peak
                        // behind them instead.
                        return false;
                    }
                    return TryStrengthenEnvelope(ref transitions[slot], left,
                        epoch);
                }
            }

            return false;
        }

        private static bool TryStrengthenEnvelope(
            ref ViiperInputEnvelope envelope, bool left,
            in TriggerEpoch epoch)
        {
            byte triggerBit = left ? (byte)1 : (byte)2;
            byte otherTrigger = (byte)(envelope.PeakUpgradeMask &
                ~triggerBit);

            // One pending initial may absorb multiple upgrades of the same
            // trigger. It may absorb the other trigger only when both maxima
            // were observed in the exact same received complete state. This
            // prevents synthesizing (L2 max, R2 max) from independently timed
            // physical reports.
            if (otherTrigger != 0 && envelope.PeakUpgradeReceiveId !=
                    epoch.PeakReceiveId)
            {
                return false;
            }

            if (!envelope.State.StrengthenTrigger(left, epoch.PeakState))
            {
                return false;
            }
            envelope.PeakUpgradeMask |= triggerBit;
            envelope.PeakUpgradeReceiveId = epoch.PeakReceiveId;
            return true;
        }

        private bool EnqueueTransition(in ViiperInputEnvelope envelope)
        {
            if (transitionCount >= transitions.Length)
            {
                overflowCount++;
                return false;
            }

            int tail = (transitionHead + transitionCount) %
                transitions.Length;
            transitions[tail] = envelope;
            transitionCount++;
            if (transitionCount > transitionHighWater)
            {
                transitionHighWater = transitionCount;
            }
            return true;
        }

        private static bool IsTransition(bool baselineKnown,
            in ViiperMappedInputState previous,
            in ViiperMappedInputState current)
        {
            if (!baselineKnown)
            {
                return !current.IsNeutral;
            }

            return previous.Buttons != current.Buttons ||
                previous.DPad != current.DPad ||
                TouchTransition(previous.Touch0, current.Touch0) ||
                TouchTransition(previous.Touch1, current.Touch1) ||
                (previous.L2 == 0) != (current.L2 == 0) ||
                (previous.R2 == 0) != (current.R2 == 0);
        }

        private static bool TouchTransition(
            in ViiperMappedTouchState previous,
            in ViiperMappedTouchState current)
        {
            return previous.IsActive != current.IsActive ||
                current.IsActive && previous.IsActive &&
                    previous.TrackingId != current.TrackingId;
        }

        private static void RecordMaximum(ref long target, long candidate)
        {
            if (candidate > target)
            {
                target = candidate;
            }
        }

        private struct TriggerEpoch
        {
            public bool Active;
            public long Id;
            public long InitialPublicationId;
            public byte PeakValue;
            public ViiperMappedInputState PeakState;
            public bool PresentedPeakKnown;
            public ViiperMappedInputState PresentedPeakState;
            public long PeakTimestamp;
            public long PeakReceiveId;
        }
    }

    internal struct ViiperInputEnvelope
    {
        public ViiperMappedInputState State;
        public long PublicationId;
        public long Generation;
        public long QueuedTimestamp;
        public long TransportedTimestamp;
        public long L2EpochId;
        public long R2EpochId;
        public long PeakUpgradeReceiveId;
        public byte PeakUpgradeMask;
    }

    internal readonly struct ViiperInputClaim
    {
        private readonly ViiperInputEnvelope envelope;

        internal ViiperInputClaim(in ViiperInputEnvelope envelope,
            bool isTransition)
        {
            this.envelope = envelope;
            IsTransition = isTransition;
        }

        internal ViiperMappedInputState State => envelope.State;
        internal long PublicationId => envelope.PublicationId;
        internal long Generation => envelope.Generation;
        internal long QueuedTimestamp => envelope.QueuedTimestamp;
        internal bool IsTransition { get; }
    }

    internal readonly struct ViiperInputPublication
    {
        internal ViiperInputPublication(bool accepted, bool isTransition,
            long publicationId, int transitionDepth, bool continuousPending)
        {
            Accepted = accepted;
            IsTransition = isTransition;
            PublicationId = publicationId;
            TransitionDepth = transitionDepth;
            ContinuousPending = continuousPending;
        }

        internal bool Accepted { get; }
        internal bool IsTransition { get; }
        internal long PublicationId { get; }
        internal int TransitionDepth { get; }
        internal bool ContinuousPending { get; }
    }

    internal readonly struct ViiperInputSchedulerSnapshot
    {
        internal ViiperInputSchedulerSnapshot(long generation,
            int transitionDepth, int transitionHighWater,
            bool continuousPending, long receivedCount,
            long transitionCount, long replacementCount,
            long peakUpgradeCount, long overflowCount,
            long maximumQueueAgeTicks, bool previousReceivedKnown,
            ViiperMappedInputState previousReceived,
            bool lastClaimedKnown, ViiperMappedInputState lastClaimed,
            bool lastTransportedKnown,
            ViiperMappedInputState lastTransported)
        {
            Generation = generation;
            TransitionDepth = transitionDepth;
            TransitionHighWater = transitionHighWater;
            ContinuousPending = continuousPending;
            ReceivedCount = receivedCount;
            TransitionCount = transitionCount;
            ReplacementCount = replacementCount;
            PeakUpgradeCount = peakUpgradeCount;
            OverflowCount = overflowCount;
            MaximumQueueAgeTicks = maximumQueueAgeTicks;
            PreviousReceivedKnown = previousReceivedKnown;
            PreviousReceived = previousReceived;
            LastClaimedKnown = lastClaimedKnown;
            LastClaimed = lastClaimed;
            LastTransportedKnown = lastTransportedKnown;
            LastTransported = lastTransported;
        }

        internal long Generation { get; }
        internal int TransitionDepth { get; }
        internal int TransitionHighWater { get; }
        internal bool ContinuousPending { get; }
        internal long ReceivedCount { get; }
        internal long TransitionCount { get; }
        internal long ReplacementCount { get; }
        internal long PeakUpgradeCount { get; }
        internal long OverflowCount { get; }
        internal long MaximumQueueAgeTicks { get; }
        internal bool PreviousReceivedKnown { get; }
        internal ViiperMappedInputState PreviousReceived { get; }
        internal bool LastClaimedKnown { get; }
        internal ViiperMappedInputState LastClaimed { get; }
        internal bool LastTransportedKnown { get; }
        internal ViiperMappedInputState LastTransported { get; }
    }
}
