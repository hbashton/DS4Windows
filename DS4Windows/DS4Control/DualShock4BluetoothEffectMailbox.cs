using System;

namespace DS4Windows
{
    /// <summary>
    /// Fixed latest-value handoff from the physical input owner to the
    /// Bluetooth audio output owner. Publishing only copies bytes under this
    /// short lock; it never waits for HID I/O or for an audio completion.
    /// </summary>
    internal sealed class DualShock4BluetoothEffectMailbox
    {
        private readonly object gate = new object();
        private readonly byte[] latestReport;
        private int latestLength;
        private long latestVersion;
        private long claimedVersion;
        private bool pending;
        private bool accepting = true;

        internal DualShock4BluetoothEffectMailbox(int maximumReportLength)
        {
            if (maximumReportLength <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumReportLength));
            }

            latestReport = new byte[maximumReportLength];
        }

        internal bool TryPublish(byte[] report)
        {
            if (report == null || report.Length == 0 ||
                report.Length > latestReport.Length)
            {
                return false;
            }

            lock (gate)
            {
                if (!accepting)
                {
                    return false;
                }

                Buffer.BlockCopy(report, 0, latestReport, 0,
                    report.Length);
                latestLength = report.Length;
                latestVersion++;
                if (latestVersion == 0)
                {
                    latestVersion = 1;
                }
                pending = true;
                return true;
            }
        }

        internal bool HasPending
        {
            get
            {
                lock (gate)
                {
                    return pending;
                }
            }
        }

        internal bool HasUnclaimed
        {
            get
            {
                lock (gate)
                {
                    return pending && claimedVersion != latestVersion;
                }
            }
        }

        /// <summary>
        /// Copies and claims the current latest value. The caller supplies
        /// fixed storage and must have reserved its output capacity before
        /// claiming it. Completion must later acknowledge or reject the token.
        /// </summary>
        internal bool TryClaim(byte[] destination, out int length,
            out long version)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            lock (gate)
            {
                if (!pending || claimedVersion == latestVersion)
                {
                    length = 0;
                    version = 0;
                    return false;
                }
                if (destination.Length < latestLength)
                {
                    throw new ArgumentException(
                        "The destination is smaller than the pending report.",
                        nameof(destination));
                }

                length = latestLength;
                Buffer.BlockCopy(latestReport, 0, destination, 0, length);
                version = latestVersion;
                claimedVersion = version;
                return true;
            }
        }

        internal void Acknowledge(long version)
        {
            lock (gate)
            {
                if (pending && latestVersion == version)
                {
                    pending = false;
                    claimedVersion = 0;
                }
            }
        }

        internal void Reject(long version)
        {
            lock (gate)
            {
                if (pending && latestVersion == version &&
                    claimedVersion == version)
                {
                    // Preserve the same bytes as unclaimed. If a newer value
                    // already replaced this version, it remains authoritative.
                    claimedVersion = 0;
                }
            }
        }

        /// <summary>
        /// Permanently rejects new input-side effects. A value already
        /// admitted remains pending so the final ordered control barrier can
        /// write it before disabling the audio plane.
        /// </summary>
        internal void StopAccepting()
        {
            lock (gate)
            {
                accepting = false;
            }
        }
    }
}
