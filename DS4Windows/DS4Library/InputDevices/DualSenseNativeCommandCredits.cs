using System;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Fixed identity ledger used by the parent and helper for native Bluetooth
    /// commands. Each caller serializes every operation with its state monitor;
    /// this type never waits or calls outside code.
    /// </summary>
    internal sealed class DualSenseNativeCommandCredits
    {
        private readonly long[] reportIds;
        private readonly int[] generations;
        private int count;

        internal DualSenseNativeCommandCredits(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            reportIds = new long[capacity];
            generations = new int[capacity];
        }

        internal int Count => count;

        /// <summary>
        /// Reserve before outbound admission; roll back only a definite
        /// pre-send failure. IDs come from the pacer's shared report sequence,
        /// so an outstanding ID cannot be reserved in another generation.
        /// IDs and generations are signed and nonzero. Generation wrap follows
        /// the shared PCM ring's signed serial ordering; zero is a sentinel.
        /// </summary>
        internal bool TryReserve(long id, int generation)
        {
            if (id == 0 || generation == 0 || count == reportIds.Length)
                return false;

            int vacant = -1;
            for (int index = 0; index < reportIds.Length; index++)
            {
                if (reportIds[index] == id)
                    return false;
                if (reportIds[index] == 0 && vacant < 0)
                    vacant = index;
            }

            if (vacant < 0)
                return false;

            reportIds[vacant] = id;
            generations[vacant] = generation;
            count++;
            return true;
        }

        /// <summary>
        /// One exact ACK or definite pre-send cancellation releases one parent
        /// slot. The helper releases its slot when queuing the terminal ACK.
        /// Duplicate, unknown and stale-generation ACKs cannot release a newer
        /// reservation. Sent credits survive ordinary Reset/Clear boundaries.
        /// </summary>
        internal bool TryRelease(long id, int generation)
        {
            if (id == 0 || generation == 0)
                return false;

            for (int index = 0; index < reportIds.Length; index++)
            {
                if (reportIds[index] != id || generations[index] != generation)
                    continue;

                reportIds[index] = 0;
                generations[index] = 0;
                count--;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Terminal owner Stop/error only, after further admission is sealed.
        /// Never call for a normal epoch/reset: commands already sent must
        /// remain charged until their presented/cancellation ACK is consumed.
        /// </summary>
        internal void Clear()
        {
            Array.Clear(reportIds, 0, reportIds.Length);
            Array.Clear(generations, 0, generations.Length);
            count = 0;
        }
    }
}
