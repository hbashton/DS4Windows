using DS4Windows;
using DS4Windows.InputDevices;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseBluetoothSpeakerLifecyclePolicyTests
    {
        [TestMethod]
        public void VirtualAudioEndpointGetsAFullEnumerationWindow()
        {
            int retryWindowMilliseconds =
                DualSenseAudioPassthrough.BluetoothStartRetryAttempts *
                DualSenseAudioPassthrough
                    .BluetoothStartRetryDelayMilliseconds;

            Assert.IsTrue(retryWindowMilliseconds >= 60000,
                "A freshly created VIIPER audio endpoint can take longer than ten seconds to enumerate.");
            Assert.IsTrue(DualSenseAudioPassthrough
                    .BluetoothStartRetryDelayMilliseconds <= 250,
                "Cancellation should be observed promptly while the endpoint is pending.");
        }

        private static readonly FieldInfo ConnectionTypeField =
            typeof(DS4Device).GetField("conType",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveSpeakerSessionField =
            typeof(DualSenseDevice).GetField(
                "bluetoothActiveSpeakerSession",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveSpeakerGenerationField =
            typeof(DualSenseDevice).GetField(
                "bluetoothActiveSpeakerGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic);

        [TestMethod]
        public void CooldownDoesNotConsumeSegmentAttempt()
        {
            const long now = 10_000;
            const long retryAfter = 12_000;

            Assert.IsFalse(
                DualSenseBluetoothSpeakerPassthrough.ShouldAttemptPacerLifecycle(
                    recoveryRequested: false, helperActive: false,
                    segmentAttempted: false, retryAfter, now),
                "An audible callback during cooldown consumed its only helper-start opportunity.");

            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough.ShouldAttemptPacerLifecycle(
                    recoveryRequested: false, helperActive: false,
                    segmentAttempted: false, retryAfter,
                    nowTimestamp: retryAfter),
                "The preserved attempt did not become eligible at cooldown expiry.");
        }

        [TestMethod]
        public void ActiveHelperSuppressesRedundantPrewarm()
        {
            Assert.IsFalse(
                DualSenseBluetoothSpeakerPassthrough.ShouldAttemptPacerLifecycle(
                    recoveryRequested: false, helperActive: true,
                    segmentAttempted: false, retryAfterTimestamp: 0,
                    nowTimestamp: 100));
        }

        [TestMethod]
        public void ActiveSpeakerClockAlwaysCarriesMicrophoneTransition()
        {
            Assert.IsTrue(DualSenseDevice.
                ShouldPublishMicrophoneStateThroughSpeakerClock(
                    speakerOutputEnabled: true,
                    speakerClockActive: true),
                "A prewarm-to-active race must not fall back to a draining " +
                "control-only write.");
            Assert.IsFalse(DualSenseDevice.
                ShouldPublishMicrophoneStateThroughSpeakerClock(
                    speakerOutputEnabled: true,
                    speakerClockActive: false));
            Assert.IsFalse(DualSenseDevice.
                ShouldPublishMicrophoneStateThroughSpeakerClock(
                    speakerOutputEnabled: false,
                    speakerClockActive: true));
        }

        [TestMethod]
        public void ExplicitRecoveryOverridesSegmentLatchButHonorsCooldown()
        {
            Assert.IsFalse(
                DualSenseBluetoothSpeakerPassthrough.ShouldAttemptPacerLifecycle(
                    recoveryRequested: true, helperActive: false,
                    segmentAttempted: true, retryAfterTimestamp: long.MaxValue,
                    nowTimestamp: 100));
            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough.ShouldAttemptPacerLifecycle(
                    recoveryRequested: true, helperActive: false,
                    segmentAttempted: true, retryAfterTimestamp: 100,
                    nowTimestamp: 100));
        }

        [TestMethod]
        public void RetryWaitIsCeilingRoundedAndBounded()
        {
            Assert.AreEqual(0,
                DualSenseBluetoothSpeakerPassthrough.
                    GetPacerRetryWaitMilliseconds(10, 10, 10_000));
            Assert.AreEqual(1,
                DualSenseBluetoothSpeakerPassthrough.
                    GetPacerRetryWaitMilliseconds(11, 10, 10_000));
            Assert.AreEqual(1000,
                DualSenseBluetoothSpeakerPassthrough.
                    GetPacerRetryWaitMilliseconds(100_000, 0, 10_000));
        }

        [TestMethod]
        public void StartupWarmupIsEightAcceptedCadenceReports()
        {
            Assert.AreEqual(8,
                DualSenseBluetoothSpeakerPassthrough.StartupWarmupReportCount);
            Assert.AreEqual(256.0 / 3.0,
                DualSenseBluetoothSpeakerPassthrough.
                    StartupWarmupLatencyMilliseconds, 0.0001);

            int remaining =
                DualSenseBluetoothSpeakerPassthrough.StartupWarmupReportCount;
            for (int report = 0;
                report < DualSenseBluetoothSpeakerPassthrough.
                    StartupWarmupReportCount;
                report++)
            {
                Assert.IsTrue(
                    DualSenseBluetoothSpeakerPassthrough.
                        ShouldEmitStartupWarmup(remaining,
                            lifecycleGateActive: false,
                            recoveryRequired: false));
                remaining--;
            }

            Assert.IsFalse(
                DualSenseBluetoothSpeakerPassthrough.ShouldEmitStartupWarmup(
                    remaining, lifecycleGateActive: false,
                    recoveryRequired: false),
                "Content remained gated after all eight warmup reports were accepted.");
        }

        [TestMethod]
        public void FreshActiveCaptureShortageUsesReservoirBackpressure()
        {
            Assert.AreEqual(100,
                DualSenseBluetoothSpeakerPassthrough.
                    TransientCaptureShortageLeaseMs);
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldDeferTransientCaptureShortage(true, true, true));
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldDeferTransientCaptureShortage(false, true, true));
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldDeferTransientCaptureShortage(true, false, true));
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldDeferTransientCaptureShortage(true, true, false));
        }

        [TestMethod]
        public void CaptureClockUsesBoundedFractionalCorrection()
        {
            int targetFrames = 48000 *
                DualSenseBluetoothSpeakerPassthrough.TargetBufferMs / 1000;

            Assert.AreEqual(1.0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockTargetRatio(
                        targetFrames, targetFrames),
                1.0e-12);
            Assert.AreEqual(1.0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockTargetRatio(
                        targetFrames + 2.0, targetFrames),
                1.0e-12);
            Assert.AreEqual(1.001,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockTargetRatio(
                        targetFrames + 1000.0, targetFrames),
                1.0e-12);
            Assert.AreEqual(0.999,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockTargetRatio(
                        targetFrames - 1000.0, targetFrames),
                1.0e-12);
        }

        [TestMethod]
        public void CaptureClockCorrectionSlewsWithoutPitchStep()
        {
            Assert.AreEqual(1.000002,
                DualSenseBluetoothSpeakerPassthrough.
                    SlewCaptureClockRatio(1.0, 1.001),
                1.0e-12);
            Assert.AreEqual(0.999998,
                DualSenseBluetoothSpeakerPassthrough.
                    SlewCaptureClockRatio(1.0, 0.999),
                1.0e-12);
            Assert.AreEqual(1.0000005,
                DualSenseBluetoothSpeakerPassthrough.
                    SlewCaptureClockRatio(1.0, 1.0000005),
                1.0e-12);
        }

        [TestMethod]
        public void ControllerClockFeedForwardUsesReciprocalSourceRatio()
        {
            const double controllerClockRatio = 0.999800;
            Assert.AreEqual(1.0 / controllerClockRatio,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateControllerLockedInputRateRatio(
                        controllerClockRatio, true), 1.0e-12);
            Assert.AreEqual(1.0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateControllerLockedInputRateRatio(
                        controllerClockRatio, false), 1.0e-12);
        }

        [TestMethod]
        public void IndependentSourceAndControllerClocksUseTheirQuotient()
        {
            const double sourceClockRatio = 0.999670;
            const double controllerClockRatio = 0.999800;
            Assert.AreEqual(sourceClockRatio / controllerClockRatio,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateSourceControllerLockedInputRateRatio(
                        sourceClockRatio, true,
                        controllerClockRatio, true), 1.0e-12);
            Assert.AreEqual(sourceClockRatio,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateSourceControllerLockedInputRateRatio(
                        sourceClockRatio, true,
                        controllerClockRatio, false), 1.0e-12);
        }

        [TestMethod]
        public void ProducerCadenceTracksThePhysicalPresentationClock()
        {
            const long frequency = 10_000_000;
            long nominal = DualSenseBluetoothSpeakerPassthrough.
                CalculateBluetoothSpeakerCadenceTicks(frequency, 1.0);
            long faster = DualSenseBluetoothSpeakerPassthrough.
                CalculateBluetoothSpeakerCadenceTicks(frequency, 1.001);
            long slower = DualSenseBluetoothSpeakerPassthrough.
                CalculateBluetoothSpeakerCadenceTicks(frequency, 0.999);

            Assert.IsTrue(faster < nominal);
            Assert.IsTrue(slower > nominal);
            Assert.AreEqual(106_667, nominal);
            Assert.AreEqual(106_560, faster);
            Assert.AreEqual(106_773, slower);
        }

        [DataTestMethod]
        [DataRow(8, true, false)]
        [DataRow(8, false, true)]
        [DataRow(0, false, false)]
        public void WarmupNeverRunsAcrossLifecycleGateOrAfterCompletion(
            int remaining, bool lifecycleGate, bool recovery)
        {
            Assert.IsFalse(
                DualSenseBluetoothSpeakerPassthrough.ShouldEmitStartupWarmup(
                    remaining, lifecycleGate, recovery));
        }

        [TestMethod]
        public void WarmupCanArmIdleCarrierBeforeContentArrives()
        {
            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough.ShouldEmitStartupWarmup(
                    reportsRemaining: 8, lifecycleGateActive: false,
                    recoveryRequired: false));
        }

        [TestMethod]
        public void ProducerCannotAccumulateBeyondItsLatencyReservoir()
        {
            int target = DualSenseBluetoothSpeakerPassthrough.
                PacerReservoirTargetFrames;

            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, target - 1,
                    presentedReports: 1));
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, target,
                    presentedReports: 1));
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, target + 40,
                    presentedReports: 1));
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(false, target,
                    presentedReports: 1));

            int prime = DualSenseBluetoothAudioPacer.NativePrimeReportCount;
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, prime - 1,
                    presentedReports: 0));
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, prime,
                    presentedReports: 0));
        }

        [TestMethod]
        public void EightWarmupPacketsAreValidFixedSizeCbrOpus()
        {
            var encoder = DualSenseBluetoothSpeakerPassthrough.
                CreateSpeakerOpusEncoder();
            float[] silence = new float[480 * 2];
            byte[] packet = new byte[200];

            for (int report = 0;
                report < DualSenseBluetoothSpeakerPassthrough.
                    StartupWarmupReportCount;
                report++)
            {
                Assert.AreEqual(200, encoder.Encode(silence.AsSpan(), 480,
                    packet.AsSpan(), packet.Length));
            }
        }

        [DataTestMethod]
        [DataRow(false, false, false, false)]
        [DataRow(true, false, false, true)]
        [DataRow(false, true, false, true)]
        [DataRow(false, false, true, true)]
        public void DisposeCleanupDefersUntilEveryConsumerHasExited(
            bool workerAlive, bool capturePumpAlive, bool lifecycleAlive,
            bool expected)
        {
            Assert.AreEqual(expected,
                DualSenseBluetoothSpeakerPassthrough.
                    ShouldDeferDisposeCleanup(workerAlive, capturePumpAlive,
                        lifecycleAlive));
        }

        [TestMethod]
        public void FailedFinalClearIsRetriedWithoutOverwritingNewerGeneration()
        {
            Assert.AreEqual(7L,
                DualSenseBluetoothSpeakerPassthrough.
                    SelectPacerFinalClearGenerationForRetry(
                        pendingGeneration: 0, failedGeneration: 7));
            Assert.AreEqual(11L,
                DualSenseBluetoothSpeakerPassthrough.
                    SelectPacerFinalClearGenerationForRetry(
                        pendingGeneration: 11, failedGeneration: 7),
                "Retrying a failed cleanup must not replace a newer pending generation.");
        }

        [TestMethod]
        public void StaleGenerationCannotClearReplacementSession()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            long oldSession = device.CreateBluetoothSpeakerSession();
            long replacementSession = device.CreateBluetoothSpeakerSession();
            Assert.IsTrue(device.ActivateBluetoothSpeakerSession(oldSession));
            SetFieldValue(ActiveSpeakerGenerationField, device, 7L);
            Assert.IsTrue(device.ActivateBluetoothSpeakerSession(
                replacementSession));
            SetFieldValue(ActiveSpeakerGenerationField, device, 11L);

            Assert.IsFalse(device.EndBluetoothSpeakerGeneration(oldSession, 7));
            Assert.IsFalse(device.ResetBluetoothSpeakerSession(oldSession));
            Assert.AreEqual(replacementSession, GetFieldValue<long>(
                ActiveSpeakerSessionField, device));
            Assert.AreEqual(11L, GetFieldValue<long>(
                ActiveSpeakerGenerationField, device));
        }

        [TestMethod]
        public void OlderSessionCannotReplaceNewerSession()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            long oldSession = device.CreateBluetoothSpeakerSession();
            long replacementSession = device.CreateBluetoothSpeakerSession();

            Assert.IsTrue(device.ActivateBluetoothSpeakerSession(
                replacementSession));
            Assert.IsFalse(device.ActivateBluetoothSpeakerSession(oldSession));
            Assert.AreEqual(replacementSession, GetFieldValue<long>(
                ActiveSpeakerSessionField, device));
        }

        private static DualSenseDevice CreateBluetoothDevice()
        {
            var hidDevice = (HidDevice)RuntimeHelpers.GetUninitializedObject(
                typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice,
                "Bluetooth lifecycle policy test");
            SetFieldValue(ConnectionTypeField, device, ConnectionType.BT);
            return device;
        }

        private static T GetFieldValue<T>(FieldInfo field, object instance)
        {
            Assert.IsNotNull(field);
            return (T)field.GetValue(instance);
        }

        private static void SetFieldValue(FieldInfo field, object instance,
            object value)
        {
            Assert.IsNotNull(field);
            field.SetValue(instance, value);
        }
    }
}
