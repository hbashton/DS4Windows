using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// One lossless single-producer/single-consumer ring shared by the main
    /// process and the isolated physical HID writer. A slot remains owned by
    /// the consumer until the corresponding HID write is accepted, so retrying
    /// a saturated write cannot duplicate, replace, or reorder tactile media.
    /// </summary>
    internal sealed class DualSenseRealtimeHapticsSharedRing : IDisposable
    {
        internal const int Version = 1;
        internal const int DefaultCapacity = 64;
        internal const int PayloadLength = 64;

        private const int Magic = 0x48523444; // "D4RH"
        private const long MagicOffset = 0;
        private const long VersionOffset = 4;
        private const long CapacityOffset = 8;
        private const long PayloadLengthOffset = 12;
        private const long WriteSequenceOffset = 16;
        private const long ReadSequenceOffset = 24;
        private const int HeaderLength = 64;

        private const int SlotPublishedSequenceOffset = 0;
        private const int SlotEnqueuedQpcOffset = 8;
        private const int SlotExpiryQpcOffset = 16;
        private const int SlotGenerationOffset = 24;
        private const int SlotPayloadOffset = 32;
        private const int SlotStride = SlotPayloadOffset + PayloadLength;

        private readonly MemoryMappedFile map;
        private readonly MemoryMappedViewAccessor view;
        private readonly EventWaitHandle spaceAvailable;
        private readonly EventWaitHandle stopRequested;
        private readonly WaitHandle[] producerWaitHandles;
        private readonly object producerLock = new object();
        private readonly object consumerLock = new object();
        private readonly byte[] preparedPayload = new byte[PayloadLength];
        private readonly int capacity;
        private bool prepared;
        private long preparedSequence;
        private bool hasReceivedGeneration;
        private int acceptedGeneration = 1;
        private int maximumQueueDepth;
        private long maximumQueueAgeTicks;
        private long presentedCount;
        private int disposed;

        private DualSenseRealtimeHapticsSharedRing(string mapName,
            string spaceAvailableName, string stopRequestedName,
            bool create, int capacity)
        {
            if (string.IsNullOrWhiteSpace(mapName))
            {
                throw new ArgumentException("A shared-ring map name is required.",
                    nameof(mapName));
            }
            if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity),
                    "Shared-ring capacity must be a power of two.");
            }

            MapName = mapName;
            SpaceAvailableName = spaceAvailableName;
            StopRequestedName = stopRequestedName;
            this.capacity = capacity;
            long mapLength = HeaderLength + (long)capacity * SlotStride;

            if (create)
            {
                map = MemoryMappedFile.CreateNew(mapName, mapLength,
                    MemoryMappedFileAccess.ReadWrite);
                spaceAvailable = new EventWaitHandle(false,
                    EventResetMode.AutoReset, spaceAvailableName);
                stopRequested = new EventWaitHandle(false,
                    EventResetMode.ManualReset, stopRequestedName);
            }
            else
            {
                map = MemoryMappedFile.OpenExisting(mapName,
                    MemoryMappedFileRights.ReadWrite);
                spaceAvailable = EventWaitHandle.OpenExisting(
                    spaceAvailableName);
                stopRequested = EventWaitHandle.OpenExisting(
                    stopRequestedName);
            }

            view = map.CreateViewAccessor(0, mapLength,
                MemoryMappedFileAccess.ReadWrite);
            producerWaitHandles = new WaitHandle[]
            {
                spaceAvailable,
                stopRequested,
            };

            if (create)
            {
                view.Write(MagicOffset, Magic);
                view.Write(VersionOffset, Version);
                view.Write(CapacityOffset, capacity);
                view.Write(PayloadLengthOffset, PayloadLength);
                view.Write(WriteSequenceOffset, 0L);
                view.Write(ReadSequenceOffset, 0L);
                view.Flush();
            }
            else
            {
                ValidateHeader(capacity);
            }
        }

        internal string MapName { get; }
        internal string SpaceAvailableName { get; }
        internal string StopRequestedName { get; }
        internal int Capacity => capacity;
        internal int Count
        {
            get
            {
                long depth = Math.Max(0,
                    view.ReadInt64(WriteSequenceOffset) -
                    view.ReadInt64(ReadSequenceOffset));
                return (int)Math.Min(capacity, depth);
            }
        }
        internal bool HasPreparedGeneration => prepared;
        internal int MaximumQueueDepth => maximumQueueDepth;
        internal long MaximumQueueAgeTicks => maximumQueueAgeTicks;
        internal long PresentedCount => presentedCount;

        internal static DualSenseRealtimeHapticsSharedRing CreateOwner(
            string namePrefix, int capacity = DefaultCapacity)
        {
            return new DualSenseRealtimeHapticsSharedRing(
                namePrefix + ".RealtimeHaptics",
                namePrefix + ".RealtimeHaptics.Space",
                namePrefix + ".RealtimeHaptics.Stop",
                create: true, capacity);
        }

        internal static DualSenseRealtimeHapticsSharedRing OpenConsumer(
            string mapName, string spaceAvailableName,
            string stopRequestedName, int capacity)
        {
            return new DualSenseRealtimeHapticsSharedRing(mapName,
                spaceAvailableName, stopRequestedName, create: false,
                capacity);
        }

        internal bool Publish(byte[] source, int offset, int generation,
            long expiryQpc, long enqueuedAtQpc)
        {
            if (source == null || offset < 0 ||
                offset + PayloadLength > source.Length)
            {
                throw new ArgumentException(
                    "Realtime haptics source does not contain one complete generation.",
                    nameof(source));
            }

            while (Volatile.Read(ref disposed) == 0 &&
                !stopRequested.WaitOne(0))
            {
                lock (producerLock)
                {
                    long writeSequence = view.ReadInt64(WriteSequenceOffset);
                    long readSequence = view.ReadInt64(ReadSequenceOffset);
                    if (writeSequence - readSequence < capacity)
                    {
                        int slotIndex = (int)(writeSequence & (capacity - 1));
                        long slotOffset = HeaderLength +
                            (long)slotIndex * SlotStride;
                        view.Write(slotOffset + SlotEnqueuedQpcOffset,
                            enqueuedAtQpc);
                        view.Write(slotOffset + SlotExpiryQpcOffset,
                            expiryQpc);
                        view.Write(slotOffset + SlotGenerationOffset,
                            generation);
                        view.WriteArray(slotOffset + SlotPayloadOffset,
                            source, offset, PayloadLength);
                        Thread.MemoryBarrier();
                        view.Write(slotOffset + SlotPublishedSequenceOffset,
                            writeSequence + 1);
                        Thread.MemoryBarrier();
                        view.Write(WriteSequenceOffset, writeSequence + 1);
                        return true;
                    }
                }

                // Backpressure is lossless. A lifecycle stop is the only
                // condition that aborts publication; no full-ring frame is
                // replaced or retired for being late.
                if (WaitHandle.WaitAny(producerWaitHandles, 20) == 1)
                {
                    return false;
                }
            }

            return false;
        }

        internal bool PrepareForPresentation(byte[] report, long nowQpc)
        {
            if (report == null ||
                DualSenseBluetoothAudioPacer.RealtimeHapticsDataOffset +
                    PayloadLength > report.Length)
            {
                throw new ArgumentException(
                    "Physical report cannot carry one realtime haptics generation.",
                    nameof(report));
            }

            lock (consumerLock)
            {
                if (prepared)
                {
                    ApplyPrepared(report);
                    return true;
                }

                while (true)
                {
                    long readSequence = view.ReadInt64(ReadSequenceOffset);
                    long writeSequence = view.ReadInt64(WriteSequenceOffset);
                    int depth = (int)Math.Min(capacity,
                        Math.Max(0, writeSequence - readSequence));
                    if (depth > maximumQueueDepth)
                    {
                        maximumQueueDepth = depth;
                    }
                    if (readSequence >= writeSequence)
                    {
                        if (hasReceivedGeneration)
                        {
                            Silence(report);
                        }
                        return false;
                    }

                    int slotIndex = (int)(readSequence & (capacity - 1));
                    long slotOffset = HeaderLength +
                        (long)slotIndex * SlotStride;
                    Thread.MemoryBarrier();
                    if (view.ReadInt64(slotOffset +
                            SlotPublishedSequenceOffset) != readSequence + 1)
                    {
                        return false;
                    }

                    int generation = view.ReadInt32(slotOffset +
                        SlotGenerationOffset);
                    int generationDelta = unchecked(generation -
                        acceptedGeneration);
                    if (generationDelta > 0)
                    {
                        // A lifecycle command for this generation is already
                        // in the ordered pipe but has not been applied yet.
                        return false;
                    }
                    if (generationDelta < 0)
                    {
                        AdvanceRead(readSequence);
                        continue;
                    }

                    view.ReadArray(slotOffset + SlotPayloadOffset,
                        preparedPayload, 0, PayloadLength);
                    long queuedAt = view.ReadInt64(slotOffset +
                        SlotEnqueuedQpcOffset);
                    if (queuedAt > 0)
                    {
                        maximumQueueAgeTicks = Math.Max(
                            maximumQueueAgeTicks,
                            Math.Max(0, nowQpc - queuedAt));
                    }
                    preparedSequence = readSequence;
                    prepared = true;
                    hasReceivedGeneration = true;
                    ApplyPrepared(report);
                    return true;
                }
            }
        }

        internal void CommitPrepared()
        {
            lock (consumerLock)
            {
                if (!prepared)
                {
                    return;
                }

                AdvanceRead(preparedSequence);
                Array.Clear(preparedPayload, 0, preparedPayload.Length);
                prepared = false;
                presentedCount++;
            }
        }

        internal void AcceptGeneration(int generation,
            bool silenceFutureReports)
        {
            lock (consumerLock)
            {
                acceptedGeneration = generation;
                prepared = false;
                Array.Clear(preparedPayload, 0, preparedPayload.Length);
                hasReceivedGeneration = silenceFutureReports;
            }
        }

        internal void RequestStop()
        {
            try { stopRequested.Set(); } catch { }
        }

        private void ApplyPrepared(byte[] report)
        {
            int offset = DualSenseBluetoothAudioPacer.
                RealtimeHapticsDataOffset;
            report[offset - 2] = 0x92;
            report[offset - 1] = PayloadLength;
            Buffer.BlockCopy(preparedPayload, 0, report, offset,
                PayloadLength);
        }

        private static void Silence(byte[] report)
        {
            int offset = DualSenseBluetoothAudioPacer.
                RealtimeHapticsDataOffset;
            report[offset - 2] = 0x92;
            report[offset - 1] = PayloadLength;
            Array.Clear(report, offset, PayloadLength);
        }

        private void AdvanceRead(long readSequence)
        {
            Thread.MemoryBarrier();
            view.Write(ReadSequenceOffset, readSequence + 1);
            Thread.MemoryBarrier();
            spaceAvailable.Set();
        }

        private void ValidateHeader(int expectedCapacity)
        {
            if (view.ReadInt32(MagicOffset) != Magic ||
                view.ReadInt32(VersionOffset) != Version ||
                view.ReadInt32(CapacityOffset) != expectedCapacity ||
                view.ReadInt32(PayloadLengthOffset) != PayloadLength)
            {
                throw new InvalidDataException(
                    "The realtime haptics shared-ring header is invalid.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            RequestStop();
            view.Dispose();
            map.Dispose();
            spaceAvailable.Dispose();
            stopRequested.Dispose();
        }
    }
}
