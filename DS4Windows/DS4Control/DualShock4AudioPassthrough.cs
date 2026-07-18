using System;
using System.Threading.Tasks;

namespace DS4Windows
{
    /// <summary>
    /// Owns one Bluetooth speaker worker per controller slot. Profile changes
    /// replace workers atomically so stale capture callbacks cannot write into
    /// a newly selected controller.
    /// </summary>
    public sealed class DualShock4AudioPassthrough : IDisposable
    {
        private const int ControllerCount = ControlService.MAX_DS4_CONTROLLER_COUNT;
        private readonly object syncRoot = new object();
        private readonly DualShock4BluetoothSpeakerPassthrough[] slots =
            new DualShock4BluetoothSpeakerPassthrough[ControllerCount];
        private readonly int[] startGenerations = new int[ControllerCount];

        public void Start(int slot, DS4Device device, byte speakerVolume,
            DualSenseSpeakerCompression compression, byte bassBoost,
            string captureEndpointId, OutContType emulatedControllerType)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            DualShock4BluetoothSpeakerPassthrough previous;
            int generation;
            ControllerAudioEndpointKind endpointKind =
                DualSenseAudioPassthrough.GetEndpointKind(emulatedControllerType);
            lock (syncRoot)
            {
                if (slots[slot]?.Matches(device, speakerVolume, compression,
                    bassBoost, captureEndpointId, endpointKind) == true)
                {
                    return;
                }

                previous = slots[slot];
                slots[slot] = null;
                generation = ++startGenerations[slot];
            }

            DisposeInBackground(previous);
            _ = Task.Run(() => StartWorker(slot, device, speakerVolume,
                compression, bassBoost, captureEndpointId, endpointKind, generation));
        }

        public void Stop(int slot)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            DualShock4BluetoothSpeakerPassthrough playback;
            lock (syncRoot)
            {
                playback = slots[slot];
                slots[slot] = null;
                startGenerations[slot]++;
            }

            DisposeInBackground(playback);
        }

        public void Dispose()
        {
            var previous = new DualShock4BluetoothSpeakerPassthrough[slots.Length];
            lock (syncRoot)
            {
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    previous[slot] = slots[slot];
                    slots[slot] = null;
                    startGenerations[slot]++;
                }
            }

            foreach (DualShock4BluetoothSpeakerPassthrough playback in previous)
            {
                playback?.Dispose();
            }
        }

        private void StartWorker(int slot, DS4Device device, byte speakerVolume,
            DualSenseSpeakerCompression compression, byte bassBoost,
            string captureEndpointId, ControllerAudioEndpointKind endpointKind,
            int generation)
        {
            var playback = new DualShock4BluetoothSpeakerPassthrough(device,
                speakerVolume, compression, bassBoost, captureEndpointId,
                endpointKind);
            try
            {
                playback.Start();
                bool stale;
                lock (syncRoot)
                {
                    stale = generation != startGenerations[slot];
                    if (!stale)
                    {
                        slots[slot] = playback;
                    }
                }

                if (stale)
                {
                    playback.Dispose();
                }
            }
            catch (Exception ex)
            {
                playback.Dispose();
                lock (syncRoot)
                {
                    if (generation != startGenerations[slot])
                    {
                        return;
                    }
                }

                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker passthrough could not start: {ex.Message}",
                    true);
            }
        }

        private static void DisposeInBackground(
            DualShock4BluetoothSpeakerPassthrough playback)
        {
            if (playback != null)
            {
                _ = Task.Run(playback.Dispose);
            }
        }
    }
}
