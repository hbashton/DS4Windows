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

        private readonly object gate = new object();
        private Snapshot current = new Snapshot(false, false, 0x4F, 0x4F,
            0x40);

        public Snapshot Current => Volatile.Read(ref current);

        public bool Update(bool? speakerEnabled,
            bool? microphoneEnabled, byte? speakerVolume,
            byte? headphoneVolume, byte? microphoneVolume,
            Func<Snapshot, bool> publish)
        {
            lock (gate)
            {
                Snapshot previous = current;
                var next = new Snapshot(
                    speakerEnabled ?? previous.SpeakerEnabled,
                    microphoneEnabled ?? previous.MicrophoneEnabled,
                    speakerVolume ?? previous.SpeakerVolume,
                    headphoneVolume ?? previous.HeadphoneVolume,
                    microphoneVolume ?? previous.MicrophoneVolume);
                if (publish != null && !publish(next))
                {
                    return false;
                }

                Volatile.Write(ref current, next);
                return true;
            }
        }

        public void ReadSynchronized(Action<Snapshot> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            lock (gate)
            {
                action(current);
            }
        }
    }
}
