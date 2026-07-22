using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerRuntimeStatusTests
    {
        private static ControllerRuntimeSignals Signals(
            bool present = true, bool synced = true, bool alive = true,
            bool virtualRequired = true, bool virtualConnected = true,
            bool virtualTypeMatches = true,
            ControllerRuntimeLaneState haptics = ControllerRuntimeLaneState.NotRequired,
            ControllerRuntimeLaneState speaker = ControllerRuntimeLaneState.NotRequired,
            ControllerRuntimeLaneState microphone = ControllerRuntimeLaneState.NotRequired,
            ControllerRuntimeLaneState audioHaptics = ControllerRuntimeLaneState.NotRequired) =>
            new ControllerRuntimeSignals(present, synced, alive,
                virtualRequired, virtualConnected, virtualTypeMatches,
                haptics, speaker, microphone, audioHaptics, "DualSense");

        [TestMethod]
        public void ReportsPhysicalConnectionStagesBeforeVirtualReadiness()
        {
            ControllerStartupStatus disconnected =
                ControllerRuntimeStatusPolicy.Evaluate(Signals(present: false,
                    synced: false, alive: false, virtualRequired: false,
                    virtualConnected: false, virtualTypeMatches: false));
            Assert.AreEqual("Disconnected", disconnected.Title);

            ControllerStartupStatus connecting =
                ControllerRuntimeStatusPolicy.Evaluate(Signals(synced: false,
                    alive: false, virtualConnected: false));
            Assert.AreEqual("Connecting", connecting.Title);

            ControllerStartupStatus creating =
                ControllerRuntimeStatusPolicy.Evaluate(Signals(
                    virtualConnected: false));
            Assert.AreEqual("Connected", creating.Title);
            StringAssert.Contains(creating.Detail, "Creating");
        }

        [DataTestMethod]
        [DataRow(ControllerRuntimeLaneState.Starting, ControllerRuntimeLaneState.NotRequired,
            ControllerRuntimeLaneState.NotRequired, "Arming haptics")]
        [DataRow(ControllerRuntimeLaneState.Ready, ControllerRuntimeLaneState.Starting,
            ControllerRuntimeLaneState.NotRequired, "Starting speaker")]
        [DataRow(ControllerRuntimeLaneState.Ready, ControllerRuntimeLaneState.Ready,
            ControllerRuntimeLaneState.Starting, "Starting microphone")]
        public void ReportsEachRequiredLaneBeforeReady(
            ControllerRuntimeLaneState haptics,
            ControllerRuntimeLaneState speaker,
            ControllerRuntimeLaneState microphone, string detail)
        {
            ControllerStartupStatus status =
                ControllerRuntimeStatusPolicy.Evaluate(Signals(
                    haptics: haptics, speaker: speaker,
                    microphone: microphone));
            Assert.IsFalse(status.IsReady);
            Assert.AreEqual(detail, status.Title);
        }

        [TestMethod]
        public void ReadyRequiresEveryRequestedLane()
        {
            ControllerStartupStatus status =
                ControllerRuntimeStatusPolicy.Evaluate(Signals(
                    haptics: ControllerRuntimeLaneState.Ready,
                    speaker: ControllerRuntimeLaneState.Ready,
                    microphone: ControllerRuntimeLaneState.Ready,
                    audioHaptics: ControllerRuntimeLaneState.Ready));
            Assert.IsTrue(status.IsReady);
            Assert.AreEqual("Ready", status.Title);
        }

        [TestMethod]
        public void FailedLaneRequiresAttention()
        {
            ControllerStartupStatus status =
                ControllerRuntimeStatusPolicy.Evaluate(Signals(
                    speaker: ControllerRuntimeLaneState.Unavailable));
            Assert.IsTrue(status.NeedsAttention);
            Assert.IsFalse(status.IsReady);
        }
    }
}
