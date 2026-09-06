using System;
using System.Threading;
using DS4Windows.Switch2;

namespace DS4Windows
{
    // One cold-path attempt, not an index-only sticky error. A completion may
    // mutate only this object; replacing the array entry fences an older
    // asynchronous attempt from changing its successor's readiness.
    internal sealed class ControllerVirtualOutputAttempt
    {
        private readonly DS4Device physicalDevice;
        private readonly OutContType outputType;
        private int failed;

        internal ControllerVirtualOutputAttempt(DS4Device physicalDevice, OutContType outputType)
        {
            this.physicalDevice = physicalDevice ?? throw new ArgumentNullException(nameof(physicalDevice));
            this.outputType = outputType.Normalize();
        }

        internal bool Matches(DS4Device device, OutContType requestedType, bool virtualRequired) =>
            virtualRequired && ReferenceEquals(physicalDevice, device) && outputType == requestedType.Normalize();
        internal bool Failed => Volatile.Read(ref failed) != 0;
        internal void MarkFailed() => Volatile.Write(ref failed, 1);
    }

    public enum ControllerRuntimeLaneState : byte
    {
        NotRequired,
        Starting,
        Ready,
        Unavailable,
    }

    public enum ControllerStartupStage : byte
    {
        Disconnected,
        Connecting,
        Connected,
        CreatingVirtualController,
        ArmingAdvancedHaptics,
        StartingSpeaker,
        StartingMicrophone,
        StartingAudioHaptics,
        Ready,
        Attention,
    }

    public readonly struct ControllerRuntimeSignals
    {
        public ControllerRuntimeSignals(bool physicalPresent,
            bool physicalSynced, bool physicalAlive, bool virtualRequired,
            bool virtualConnected, bool virtualTypeMatches,
            ControllerRuntimeLaneState advancedHaptics,
            ControllerRuntimeLaneState speaker,
            ControllerRuntimeLaneState microphone,
            ControllerRuntimeLaneState audioHaptics,
            string virtualControllerName, bool virtualFailed = false,
            bool physicalCleanupQuarantined = false)
        {
            PhysicalPresent = physicalPresent;
            PhysicalSynced = physicalSynced;
            PhysicalAlive = physicalAlive;
            VirtualRequired = virtualRequired;
            VirtualConnected = virtualConnected;
            VirtualTypeMatches = virtualTypeMatches;
            AdvancedHaptics = advancedHaptics;
            Speaker = speaker;
            Microphone = microphone;
            AudioHaptics = audioHaptics;
            VirtualControllerName = virtualControllerName ?? "virtual controller";
            VirtualFailed = virtualFailed;
            PhysicalCleanupQuarantined = physicalCleanupQuarantined;
        }

        public bool PhysicalPresent { get; }
        public bool PhysicalSynced { get; }
        public bool PhysicalAlive { get; }
        public bool VirtualRequired { get; }
        public bool VirtualConnected { get; }
        public bool VirtualTypeMatches { get; }
        public ControllerRuntimeLaneState AdvancedHaptics { get; }
        public ControllerRuntimeLaneState Speaker { get; }
        public ControllerRuntimeLaneState Microphone { get; }
        public ControllerRuntimeLaneState AudioHaptics { get; }
        public string VirtualControllerName { get; }
        public bool VirtualFailed { get; }
        public bool PhysicalCleanupQuarantined { get; }
    }

    public readonly struct ControllerStartupStatus : IEquatable<ControllerStartupStatus>
    {
        public ControllerStartupStatus(ControllerStartupStage stage,
            string title, string detail)
        {
            Stage = stage;
            Title = title ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public ControllerStartupStage Stage { get; }
        public string Title { get; }
        public string Detail { get; }
        public bool IsReady => Stage == ControllerStartupStage.Ready;
        public bool NeedsAttention => Stage == ControllerStartupStage.Attention;

        public bool Equals(ControllerStartupStatus other) =>
            Stage == other.Stage && Title == other.Title && Detail == other.Detail;

        public override bool Equals(object obj) =>
            obj is ControllerStartupStatus other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Stage, Title, Detail);

        public static bool operator ==(ControllerStartupStatus left,
            ControllerStartupStatus right) => left.Equals(right);

        public static bool operator !=(ControllerStartupStatus left,
            ControllerStartupStatus right) => !left.Equals(right);
    }

    public static class ControllerRuntimeStatusPolicy
    {
        internal static bool HasQuarantinedPhysicalRuntime(DS4Device device,
            int index, InputControllerRegistrationTable table)
        {
            if (device is not Switch2RuntimeInputDevice runtime ||
                runtime.RuntimeState is not (Switch2RuntimeInputDeviceState.Terminal or
                    Switch2RuntimeInputDeviceState.AbortedUnpublished) ||
                table == null || index < 0 || index >= table.SlotCount)
                return false;

            // This is cold status observation, not a cleanup credential. A
            // terminal runtime alone is normal during removal; only an exact
            // quarantined registration proves that cleanup needs attention.
            InputControllerSlotSnapshot snapshot = table.GetSnapshot()[index];
            return snapshot.State == InputControllerSlotState.Quarantined &&
                snapshot.Token.IsValid && snapshot.Token.Slot == index &&
                ReferenceEquals(snapshot.Token.Registration.Device, runtime) &&
                snapshot.Token.Registration.OwnershipKind == InputControllerOwnershipKind.Switch2Runtime &&
                snapshot.Token.Registration.Generation == runtime.RuntimeGeneration;
        }

        public static ControllerStartupStatus Evaluate(
            ControllerRuntimeSignals signals)
        {
            if (!signals.PhysicalPresent)
            {
                return new ControllerStartupStatus(
                    ControllerStartupStage.Disconnected, "Disconnected",
                    "No physical controller is assigned to this slot.");
            }

            if (signals.PhysicalCleanupQuarantined)
            {
                return new ControllerStartupStatus(ControllerStartupStage.Attention,
                    "Needs attention", "This controller session ended, but its cleanup is incomplete. Check the log before retrying; the slot remains reserved for safe cleanup.");
            }

            if (!signals.PhysicalSynced || !signals.PhysicalAlive)
            {
                return new ControllerStartupStatus(
                    ControllerStartupStage.Connecting, "Connecting",
                    "Waiting for stable input from the physical controller.");
            }

            if (signals.VirtualRequired && signals.VirtualFailed)
            {
                return new ControllerStartupStatus(ControllerStartupStage.Attention,
                    "Needs attention", $"The virtual {signals.VirtualControllerName} pad failed to start or its connection ended. Check the log, then retry the output connection.");
            }

            if (signals.VirtualRequired && !signals.VirtualConnected)
            {
                return new ControllerStartupStatus(
                    ControllerStartupStage.CreatingVirtualController,
                    "Connected",
                    $"Creating the virtual {signals.VirtualControllerName} pad.");
            }

            if (signals.VirtualRequired && !signals.VirtualTypeMatches)
            {
                return new ControllerStartupStatus(
                    ControllerStartupStage.CreatingVirtualController,
                    "Connected",
                    $"Switching to the virtual {signals.VirtualControllerName} pad.");
            }

            ControllerStartupStatus laneStatus = EvaluateLane(
                signals.AdvancedHaptics,
                ControllerStartupStage.ArmingAdvancedHaptics,
                "Arming haptics", "advanced haptics lane");
            if (laneStatus.Stage != ControllerStartupStage.Ready)
            {
                return laneStatus;
            }

            laneStatus = EvaluateLane(signals.Speaker,
                ControllerStartupStage.StartingSpeaker,
                "Starting speaker", "controller speaker and headset audio");
            if (laneStatus.Stage != ControllerStartupStage.Ready)
            {
                return laneStatus;
            }

            laneStatus = EvaluateLane(signals.Microphone,
                ControllerStartupStage.StartingMicrophone,
                "Starting microphone", "controller microphone");
            if (laneStatus.Stage != ControllerStartupStage.Ready)
            {
                return laneStatus;
            }

            laneStatus = EvaluateLane(signals.AudioHaptics,
                ControllerStartupStage.StartingAudioHaptics,
                "Starting Audio Haptics", "Audio Haptics capture");
            if (laneStatus.Stage != ControllerStartupStage.Ready)
            {
                return laneStatus;
            }

            string detail = signals.VirtualRequired
                ? "Physical input, virtual pad, and enabled media lanes are stable."
                : "Physical input and every enabled media lane are stable.";
            return new ControllerStartupStatus(ControllerStartupStage.Ready,
                "Ready", detail);
        }

        private static ControllerStartupStatus EvaluateLane(
            ControllerRuntimeLaneState state, ControllerStartupStage stage,
            string startingTitle, string laneName)
        {
            return state switch
            {
                ControllerRuntimeLaneState.Starting =>
                    new ControllerStartupStatus(stage, startingTitle,
                        $"Waiting for the {laneName} to become stable."),
                ControllerRuntimeLaneState.Unavailable =>
                    new ControllerStartupStatus(ControllerStartupStage.Attention,
                        "Needs attention",
                        $"The enabled {laneName} could not be armed."),
                _ => new ControllerStartupStatus(
                    ControllerStartupStage.Ready, "Ready", string.Empty),
            };
        }
    }
}
