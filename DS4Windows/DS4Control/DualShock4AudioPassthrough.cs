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
        private readonly bool[] startFailed = new bool[ControllerCount];
        private readonly object[] slotWorkerLocks = CreateSlotWorkerLocks();

        public ControllerRuntimeLaneState GetStatus(int slot)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return ControllerRuntimeLaneState.Unavailable;
            }

            lock (syncRoot)
            {
                if (slots[slot] != null)
                {
                    return ControllerRuntimeLaneState.Ready;
                }

                if (startFailed[slot])
                {
                    return ControllerRuntimeLaneState.Unavailable;
                }

                return pendingStarts[slot] != null
                    ? ControllerRuntimeLaneState.Starting
                    : ControllerRuntimeLaneState.Unavailable;
            }
        }

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
            int virtualOwnerToken = ViiperPnPOwnershipRegistry.GetToken(
                directSpeakerSource);
            DirectSpeakerRouteDecision initialRoute =
                DualSenseAudioPassthrough.EvaluateDirectSpeakerRoute(
                    captureEndpointId, endpointKind, directSpeakerSource);
            if (initialRoute == DirectSpeakerRouteDecision.Loopback)
            {
                directSpeakerSource = null;
                virtualOwnerToken = -1;
            }
            lock (syncRoot)
            {
                if (slots[slot]?.Matches(device, speakerVolume, compression,
                    bassBoost, captureEndpointId, endpointKind,
                    directSpeakerSource, virtualOwnerToken) == true)
                {
                    return;
                }

                if (pendingStarts[slot]?.Matches(device, speakerVolume,
                    compression, bassBoost, captureEndpointId,
                    endpointKind, directSpeakerSource,
                    virtualOwnerToken) == true)
                {
                    return;
                }

                previous = slots[slot];
                slots[slot] = null;
                startFailed[slot] = false;
                generation = ++startGenerations[slot];
                pendingStarts[slot] = new StartRequest(device, speakerVolume,
                    compression, bassBoost, captureEndpointId, endpointKind,
                    directSpeakerSource, virtualOwnerToken, generation);
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
                        directSpeakerSource, virtualOwnerToken, generation);
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
                startFailed[slot] = true;
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

        public void ResetForServiceStop()
        {
            var previous = new DualShock4BluetoothSpeakerPassthrough[slots.Length];
            lock (syncRoot)
            {
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    previous[slot] = slots[slot];
                    slots[slot] = null;
                    pendingStarts[slot] = null;
                    startFailed[slot] = false;
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
                    startFailed[slot] = false;
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
            ViiperOutDevice directSpeakerSource, int virtualOwnerToken,
            int generation)
        {
            const int attempts = 20;
            Exception lastError = null;

            // Apply this before opening the audio HID lane. Reboots and power
            // plan changes can silently restore selective suspend, producing
            // periodic 40-110 ms radio stalls even though every 4 ms speaker
            // report was submitted on time.
            DualShock4BluetoothPowerPolicy.
                EnsureDisabledForActivePowerScheme();

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                lock (syncRoot)
                {
                    if (generation != startGenerations[slot])
                    {
                        if (pendingStarts[slot] == null)
                        {
                            startFailed[slot] = true;
                        }

                        return;
                    }
                }

                DirectSpeakerRouteDecision route =
                    DualSenseAudioPassthrough.EvaluateDirectSpeakerRoute(
                        captureEndpointId, endpointKind, directSpeakerSource);
                virtualOwnerToken = ViiperPnPOwnershipRegistry.GetToken(
                    directSpeakerSource);
                ViiperOutDevice activeDirectSpeakerSource =
                    route == DirectSpeakerRouteDecision.Direct ?
                        directSpeakerSource : null;
                var playback = new DualShock4BluetoothSpeakerPassthrough(device,
                    speakerVolume, compression, bassBoost, captureEndpointId,
                    endpointKind, activeDirectSpeakerSource,
                    virtualOwnerToken);
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
                        startFailed[slot] = false;
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
                    startFailed[slot] = true;
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
            private readonly int virtualOwnerToken;

            public StartRequest(DS4Device device, byte speakerVolume,
                DualSenseSpeakerCompression compression, byte bassBoost,
                string sourceEndpointId,
                ControllerAudioEndpointKind sourceEndpointKind,
                ViiperOutDevice directSpeakerSource, int virtualOwnerToken,
                int generation)
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
                this.virtualOwnerToken = virtualOwnerToken;
                Generation = generation;
            }

            public int Generation { get; }

            public bool Matches(DS4Device candidate, byte candidateVolume,
                DualSenseSpeakerCompression candidateCompression,
                byte candidateBassBoost, string candidateSourceEndpointId,
                ControllerAudioEndpointKind candidateSourceEndpointKind,
                ViiperOutDevice candidateDirectSpeakerSource,
                int candidateVirtualOwnerToken)
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
                    virtualOwnerToken == candidateVirtualOwnerToken &&
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
