using System;
using System.Runtime.CompilerServices;
using System.Threading;

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
        private readonly StartRequest[] pendingStarts =
            new StartRequest[ControllerCount];
        private readonly int[] startGenerations = new int[ControllerCount];
        private readonly object[] slotWorkerLocks = CreateSlotWorkerLocks();

        public void Start(int slot, DS4Device device, byte speakerVolume,
            DualSenseSpeakerCompression compression, byte bassBoost,
            string captureEndpointId, OutContType emulatedControllerType,
            ViiperOutDevice directSpeakerSource = null)
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
                    bassBoost, captureEndpointId, endpointKind,
                    directSpeakerSource) == true)
                {
                    return;
                }

                if (pendingStarts[slot]?.Matches(device, speakerVolume,
                    compression, bassBoost, captureEndpointId,
                    endpointKind, directSpeakerSource) == true)
                {
                    return;
                }

                previous = slots[slot];
                slots[slot] = null;
                generation = ++startGenerations[slot];
                pendingStarts[slot] = new StartRequest(device, speakerVolume,
                    compression, bassBoost, captureEndpointId, endpointKind,
                    directSpeakerSource, generation);
            }

            AppLogger.LogToGui(
                $"DS4 audio owner slot {slot + 1}: start generation {generation}, endpointKind={endpointKind}, replacingActive={previous != null}.",
                false);

            StartBackgroundThread(() =>
            {
                lock (slotWorkerLocks[slot])
                {
                    previous?.Dispose();
                    StartWorker(slot, device, speakerVolume, compression,
                        bassBoost, captureEndpointId, endpointKind,
                        directSpeakerSource, generation);
                }
            }, $"DualShock 4 audio startup {slot + 1}");
        }

        public void Stop(int slot, [CallerMemberName] string caller = null)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            DualShock4BluetoothSpeakerPassthrough playback;
            lock (syncRoot)
            {
                playback = slots[slot];
                bool hadPendingStart = pendingStarts[slot] != null;
                slots[slot] = null;
                pendingStarts[slot] = null;
                startGenerations[slot]++;
                if (playback != null || hadPendingStart)
                {
                    AppLogger.LogToGui(
                        $"DS4 audio owner slot {slot + 1}: stop from {caller}, active={playback != null}, pending={hadPendingStart}, generation={startGenerations[slot]}.",
                        false);
                }
            }

            DisposeInBackground(slot, playback);
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
                    pendingStarts[slot] = null;
                    startGenerations[slot]++;
                }
            }

            for (int slot = 0; slot < previous.Length; slot++)
            {
                lock (slotWorkerLocks[slot])
                {
                    previous[slot]?.Dispose();
                }
            }
        }

        private void StartWorker(int slot, DS4Device device, byte speakerVolume,
            DualSenseSpeakerCompression compression, byte bassBoost,
            string captureEndpointId, ControllerAudioEndpointKind endpointKind,
            ViiperOutDevice directSpeakerSource, int generation)
        {
            const int attempts = 20;
            Exception lastError = null;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                lock (syncRoot)
                {
                    if (generation != startGenerations[slot])
                    {
                        return;
                    }
                }

                var playback = new DualShock4BluetoothSpeakerPassthrough(device,
                    speakerVolume, compression, bassBoost, captureEndpointId,
                    endpointKind, directSpeakerSource);
                try
                {
                    playback.Start();
                    lock (syncRoot)
                    {
                        if (generation != startGenerations[slot])
                        {
                            playback.Dispose();
                            return;
                        }

                        slots[slot] = playback;
                        pendingStarts[slot] = null;
                    }

                    AppLogger.LogToGui(
                        $"DS4 audio owner slot {slot + 1}: generation {generation} became active.",
                        false);

                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    playback.Dispose();
                    lock (syncRoot)
                    {
                        if (generation != startGenerations[slot])
                        {
                            return;
                        }
                    }

                    Thread.Sleep(500);
                }
            }

            AppLogger.LogToGui(
                $"DualShock 4 Bluetooth speaker passthrough could not start after waiting for the selected audio endpoint: {lastError?.Message}",
                true);
            lock (syncRoot)
            {
                if (generation == startGenerations[slot])
                {
                    pendingStarts[slot] = null;
                }
            }
        }

        private void DisposeInBackground(
            int slot, DualShock4BluetoothSpeakerPassthrough playback)
        {
            if (playback != null)
            {
                StartBackgroundThread(() =>
                {
                    lock (slotWorkerLocks[slot])
                    {
                        playback.Dispose();
                    }
                }, "DualShock 4 audio cleanup");
            }
        }

        private static object[] CreateSlotWorkerLocks()
        {
            var locks = new object[ControllerCount];
            for (int slot = 0; slot < locks.Length; slot++)
            {
                locks[slot] = new object();
            }

            return locks;
        }

        private sealed class StartRequest
        {
            private readonly DS4Device device;
            private readonly byte speakerVolume;
            private readonly DualSenseSpeakerCompression compression;
            private readonly byte bassBoost;
            private readonly string sourceEndpointId;
            private readonly ControllerAudioEndpointKind sourceEndpointKind;
            private readonly ViiperOutDevice directSpeakerSource;

            public StartRequest(DS4Device device, byte speakerVolume,
                DualSenseSpeakerCompression compression, byte bassBoost,
                string sourceEndpointId,
                ControllerAudioEndpointKind sourceEndpointKind,
                ViiperOutDevice directSpeakerSource, int generation)
            {
                this.device = device;
                this.speakerVolume = speakerVolume;
                this.compression =
                    (DualSenseSpeakerCompression)Math.Clamp((int)compression,
                        (int)DualSenseSpeakerCompression.Off,
                        (int)DualSenseSpeakerCompression.Strong);
                this.bassBoost = Math.Min(bassBoost,
                    DualSenseSpeakerProcessor.MaximumBassBoostDb);
                this.sourceEndpointId = sourceEndpointId ?? string.Empty;
                this.sourceEndpointKind = sourceEndpointKind;
                this.directSpeakerSource = directSpeakerSource;
                Generation = generation;
            }

            public int Generation { get; }

            public bool Matches(DS4Device candidate, byte candidateVolume,
                DualSenseSpeakerCompression candidateCompression,
                byte candidateBassBoost, string candidateSourceEndpointId,
                ControllerAudioEndpointKind candidateSourceEndpointKind,
                ViiperOutDevice candidateDirectSpeakerSource)
            {
                return ReferenceEquals(device, candidate) &&
                    speakerVolume == candidateVolume &&
                    compression ==
                        (DualSenseSpeakerCompression)Math.Clamp(
                            (int)candidateCompression,
                            (int)DualSenseSpeakerCompression.Off,
                            (int)DualSenseSpeakerCompression.Strong) &&
                    bassBoost == Math.Min(candidateBassBoost,
                        DualSenseSpeakerProcessor.MaximumBassBoostDb) &&
                    sourceEndpointKind == candidateSourceEndpointKind &&
                    ReferenceEquals(directSpeakerSource,
                        candidateDirectSpeakerSource) &&
                    string.Equals(sourceEndpointId,
                        candidateSourceEndpointId ?? string.Empty,
                        StringComparison.Ordinal);
            }
        }

        private static void StartBackgroundThread(ThreadStart action, string name)
        {
            var thread = new Thread(action)
            {
                IsBackground = true,
                Name = name,
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }
    }
}
