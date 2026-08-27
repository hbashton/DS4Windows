using System;
using System.Threading;

namespace DS4WinWPF.DS4Control
{
    /// <summary>
    /// Serializes DS4 Bluetooth audio lane changes and the control write which
    /// publishes each change. Speaker payload reports deliberately do not use
    /// this coordinator; only mode-changing 0x11 reports belong here.
    /// </summary>
    internal sealed class DualShock4BluetoothAudioState
    {
        internal sealed class Snapshot
        {
            public Snapshot(bool speakerEnabled, bool microphoneEnabled,
                byte speakerVolume, byte headphoneVolume,
                byte microphoneVolume)
            {
                SpeakerEnabled = speakerEnabled;
                MicrophoneEnabled = microphoneEnabled;
                SpeakerVolume = speakerVolume;
                HeadphoneVolume = headphoneVolume;
                MicrophoneVolume = microphoneVolume;
            }

            public bool SpeakerEnabled { get; }
            public bool MicrophoneEnabled { get; }
            public byte SpeakerVolume { get; }
            public byte HeadphoneVolume { get; }
            public byte MicrophoneVolume { get; }
        }

        // Only mode-changing publishers serialize here. Realtime readers take
        // a non-blocking lease over one immutable snapshot. A publisher waits
        // for readers which already own the old mode, then marks the short
        // transition so new input-side readers defer instead of waiting behind
        // control I/O.
        private readonly object publishGate = new object();
        private readonly object stateGate = new object();
        private Snapshot current = new Snapshot(false, false, 0x4F, 0x4F,
            0x40);
        private bool transitioning;
        private int activeReaders;

        public Snapshot Current => Volatile.Read(ref current);

        /// <summary>
        /// A stack-only reader admission. Holding the lease prevents a mode
        /// publisher from putting its control report on the wire until the
        /// admitted physical writer has completed. The lease cannot escape to
        /// the heap, so the controller input path does not allocate a delegate
        /// or closure for every report.
        /// </summary>
        internal ref struct ReadLease
        {
            private DualShock4BluetoothAudioState owner;

            internal ReadLease(DualShock4BluetoothAudioState owner,
                Snapshot snapshot)
            {
                this.owner = owner;
                Snapshot = snapshot;
            }

            public Snapshot Snapshot { get; }

            public void Dispose()
            {
                DualShock4BluetoothAudioState releasingOwner = owner;
                if (releasingOwner == null)
                {
                    return;
                }

                owner = null;
                releasingOwner.ReleaseRead();
            }
        }

        public bool Update(bool? speakerEnabled,
            bool? microphoneEnabled, byte? speakerVolume,
            byte? headphoneVolume, byte? microphoneVolume,
            Func<Snapshot, bool> publish)
        {
            lock (publishGate)
            {
                Snapshot next;
                lock (stateGate)
                {
                    BeginTransitionNoLock();
                    Snapshot previous = current;
                    next = new Snapshot(
                        speakerEnabled ?? previous.SpeakerEnabled,
                        microphoneEnabled ?? previous.MicrophoneEnabled,
                        speakerVolume ?? previous.SpeakerVolume,
                        headphoneVolume ?? previous.HeadphoneVolume,
                        microphoneVolume ?? previous.MicrophoneVolume);
                }

                bool published = false;
                try
                {
                    published = publish == null || publish(next);
                    return published;
                }
                finally
                {
                    lock (stateGate)
                    {
                        if (published)
                        {
                            Volatile.Write(ref current, next);
                        }
                        EndTransitionNoLock();
                    }
                }
            }
        }

        /// <summary>
        /// Retires the host-owned speaker lane even when the final physical
        /// control write cannot be delivered (for example, during device
        /// removal). Normal state changes remain transactional; teardown must
        /// not leave the output loop believing a disposed audio lane is still
        /// available.
        /// </summary>
        public bool RetireSpeaker(byte? speakerVolume,
            Func<Snapshot, bool> publish)
        {
            lock (publishGate)
            {
                Snapshot next;
                lock (stateGate)
                {
                    BeginTransitionNoLock();
                    Snapshot previous = current;
                    next = new Snapshot(false,
                        previous.MicrophoneEnabled,
                        speakerVolume ?? previous.SpeakerVolume,
                        previous.HeadphoneVolume,
                        previous.MicrophoneVolume);
                    Volatile.Write(ref current, next);
                }

                try
                {
                    return publish == null || publish(next);
                }
                finally
                {
                    lock (stateGate)
                    {
                        EndTransitionNoLock();
                    }
                }
            }
        }

        /// <summary>
        /// Replays the already-retired mode after deferred native I/O drains,
        /// but only while no newer owner has enabled the speaker. The publisher
        /// gate orders a late disable before a concurrently waiting enable.
        /// </summary>
        public bool PublishRetiredSpeakerIfStillDisabled(
            Func<Snapshot, bool> publish)
        {
            if (publish == null)
            {
                throw new ArgumentNullException(nameof(publish));
            }

            lock (publishGate)
            {
                Snapshot snapshot;
                lock (stateGate)
                {
                    BeginTransitionNoLock();
                    snapshot = current;
                    if (snapshot.SpeakerEnabled)
                    {
                        EndTransitionNoLock();
                        return false;
                    }
                }

                try
                {
                    return publish(snapshot);
                }
                finally
                {
                    lock (stateGate)
                    {
                        EndTransitionNoLock();
                    }
                }
            }
        }

        public bool TryReadSynchronized(Action<Snapshot> action)
        {
            return ReadSnapshot(action, waitForTransition: false);
        }

        internal bool TryAcquireRead(out ReadLease lease)
        {
            lock (stateGate)
            {
                if (transitioning)
                {
                    lease = default;
                    return false;
                }

                activeReaders++;
                lease = new ReadLease(this, current);
                return true;
            }
        }

        internal ReadLease AcquireRead()
        {
            lock (stateGate)
            {
                while (transitioning)
                {
                    Monitor.Wait(stateGate);
                }

                activeReaders++;
                return new ReadLease(this, current);
            }
        }

        public void ReadSynchronized(Action<Snapshot> action)
        {
            ReadSnapshot(action, waitForTransition: true);
        }

        private bool ReadSnapshot(Action<Snapshot> action,
            bool waitForTransition)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            Snapshot snapshot;
            lock (stateGate)
            {
                while (transitioning)
                {
                    if (!waitForTransition)
                    {
                        return false;
                    }
                    Monitor.Wait(stateGate);
                }

                activeReaders++;
                snapshot = current;
            }

            try
            {
                action(snapshot);
                return true;
            }
            finally
            {
                ReleaseRead();
            }
        }

        private void ReleaseRead()
        {
            lock (stateGate)
            {
                activeReaders--;
                if (activeReaders == 0)
                {
                    Monitor.PulseAll(stateGate);
                }
            }
        }

        private void BeginTransitionNoLock()
        {
            transitioning = true;
            while (activeReaders != 0)
            {
                Monitor.Wait(stateGate);
            }
        }

        private void EndTransitionNoLock()
        {
            transitioning = false;
            Monitor.PulseAll(stateGate);
        }
    }
}
