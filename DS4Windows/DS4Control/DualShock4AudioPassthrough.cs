using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
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
        private readonly StartRequest[] pendingStarts =
            new StartRequest[ControllerCount];
        private readonly int[] startGenerations = new int[ControllerCount];
        private readonly bool[] startFailed = new bool[ControllerCount];
        private readonly DualShock4AudioSlotWorkQueue[] slotWorkQueues =
            CreateSlotWorkQueues();

        public ControllerRuntimeLaneState GetStatus(int slot)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return ControllerRuntimeLaneState.Unavailable;
            }

            lock (syncRoot)
            {
                if (slots[slot]?.IsOperational == true)
                {
                    return ControllerRuntimeLaneState.Ready;
                }

                if (slots[slot] != null)
                {
                    return ControllerRuntimeLaneState.Unavailable;
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
            ViiperOutDevice directSpeakerSource = null,
            bool headsetOnlyAudio = false,
            Func<ViiperOutDevice> directSpeakerSourceResolver = null)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            DualShock4BluetoothSpeakerPassthrough previous;
            int generation;
            ControllerAudioEndpointKind endpointKind =
                DualSenseAudioPassthrough.GetEndpointKind(emulatedControllerType);
            ViiperOutDevice currentDirectSpeakerSource =
                ResolveDirectSpeakerSource(directSpeakerSourceResolver,
                    directSpeakerSource);
            DirectSpeakerRouteDecision initialRoute =
                DualSenseAudioPassthrough.EvaluateDirectSpeakerRoute(
                    captureEndpointId, endpointKind,
                    currentDirectSpeakerSource);
            if (initialRoute == DirectSpeakerRouteDecision.Loopback)
            {
                currentDirectSpeakerSource = null;
            }
            lock (syncRoot)
            {
                if (slots[slot]?.Matches(device, speakerVolume, compression,
                    bassBoost, captureEndpointId, endpointKind,
                    currentDirectSpeakerSource, headsetOnlyAudio) == true)
                {
                    return;
                }

                if (pendingStarts[slot]?.Matches(device, speakerVolume,
                    compression, bassBoost, captureEndpointId,
                    endpointKind, currentDirectSpeakerSource,
                    headsetOnlyAudio) == true)
                {
                    return;
                }

                previous = slots[slot];
                previous?.RequestStop();
                slots[slot] = null;
                startFailed[slot] = false;
                generation = ++startGenerations[slot];
                pendingStarts[slot] = new StartRequest(device, speakerVolume,
                    compression, bassBoost, captureEndpointId, endpointKind,
                    currentDirectSpeakerSource, headsetOnlyAudio, generation);
                slotWorkQueues[slot].EnqueueWhileHolding(syncRoot, () =>
                {
                    previous?.Dispose();
                    StartWorker(slot, device, speakerVolume, compression,
                        bassBoost, captureEndpointId, endpointKind,
                        directSpeakerSource, directSpeakerSourceResolver,
                        headsetOnlyAudio, generation);
                });
            }

            AppLogger.LogToGui(
                $"DS4 audio owner slot {slot + 1}: start generation {generation}, endpointKind={endpointKind}, replacingActive={previous != null}.",
                false);

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
                playback?.RequestStop();
                bool hadPendingStart = pendingStarts[slot] != null;
                slots[slot] = null;
                pendingStarts[slot] = null;
                startFailed[slot] = true;
                startGenerations[slot]++;
                // Enqueue under the same state lock which publishes this
                // generation. Therefore command order and generation order
                // have one linearization point.
                slotWorkQueues[slot].EnqueueWhileHolding(syncRoot,
                    () => playback?.Dispose());
                if (playback != null || hadPendingStart)
                {
                    AppLogger.LogToGui(
                        $"DS4 audio owner slot {slot + 1}: stop from {caller}, active={playback != null}, pending={hadPendingStart}, generation={startGenerations[slot]}.",
                        false);
                }
            }

        }

        public void ResetForServiceStop()
        {
            RetireAllSlots();
        }

        public void Dispose()
        {
            RetireAllSlots();
        }

        private void RetireAllSlots()
        {
            var retirements = new Task[slots.Length];
            lock (syncRoot)
            {
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    DualShock4BluetoothSpeakerPassthrough playback =
                        slots[slot];
                    playback?.RequestStop();
                    slots[slot] = null;
                    pendingStarts[slot] = null;
                    startFailed[slot] = false;
                    startGenerations[slot]++;
                    retirements[slot] = slotWorkQueues[slot].
                        EnqueueWhileHolding(syncRoot,
                            () => playback?.Dispose());
                }
            }

            // WaitAll observes every retirement even when one item faults, so
            // a failed slot cannot make service stop abandon the remaining
            // controller owners.
            Task.WaitAll(retirements);
        }

        private void StartWorker(int slot, DS4Device device, byte speakerVolume,
            DualSenseSpeakerCompression compression, byte bassBoost,
            string captureEndpointId, ControllerAudioEndpointKind endpointKind,
            ViiperOutDevice directSpeakerSource,
            Func<ViiperOutDevice> directSpeakerSourceResolver,
            bool headsetOnlyAudio,
            int generation)
        {
            const int attempts = 20;
            Exception lastError = null;
            bool prolongedWaitLogged = false;

            // Apply this before opening the audio HID lane. Reboots and power
            // plan changes can silently restore selective suspend, producing
            // periodic 40-110 ms radio stalls even though every 8 ms speaker
            // report was submitted on time.
            DualShock4BluetoothPowerPolicy.
                EnsureDisabledForActivePowerScheme();

            for (int attempt = 0; ; attempt++)
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

                ViiperOutDevice currentDirectSpeakerSource =
                    ResolveDirectSpeakerSource(directSpeakerSourceResolver,
                        directSpeakerSource);
                bool directRouteExpected = directSpeakerSourceResolver != null &&
                    !ProcessLoopbackWaveCapture.IsProcessEndpointId(
                        captureEndpointId) &&
                    !string.Equals(captureEndpointId,
                        DualSenseAudioPassthrough.DefaultSystemAudioEndpointId,
                        StringComparison.Ordinal);
                if (directRouteExpected && currentDirectSpeakerSource == null)
                {
                    lastError = new InvalidOperationException(
                        "The current VIIPER controller audio stream is being recreated.");
                    if (!prolongedWaitLogged && attempt + 1 >= attempts)
                    {
                        prolongedWaitLogged = true;
                        LogProlongedWait(lastError);
                    }
                    Thread.Sleep(prolongedWaitLogged ? 1000 : 500);
                    continue;
                }

                DirectSpeakerRouteDecision route =
                    DualSenseAudioPassthrough.EvaluateDirectSpeakerRoute(
                        captureEndpointId, endpointKind,
                        currentDirectSpeakerSource);
                if (route == DirectSpeakerRouteDecision.Pending)
                {
                    lastError = new InvalidOperationException(
                        "The selected VIIPER controller audio endpoint is still enumerating or its direct stream is recovering.");
                    if (!prolongedWaitLogged && attempt + 1 >= attempts)
                    {
                        prolongedWaitLogged = true;
                        LogProlongedWait(lastError);
                    }
                    Thread.Sleep(prolongedWaitLogged ? 1000 : 500);
                    continue;
                }

                ViiperOutDevice activeDirectSpeakerSource =
                    route == DirectSpeakerRouteDecision.Direct ?
                        currentDirectSpeakerSource : null;
                var playback = new DualShock4BluetoothSpeakerPassthrough(device,
                    speakerVolume, compression, bassBoost, captureEndpointId,
                    endpointKind, activeDirectSpeakerSource,
                    headsetOnlyAudio, owner =>
                        HandleUnexpectedWorkerExit(slot, generation, owner));
                try
                {
                    playback.Start();
                    bool obsolete;
                    bool workerEnded;
                    lock (syncRoot)
                    {
                        obsolete = generation != startGenerations[slot];
                        workerEnded = !playback.IsOperational;
                        if (!obsolete && !workerEnded)
                        {
                            slots[slot] = playback;
                            pendingStarts[slot] = null;
                            startFailed[slot] = false;
                        }
                    }
                    if (obsolete)
                    {
                        playback.Dispose();
                        return;
                    }
                    if (workerEnded)
                    {
                        throw new IOException(
                            "The DS4 Bluetooth audio worker ended during startup.");
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

                if (!prolongedWaitLogged && attempt + 1 >= attempts)
                {
                    prolongedWaitLogged = true;
                    LogProlongedWait(lastError);
                }
            }
        }

        private void HandleUnexpectedWorkerExit(int slot, int generation,
            DualShock4BluetoothSpeakerPassthrough owner)
        {
            bool withdrawn = false;
            int terminalGeneration = generation;
            lock (syncRoot)
            {
                if (generation != startGenerations[slot] ||
                    !ReferenceEquals(slots[slot], owner))
                {
                    return;
                }

                owner.RequestStop();
                pendingStarts[slot] = null;
                startFailed[slot] = true;
                withdrawn = TryEnqueueUnexpectedRetirementWhileHolding(
                    syncRoot, slots, startGenerations, slot, generation,
                    owner, slotWorkQueues[slot], owner.Dispose);
                terminalGeneration = startGenerations[slot];
            }

            if (withdrawn)
            {
                AppLogger.LogToGui(
                    $"DS4 audio owner slot {slot + 1}: generation " +
                    $"{generation} exited and retirement was queued before " +
                    $"generation {terminalGeneration}.", true);
            }
        }

        internal static bool TryEnqueueUnexpectedRetirementWhileHolding<T>(
            object ownerGate, T[] owners, int[] generations, int slot,
            int generation, T owner, DualShock4AudioSlotWorkQueue queue,
            Action retirement) where T : class
        {
            if (!Monitor.IsEntered(ownerGate))
            {
                throw new InvalidOperationException(
                    "Unexpected DS4 audio retirement must publish under the owner lock.");
            }
            if (owners == null || generations == null || queue == null ||
                retirement == null || slot < 0 || slot >= owners.Length ||
                slot >= generations.Length)
            {
                throw new ArgumentException(
                    "Invalid DS4 audio retirement publication.");
            }
            if (generations[slot] != generation ||
                !ReferenceEquals(owners[slot], owner))
            {
                return false;
            }

            owners[slot] = null;
            generations[slot]++;
            queue.EnqueueWhileHolding(ownerGate, retirement);
            return true;
        }

        private static void LogProlongedWait(Exception lastError)
        {
            AppLogger.LogToGui(
                $"DualShock 4 Bluetooth speaker is still waiting for its current VIIPER audio stream and will keep recovering in the background: {lastError?.Message}",
                true);
        }

        private static ViiperOutDevice ResolveDirectSpeakerSource(
            Func<ViiperOutDevice> resolver, ViiperOutDevice fallback)
        {
            if (resolver == null)
            {
                return fallback;
            }

            try
            {
                return resolver();
            }
            catch
            {
                return null;
            }
        }

        private static DualShock4AudioSlotWorkQueue[] CreateSlotWorkQueues()
        {
            var queues = new DualShock4AudioSlotWorkQueue[ControllerCount];
            for (int slot = 0; slot < queues.Length; slot++)
            {
                queues[slot] = new DualShock4AudioSlotWorkQueue();
            }

            return queues;
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
            private readonly bool headsetOnlyAudio;

            public StartRequest(DS4Device device, byte speakerVolume,
                DualSenseSpeakerCompression compression, byte bassBoost,
                string sourceEndpointId,
                ControllerAudioEndpointKind sourceEndpointKind,
                ViiperOutDevice directSpeakerSource, bool headsetOnlyAudio,
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
                this.headsetOnlyAudio = headsetOnlyAudio;
                Generation = generation;
            }

            public int Generation { get; }

            public bool Matches(DS4Device candidate, byte candidateVolume,
                DualSenseSpeakerCompression candidateCompression,
                byte candidateBassBoost, string candidateSourceEndpointId,
                ControllerAudioEndpointKind candidateSourceEndpointKind,
                ViiperOutDevice candidateDirectSpeakerSource,
                bool candidateHeadsetOnlyAudio)
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
                    headsetOnlyAudio == candidateHeadsetOnlyAudio &&
                    string.Equals(sourceEndpointId,
                        candidateSourceEndpointId ?? string.Empty,
                        StringComparison.Ordinal);
            }
        }

    }

    /// <summary>
    /// A strict per-controller continuation chain. Stop retirement, stale
    /// startup cancellation, and the next generation execute in enqueue order;
    /// no correctness property depends on Monitor waiter fairness.
    /// </summary>
    internal sealed class DualShock4AudioSlotWorkQueue
    {
        private readonly object gate = new object();
        private readonly Queue<WorkItem> pending = new Queue<WorkItem>();
        private bool workerRunning;

        internal Task EnqueueWhileHolding(object ownerGate, Action action)
        {
            if (ownerGate == null)
            {
                throw new ArgumentNullException(nameof(ownerGate));
            }
            if (!Monitor.IsEntered(ownerGate))
            {
                throw new InvalidOperationException(
                    "DS4 audio lifecycle state and command publication must be atomic.");
            }

            return Enqueue(action);
        }

        internal Task Enqueue(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                pending.Enqueue(new WorkItem(action, completion));
                if (!workerRunning)
                {
                    workerRunning = true;
                    var worker = new Thread(Drain)
                    {
                        IsBackground = true,
                        Name = "DualShock 4 audio lifecycle",
                        Priority = ThreadPriority.BelowNormal,
                    };
                    worker.Start();
                }
            }
            return completion.Task;
        }

        private void Drain()
        {
            while (true)
            {
                WorkItem work;
                lock (gate)
                {
                    if (pending.Count == 0)
                    {
                        workerRunning = false;
                        return;
                    }
                    work = pending.Dequeue();
                }

                try
                {
                    work.Action();
                    work.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    AppLogger.LogToGui(
                        $"DualShock 4 audio lifecycle work failed: {ex.Message}",
                        true);
                    work.Completion.TrySetException(ex);
                }
            }
        }

        private sealed class WorkItem
        {
            public WorkItem(Action action,
                TaskCompletionSource<bool> completion)
            {
                Action = action;
                Completion = completion;
            }

            public Action Action { get; }
            public TaskCompletionSource<bool> Completion { get; }
        }
    }
}
