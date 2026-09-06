/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
*/

using System;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Complete normalized actuator state used between a protocol decoder and
    /// the canonical feedback runtime. Zero in every channel is an explicit
    /// lease-retaining Neutral, never an absent update.
    /// </summary>
    internal readonly struct ControllerFeedbackActuatorState :
        IEquatable<ControllerFeedbackActuatorState>
    {
        internal ControllerFeedbackActuatorState(ushort bodyLow,
            ushort bodyHigh, ushort leftTrigger, ushort rightTrigger)
        {
            BodyLow = bodyLow;
            BodyHigh = bodyHigh;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
        }

        internal ushort BodyLow { get; }
        internal ushort BodyHigh { get; }
        internal ushort LeftTrigger { get; }
        internal ushort RightTrigger { get; }
        internal bool IsNeutral => BodyLow == 0 && BodyHigh == 0 &&
            LeftTrigger == 0 && RightTrigger == 0;

        public bool Equals(ControllerFeedbackActuatorState other) =>
            BodyLow == other.BodyLow && BodyHigh == other.BodyHigh &&
            LeftTrigger == other.LeftTrigger &&
            RightTrigger == other.RightTrigger;

        public override bool Equals(object obj) =>
            obj is ControllerFeedbackActuatorState other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(BodyLow,
            BodyHigh, LeftTrigger, RightTrigger);

        public static bool operator ==(ControllerFeedbackActuatorState left,
            ControllerFeedbackActuatorState right) => left.Equals(right);

        public static bool operator !=(ControllerFeedbackActuatorState left,
            ControllerFeedbackActuatorState right) => !left.Equals(right);
    }

    /// <summary>
    /// Transport callback owned by one physical-output worker. Implementations
    /// must treat repeated Stop deliveries with the same DeliveryEpoch as an
    /// idempotent retry of one logical neutral operation.
    /// </summary>
    internal interface IControllerFeedbackDeliverySink
    {
        bool TryDeliver(in ControllerFeedbackDelivery delivery);
    }

    internal enum ControllerFeedbackPumpDisposition : byte
    {
        None = 0,
        Delivered = 1,
        RetryPending = 2,
        Superseded = 3,
        Busy = 4,
        Retired = 5,
    }

    internal enum ControllerFeedbackLeaseServiceDisposition : byte
    {
        None = 0,
        Renewed = 1,
        StopRequested = 2,
        Inactive = 3,
    }

    /// <summary>
    /// One scheduler-independent arbitration and sole-writer owner for one
    /// physical-device lifetime. Every state-lane origin targeting that device
    /// is created by this owner and publishes into its one shared runtime.
    /// Only this owner can claim, admit, deliver, or retire physical work.
    /// </summary>
    internal sealed class ControllerFeedbackStateLanePump
    {
        /// <summary>
        /// One fixed origin/source publisher. A lane owns ordering and renewal
        /// state only; it cannot acquire a writer or invoke a physical sink.
        /// </summary>
        internal sealed class Lane
        {
            private readonly object publicationLock = new();
            private readonly ControllerFeedbackStateLanePump owner;
            private readonly ControllerFeedbackPublicationOrigin origin;
            private readonly ControllerFeedbackSource source;
            private readonly ulong timeToLiveMicroseconds;
            private readonly ulong renewalIntervalMicroseconds;

            private ControllerFeedbackActuatorState latestState;
            private ulong ownershipEpoch;
            private ulong sequence;
            private ulong lastPublicationMicroseconds;
            private ulong nextRenewalMicroseconds;
            private bool hasState;
            private bool stopRequested;

            internal Lane(ControllerFeedbackStateLanePump owner,
                ControllerFeedbackPublicationOrigin origin,
                ControllerFeedbackSource source, ulong ownershipEpoch,
                ulong timeToLiveMicroseconds,
                ulong renewalIntervalMicroseconds)
            {
                this.owner = owner;
                this.origin = origin;
                this.source = source;
                this.ownershipEpoch = ownershipEpoch;
                this.timeToLiveMicroseconds = timeToLiveMicroseconds;
                this.renewalIntervalMicroseconds =
                    renewalIntervalMicroseconds;
            }

            internal ControllerFeedbackPublicationOrigin Origin => origin;

            internal ControllerFeedbackSource Source => source;

            internal bool TryPublish(
                in ControllerFeedbackActuatorState state,
                ulong nowMicroseconds) => TryPublish(state,
                    nowMicroseconds, out _);

            /// <summary>
            /// Publishes through this fixed-origin lane and returns the exact
            /// admitted canonical frame. Rich source renderers use that frame
            /// only as an identity key; arbitration and physical ownership
            /// remain in the ordinary runtime/pump path.
            /// </summary>
            internal bool TryPublish(
                in ControllerFeedbackActuatorState state,
                ulong nowMicroseconds,
                out ControllerFeedbackFrame frame)
            {
                frame = default;
                lock (publicationLock)
                {
                    if (stopRequested || owner.IsRetired ||
                        (hasState && nowMicroseconds <
                            lastPublicationMicroseconds) ||
                        !TryAdvanceOrderingLocked())
                    {
                        return false;
                    }

                    if (!TryPublishLocked(state, nowMicroseconds,
                            out frame))
                    {
                        return false;
                    }

                    latestState = state;
                    hasState = true;
                    lastPublicationMicroseconds = nowMicroseconds;
                    nextRenewalMicroseconds = AddSaturating(
                        nowMicroseconds, renewalIntervalMicroseconds);
                    return true;
                }
            }

            /// <summary>
            /// Refreshes unchanged state only at its renewal boundary. Missing
            /// expiry requests Stop instead of resurrecting stale output.
            /// </summary>
            internal ControllerFeedbackLeaseServiceDisposition ServiceLease(
                ulong nowMicroseconds)
            {
                lock (publicationLock)
                {
                    if (!hasState || stopRequested || owner.IsStopping ||
                        owner.IsRetired)
                    {
                        return ControllerFeedbackLeaseServiceDisposition.
                            Inactive;
                    }

                    if (nowMicroseconds < nextRenewalMicroseconds)
                    {
                        return ControllerFeedbackLeaseServiceDisposition.None;
                    }

                    if (nowMicroseconds - lastPublicationMicroseconds >=
                        timeToLiveMicroseconds)
                    {
                        return RequestStopLocked(nowMicroseconds) ?
                            ControllerFeedbackLeaseServiceDisposition.
                                StopRequested :
                            ControllerFeedbackLeaseServiceDisposition.
                                Inactive;
                    }

                    if (!TryAdvanceOrderingLocked() ||
                        !TryPublishLocked(latestState, nowMicroseconds,
                            out _))
                    {
                        return ControllerFeedbackLeaseServiceDisposition.
                            Inactive;
                    }

                    lastPublicationMicroseconds = nowMicroseconds;
                    nextRenewalMicroseconds = AddSaturating(
                        nowMicroseconds, renewalIntervalMicroseconds);
                    return ControllerFeedbackLeaseServiceDisposition.Renewed;
                }
            }

            internal bool RequestStop(ulong nowMicroseconds)
            {
                lock (publicationLock)
                {
                    return RequestStopLocked(nowMicroseconds);
                }
            }

            /// <summary>
            /// Withdraws the current state while keeping this fixed origin lane
            /// reusable. The runtime preserves any required physical Stop; a
            /// successor publication advances to a new ownership epoch and can
            /// only win after that Stop completes.
            /// </summary>
            internal bool TryWithdraw(ulong nowMicroseconds)
            {
                lock (publicationLock)
                {
                    if (!hasState)
                    {
                        return true;
                    }
                    if (stopRequested || owner.IsStopping || owner.IsRetired ||
                        ownershipEpoch == ulong.MaxValue ||
                        !owner.TryWithdraw(this, nowMicroseconds))
                    {
                        return false;
                    }

                    ownershipEpoch++;
                    sequence = 0;
                    latestState = default;
                    lastPublicationMicroseconds = 0;
                    nextRenewalMicroseconds = 0;
                    hasState = false;
                    return true;
                }
            }

            internal bool EnsureStopRequested(ulong nowMicroseconds)
            {
                lock (publicationLock)
                {
                    return stopRequested ||
                        RequestStopLocked(nowMicroseconds);
                }
            }

            private bool RequestStopLocked(ulong nowMicroseconds)
            {
                if (stopRequested || owner.IsRetired)
                {
                    return false;
                }

                if (!hasState)
                {
                    stopRequested = true;
                    return true;
                }

                ulong stopTimestamp = nowMicroseconds <
                    lastPublicationMicroseconds ? lastPublicationMicroseconds :
                    nowMicroseconds;
                if (!TryAdvanceOrderingLocked() ||
                    !ControllerFeedbackFrame.TryCreate(source,
                        ControllerFeedbackCommand.Stop,
                        ControllerFeedbackActuators.All, 0, 0, 0, 0,
                        sequence, owner.deviceGeneration,
                        owner.transportGeneration, ownershipEpoch,
                        stopTimestamp, timeToLiveMicroseconds,
                        out ControllerFeedbackFrame frame) ||
                    !ControllerFeedbackPublication.TryCreate(origin, frame,
                        out ControllerFeedbackPublication publication) ||
                    !owner.TryPublish(this, publication,
                        allowWhileStopping: true))
                {
                    return false;
                }

                stopRequested = true;
                return true;
            }

            private bool TryPublishLocked(
                in ControllerFeedbackActuatorState state,
                ulong nowMicroseconds,
                out ControllerFeedbackFrame frame)
            {
                frame = default;
                ControllerFeedbackCommand command = state.IsNeutral ?
                    ControllerFeedbackCommand.Neutral :
                    ControllerFeedbackCommand.Apply;
                return ControllerFeedbackFrame.TryCreate(source, command,
                        ControllerFeedbackActuators.All, state.BodyLow,
                        state.BodyHigh, state.LeftTrigger,
                        state.RightTrigger, sequence,
                        owner.deviceGeneration, owner.transportGeneration,
                        ownershipEpoch, nowMicroseconds,
                        timeToLiveMicroseconds,
                        out frame) &&
                    ControllerFeedbackPublication.TryCreate(origin, frame,
                        out ControllerFeedbackPublication publication) &&
                    owner.TryPublish(this, publication,
                        allowWhileStopping: false);
            }

            private bool TryAdvanceOrderingLocked()
            {
                if (sequence != ulong.MaxValue)
                {
                    sequence++;
                    return true;
                }

                if (ownershipEpoch == ulong.MaxValue)
                {
                    return false;
                }

                ownershipEpoch++;
                sequence = 1;
                return true;
            }
        }

        private readonly object lifecycleLock = new();
        private readonly ControllerFeedbackRuntime runtime = new();
        private readonly ControllerFeedbackWriterLease writer;
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;

        private Lane profileLane;
        private Lane audioLane;
        private Lane gameLane;
        private Lane previewLane;
        private int stopping;
        private int retired;
        private int pumpActive;

        private ControllerFeedbackStateLanePump(ulong deviceGeneration,
            ulong transportGeneration)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
            if (!runtime.TryAcquireWriter(deviceGeneration,
                    transportGeneration, out writer))
            {
                throw new InvalidOperationException(
                    "The canonical feedback writer lease was unavailable.");
            }
        }

        internal bool IsRetired => Volatile.Read(ref retired) != 0;

        internal bool IsStopping => Volatile.Read(ref stopping) != 0;

        internal static bool TryCreate(ulong deviceGeneration,
            ulong transportGeneration,
            out ControllerFeedbackStateLanePump pump)
        {
            pump = null;
            if (deviceGeneration == 0 || transportGeneration == 0)
            {
                return false;
            }

            pump = new ControllerFeedbackStateLanePump(deviceGeneration,
                transportGeneration);
            return true;
        }

        internal bool TryCreateLane(
            ControllerFeedbackPublicationOrigin origin,
            ControllerFeedbackSource source, ulong ownershipEpoch,
            ulong timeToLiveMicroseconds,
            ulong renewalIntervalMicroseconds, out Lane lane)
        {
            lane = null;
            if (origin < ControllerFeedbackPublicationOrigin.ProfileEffect ||
                origin > ControllerFeedbackPublicationOrigin.TestPreview ||
                source < ControllerFeedbackSource.XboxOneVirtualDevice ||
                source > ControllerFeedbackSource.Switch2VirtualDevice ||
                ownershipEpoch == 0 || timeToLiveMicroseconds == 0 ||
                timeToLiveMicroseconds >
                    ControllerFeedbackFrame.MaxTimeToLiveMicroseconds ||
                renewalIntervalMicroseconds == 0 ||
                renewalIntervalMicroseconds >= timeToLiveMicroseconds)
            {
                return false;
            }

            lock (lifecycleLock)
            {
                if (IsStopping || IsRetired || GetLaneNoLock(origin) != null)
                {
                    return false;
                }

                lane = new Lane(this, origin, source, ownershipEpoch,
                    timeToLiveMicroseconds, renewalIntervalMicroseconds);
                SetLaneNoLock(origin, lane);
                return true;
            }
        }

        /// <summary>
        /// Creates the reviewed VIIPER CFBK receive edge against this pump's
        /// one canonical runtime. The returned ingress still authenticates
        /// source, physical generations, ownership epoch, ordering, and TTL;
        /// it cannot claim or write the physical transport itself.
        /// </summary>
        internal bool TryCreateBrokerIngress(ControllerFeedbackSource source,
            ulong ownershipEpoch, out ControllerFeedbackIngress ingress)
        {
            ingress = null;
            if (IsStopping || IsRetired)
            {
                return false;
            }
            return ControllerFeedbackIngress.TryCreate(runtime, source,
                deviceGeneration, transportGeneration, ownershipEpoch,
                out ingress);
        }

        /// <summary>
        /// Runs at most one external write across every origin in this physical
        /// lifetime. The sink is always invoked outside runtime/lifecycle locks.
        /// </summary>
        internal ControllerFeedbackPumpDisposition PumpOnce(
            ulong nowMicroseconds, IControllerFeedbackDeliverySink sink,
            out ControllerFeedbackDelivery delivery)
        {
            delivery = default;
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (IsRetired)
            {
                return ControllerFeedbackPumpDisposition.Retired;
            }

            if (Interlocked.CompareExchange(ref pumpActive, 1, 0) != 0)
            {
                return ControllerFeedbackPumpDisposition.Busy;
            }

            try
            {
                if (IsRetired)
                {
                    return ControllerFeedbackPumpDisposition.Retired;
                }

                ControllerFeedbackDeliveryDisposition disposition =
                    runtime.Claim(nowMicroseconds, writer, out delivery,
                        out ulong token);
                if (disposition ==
                    ControllerFeedbackDeliveryDisposition.None)
                {
                    delivery = default;
                    return ControllerFeedbackPumpDisposition.None;
                }

                if (!runtime.TryAdmit(writer, token, nowMicroseconds))
                {
                    runtime.Complete(writer, token, delivered: false,
                        nowMicroseconds);
                    delivery = default;
                    return ControllerFeedbackPumpDisposition.Superseded;
                }

                bool delivered;
                try
                {
                    delivered = sink.TryDeliver(delivery);
                }
                catch
                {
                    runtime.Complete(writer, token, delivered: false,
                        nowMicroseconds);
                    throw;
                }

                if (!runtime.Complete(writer, token, delivered,
                        nowMicroseconds))
                {
                    return ControllerFeedbackPumpDisposition.Superseded;
                }

                return delivered ?
                    ControllerFeedbackPumpDisposition.Delivered :
                    ControllerFeedbackPumpDisposition.RetryPending;
            }
            finally
            {
                Volatile.Write(ref pumpActive, 0);
            }
        }

        /// <summary>
        /// Requests one presentation of the newest canonical frame after a
        /// downstream profile renderer changes. This uses the same pump fence
        /// as physical delivery and does not affect controller input cadence.
        /// </summary>
        internal bool TryRefreshCurrentPresentation(ulong nowMicroseconds)
        {
            if (IsStopping || IsRetired ||
                Interlocked.CompareExchange(ref pumpActive, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                return !IsStopping && !IsRetired &&
                    runtime.TryRefreshCurrentPresentation(writer,
                        nowMicroseconds);
            }
            finally
            {
                Volatile.Write(ref pumpActive, 0);
            }
        }

        /// <summary>
        /// Stops every registered origin, performs no more than maxAttempts
        /// sink calls, and retires only when the shared runtime has no event.
        /// An unresolved event remains retryable under the same writer lease.
        /// </summary>
        internal bool TryStopAndRetire(ulong nowMicroseconds,
            IControllerFeedbackDeliverySink sink, int maxAttempts)
        {
            return TryStopAndRetireCore(nowMicroseconds, sink, maxAttempts,
                requireExactTerminalNeutral: false);
        }

        /// <summary>
        /// Terminal owned-output variant. Even an empty lifetime receives one
        /// canonical Stop delivery allocated by this pump's existing runtime;
        /// no synthetic frame or transport bytes bypass the sink/writer path.
        /// </summary>
        internal bool TryTerminalNeutralAndRetire(ulong nowMicroseconds,
            IControllerFeedbackDeliverySink sink, int maxAttempts)
        {
            return TryStopAndRetireCore(nowMicroseconds, sink, maxAttempts,
                requireExactTerminalNeutral: true);
        }

        private bool TryStopAndRetireCore(ulong nowMicroseconds,
            IControllerFeedbackDeliverySink sink, int maxAttempts,
            bool requireExactTerminalNeutral)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }
            if (maxAttempts < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }
            if (IsRetired)
            {
                return true;
            }
            bool allLanesStopped = TryRequestStopAll(nowMicroseconds, out _);
            if (allLanesStopped && requireExactTerminalNeutral &&
                !runtime.TryEnsureTerminalNeutral(writer))
            {
                return false;
            }

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ControllerFeedbackPumpDisposition result = PumpOnce(
                    nowMicroseconds, sink, out _);
                if (result is ControllerFeedbackPumpDisposition.Busy or
                    ControllerFeedbackPumpDisposition.Retired or
                    ControllerFeedbackPumpDisposition.None)
                {
                    break;
                }
            }

            if (!allLanesStopped)
            {
                // Other lanes may still have created a neutral obligation.
                // Service the bounded attempts above, but never retire while
                // any registered lane failed to become terminal.
                return false;
            }

            if (Interlocked.CompareExchange(ref pumpActive, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                lock (lifecycleLock)
                {
                    if (IsRetired)
                    {
                        return true;
                    }
                    if (runtime.TryReadCurrent(out _, out _) ||
                        !runtime.TryRetireWriter(writer))
                    {
                        return false;
                    }

                    Volatile.Write(ref retired, 1);
                    return true;
                }
            }
            finally
            {
                Volatile.Write(ref pumpActive, 0);
            }
        }

        /// <summary>
        /// Retire without acknowledging a physical Stop. The owner must first
        /// seal the exact transport on definite native device-removal evidence
        /// and drain all its native output operations. No successor writer is
        /// created and pending effects are never replayed to a new connection.
        /// </summary>
        internal bool TryRetireDisconnectedTarget()
        {
            if (!SealPublications() ||
                Interlocked.CompareExchange(ref pumpActive, 1, 0) != 0)
            {
                return false;
            }
            try
            {
                lock (lifecycleLock)
                {
                    if (IsRetired)
                    {
                        return true;
                    }
                    if (!runtime.TryRetireWriter(writer))
                    {
                        return false;
                    }
                    Volatile.Write(ref retired, 1);
                    return true;
                }
            }
            finally
            {
                Volatile.Write(ref pumpActive, 0);
            }
        }

        internal bool RequestStop(ulong nowMicroseconds) =>
            TryRequestStopAll(nowMicroseconds, out bool changed) && changed;

        /// <summary>
        /// Atomically closes producer admission without publishing a Stop.
        /// Existing lane objects remain valid capabilities but every later
        /// TryPublish/ServiceLease call observes the shared stopping fence.
        /// Terminal neutralization uses this before draining a retained native
        /// output, so a different Stop cannot overtake the exact prior report.
        /// </summary>
        internal bool SealPublications()
        {
            lock (lifecycleLock)
            {
                if (IsRetired)
                {
                    return true;
                }
                Volatile.Write(ref stopping, 1);
                return true;
            }
        }

        private bool TryRequestStopAll(ulong nowMicroseconds,
            out bool changed)
        {
            Lane profile;
            Lane audio;
            Lane game;
            Lane preview;
            lock (lifecycleLock)
            {
                if (IsRetired)
                {
                    changed = false;
                    return true;
                }

                changed = !IsStopping;
                Volatile.Write(ref stopping, 1);
                profile = profileLane;
                audio = audioLane;
                game = gameLane;
                preview = previewLane;
            }

            // Lane locks and runtime publication happen only after releasing
            // the lifecycle lock, avoiding lock inversion with publishers.
            bool succeeded = true;
            succeeded &= profile == null ||
                profile.EnsureStopRequested(nowMicroseconds);
            succeeded &= audio == null ||
                audio.EnsureStopRequested(nowMicroseconds);
            succeeded &= game == null ||
                game.EnsureStopRequested(nowMicroseconds);
            succeeded &= preview == null ||
                preview.EnsureStopRequested(nowMicroseconds);
            return succeeded;
        }

        private bool TryPublish(Lane lane,
            in ControllerFeedbackPublication publication,
            bool allowWhileStopping)
        {
            lock (lifecycleLock)
            {
                if (IsRetired || !IsRegisteredNoLock(lane) ||
                    IsStopping && !allowWhileStopping)
                {
                    return false;
                }

                return runtime.TryPublish(publication);
            }
        }

        private bool TryWithdraw(Lane lane, ulong nowMicroseconds)
        {
            lock (lifecycleLock)
            {
                return !IsStopping && !IsRetired &&
                    IsRegisteredNoLock(lane) &&
                    runtime.TryWithdraw(lane.Origin, nowMicroseconds);
            }
        }

        internal bool AuthenticatesLane(Lane lane,
            ControllerFeedbackPublicationOrigin origin,
            ControllerFeedbackSource source)
        {
            lock (lifecycleLock)
            {
                return !IsStopping && !IsRetired && lane != null &&
                    lane.Origin == origin && lane.Source == source &&
                    IsRegisteredNoLock(lane);
            }
        }

        private Lane GetLaneNoLock(
            ControllerFeedbackPublicationOrigin origin) => origin switch
        {
            ControllerFeedbackPublicationOrigin.ProfileEffect => profileLane,
            ControllerFeedbackPublicationOrigin.AudioHaptics => audioLane,
            ControllerFeedbackPublicationOrigin.NativeGame => gameLane,
            _ => previewLane,
        };

        private void SetLaneNoLock(ControllerFeedbackPublicationOrigin origin,
            Lane lane)
        {
            switch (origin)
            {
                case ControllerFeedbackPublicationOrigin.ProfileEffect:
                    profileLane = lane;
                    break;
                case ControllerFeedbackPublicationOrigin.AudioHaptics:
                    audioLane = lane;
                    break;
                case ControllerFeedbackPublicationOrigin.NativeGame:
                    gameLane = lane;
                    break;
                default:
                    previewLane = lane;
                    break;
            }
        }

        private bool IsRegisteredNoLock(Lane lane) => lane != null &&
            ReferenceEquals(GetLaneNoLock(lane.Origin), lane);

        private static ulong AddSaturating(ulong left, ulong right) =>
            left > ulong.MaxValue - right ? ulong.MaxValue : left + right;
    }

    /// <summary>
    /// Exact DS4Windows adapter for VIIPER's versioned Xbox 360 server-to-
    /// client feedback payload: byte zero is the low/heavy body motor and byte
    /// one is the high/light body motor. The scale x*257 is bijective for all
    /// byte values, so projecting back to the legacy physical path is exact.
    /// </summary>
    internal static class Xbox360CanonicalFeedbackAdapter
    {
        internal const int WireLength = 2;

        internal static bool TryDecode(byte[] source, int length,
            out ControllerFeedbackActuatorState state)
        {
            state = default;
            if (source == null || length != WireLength ||
                source.Length < WireLength)
            {
                return false;
            }

            state = new ControllerFeedbackActuatorState(
                ScaleByte(source[0]), ScaleByte(source[1]), 0, 0);
            return true;
        }

        internal static void ProjectLegacy(
            in ControllerFeedbackActuatorState state, out byte heavySlow,
            out byte lightFast)
        {
            heavySlow = ScaleUShort(state.BodyLow);
            lightFast = ScaleUShort(state.BodyHigh);
        }

        private static ushort ScaleByte(byte value) =>
            (ushort)(value * 257);

        private static byte ScaleUShort(ushort value) =>
            (byte)((value + 128) / 257);
    }
}
