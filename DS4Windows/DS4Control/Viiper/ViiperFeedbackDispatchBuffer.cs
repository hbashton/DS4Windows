/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Preallocated hand-off between VIIPER's framed TCP reader and the
    /// potentially blocking physical-controller feedback paths. Speaker PCM
    /// is lossless while capacity remains. Time-bearing native feedback uses
    /// an ordered FIFO, while ordinary controller state can intentionally
    /// coalesce because only the newest rumble/light/trigger state matters.
    /// </summary>
    internal sealed class ViiperFeedbackDispatchBuffer
    {
        private readonly object syncRoot = new object();
        private readonly byte[][] speakerSlots;
        private readonly int[] speakerLengths;
        private readonly long[] speakerGenerations;
        private readonly int speakerSlotLength;
        private readonly byte[] controlSlot;
        private readonly byte[][] orderedControlSlots;
        private readonly int[] orderedControlLengths;
        private readonly long[] orderedControlGenerations;
        private readonly int[] orderedControlDeviceIndexes;
        private int speakerReadIndex;
        private int speakerWriteIndex;
        private int speakerCount;
        private int controlLength;
        private long controlGeneration;
        private int controlDeviceIndex;
        private bool controlPending;
        private int orderedControlReadIndex;
        private int orderedControlWriteIndex;
        private int orderedControlCount;
        private long speakerEnqueued;
        private long speakerDequeued;
        private long speakerDropped;
        private long speakerHighWater;
        private long controlEnqueued;
        private long controlDequeued;
        private long controlCoalesced;
        private long controlDropped;
        private long orderedControlEnqueued;
        private long orderedControlDequeued;
        private long orderedControlDropped;
        private long orderedControlHighWater;

        internal ViiperFeedbackDispatchBuffer(int speakerCapacity,
            int speakerSlotLength, int controlSlotLength,
            int orderedControlCapacity = 0)
        {
            if (speakerCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(speakerCapacity));
            }

            if (speakerSlotLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(speakerSlotLength));
            }

            if (controlSlotLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlSlotLength));
            }

            this.speakerSlotLength = speakerSlotLength;
            speakerSlots = new byte[speakerCapacity][];
            speakerLengths = new int[speakerCapacity];
            speakerGenerations = new long[speakerCapacity];
            for (int index = 0; index < speakerSlots.Length; index++)
            {
                speakerSlots[index] = new byte[speakerSlotLength];
            }

            controlSlot = new byte[controlSlotLength];
            if (orderedControlCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(orderedControlCapacity));
            }

            orderedControlSlots = new byte[orderedControlCapacity][];
            orderedControlLengths = new int[orderedControlCapacity];
            orderedControlGenerations = new long[orderedControlCapacity];
            orderedControlDeviceIndexes = new int[orderedControlCapacity];
            for (int index = 0; index < orderedControlSlots.Length; index++)
            {
                orderedControlSlots[index] = new byte[controlSlotLength];
                orderedControlDeviceIndexes[index] = -1;
            }
        }

        internal int SpeakerCapacity => speakerSlots.Length;
        internal int SpeakerSlotLength => speakerSlotLength;
        internal int ControlSlotLength => controlSlot.Length;
        internal int PendingSpeakerCount
        {
            get
            {
                lock (syncRoot)
                {
                    return speakerCount;
                }
            }
        }
        internal int PendingOrderedControlCount
        {
            get
            {
                lock (syncRoot)
                {
                    return orderedControlCount;
                }
            }
        }

        internal long SpeakerEnqueued => Interlocked.Read(ref speakerEnqueued);
        internal long SpeakerDequeued => Interlocked.Read(ref speakerDequeued);
        internal long SpeakerDropped => Interlocked.Read(ref speakerDropped);
        internal long SpeakerHighWater => Interlocked.Read(ref speakerHighWater);
        internal long ControlEnqueued => Interlocked.Read(ref controlEnqueued);
        internal long ControlDequeued => Interlocked.Read(ref controlDequeued);
        internal long ControlCoalesced => Interlocked.Read(ref controlCoalesced);
        internal long ControlDropped => Interlocked.Read(ref controlDropped);
        internal long OrderedControlEnqueued =>
            Interlocked.Read(ref orderedControlEnqueued);
        internal long OrderedControlDequeued =>
            Interlocked.Read(ref orderedControlDequeued);
        internal long OrderedControlDropped =>
            Interlocked.Read(ref orderedControlDropped);
        internal long OrderedControlHighWater =>
            Interlocked.Read(ref orderedControlHighWater);

        internal bool TryEnqueueSpeaker(byte[] source, int length,
            long generation)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (length <= 0 || length > source.Length ||
                length > speakerSlotLength)
            {
                Interlocked.Increment(ref speakerDropped);
                return false;
            }

            lock (syncRoot)
            {
                if (speakerCount == speakerSlots.Length)
                {
                    // Live audio must remain bounded in time. Retaining a full
                    // stale FIFO while rejecting every new frame can leave the
                    // controller permanently seconds behind after a stall.
                    speakerLengths[speakerReadIndex] = 0;
                    speakerGenerations[speakerReadIndex] = 0;
                    speakerReadIndex = (speakerReadIndex + 1) %
                        speakerSlots.Length;
                    speakerCount--;
                    Interlocked.Increment(ref speakerDropped);
                }

                Buffer.BlockCopy(source, 0, speakerSlots[speakerWriteIndex],
                    0, length);
                speakerLengths[speakerWriteIndex] = length;
                speakerGenerations[speakerWriteIndex] = generation;
                speakerWriteIndex = (speakerWriteIndex + 1) %
                    speakerSlots.Length;
                speakerCount++;
                Interlocked.Increment(ref speakerEnqueued);
                RecordMaximum(ref speakerHighWater, speakerCount);
                return true;
            }
        }

        internal bool TryDequeueSpeaker(byte[] destination, out int length,
            out long generation)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            lock (syncRoot)
            {
                if (speakerCount == 0)
                {
                    length = 0;
                    generation = 0;
                    return false;
                }

                length = speakerLengths[speakerReadIndex];
                if (destination.Length < length)
                {
                    throw new ArgumentException(
                        "The speaker dispatch destination is too small.",
                        nameof(destination));
                }

                Buffer.BlockCopy(speakerSlots[speakerReadIndex], 0,
                    destination, 0, length);
                generation = speakerGenerations[speakerReadIndex];
                speakerLengths[speakerReadIndex] = 0;
                speakerGenerations[speakerReadIndex] = 0;
                speakerReadIndex = (speakerReadIndex + 1) %
                    speakerSlots.Length;
                speakerCount--;
                Interlocked.Increment(ref speakerDequeued);
                return true;
            }
        }

        internal bool QueueControl(byte[] source, int length,
            long generation, int deviceIndex)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (length <= 0 || length > source.Length ||
                length > controlSlot.Length)
            {
                Interlocked.Increment(ref controlDropped);
                return false;
            }

            lock (syncRoot)
            {
                if (controlPending)
                {
                    Interlocked.Increment(ref controlCoalesced);
                }

                Buffer.BlockCopy(source, 0, controlSlot, 0, length);
                controlLength = length;
                controlGeneration = generation;
                controlDeviceIndex = deviceIndex;
                controlPending = true;
                Interlocked.Increment(ref controlEnqueued);
                return true;
            }
        }

        internal bool TryEnqueueOrderedControl(byte[] source, int length,
            long generation, int deviceIndex)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (orderedControlSlots.Length == 0 || length <= 0 ||
                length > source.Length || length > controlSlot.Length)
            {
                Interlocked.Increment(ref orderedControlDropped);
                return false;
            }

            lock (syncRoot)
            {
                if (orderedControlCount == orderedControlSlots.Length)
                {
                    // Advanced haptics are also real-time data. If the physical
                    // transport cannot keep up, keep the newest bounded window
                    // instead of replaying stale actuator samples indefinitely.
                    orderedControlLengths[orderedControlReadIndex] = 0;
                    orderedControlGenerations[orderedControlReadIndex] = 0;
                    orderedControlDeviceIndexes[orderedControlReadIndex] = -1;
                    orderedControlReadIndex =
                        (orderedControlReadIndex + 1) %
                        orderedControlSlots.Length;
                    orderedControlCount--;
                    Interlocked.Increment(ref orderedControlDropped);
                }

                Buffer.BlockCopy(source, 0,
                    orderedControlSlots[orderedControlWriteIndex], 0, length);
                orderedControlLengths[orderedControlWriteIndex] = length;
                orderedControlGenerations[orderedControlWriteIndex] =
                    generation;
                orderedControlDeviceIndexes[orderedControlWriteIndex] =
                    deviceIndex;
                orderedControlWriteIndex = (orderedControlWriteIndex + 1) %
                    orderedControlSlots.Length;
                orderedControlCount++;
                Interlocked.Increment(ref orderedControlEnqueued);
                RecordMaximum(ref orderedControlHighWater,
                    orderedControlCount);
                return true;
            }
        }

        internal bool TryDequeueOrderedControl(byte[] destination,
            out int length, out long generation, out int deviceIndex)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            lock (syncRoot)
            {
                if (orderedControlCount == 0)
                {
                    length = 0;
                    generation = 0;
                    deviceIndex = -1;
                    return false;
                }

                length = orderedControlLengths[orderedControlReadIndex];
                if (destination.Length < length)
                {
                    throw new ArgumentException(
                        "The ordered control dispatch destination is too small.",
                        nameof(destination));
                }

                Buffer.BlockCopy(
                    orderedControlSlots[orderedControlReadIndex], 0,
                    destination, 0, length);
                generation =
                    orderedControlGenerations[orderedControlReadIndex];
                deviceIndex =
                    orderedControlDeviceIndexes[orderedControlReadIndex];
                orderedControlLengths[orderedControlReadIndex] = 0;
                orderedControlGenerations[orderedControlReadIndex] = 0;
                orderedControlDeviceIndexes[orderedControlReadIndex] = -1;
                orderedControlReadIndex = (orderedControlReadIndex + 1) %
                    orderedControlSlots.Length;
                orderedControlCount--;
                Interlocked.Increment(ref orderedControlDequeued);
                return true;
            }
        }

        internal bool TryTakeControl(byte[] destination, out int length,
            out long generation, out int deviceIndex)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            lock (syncRoot)
            {
                if (!controlPending)
                {
                    length = 0;
                    generation = 0;
                    deviceIndex = -1;
                    return false;
                }

                length = controlLength;
                if (destination.Length < length)
                {
                    throw new ArgumentException(
                        "The control dispatch destination is too small.",
                        nameof(destination));
                }

                Buffer.BlockCopy(controlSlot, 0, destination, 0, length);
                generation = controlGeneration;
                deviceIndex = controlDeviceIndex;
                controlLength = 0;
                controlGeneration = 0;
                controlDeviceIndex = -1;
                controlPending = false;
                if (orderedControlLengths.Length > 0)
                {
                    Array.Clear(orderedControlLengths, 0,
                        orderedControlLengths.Length);
                    Array.Clear(orderedControlGenerations, 0,
                        orderedControlGenerations.Length);
                    Array.Fill(orderedControlDeviceIndexes, -1);
                }
                orderedControlReadIndex = 0;
                orderedControlWriteIndex = 0;
                orderedControlCount = 0;
                Interlocked.Increment(ref controlDequeued);
                return true;
            }
        }

        internal void ClearPending()
        {
            lock (syncRoot)
            {
                Array.Clear(speakerLengths, 0, speakerLengths.Length);
                Array.Clear(speakerGenerations, 0,
                    speakerGenerations.Length);
                speakerReadIndex = 0;
                speakerWriteIndex = 0;
                speakerCount = 0;
                controlLength = 0;
                controlGeneration = 0;
                controlDeviceIndex = -1;
                controlPending = false;
                Array.Clear(orderedControlLengths, 0,
                    orderedControlLengths.Length);
                Array.Clear(orderedControlGenerations, 0,
                    orderedControlGenerations.Length);
                Array.Fill(orderedControlDeviceIndexes, -1);
                orderedControlReadIndex = 0;
                orderedControlWriteIndex = 0;
                orderedControlCount = 0;
            }
        }

        internal void Reset()
        {
            ClearPending();
            Interlocked.Exchange(ref speakerEnqueued, 0);
            Interlocked.Exchange(ref speakerDequeued, 0);
            Interlocked.Exchange(ref speakerDropped, 0);
            Interlocked.Exchange(ref speakerHighWater, 0);
            Interlocked.Exchange(ref controlEnqueued, 0);
            Interlocked.Exchange(ref controlDequeued, 0);
            Interlocked.Exchange(ref controlCoalesced, 0);
            Interlocked.Exchange(ref controlDropped, 0);
            Interlocked.Exchange(ref orderedControlEnqueued, 0);
            Interlocked.Exchange(ref orderedControlDequeued, 0);
            Interlocked.Exchange(ref orderedControlDropped, 0);
            Interlocked.Exchange(ref orderedControlHighWater, 0);
        }

        private static void RecordMaximum(ref long target, long candidate)
        {
            long current = Interlocked.Read(ref target);
            while (candidate > current)
            {
                long observed = Interlocked.CompareExchange(ref target,
                    candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
