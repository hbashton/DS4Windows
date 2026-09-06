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

namespace DS4Windows
{
    /// <summary>
    /// Identifies one fixed canonical-feedback publisher slot. The numeric
    /// order is also the deterministic winner priority: explicit test output
    /// is highest, then native game output, audio-derived effects, and profile
    /// effects. Release is a lifecycle transition, never a competing source.
    /// </summary>
    internal enum ControllerFeedbackPublicationOrigin : byte
    {
        Invalid = 0,
        ProfileEffect = 1,
        AudioHaptics = 2,
        NativeGame = 3,
        TestPreview = 4,
        // Reserved for the sole writer's terminal lifecycle action. It is a
        // delivery identity, never a producer slot and never accepted by
        // ControllerFeedbackPublication.TryCreate.
        LifecycleNeutralization = 5,
    }

    /// <summary>
    /// A typed publisher update. Each origin owns exactly one replaceable
    /// ordering watermark. The embedded CFBK frame remains the authoritative
    /// virtual-device source and lease identity.
    /// </summary>
    internal readonly struct ControllerFeedbackPublication :
        IEquatable<ControllerFeedbackPublication>
    {
        private ControllerFeedbackPublication(
            ControllerFeedbackPublicationOrigin origin,
            in ControllerFeedbackFrame frame)
        {
            Origin = origin;
            Frame = frame;
        }

        internal ControllerFeedbackPublicationOrigin Origin { get; }
        internal ControllerFeedbackFrame Frame { get; }

        internal static bool TryCreate(
            ControllerFeedbackPublicationOrigin origin,
            in ControllerFeedbackFrame frame,
            out ControllerFeedbackPublication publication)
        {
            publication = new ControllerFeedbackPublication(origin, frame);
            if (publication.HasValidInvariants())
            {
                return true;
            }

            publication = default;
            return false;
        }

        internal bool HasValidInvariants() =>
            Origin >= ControllerFeedbackPublicationOrigin.ProfileEffect &&
            Origin <= ControllerFeedbackPublicationOrigin.TestPreview &&
            Frame.HasValidInvariants();

        public bool Equals(ControllerFeedbackPublication other) =>
            Origin == other.Origin && Frame == other.Frame;

        public override bool Equals(object obj) =>
            obj is ControllerFeedbackPublication other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Origin, Frame);

        public static bool operator ==(ControllerFeedbackPublication left,
            ControllerFeedbackPublication right) => left.Equals(right);

        public static bool operator !=(ControllerFeedbackPublication left,
            ControllerFeedbackPublication right) => !left.Equals(right);
    }

    /// <summary>
    /// The transport-neutral action reserved for one sole physical writer.
    /// Stop contains no protocol bytes: it is an idempotent obligation to zero
    /// all four canonical actuators for the exact delivery epoch.
    /// </summary>
    internal enum ControllerFeedbackDeliveryDisposition : byte
    {
        None = 0,
        Frame = 1,
        Stop = 2,
    }

    internal readonly struct ControllerFeedbackDelivery :
        IEquatable<ControllerFeedbackDelivery>
    {
        internal ControllerFeedbackDelivery(
            ControllerFeedbackDeliveryDisposition disposition,
            ControllerFeedbackPublicationOrigin origin,
            in ControllerFeedbackFrame frame,
            ulong deviceGeneration, ulong transportGeneration,
            ulong deliveryEpoch)
        {
            Disposition = disposition;
            Origin = origin;
            Frame = frame;
            DeviceGeneration = deviceGeneration;
            TransportGeneration = transportGeneration;
            DeliveryEpoch = deliveryEpoch;
        }

        internal ControllerFeedbackDeliveryDisposition Disposition { get; }
        internal ControllerFeedbackPublicationOrigin Origin { get; }
        internal ControllerFeedbackFrame Frame { get; }
        internal ulong DeviceGeneration { get; }
        internal ulong TransportGeneration { get; }
        internal ulong DeliveryEpoch { get; }

        internal bool HasValidInvariants()
        {
            if (Origin < ControllerFeedbackPublicationOrigin.ProfileEffect ||
                Origin > ControllerFeedbackPublicationOrigin.
                    LifecycleNeutralization ||
                DeviceGeneration == 0 || TransportGeneration == 0 ||
                DeliveryEpoch == 0)
            {
                return false;
            }

            return Disposition == ControllerFeedbackDeliveryDisposition.Frame ?
                Frame.HasValidInvariants() &&
                    Frame.DeviceGeneration == DeviceGeneration &&
                    Frame.TransportGeneration == TransportGeneration :
                Disposition == ControllerFeedbackDeliveryDisposition.Stop &&
                    Frame == default;
        }

        public bool Equals(ControllerFeedbackDelivery other) =>
            Disposition == other.Disposition && Origin == other.Origin &&
            Frame == other.Frame &&
            DeviceGeneration == other.DeviceGeneration &&
            TransportGeneration == other.TransportGeneration &&
            DeliveryEpoch == other.DeliveryEpoch;

        public override bool Equals(object obj) =>
            obj is ControllerFeedbackDelivery other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Disposition,
            Origin, Frame, DeviceGeneration, TransportGeneration,
            DeliveryEpoch);

        public static bool operator ==(ControllerFeedbackDelivery left,
            ControllerFeedbackDelivery right) => left.Equals(right);

        public static bool operator !=(ControllerFeedbackDelivery left,
            ControllerFeedbackDelivery right) => !left.Equals(right);
    }

    /// <summary>
    /// One generation-bound sole-writer lease. Acquisition is infrequent and
    /// allocates this ownership object; publication, selection, claim,
    /// admission, and completion do not allocate after warm-up.
    /// </summary>
    internal sealed class ControllerFeedbackWriterLease
    {
        internal ControllerFeedbackRuntime Owner;
        internal ulong WriterGeneration;
        internal ulong DeviceGeneration;
        internal ulong TransportGeneration;
        internal ulong NextClaimToken;
        internal ulong InFlightClaimToken;
        internal ulong InFlightEventRevision;
        internal ControllerFeedbackDelivery InFlightDelivery;
        internal bool InFlightAdmitted;
        internal ulong CompletedEventRevision;
        internal bool Active;
    }

    /// <summary>
    /// Fixed-slot, backend-independent canonical feedback arbitration.
    ///
    /// Publication only copies complete values and ordering watermarks. Claim
    /// selects the highest-priority live source in the newest observed device
    /// and transport generation. Once a frame reaches final admission, every
    /// replacement or expiry produces one logical Stop for that delivery epoch
    /// before a successor becomes eligible. Failed completion retries the same
    /// Stop value and epoch with a new claim token.
    ///
    /// Exactly one writer lease can be active. Final admission binds a claim to
    /// that lease's generation. A lease cannot retire while admitted work is
    /// outstanding, so a successor writer can never race a late admitted write.
    /// This type invokes no callbacks, performs no translation, and does no I/O.
    /// Product integration must not create one runtime per origin: one
    /// physical-device lifetime owner contains one instance and all origin
    /// lanes publish into its fixed slots. Direct construction is retained for
    /// adversarial engine tests only.
    /// </summary>
    internal sealed class ControllerFeedbackRuntime
    {
        private struct PublicationSlot
        {
            internal ControllerFeedbackPublication Publication;
            internal bool HasValue;
        }

        private readonly object syncRoot = new();
        private PublicationSlot profileSlot;
        private PublicationSlot audioSlot;
        private PublicationSlot gameSlot;
        private PublicationSlot previewSlot;

        private ControllerFeedbackWriterLease activeWriter;
        private ulong nextWriterGeneration;

        private bool hasOwner;
        private bool stopping;
        private bool ownerMayHaveActuated;
        private ControllerFeedbackPublication owner;
        private ulong ownerDeliveryEpoch;
        private ulong nextDeliveryEpoch;

        private bool hasEvent;
        private ControllerFeedbackDelivery currentEvent;
        private ulong currentEventRevision;

        internal bool TryPublish(in ControllerFeedbackPublication publication)
        {
            if (!publication.HasValidInvariants())
            {
                return false;
            }

            lock (syncRoot)
            {
                ref PublicationSlot slot = ref GetSlot(publication.Origin);
                if (slot.HasValue && !IsNewer(publication.Frame,
                    slot.Publication.Frame))
                {
                    return false;
                }

                slot.Publication = publication;
                slot.HasValue = true;
                return true;
            }
        }

        /// <summary>
        /// Removes one reusable producer slot without weakening the physical
        /// owner's Stop obligation. An already-admitted claim cannot be
        /// withdrawn. If this origin may have actuated, one exact Stop remains
        /// the current event until the writer completes it; otherwise the next
        /// eligible origin can be selected immediately.
        /// </summary>
        internal bool TryWithdraw(
            ControllerFeedbackPublicationOrigin origin,
            ulong nowMicroseconds)
        {
            if (origin < ControllerFeedbackPublicationOrigin.ProfileEffect ||
                origin > ControllerFeedbackPublicationOrigin.TestPreview)
            {
                return false;
            }

            lock (syncRoot)
            {
                if (activeWriter != null &&
                    activeWriter.InFlightClaimToken != 0)
                {
                    return false;
                }

                ref PublicationSlot slot = ref GetSlot(origin);
                if (!slot.HasValue)
                {
                    return true;
                }
                slot = default;

                if (!hasOwner || owner.Origin != origin)
                {
                    Reevaluate(nowMicroseconds);
                    return true;
                }
                if (stopping)
                {
                    return true;
                }
                if (!ownerMayHaveActuated)
                {
                    hasOwner = false;
                    owner = default;
                    ownerDeliveryEpoch = 0;
                    hasEvent = false;
                    currentEvent = default;
                    Reevaluate(nowMicroseconds);
                    return true;
                }

                stopping = true;
                SetEvent(new ControllerFeedbackDelivery(
                    ControllerFeedbackDeliveryDisposition.Stop,
                    owner.Origin, default,
                    owner.Frame.DeviceGeneration,
                    owner.Frame.TransportGeneration,
                    ownerDeliveryEpoch));
                return true;
            }
        }

        internal bool TryAcquireWriter(ulong deviceGeneration,
            ulong transportGeneration,
            out ControllerFeedbackWriterLease writer)
        {
            writer = null;
            if (deviceGeneration == 0 || transportGeneration == 0)
            {
                return false;
            }

            lock (syncRoot)
            {
                if (activeWriter != null ||
                    nextWriterGeneration == ulong.MaxValue ||
                    (hasEvent &&
                     (currentEvent.DeviceGeneration != deviceGeneration ||
                      currentEvent.TransportGeneration !=
                        transportGeneration)))
                {
                    return false;
                }

                ulong generation = ++nextWriterGeneration;
                writer = new ControllerFeedbackWriterLease
                {
                    Owner = this,
                    WriterGeneration = generation,
                    DeviceGeneration = deviceGeneration,
                    TransportGeneration = transportGeneration,
                    Active = true,
                };
                activeWriter = writer;
                return true;
            }
        }

        /// <summary>
        /// Retires an unadmitted writer generation. An admitted delivery must
        /// first complete because its external effect may already be underway.
        /// An unadmitted reservation is safely discarded and remains pending
        /// for a successor writer of the same target generation.
        /// </summary>
        internal bool TryRetireWriter(ControllerFeedbackWriterLease writer)
        {
            lock (syncRoot)
            {
                if (!IsCurrentWriter(writer) || writer.InFlightAdmitted)
                {
                    return false;
                }

                ClearClaim(writer);
                writer.Active = false;
                activeWriter = null;
                return true;
            }
        }

        internal ControllerFeedbackDeliveryDisposition Claim(
            ulong nowMicroseconds, ControllerFeedbackWriterLease writer,
            out ControllerFeedbackDelivery delivery, out ulong claimToken)
        {
            lock (syncRoot)
            {
                if (!IsCurrentWriter(writer))
                {
                    delivery = default;
                    claimToken = 0;
                    return ControllerFeedbackDeliveryDisposition.None;
                }

                Reevaluate(nowMicroseconds);
                if (!hasEvent || writer.InFlightClaimToken != 0 ||
                    writer.CompletedEventRevision == currentEventRevision ||
                    writer.DeviceGeneration != currentEvent.DeviceGeneration ||
                    writer.TransportGeneration !=
                        currentEvent.TransportGeneration)
                {
                    delivery = default;
                    claimToken = 0;
                    return ControllerFeedbackDeliveryDisposition.None;
                }

                ulong token = unchecked(writer.NextClaimToken + 1);
                if (token == 0)
                {
                    token = 1;
                }

                writer.NextClaimToken = token;
                writer.InFlightClaimToken = token;
                writer.InFlightEventRevision = currentEventRevision;
                writer.InFlightDelivery = currentEvent;
                writer.InFlightAdmitted = false;
                delivery = currentEvent;
                claimToken = token;
                return delivery.Disposition;
            }
        }

        /// <summary>
        /// Performs the final nonblocking admission check and pins the active
        /// writer generation until Complete. A changed winner, source expiry,
        /// target generation, event revision, writer, or token fails closed.
        /// The later transport adapter must additionally bound its actual write
        /// by the claimed frame deadline.
        /// </summary>
        internal bool TryAdmit(ControllerFeedbackWriterLease writer,
            ulong claimToken, ulong nowMicroseconds)
        {
            lock (syncRoot)
            {
                if (!HasExactClaim(writer, claimToken) ||
                    writer.InFlightAdmitted)
                {
                    return false;
                }

                Reevaluate(nowMicroseconds);
                if (!hasEvent ||
                    currentEventRevision != writer.InFlightEventRevision ||
                    currentEvent != writer.InFlightDelivery ||
                    writer.DeviceGeneration != currentEvent.DeviceGeneration ||
                    writer.TransportGeneration !=
                        currentEvent.TransportGeneration)
                {
                    return false;
                }

                if (currentEvent.Disposition ==
                    ControllerFeedbackDeliveryDisposition.Frame &&
                    !currentEvent.Frame.IsFreshAt(nowMicroseconds))
                {
                    return false;
                }

                writer.InFlightAdmitted = true;
                if (currentEvent.Disposition ==
                    ControllerFeedbackDeliveryDisposition.Frame)
                {
                    ownerMayHaveActuated = true;
                }
                return true;
            }
        }

        /// <summary>
        /// Resolves one exact reservation. Delivered=true requires prior final
        /// admission. Failure clears only the reservation so the unchanged
        /// event is retryable. Successful Stop completion advances to the
        /// newest currently eligible successor; it never republishes that Stop.
        /// </summary>
        internal bool Complete(ControllerFeedbackWriterLease writer,
            ulong claimToken, bool delivered, ulong nowMicroseconds)
        {
            lock (syncRoot)
            {
                if (!HasExactClaim(writer, claimToken) ||
                    (delivered && !writer.InFlightAdmitted))
                {
                    return false;
                }

                ulong claimedRevision = writer.InFlightEventRevision;
                ControllerFeedbackDelivery claimed =
                    writer.InFlightDelivery;
                if (delivered)
                {
                    writer.CompletedEventRevision = claimedRevision;
                }

                ClearClaim(writer);
                if (delivered &&
                    claimed.Disposition ==
                        ControllerFeedbackDeliveryDisposition.Stop &&
                    hasEvent && currentEventRevision == claimedRevision &&
                    currentEvent == claimed)
                {
                    hasOwner = false;
                    stopping = false;
                    ownerMayHaveActuated = false;
                    owner = default;
                    ownerDeliveryEpoch = 0;
                    hasEvent = false;
                    currentEvent = default;
                }

                Reevaluate(nowMicroseconds);
                return true;
            }
        }

        internal bool TryReadCurrent(out ControllerFeedbackDelivery delivery,
            out ulong eventRevision)
        {
            lock (syncRoot)
            {
                delivery = currentEvent;
                eventRevision = currentEventRevision;
                return hasEvent;
            }
        }

        /// <summary>
        /// Re-presents the newest canonical frame only when its downstream
        /// renderer changed. Ordinary lease renewals remain deduplicated; this
        /// explicit call is reserved for a profile-controlled translation
        /// policy transition. It never fabricates a frame, owner, or epoch.
        /// </summary>
        internal bool TryRefreshCurrentPresentation(
            ControllerFeedbackWriterLease writer, ulong nowMicroseconds,
            bool allowNoFrame = false)
        {
            lock (syncRoot)
            {
                if (!IsCurrentWriter(writer) || (stopping && !allowNoFrame) ||
                    writer.InFlightClaimToken != 0)
                {
                    return false;
                }

                Reevaluate(nowMicroseconds);
                if (stopping || !hasOwner || !hasEvent ||
                    currentEvent.Disposition !=
                        ControllerFeedbackDeliveryDisposition.Frame)
                {
                    // A newly admitted expired watermark can leave no event,
                    // or a pending Stop for an older effect. There is nothing
                    // to re-render, but the caller must still pump that Stop.
                    // Do not treat writer/claim contention as this no-op case.
                    return allowNoFrame && (!hasEvent || currentEvent.Disposition ==
                        ControllerFeedbackDeliveryDisposition.Stop);
                }

                if (writer.CompletedEventRevision == currentEventRevision)
                {
                    SetFrameEvent();
                }
                return true;
            }
        }

        /// <summary>
        /// Creates or preserves one canonical terminal Stop for the active
        /// writer. This is the no-owner neutral path: it allocates a delivery
        /// epoch inside the same runtime and never fabricates a frame or raw
        /// transport bytes. The caller must first seal every producer and must
        /// serialize PumpOnce, so an existing claim is a proven rejection.
        /// Repeated calls preserve the exact Stop value and epoch.
        /// </summary>
        internal bool TryEnsureTerminalNeutral(
            ControllerFeedbackWriterLease writer)
        {
            lock (syncRoot)
            {
                if (!IsCurrentWriter(writer) ||
                    writer.InFlightClaimToken != 0)
                {
                    return false;
                }
                if (hasEvent && currentEvent.Disposition ==
                        ControllerFeedbackDeliveryDisposition.Stop &&
                    currentEvent.DeviceGeneration == writer.DeviceGeneration &&
                    currentEvent.TransportGeneration ==
                        writer.TransportGeneration)
                {
                    return true;
                }

                ulong epoch = hasOwner && ownerDeliveryEpoch != 0 ?
                    ownerDeliveryEpoch : unchecked(nextDeliveryEpoch + 1);
                if (epoch == 0)
                {
                    return false;
                }
                if (!hasOwner)
                {
                    nextDeliveryEpoch = epoch;
                    ownerDeliveryEpoch = epoch;
                }

                ControllerFeedbackPublicationOrigin terminalOrigin =
                    hasOwner ? owner.Origin :
                        ControllerFeedbackPublicationOrigin.
                            LifecycleNeutralization;
                stopping = true;
                ownerMayHaveActuated = true;
                SetEvent(new ControllerFeedbackDelivery(
                    ControllerFeedbackDeliveryDisposition.Stop,
                    terminalOrigin,
                    default, writer.DeviceGeneration,
                    writer.TransportGeneration, epoch));
                return true;
            }
        }

        private void Reevaluate(ulong nowMicroseconds)
        {
            if (stopping)
            {
                return;
            }

            bool hasWinner = TrySelectWinner(nowMicroseconds,
                out ControllerFeedbackPublication winner);
            if (!hasOwner)
            {
                if (hasWinner)
                {
                    StartOwner(winner);
                }
                return;
            }

            if (hasWinner && SameOwnership(winner, owner))
            {
                if (winner != owner)
                {
                    bool effectChanged = !SameEffect(owner.Frame,
                        winner.Frame);
                    owner = winner;
                    if (effectChanged ||
                        !ActiveWriterCompletedCurrentEvent())
                    {
                        SetFrameEvent();
                    }
                }
                return;
            }

            if (!ownerMayHaveActuated)
            {
                if (hasWinner)
                {
                    ReplaceUnadmittedOwner(winner);
                }
                else
                {
                    hasOwner = false;
                    owner = default;
                    ownerDeliveryEpoch = 0;
                    hasEvent = false;
                    currentEvent = default;
                }
                return;
            }

            stopping = true;
            SetEvent(new ControllerFeedbackDelivery(
                ControllerFeedbackDeliveryDisposition.Stop,
                owner.Origin, default,
                owner.Frame.DeviceGeneration,
                owner.Frame.TransportGeneration,
                ownerDeliveryEpoch));
        }

        private void StartOwner(in ControllerFeedbackPublication publication)
        {
            ulong epoch = unchecked(nextDeliveryEpoch + 1);
            if (epoch == 0)
            {
                // Exhaustion after 2^64-1 ownership transitions fails closed.
                return;
            }

            nextDeliveryEpoch = epoch;
            ownerDeliveryEpoch = epoch;
            owner = publication;
            hasOwner = true;
            stopping = false;
            ownerMayHaveActuated = false;
            SetFrameEvent();
        }

        private void ReplaceUnadmittedOwner(
            in ControllerFeedbackPublication publication)
        {
            hasOwner = false;
            owner = default;
            ownerDeliveryEpoch = 0;
            hasEvent = false;
            currentEvent = default;
            StartOwner(publication);
        }

        private void SetFrameEvent()
        {
            SetEvent(new ControllerFeedbackDelivery(
                ControllerFeedbackDeliveryDisposition.Frame,
                owner.Origin, owner.Frame,
                owner.Frame.DeviceGeneration,
                owner.Frame.TransportGeneration,
                ownerDeliveryEpoch));
        }

        private void SetEvent(in ControllerFeedbackDelivery delivery)
        {
            ulong revision = unchecked(currentEventRevision + 1);
            if (revision == 0)
            {
                revision = 1;
            }

            currentEventRevision = revision;
            currentEvent = delivery;
            hasEvent = true;
        }

        private bool TrySelectWinner(ulong nowMicroseconds,
            out ControllerFeedbackPublication winner)
        {
            winner = default;
            if (!TryFindNewestTarget(out ulong newestDeviceGeneration,
                out ulong newestTransportGeneration))
            {
                return false;
            }

            bool found = false;
            Consider(ref profileSlot, nowMicroseconds,
                newestDeviceGeneration, newestTransportGeneration,
                ref winner, ref found);
            Consider(ref audioSlot, nowMicroseconds,
                newestDeviceGeneration, newestTransportGeneration,
                ref winner, ref found);
            Consider(ref gameSlot, nowMicroseconds,
                newestDeviceGeneration, newestTransportGeneration,
                ref winner, ref found);
            Consider(ref previewSlot, nowMicroseconds,
                newestDeviceGeneration, newestTransportGeneration,
                ref winner, ref found);
            return found;
        }

        private static void Consider(ref PublicationSlot slot,
            ulong nowMicroseconds, ulong newestDeviceGeneration,
            ulong newestTransportGeneration,
            ref ControllerFeedbackPublication winner, ref bool found)
        {
            if (!slot.HasValue ||
                slot.Publication.Frame.DeviceGeneration !=
                    newestDeviceGeneration ||
                slot.Publication.Frame.TransportGeneration !=
                    newestTransportGeneration ||
                slot.Publication.Frame.IsStop ||
                !slot.Publication.Frame.IsFreshAt(nowMicroseconds))
            {
                return;
            }

            if (!found || slot.Publication.Origin > winner.Origin)
            {
                winner = slot.Publication;
                found = true;
            }
        }

        private bool TryFindNewestTarget(out ulong deviceGeneration,
            out ulong transportGeneration)
        {
            deviceGeneration = 0;
            transportGeneration = 0;
            bool found = false;
            FindNewestTarget(ref profileSlot, ref deviceGeneration,
                ref transportGeneration, ref found);
            FindNewestTarget(ref audioSlot, ref deviceGeneration,
                ref transportGeneration, ref found);
            FindNewestTarget(ref gameSlot, ref deviceGeneration,
                ref transportGeneration, ref found);
            FindNewestTarget(ref previewSlot, ref deviceGeneration,
                ref transportGeneration, ref found);
            return found;
        }

        private static void FindNewestTarget(ref PublicationSlot slot,
            ref ulong deviceGeneration, ref ulong transportGeneration,
            ref bool found)
        {
            if (!slot.HasValue)
            {
                return;
            }

            ControllerFeedbackFrame frame = slot.Publication.Frame;
            if (!found || frame.DeviceGeneration > deviceGeneration ||
                (frame.DeviceGeneration == deviceGeneration &&
                 frame.TransportGeneration > transportGeneration))
            {
                deviceGeneration = frame.DeviceGeneration;
                transportGeneration = frame.TransportGeneration;
                found = true;
            }
        }

        private static bool SameOwnership(
            in ControllerFeedbackPublication left,
            in ControllerFeedbackPublication right) =>
            left.Origin == right.Origin &&
            left.Frame.Source == right.Frame.Source &&
            left.Frame.DeviceGeneration == right.Frame.DeviceGeneration &&
            left.Frame.TransportGeneration ==
                right.Frame.TransportGeneration &&
            left.Frame.OwnershipEpoch == right.Frame.OwnershipEpoch;

        /// <summary>
        /// Sequence, timestamp, and TTL refresh the source lease; they do not
        /// change the physical effect. Keeping a completed event retired when
        /// only those ordering fields advance lets a producer renew before
        /// expiry without generating periodic duplicate transport writes.
        /// </summary>
        private static bool SameEffect(in ControllerFeedbackFrame left,
            in ControllerFeedbackFrame right) =>
            left.Source == right.Source &&
            left.Command == right.Command &&
            left.Actuators == right.Actuators &&
            left.BodyLow == right.BodyLow &&
            left.BodyHigh == right.BodyHigh &&
            left.LeftTrigger == right.LeftTrigger &&
            left.RightTrigger == right.RightTrigger &&
            left.DeviceGeneration == right.DeviceGeneration &&
            left.TransportGeneration == right.TransportGeneration &&
            left.OwnershipEpoch == right.OwnershipEpoch;

        private bool ActiveWriterCompletedCurrentEvent() =>
            activeWriter != null && activeWriter.Active && hasEvent &&
            activeWriter.CompletedEventRevision == currentEventRevision;

        private static bool IsNewer(in ControllerFeedbackFrame candidate,
            in ControllerFeedbackFrame current)
        {
            if (candidate.DeviceGeneration != current.DeviceGeneration)
            {
                return candidate.DeviceGeneration > current.DeviceGeneration;
            }

            if (candidate.TransportGeneration != current.TransportGeneration)
            {
                return candidate.TransportGeneration >
                    current.TransportGeneration;
            }

            if (candidate.OwnershipEpoch != current.OwnershipEpoch)
            {
                return candidate.OwnershipEpoch > current.OwnershipEpoch;
            }

            return current.Command != ControllerFeedbackCommand.Stop &&
                candidate.Source == current.Source &&
                candidate.Sequence > current.Sequence;
        }

        private bool IsCurrentWriter(ControllerFeedbackWriterLease writer) =>
            writer != null && writer.Active &&
            ReferenceEquals(writer.Owner, this) &&
            ReferenceEquals(activeWriter, writer) &&
            writer.WriterGeneration == nextWriterGeneration;

        private bool HasExactClaim(ControllerFeedbackWriterLease writer,
            ulong claimToken) => IsCurrentWriter(writer) && claimToken != 0 &&
            writer.InFlightClaimToken == claimToken &&
            writer.InFlightEventRevision != 0 &&
            writer.InFlightDelivery.HasValidInvariants();

        private static void ClearClaim(ControllerFeedbackWriterLease writer)
        {
            writer.InFlightClaimToken = 0;
            writer.InFlightEventRevision = 0;
            writer.InFlightDelivery = default;
            writer.InFlightAdmitted = false;
        }

        private ref PublicationSlot GetSlot(
            ControllerFeedbackPublicationOrigin origin)
        {
            switch (origin)
            {
                case ControllerFeedbackPublicationOrigin.ProfileEffect:
                    return ref profileSlot;
                case ControllerFeedbackPublicationOrigin.AudioHaptics:
                    return ref audioSlot;
                case ControllerFeedbackPublicationOrigin.NativeGame:
                    return ref gameSlot;
                default:
                    return ref previewSlot;
            }
        }
    }
}
