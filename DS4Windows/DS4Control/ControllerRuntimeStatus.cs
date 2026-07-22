using System;

namespace DS4Windows
{
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
            string virtualControllerName)
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
        public static ControllerStartupStatus Evaluate(
            ControllerRuntimeSignals signals)
        {
            if (!signals.PhysicalPresent)
            {
                return new ControllerStartupStatus(
                    ControllerStartupStage.Disconnected, "Disconnected",
                    "No physical controller is assigned to this slot.");
            }

            if (!signals.PhysicalSynced || !signals.PhysicalAlive)
            {
                return new ControllerStartupStatus(
                    ControllerStartupStage.Connecting, "Connecting",
                    "Waiting for stable input from the physical controller.");
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
