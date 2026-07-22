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
        public void StartupWarmupIsSixAcceptedCadenceReports()
        {
            Assert.AreEqual(6,
                DualSenseBluetoothSpeakerPassthrough.StartupWarmupReportCount);
            Assert.AreEqual(64.0,
                DualSenseBluetoothSpeakerPassthrough.
                    StartupWarmupLatencyMilliseconds, 0.0001);

            int remaining =
                DualSenseBluetoothSpeakerPassthrough.StartupWarmupReportCount;
            for (int report = 0; report < 6; report++)
            {
                Assert.IsTrue(
                    DualSenseBluetoothSpeakerPassthrough.
                        ShouldEmitStartupWarmup(remaining,
                            lifecycleGateActive: false,
                            recoveryRequired: false,
                            captureReady: true));
                remaining--;
            }

            Assert.IsFalse(
                DualSenseBluetoothSpeakerPassthrough.ShouldEmitStartupWarmup(
                    remaining, lifecycleGateActive: false,
                    recoveryRequired: false, captureReady: true),
                "Content remained gated after all six warmup reports were accepted.");
        }

        [TestMethod]
        public void FreshActiveCaptureShortageUsesReservoirBackpressure()
        {
            Assert.AreEqual(50,
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
        public void CaptureClockUsesPadForgeTwentyMillisecondDeadbandTrim()
        {
            int targetFrames = 48000 *
                DualSenseBluetoothSpeakerPassthrough.TargetBufferMs / 1000;

            Assert.AreEqual(1.0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockRatio(targetFrames, targetFrames),
                1.0e-12);
            Assert.AreEqual(1.0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockRatio(
                        targetFrames + 240, targetFrames),
                1.0e-12);
            Assert.AreEqual(516.0 / 512.0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockRatio(
                        targetFrames + 241, targetFrames),
                1.0e-12);
            Assert.AreEqual(508.0 / 512.0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateCaptureClockRatio(
                        targetFrames - 241, targetFrames),
                1.0e-12);
        }

        [DataTestMethod]
        [DataRow(6, true, false, true)]
        [DataRow(6, false, true, true)]
        [DataRow(6, false, false, false)]
        public void WarmupNeverRunsAcrossLifecycleGateOrWithoutContentReady(
            int remaining, bool lifecycleGate, bool recovery,
            bool captureReady)
        {
            Assert.IsFalse(
                DualSenseBluetoothSpeakerPassthrough.ShouldEmitStartupWarmup(
                    remaining, lifecycleGate, recovery, captureReady));
        }

        [TestMethod]
        public void SixWarmupPacketsAreValidFixedSizeCbrOpus()
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
