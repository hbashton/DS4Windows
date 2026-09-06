using DS4Windows;
using DS4Windows.Switch2;
using System.Reflection;
using System.Runtime.CompilerServices;

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
            ControllerRuntimeLaneState audioHaptics = ControllerRuntimeLaneState.NotRequired,
            bool virtualFailed = false, bool physicalCleanupQuarantined = false) =>
            new ControllerRuntimeSignals(present, synced, alive,
                virtualRequired, virtualConnected, virtualTypeMatches,
                haptics, speaker, microphone, audioHaptics, "DualSense", virtualFailed,
                physicalCleanupQuarantined);

        [TestMethod]
        public void QuarantinedPhysicalCleanupPrecedesWaitingForInputOrVirtualReadiness()
        {
            foreach (bool virtualRequired in new[] { false, true })
            {
                ControllerStartupStatus status = ControllerRuntimeStatusPolicy.Evaluate(Signals(
                    synced: false, alive: false, virtualRequired: virtualRequired,
                    virtualConnected: false, physicalCleanupQuarantined: true));
                Assert.IsTrue(status.NeedsAttention);
                StringAssert.Contains(status.Detail, "cleanup is incomplete");
            }
            Assert.AreEqual(ControllerStartupStage.Connecting,
                ControllerRuntimeStatusPolicy.Evaluate(Signals(synced: false, alive: false)).Stage,
                "An active runtime waiting for its first frame is not a cleanup failure.");
            Assert.AreEqual(ControllerStartupStage.Disconnected,
                ControllerRuntimeStatusPolicy.Evaluate(Signals(present: false,
                    physicalCleanupQuarantined: true)).Stage,
                "A removed row must not inherit a stale failure signal.");
        }

        [TestMethod]
        [DoNotParallelize]
        public void PhysicalCleanupAttentionRequiresExactTerminalQuarantinedRegistration()
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(801, 802,
                Switch2Transport.BluetoothLe, out var device, out _));
            device.StartUpdate();
            var table = new InputControllerRegistrationTable(2);
            Assert.IsTrue(table.TryOpen(1, out _));
            var owner = new RuntimeOwner(device, device.RuntimeGeneration);
            Assert.IsTrue(InputControllerRegistration.TryCreate(device, device.RuntimeGeneration,
                InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
                out var registration, out _));
            Assert.IsTrue(table.TryReserveAndBindExactSlot(1, registration,
                out var token, out _, out _));
            Assert.IsTrue(table.TryActivate(token, out _));
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 1, table));
            Assert.IsTrue(device.TryPublishTerminalNeutral());
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 1, table),
                "Normal terminal publication while removal drains is not quarantine.");
            Assert.IsTrue(table.TryQuarantine(token,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure, out _));
            Assert.IsTrue(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 1, table));
            device.IsRemoving = true;
            Assert.IsTrue(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 1, table),
                "An exact published quarantine must remain visible while the removing flag is set.");
            var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            service.DS4Controllers = new DS4Device[] { null, device };
            service.outputDevices = new OutputDevice[2];
            void SetServiceField(string name, object value) => typeof(ControlService).
                GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, value);
            SetServiceField("inputRegistrationTable", table);
            SetServiceField("playStationFeatureOutputLock", new object());
            SetServiceField("playStationFeatureOutputDevices", new ViiperOutDevice[2]);
            ControllerRuntimeSignals retained = service.GetControllerRuntimeSignals(1);
            Assert.IsTrue(retained.PhysicalPresent);
            Assert.IsTrue(retained.PhysicalCleanupQuarantined);
            Assert.IsTrue(ControllerRuntimeStatusPolicy.Evaluate(retained).NeedsAttention);
            service.DS4Controllers[1] = null;
            ControllerRuntimeSignals removed = service.GetControllerRuntimeSignals(1);
            Assert.IsFalse(removed.PhysicalCleanupQuarantined);
            Assert.AreEqual(ControllerStartupStage.Disconnected,
                ControllerRuntimeStatusPolicy.Evaluate(removed).Stage);
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 0, table));
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, -1, table));
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 2, table));
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 1, null));
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(null, 1, table));

            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(801, 802,
                Switch2Transport.BluetoothLe, out var successor, out _));
            successor.StartUpdate();
            Assert.IsTrue(successor.TryPublishTerminalNeutral());
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(successor, 1, table),
                "Matching numeric generations and slot do not authorize a different device object.");
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(
                (DS4Device)RuntimeHelpers.GetUninitializedObject(typeof(DS4Device)), 1, table));
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ActiveOrWrongGenerationQuarantineCannotBecomeTerminalCleanupStatus(bool generationMatches)
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(811, 812,
                Switch2Transport.Usb, out var device, out _));
            device.StartUpdate();
            var table = new InputControllerRegistrationTable(1);
            Assert.IsTrue(table.TryOpen(1, out _));
            ulong registrationGeneration = generationMatches ? device.RuntimeGeneration : 999;
            var owner = new RuntimeOwner(device, registrationGeneration);
            Assert.IsTrue(InputControllerRegistration.TryCreate(device, registrationGeneration,
                InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
                out var registration, out _));
            Assert.IsTrue(table.TryReserveAndBind(registration, out var token, out _, out _));
            Assert.IsTrue(table.TryActivate(token, out _));
            Assert.IsTrue(table.TryQuarantine(token,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure, out _));
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 0, table));
            Assert.IsTrue(device.TryPublishTerminalNeutral());
            Assert.AreEqual(generationMatches,
                ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 0, table),
                "Exact object alone is insufficient without its logical generation.");
        }

        [TestMethod]
        public void UnpublishedAbortNeedsAttentionOnlyForExactQuarantinedSetup()
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(821, 822,
                Switch2Transport.BluetoothLe, out var device, out _));
            var table = new InputControllerRegistrationTable(1);
            Assert.IsTrue(table.TryOpen(1, out _));
            var owner = new RuntimeOwner(device, device.RuntimeGeneration);
            Assert.IsTrue(InputControllerRegistration.TryCreate(device, device.RuntimeGeneration,
                InputControllerOwnershipKind.Switch2Runtime, false, false, owner,
                out var registration, out _));
            Assert.IsTrue(table.TryReserveAndBind(registration, out _, out var claim, out _));
            Assert.IsTrue(device.TryAbortUnpublishedActivation());
            Assert.IsFalse(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 0, table));
            Assert.IsTrue(table.TryQuarantine(claim,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure, out _));
            Assert.IsTrue(ControllerRuntimeStatusPolicy.HasQuarantinedPhysicalRuntime(device, 0, table));
        }

        private sealed class RuntimeOwner(DS4Device device, ulong generation) : IInputControllerRegistrationOwner
        {
            public InputControllerOwnershipKind Kind => InputControllerOwnershipKind.Switch2Runtime;
            public bool Authenticates(DS4Device candidate, ulong candidateGeneration) =>
                ReferenceEquals(device, candidate) && generation == candidateGeneration;
            public bool TryStopAndQuiesce(DS4Device candidate, ulong candidateGeneration,
                int timeoutMilliseconds, out InputControllerOwnerOperationFailure failure) =>
                throw new InvalidOperationException("A status observation must not stop physical ownership.");
            public bool TryRemove(DS4Device candidate, ulong candidateGeneration,
                out InputControllerOwnerOperationFailure failure) =>
                throw new InvalidOperationException("A status observation must not remove physical ownership.");
        }

        [TestMethod]
        public void FailedVirtualCreationOrStreamNeedsAttentionInsteadOfEndlessCreatingOrReady()
        {
            Assert.IsTrue(ControllerRuntimeStatusPolicy.Evaluate(Signals(
                virtualConnected: false, virtualFailed: true)).NeedsAttention);
            Assert.IsTrue(ControllerRuntimeStatusPolicy.Evaluate(Signals(
                virtualConnected: true, virtualFailed: true)).NeedsAttention,
                "A retained ownership handle is not proof that its stream still works.");
            Assert.IsTrue(ControllerRuntimeStatusPolicy.Evaluate(Signals(
                virtualRequired: false, virtualFailed: true)).IsReady,
                "A no-output profile must not inherit a prior virtual-pad failure.");
            Assert.AreEqual(ControllerStartupStage.Disconnected,
                ControllerRuntimeStatusPolicy.Evaluate(Signals(
                    present: false, virtualFailed: true)).Stage);
        }

        [TestMethod]
        public void OutputAttemptFailureBelongsOnlyToExactPhysicalDeviceAndRequestedType()
        {
            var first = (DS4Device)RuntimeHelpers.GetUninitializedObject(typeof(DS4Device));
            var replacement = (DS4Device)RuntimeHelpers.GetUninitializedObject(typeof(DS4Device));
            var oldAttempt = new ControllerVirtualOutputAttempt(first, OutContType.ViiperXboxOne);
            oldAttempt.MarkFailed();
            Assert.IsTrue(oldAttempt.Failed);
            Assert.IsTrue(oldAttempt.Matches(first, OutContType.ViiperXboxOne, true));
            Assert.IsFalse(oldAttempt.Matches(replacement, OutContType.ViiperXboxOne, true));
            Assert.IsFalse(oldAttempt.Matches(first, OutContType.ViiperDualSense, true));
            Assert.IsFalse(oldAttempt.Matches(first, OutContType.ViiperXboxOne, false));
            Assert.IsFalse(oldAttempt.Matches(null, OutContType.ViiperXboxOne, true));
            var retry = new ControllerVirtualOutputAttempt(first, OutContType.ViiperXboxOne);
            oldAttempt.MarkFailed();
            Assert.IsFalse(retry.Failed, "Late completion of an old attempt cannot fault its replacement.");
        }

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

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Switch2PhysicalReportUnblocksConnectingWithoutBypassingVirtualReadiness(bool bluetooth)
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(701, 702,
                bluetooth ? Switch2Transport.BluetoothLe : Switch2Transport.Usb,
                out var device, out _));
            device.StartUpdate(); // Synthetic runtime, no transport IO.
            ControllerStartupStatus Evaluate(bool virtualConnected = true) =>
                ControllerRuntimeStatusPolicy.Evaluate(Signals(
                    synced: device.isSynced(), alive: device.IsAlive(),
                    virtualConnected: virtualConnected));
            Assert.AreEqual(ControllerStartupStage.Connecting, Evaluate().Stage,
                "Opening a runtime is not evidence of physical input.");
            Assert.IsFalse(device.TryPublishPro(Switch2RuntimeInputDeviceTests.CreateProFrame(999, 702, 0,
                bluetoothLe: bluetooth)));
            Assert.AreEqual(ControllerStartupStage.Connecting, Evaluate().Stage);
            Assert.IsTrue(device.TryPublishPro(Switch2RuntimeInputDeviceTests.CreateProFrame(701, 702, 0,
                bluetoothLe: bluetooth)));
            Assert.IsTrue(Evaluate().IsReady,
                "A validated Switch 2 frame must not depend on a DualShock-specific HID byte.");
            Assert.AreEqual(ControllerStartupStage.CreatingVirtualController, Evaluate(false).Stage,
                "Physical readiness alone cannot certify a virtual pad.");
            Assert.IsTrue(device.TryPublishTerminalNeutral());
            Assert.IsFalse(Evaluate().IsReady,
                "Synthetic terminal neutral must revoke, not establish, physical readiness.");
        }
    }
}
