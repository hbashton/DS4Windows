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
    /// Generation-bound receive edge for virtual-controller feedback authored
    /// by VIIPER. Broker sources submit the project-wide CFBK wire contract;
    /// legacy virtual devices submit an already decoded canonical actuator
    /// value. Both enter the existing NativeGame slot of the one physical
    /// controller's <see cref="ControllerFeedbackRuntime"/>.
    ///
    /// This type deliberately owns no feedback mailbox, ordering watermark,
    /// arbitration policy, clock policy, or physical translation. In
    /// particular, an expired but newer frame is still published: the
    /// canonical runtime must observe that ordering watermark so it can stop
    /// an already admitted older effect instead of resurrecting it.
    /// </summary>
    internal sealed class ControllerFeedbackIngress
    {
        private readonly object lifecycleGate = new();
        private readonly ControllerFeedbackRuntime runtime;
        private readonly ControllerFeedbackSource source;
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;
        private readonly ulong ownershipEpoch;
        private ControllerFeedbackFrame lastPublishedFrame;
        private bool hasPublishedFrame;
        private bool active;

        private ControllerFeedbackIngress(
            ControllerFeedbackRuntime runtime,
            ControllerFeedbackSource source, ulong deviceGeneration,
            ulong transportGeneration, ulong ownershipEpoch)
        {
            this.runtime = runtime;
            this.source = source;
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
            this.ownershipEpoch = ownershipEpoch;
            active = true;
        }

        /// <summary>
        /// Creates an ingress for one exact virtual source and exact non-zero
        /// target lifetime. The outer orchestrator supplies these identities;
        /// this edge never guesses them.
        /// </summary>
        internal static bool TryCreate(ControllerFeedbackRuntime runtime,
            ControllerFeedbackSource source, ulong deviceGeneration,
            ulong transportGeneration, ulong ownershipEpoch,
            out ControllerFeedbackIngress ingress)
        {
            if (runtime == null ||
                source < ControllerFeedbackSource.XboxOneVirtualDevice ||
                source > ControllerFeedbackSource.Switch2VirtualDevice ||
                deviceGeneration == 0 || transportGeneration == 0 ||
                ownershipEpoch == 0)
            {
                ingress = null;
                return false;
            }

            ingress = new ControllerFeedbackIngress(runtime, source,
                deviceGeneration, transportGeneration, ownershipEpoch);
            return true;
        }

        /// <summary>
        /// Validates exactly one CFBK v1 frame and publishes it through the
        /// canonical runtime. Sequence, TTL, Stop terminality, and source
        /// ordering are enforced by ControllerFeedbackFrame/Runtime rather
        /// than duplicated here.
        /// </summary>
        internal bool TryPublish(ReadOnlySpan<byte> wire)
        {
            if (!ControllerFeedbackFrame.TryReadFrom(wire,
                    out ControllerFeedbackFrame frame) ||
                frame.Source != source ||
                frame.DeviceGeneration != deviceGeneration ||
                frame.TransportGeneration != transportGeneration ||
                frame.OwnershipEpoch != ownershipEpoch ||
                !ControllerFeedbackPublication.TryCreate(
                    ControllerFeedbackPublicationOrigin.NativeGame, frame,
                    out ControllerFeedbackPublication publication))
            {
                return false;
            }

            lock (lifecycleGate)
            {
                if (!active || !runtime.TryPublish(publication))
                {
                    return false;
                }
                lastPublishedFrame = frame;
                hasPublishedFrame = true;
                return true;
            }
        }

        /// <summary>
        /// Authenticates a parsed broker frame before mutating presentation
        /// state or an optional bounded delay. This publishes nothing and advances no
        /// ordering watermark; the delayed owner revalidates the reconstructed
        /// frame at the actual canonical ingress boundary. Frames behind an
        /// already-published sequence or terminal Stop cannot enter the queue.
        /// </summary>
        internal bool AuthenticatesDelayedFrame(
            in ControllerFeedbackFrame frame)
        {
            lock (lifecycleGate)
            {
                return active && (!hasPublishedFrame ||
                    (!lastPublishedFrame.IsStop &&
                     frame.Sequence > lastPublishedFrame.Sequence)) &&
                    frame.HasValidInvariants() &&
                    frame.Source == source &&
                    frame.DeviceGeneration == deviceGeneration &&
                    frame.TransportGeneration == transportGeneration &&
                    frame.OwnershipEpoch == ownershipEpoch;
            }
        }

        // Canonical admission and physical delivery are different outcomes.
        // A Stop admitted before a physical retry still fences delayed Apply.
        internal bool HasPublishedTerminalStop
        {
            get
            {
                lock (lifecycleGate)
                    return hasPublishedFrame && lastPublishedFrame.IsStop;
            }
        }

        internal bool TryReadPublishedFrame(out ControllerFeedbackFrame frame)
        {
            lock (lifecycleGate)
            {
                frame = lastPublishedFrame;
                return active && hasPublishedFrame;
            }
        }

        internal ControllerFeedbackSource Source => source;

        /// <summary>
        /// Publishes one locally decoded legacy feedback state without
        /// constructing a second arbitration path. Ordering is owned here so
        /// each virtual-device session advances one exact CFBK sequence.
        /// </summary>
        internal bool TryPublish(in ControllerFeedbackActuatorState state,
            ulong nowMicroseconds, ulong timeToLiveMicroseconds) =>
            TryPublish(state, nowMicroseconds, timeToLiveMicroseconds,
                out _);

        internal bool TryPublish(in ControllerFeedbackActuatorState state,
            ulong nowMicroseconds, ulong timeToLiveMicroseconds,
            out ControllerFeedbackFrame publishedFrame)
        {
            publishedFrame = default;
            if (timeToLiveMicroseconds == 0 ||
                timeToLiveMicroseconds >
                    ControllerFeedbackFrame.MaxTimeToLiveMicroseconds)
            {
                return false;
            }

            lock (lifecycleGate)
            {
                if (!active || hasPublishedFrame &&
                        lastPublishedFrame.Sequence == ulong.MaxValue)
                {
                    return false;
                }

                ulong sequence = hasPublishedFrame ?
                    lastPublishedFrame.Sequence + 1 : 1;
                ulong timestamp = hasPublishedFrame ?
                    Math.Max(nowMicroseconds,
                        lastPublishedFrame.TimestampMicroseconds) :
                    nowMicroseconds;
                ControllerFeedbackCommand command = state.IsNeutral ?
                    ControllerFeedbackCommand.Neutral :
                    ControllerFeedbackCommand.Apply;
                if (!ControllerFeedbackFrame.TryCreate(source, command,
                        ControllerFeedbackActuators.All, state.BodyLow,
                        state.BodyHigh, state.LeftTrigger,
                        state.RightTrigger, sequence, deviceGeneration,
                        transportGeneration, ownershipEpoch, timestamp,
                        timeToLiveMicroseconds,
                        out ControllerFeedbackFrame frame) ||
                    !ControllerFeedbackPublication.TryCreate(
                        ControllerFeedbackPublicationOrigin.NativeGame,
                        frame, out ControllerFeedbackPublication publication) ||
                    !runtime.TryPublish(publication))
                {
                    return false;
                }

                lastPublishedFrame = frame;
                hasPublishedFrame = true;
                publishedFrame = frame;
                return true;
            }
        }

        /// <summary>
        /// Publishes the exact canonical terminal successor for this stream.
        /// This is used when the local VIIPER stream is retiring: it updates
        /// the NativeGame ordering watermark to Stop before the ingress is
        /// fenced, preventing a still-fresh Apply slot from being selected
        /// again after physical neutral delivery.
        /// </summary>
        internal bool TryPublishTerminalStop(ulong nowMicroseconds)
        {
            lock (lifecycleGate)
            {
                if (!active)
                {
                    return false;
                }
                if (!hasPublishedFrame)
                {
                    return true;
                }
                if (lastPublishedFrame.IsStop)
                {
                    return true;
                }
                if (lastPublishedFrame.Sequence == ulong.MaxValue)
                {
                    return false;
                }

                ulong timestamp = Math.Max(nowMicroseconds,
                    lastPublishedFrame.TimestampMicroseconds);
                if (!ControllerFeedbackFrame.TryCreate(source,
                        ControllerFeedbackCommand.Stop,
                        ControllerFeedbackActuators.All, 0, 0, 0, 0,
                        lastPublishedFrame.Sequence + 1, deviceGeneration,
                        transportGeneration, ownershipEpoch, timestamp,
                        lastPublishedFrame.TimeToLiveMicroseconds,
                        out ControllerFeedbackFrame stop) ||
                    !ControllerFeedbackPublication.TryCreate(
                        ControllerFeedbackPublicationOrigin.NativeGame, stop,
                        out ControllerFeedbackPublication publication) ||
                    !runtime.TryPublish(publication))
                {
                    return false;
                }

                lastPublishedFrame = stop;
                return true;
            }
        }

        /// <summary>
        /// Synchronously fences later broker publications. VIIPER's persona
        /// lifecycle remains responsible for publishing its ClearOutputs Stop
        /// before retirement; physical transport teardown remains the outer
        /// owner's canonical release obligation. No Stop is manufactured here.
        /// </summary>
        internal bool TryRetire()
        {
            lock (lifecycleGate)
            {
                if (!active)
                {
                    return false;
                }

                active = false;
                return true;
            }
        }
    }

    /// <summary>
    /// Compatibility surface retained for Xbox broker-focused tests and
    /// callers. The implementation delegates to the shared ingress while
    /// preserving the historical Xbox-only construction predicate.
    /// </summary>
    internal sealed class XboxOneBrokerFeedbackIngress
    {
        private readonly ControllerFeedbackIngress inner;

        private XboxOneBrokerFeedbackIngress(
            ControllerFeedbackIngress inner)
        {
            this.inner = inner;
        }

        internal static bool TryCreate(ControllerFeedbackRuntime runtime,
            ControllerFeedbackSource source, ulong deviceGeneration,
            ulong transportGeneration, ulong ownershipEpoch,
            out XboxOneBrokerFeedbackIngress ingress)
        {
            ingress = null;
            if (source is not (ControllerFeedbackSource.
                    XboxOneVirtualDevice or
                ControllerFeedbackSource.XboxSeriesVirtualDevice) ||
                !ControllerFeedbackIngress.TryCreate(runtime, source,
                    deviceGeneration, transportGeneration, ownershipEpoch,
                    out ControllerFeedbackIngress shared))
            {
                return false;
            }

            ingress = new XboxOneBrokerFeedbackIngress(shared);
            return true;
        }

        internal bool TryPublish(ReadOnlySpan<byte> wire) =>
            inner.TryPublish(wire);

        internal bool TryPublishTerminalStop(ulong nowMicroseconds) =>
            inner.TryPublishTerminalStop(nowMicroseconds);

        internal bool TryRetire() => inner.TryRetire();
    }
}
