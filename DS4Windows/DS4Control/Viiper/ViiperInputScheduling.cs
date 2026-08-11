using System;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Fixed storage for mapped controller input. Adjacent analog/motion
    /// updates may replace one another only while their discrete edge
    /// signature is unchanged; button, trigger, d-pad, and touch transitions
    /// retain FIFO order.
    /// </summary>
    internal sealed class ViiperInputPacketQueue
    {
        internal const int DefaultCapacity = 256;

        private readonly byte[][] slots;
        private readonly long[] queuedTimestamps;
        private readonly ulong[] edgeSignatures;
        private readonly byte[] latestPacket;
        private readonly byte[] retryPacket;
        private readonly int packetLength;
        private int head;
        private int count;
        private bool latestKnown;
        private ulong latestEdgeSignature;
        private bool retryPending;
        private long retryQueuedTimestamp;

        internal ViiperInputPacketQueue(int packetLength,
            int capacity = DefaultCapacity)
        {
            if (packetLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(packetLength));
            }
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            this.packetLength = packetLength;
            slots = new byte[capacity][];
            queuedTimestamps = new long[capacity];
            edgeSignatures = new ulong[capacity];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new byte[packetLength];
            }

            latestPacket = new byte[packetLength];
            retryPacket = new byte[packetLength];
        }

        internal int Count => count + (retryPending ? 1 : 0);

        internal int Capacity => slots.Length;

        internal int PacketLength => packetLength;

        internal bool TryEnqueue(byte[] packet, long queuedTimestamp,
            ulong edgeSignature, out bool coalesced)
        {
            ValidatePacket(packet);
            coalesced = false;

            if (count > 0)
            {
                int tail = (head + count - 1) % slots.Length;
                if (edgeSignatures[tail] == edgeSignature)
                {
                    Buffer.BlockCopy(packet, 0, slots[tail], 0, packetLength);
                    queuedTimestamps[tail] = queuedTimestamp;
                    coalesced = true;
                    PublishLatest(packet, edgeSignature);
                    return true;
                }
            }

            if (count == slots.Length)
            {
                return false;
            }

            int index = (head + count) % slots.Length;
            Buffer.BlockCopy(packet, 0, slots[index], 0, packetLength);
            queuedTimestamps[index] = queuedTimestamp;
            edgeSignatures[index] = edgeSignature;
            count++;
            PublishLatest(packet, edgeSignature);
            return true;
        }

        internal bool TryDequeue(byte[] destination, out long queuedTimestamp)
        {
            ValidatePacket(destination);
            if (retryPending)
            {
                Buffer.BlockCopy(retryPacket, 0, destination, 0, packetLength);
                queuedTimestamp = retryQueuedTimestamp;
                retryPending = false;
                retryQueuedTimestamp = 0;
                return true;
            }

            if (count == 0)
            {
                queuedTimestamp = 0;
                return false;
            }

            Buffer.BlockCopy(slots[head], 0, destination, 0, packetLength);
            queuedTimestamp = queuedTimestamps[head];
            queuedTimestamps[head] = 0;
            head = (head + 1) % slots.Length;
            count--;
            return true;
        }

        /// <summary>
        /// Holds one failed in-flight packet independently of the main ring.
        /// This lets recovery restore the oldest transition even if new input
        /// fills every normal slot while the transport is reconnecting.
        /// </summary>
        internal bool TryQueueRetry(byte[] packet, long queuedTimestamp)
        {
            ValidatePacket(packet);
            if (retryPending)
            {
                return false;
            }

            Buffer.BlockCopy(packet, 0, retryPacket, 0, packetLength);
            retryQueuedTimestamp = queuedTimestamp;
            retryPending = true;
            return true;
        }

        internal bool EnsureLatestQueued(long queuedTimestamp)
        {
            if (Count > 0)
            {
                return true;
            }
            if (!latestKnown)
            {
                return false;
            }

            return TryEnqueue(latestPacket, queuedTimestamp,
                latestEdgeSignature, out _);
        }

        internal void Clear()
        {
            head = 0;
            count = 0;
            retryPending = false;
            retryQueuedTimestamp = 0;
            latestKnown = false;
            latestEdgeSignature = 0;
        }

        private void PublishLatest(byte[] packet, ulong edgeSignature)
        {
            Buffer.BlockCopy(packet, 0, latestPacket, 0, packetLength);
            latestEdgeSignature = edgeSignature;
            latestKnown = true;
        }

        private void ValidatePacket(byte[] packet)
        {
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }
            if (packet.Length != packetLength)
            {
                throw new ArgumentException(
                    $"VIIPER input packets must contain exactly {packetLength} bytes.",
                    nameof(packet));
            }
        }
    }

    /// <summary>
    /// Serializes the one framed device stream while allowing waiting input to
    /// pass queued media. Media is admitted after at most two input records, so
    /// priority cannot turn into microphone starvation.
    /// </summary>
    internal sealed class ViiperPriorityWriteScheduler
    {
        private readonly object sync = new object();
        private bool active;
        private bool mediaDue;
        private int waitingInput;
        private int waitingMedia;

        internal int WaitingInput
        {
            get
            {
                lock (sync)
                {
                    return waitingInput;
                }
            }
        }

        internal int WaitingMedia
        {
            get
            {
                lock (sync)
                {
                    return waitingMedia;
                }
            }
        }

        internal void EnterInput()
        {
            lock (sync)
            {
                waitingInput++;
                try
                {
                    while (active || mediaDue && waitingMedia > 0)
                    {
                        Monitor.Wait(sync);
                    }

                    active = true;
                    mediaDue = waitingMedia > 0;
                }
                finally
                {
                    waitingInput--;
                }
            }
        }

        internal void EnterMedia()
        {
            lock (sync)
            {
                waitingMedia++;
                try
                {
                    while (active || !mediaDue && waitingInput > 0)
                    {
                        Monitor.Wait(sync);
                    }

                    active = true;
                    mediaDue = false;
                }
                finally
                {
                    waitingMedia--;
                }
            }
        }

        internal void Exit()
        {
            lock (sync)
            {
                if (!active)
                {
                    throw new SynchronizationLockException(
                        "The VIIPER stream write scheduler is not owned.");
                }

                active = false;
                Monitor.PulseAll(sync);
            }
        }
    }
}
