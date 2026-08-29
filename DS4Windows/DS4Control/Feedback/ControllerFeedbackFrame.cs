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
using System.Buffers.Binary;
using System.Diagnostics;

namespace DS4Windows
{
    /// <summary>
    /// Identifies the virtual controller contract that authored feedback.
    /// This is source identity, not the physical controller selected as the
    /// eventual feedback target.
    /// </summary>
    internal enum ControllerFeedbackSource : byte
    {
        Invalid = 0,
        XboxOneVirtualDevice = 1,
        XboxSeriesVirtualDevice = 2,
        Xbox360VirtualDevice = 3,
        DualSenseVirtualDevice = 4,
        DualSenseEdgeVirtualDevice = 5,
        DualShock4VirtualDevice = 6,
    }

    /// <summary>
    /// Gives zero actuator values unambiguous lifecycle meaning.
    /// </summary>
    internal enum ControllerFeedbackCommand : byte
    {
        Invalid = 0,

        /// <summary>Apply one or more non-zero actuator values.</summary>
        Apply = 1,

        /// <summary>
        /// Set every actuator to zero while retaining the current
        /// feedback ownership lease.
        /// </summary>
        Neutral = 2,

        /// <summary>
        /// Set every actuator to zero and retire the current
        /// feedback ownership lease.
        /// </summary>
        Stop = 3,
    }

    /// <summary>
    /// Canonical actuator channel names. Version one requires All in every
    /// valid frame; zero values represent inactive or unsupported channels.
    /// </summary>
    [Flags]
    internal enum ControllerFeedbackActuators : byte
    {
        None = 0,
        BodyLow = 0x01,
        BodyHigh = 0x02,
        LeftTrigger = 0x04,
        RightTrigger = 0x08,
        All = 0x0F,
    }

    /// <summary>
    /// State transition returned by a feedback-mailbox claim. Release is an
    /// obligation to locally zero every physical actuator, not simply the
    /// absence of a fresh frame.
    /// </summary>
    internal enum ControllerFeedbackClaimDisposition : byte
    {
        None = 0,
        Frame = 1,
        Release = 2,
    }

    /// <summary>
    /// Independently remembers frame application and expiry-release delivery.
    /// The zero value is ready for use.
    /// </summary>
    internal struct ControllerFeedbackClaimCursor
    {
        internal ulong Revision;
        internal ulong ReleaseRevision;
    }

    /// <summary>
    /// Normative cross-process clock for CFBK v1. On Windows,
    /// Stopwatch.GetTimestamp is QueryPerformanceCounter; conversion by the
    /// system QueryPerformanceFrequency gives a host-wide value with a common
    /// origin and unit in every process. Process-relative elapsed-time origins
    /// are not compatible with this clock domain.
    /// </summary>
    internal static class ControllerFeedbackClock
    {
        internal const string Domain = "windows-qpc-host-v1";
        private const ulong MicrosecondsPerSecond = 1_000_000;

        internal static bool TryGetTimestampMicroseconds(
            out ulong timestampMicroseconds)
        {
            long timestamp = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            if (timestamp < 0 || frequency <= 0)
            {
                timestampMicroseconds = 0;
                return false;
            }

            return TryConvertQpcTicks((ulong)timestamp, (ulong)frequency,
                out timestampMicroseconds);
        }

        internal static bool TryConvertQpcTicks(ulong counter,
            ulong frequency, out ulong timestampMicroseconds)
        {
            if (frequency == 0)
            {
                timestampMicroseconds = 0;
                return false;
            }

            UInt128 scaled = (UInt128)counter * MicrosecondsPerSecond;
            UInt128 converted = scaled / frequency;
            if (converted > ulong.MaxValue)
            {
                timestampMicroseconds = 0;
                return false;
            }

            timestampMicroseconds = (ulong)converted;
            return true;
        }
    }

    /// <summary>
    /// Version-one transport-neutral controller feedback snapshot. The four
    /// amplitudes use the Xbox actuator meaning and normalized unsigned
    /// 0..65535 range. Protocol adapters scale only at their device boundary.
    /// Every lifecycle fence is copied with the values, preventing a consumer
    /// from combining actuator state with a different device, transport, or
    /// ownership generation.
    ///
    /// Timestamp and TTL are unsigned monotonic microseconds. On Windows they
    /// can be derived from QPC/Stopwatch; the standardized unit means a framed
    /// transport does not depend on either process's Stopwatch frequency.
    /// A receiver must reject an expired frame rather than replaying stale
    /// actuator state. Stop is freshness-bounded too: generation and epoch
    /// fences remain authoritative across lifecycle replacement.
    /// </summary>
    internal readonly struct ControllerFeedbackFrame :
        IEquatable<ControllerFeedbackFrame>
    {
        // Little-endian bytes spell "CFBK". The independent version field is
        // authoritative; the magic only rejects a packet for another lane.
        private const uint WireMagic = 0x4B424643;
        internal const ushort CurrentVersion = 1;
        internal const int SerializedLength = 72;
        internal const ulong MaxFutureSkewMicroseconds = 5_000;
        internal const ulong MaxTimeToLiveMicroseconds = 250_000;

        private ControllerFeedbackFrame(ControllerFeedbackSource source,
            ControllerFeedbackCommand command,
            ControllerFeedbackActuators actuators, ushort bodyLow,
            ushort bodyHigh, ushort leftTrigger, ushort rightTrigger,
            ulong sequence, ulong deviceGeneration,
            ulong transportGeneration, ulong ownershipEpoch,
            ulong timestampMicroseconds, ulong timeToLiveMicroseconds)
        {
            Version = CurrentVersion;
            Source = source;
            Command = command;
            Actuators = actuators;
            BodyLow = bodyLow;
            BodyHigh = bodyHigh;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
            Sequence = sequence;
            DeviceGeneration = deviceGeneration;
            TransportGeneration = transportGeneration;
            OwnershipEpoch = ownershipEpoch;
            TimestampMicroseconds = timestampMicroseconds;
            TimeToLiveMicroseconds = timeToLiveMicroseconds;
        }

        internal ushort Version { get; }
        internal ControllerFeedbackSource Source { get; }
        internal ControllerFeedbackCommand Command { get; }
        internal ControllerFeedbackActuators Actuators { get; }
        internal ushort BodyLow { get; }
        internal ushort BodyHigh { get; }
        internal ushort LeftTrigger { get; }
        internal ushort RightTrigger { get; }
        internal ulong Sequence { get; }
        internal ulong DeviceGeneration { get; }
        internal ulong TransportGeneration { get; }
        internal ulong OwnershipEpoch { get; }
        internal ulong TimestampMicroseconds { get; }
        internal ulong TimeToLiveMicroseconds { get; }

        internal bool IsNeutral => Command == ControllerFeedbackCommand.Neutral;
        internal bool IsStop => Command == ControllerFeedbackCommand.Stop;

        internal static bool TryCreate(ControllerFeedbackSource source,
            ControllerFeedbackCommand command,
            ControllerFeedbackActuators actuators, ushort bodyLow,
            ushort bodyHigh, ushort leftTrigger, ushort rightTrigger,
            ulong sequence, ulong deviceGeneration,
            ulong transportGeneration, ulong ownershipEpoch,
            ulong timestampMicroseconds, ulong timeToLiveMicroseconds,
            out ControllerFeedbackFrame frame)
        {
            frame = new ControllerFeedbackFrame(source, command, actuators,
                bodyLow, bodyHigh, leftTrigger, rightTrigger, sequence,
                deviceGeneration, transportGeneration, ownershipEpoch,
                timestampMicroseconds, timeToLiveMicroseconds);
            if (frame.HasValidInvariants())
            {
                return true;
            }

            frame = default;
            return false;
        }

        internal bool HasValidInvariants()
        {
            if (Version != CurrentVersion ||
                Source < ControllerFeedbackSource.XboxOneVirtualDevice ||
                Source > ControllerFeedbackSource.DualShock4VirtualDevice ||
                Command < ControllerFeedbackCommand.Apply ||
                Command > ControllerFeedbackCommand.Stop ||
                Actuators != ControllerFeedbackActuators.All ||
                Sequence == 0 || DeviceGeneration == 0 ||
                TransportGeneration == 0 || OwnershipEpoch == 0 ||
                TimeToLiveMicroseconds == 0 ||
                TimeToLiveMicroseconds > MaxTimeToLiveMicroseconds)
            {
                return false;
            }

            if (((Actuators & ControllerFeedbackActuators.BodyLow) == 0 &&
                    BodyLow != 0) ||
                ((Actuators & ControllerFeedbackActuators.BodyHigh) == 0 &&
                    BodyHigh != 0) ||
                ((Actuators & ControllerFeedbackActuators.LeftTrigger) == 0 &&
                    LeftTrigger != 0) ||
                ((Actuators & ControllerFeedbackActuators.RightTrigger) == 0 &&
                    RightTrigger != 0))
            {
                return false;
            }

            bool hasAmplitude = BodyLow != 0 || BodyHigh != 0 ||
                LeftTrigger != 0 || RightTrigger != 0;
            return Command == ControllerFeedbackCommand.Apply ?
                hasAmplitude : !hasAmplitude;
        }

        /// <summary>
        /// A bounded future timestamp tolerates only producer/consumer sample
        /// races. Farther-future values fail closed rather than remaining live.
        /// </summary>
        internal bool IsFreshAt(ulong nowMicroseconds)
        {
            if (TimestampMicroseconds > nowMicroseconds)
            {
                return TimestampMicroseconds - nowMicroseconds <=
                    MaxFutureSkewMicroseconds;
            }

            return nowMicroseconds - TimestampMicroseconds <
                TimeToLiveMicroseconds;
        }

        internal bool IsExpiredAt(ulong nowMicroseconds)
        {
            return !IsFreshAt(nowMicroseconds);
        }

        internal bool TryWriteTo(Span<byte> destination)
        {
            if (destination.Length < SerializedLength ||
                !HasValidInvariants())
            {
                return false;
            }

            Span<byte> packet = destination[..SerializedLength];
            packet.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(packet, WireMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[6..],
                SerializedLength);
            packet[8] = (byte)Source;
            packet[9] = (byte)Command;
            packet[10] = (byte)Actuators;
            BinaryPrimitives.WriteUInt16LittleEndian(packet[12..], BodyLow);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[14..], BodyHigh);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[16..],
                LeftTrigger);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[18..],
                RightTrigger);
            BinaryPrimitives.WriteUInt64LittleEndian(packet[24..], Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(packet[32..],
                DeviceGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(packet[40..],
                TransportGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(packet[48..],
                OwnershipEpoch);
            BinaryPrimitives.WriteUInt64LittleEndian(packet[56..],
                TimestampMicroseconds);
            BinaryPrimitives.WriteUInt64LittleEndian(packet[64..],
                TimeToLiveMicroseconds);
            return true;
        }

        internal static bool TryReadFrom(ReadOnlySpan<byte> source,
            out ControllerFeedbackFrame frame)
        {
            frame = default;
            if (source.Length != SerializedLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(source) != WireMagic ||
                BinaryPrimitives.ReadUInt16LittleEndian(source[4..]) !=
                    CurrentVersion ||
                BinaryPrimitives.ReadUInt16LittleEndian(source[6..]) !=
                    SerializedLength ||
                source[11] != 0 || source[20] != 0 || source[21] != 0 ||
                source[22] != 0 || source[23] != 0)
            {
                return false;
            }

            return TryCreate((ControllerFeedbackSource)source[8],
                (ControllerFeedbackCommand)source[9],
                (ControllerFeedbackActuators)source[10],
                BinaryPrimitives.ReadUInt16LittleEndian(source[12..]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[14..]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[16..]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[18..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[48..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[56..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[64..]),
                out frame);
        }

        public bool Equals(ControllerFeedbackFrame other)
        {
            return Version == other.Version && Source == other.Source &&
                Command == other.Command && Actuators == other.Actuators &&
                BodyLow == other.BodyLow && BodyHigh == other.BodyHigh &&
                LeftTrigger == other.LeftTrigger &&
                RightTrigger == other.RightTrigger &&
                Sequence == other.Sequence &&
                DeviceGeneration == other.DeviceGeneration &&
                TransportGeneration == other.TransportGeneration &&
                OwnershipEpoch == other.OwnershipEpoch &&
                TimestampMicroseconds == other.TimestampMicroseconds &&
                TimeToLiveMicroseconds == other.TimeToLiveMicroseconds;
        }

        public override bool Equals(object obj) =>
            obj is ControllerFeedbackFrame other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Version);
            hash.Add(Source);
            hash.Add(Command);
            hash.Add(Actuators);
            hash.Add(BodyLow);
            hash.Add(BodyHigh);
            hash.Add(LeftTrigger);
            hash.Add(RightTrigger);
            hash.Add(Sequence);
            hash.Add(DeviceGeneration);
            hash.Add(TransportGeneration);
            hash.Add(OwnershipEpoch);
            hash.Add(TimestampMicroseconds);
            hash.Add(TimeToLiveMicroseconds);
            return hash.ToHashCode();
        }

        public static bool operator ==(ControllerFeedbackFrame left,
            ControllerFeedbackFrame right) => left.Equals(right);

        public static bool operator !=(ControllerFeedbackFrame left,
            ControllerFeedbackFrame right) => !left.Equals(right);
    }

    /// <summary>
    /// One replaceable canonical-feedback snapshot. Publication copies the
    /// complete value under one short monitor and never performs translation,
    /// callbacks, waits, logging, or I/O. Ordering is lexicographic by device,
    /// transport, ownership, then source-local sequence. A source change in an
    /// unchanged ownership epoch is rejected.
    /// </summary>
    internal sealed class ControllerFeedbackMailbox
    {
        private readonly object syncRoot = new();
        private ControllerFeedbackFrame latest;
        private ulong revision;
        private bool hasValue;

        internal bool TryPublish(in ControllerFeedbackFrame frame)
        {
            if (!frame.HasValidInvariants())
            {
                return false;
            }

            lock (syncRoot)
            {
                if (hasValue && !IsNewer(frame, latest))
                {
                    return false;
                }

                latest = frame;
                hasValue = true;
                revision++;
                return true;
            }
        }

        internal bool TryReadLatest(out ControllerFeedbackFrame frame,
            out ulong currentRevision)
        {
            lock (syncRoot)
            {
                frame = latest;
                currentRevision = revision;
                return hasValue;
            }
        }

        internal bool TryReadFresh(ulong nowMicroseconds,
            out ControllerFeedbackFrame frame, out ulong currentRevision)
        {
            lock (syncRoot)
            {
                frame = latest;
                currentRevision = revision;
                if (hasValue && latest.IsFreshAt(nowMicroseconds))
                {
                    return true;
                }

                frame = default;
                return false;
            }
        }

        /// <summary>
        /// Returns each fresh revision once as Frame. Once that revision
        /// expires or is implausibly future-dated, returns Release exactly
        /// once—even if Frame was already returned while it was fresh.
        /// Physical translation and release I/O happen after this lock.
        /// </summary>
        internal ControllerFeedbackClaimDisposition Claim(
            ulong nowMicroseconds, ref ControllerFeedbackClaimCursor cursor,
            out ControllerFeedbackFrame frame)
        {
            lock (syncRoot)
            {
                if (!hasValue)
                {
                    frame = default;
                    return ControllerFeedbackClaimDisposition.None;
                }

                if (latest.IsFreshAt(nowMicroseconds))
                {
                    if (cursor.Revision == revision)
                    {
                        frame = default;
                        return ControllerFeedbackClaimDisposition.None;
                    }

                    cursor.Revision = revision;
                    frame = latest;
                    return ControllerFeedbackClaimDisposition.Frame;
                }

                if (cursor.ReleaseRevision == revision)
                {
                    frame = default;
                    return ControllerFeedbackClaimDisposition.None;
                }

                cursor.Revision = revision;
                cursor.ReleaseRevision = revision;
                frame = default;
                return ControllerFeedbackClaimDisposition.Release;
            }
        }

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

            // Stop is terminal for an ownership epoch. A later source must
            // acquire a new epoch instead of resurrecting a retired lease by
            // incrementing only its local packet sequence.
            return current.Command != ControllerFeedbackCommand.Stop &&
                candidate.Source == current.Source &&
                candidate.Sequence > current.Sequence;
        }
    }
}
