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
        public void StartupWarmupIsOneEightReportFifoTransfer()
        {
            Assert.AreEqual(8,
                DualSenseBluetoothSpeakerPassthrough.StartupWarmupReportCount);
            Assert.AreEqual(0.0,
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
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.NativePrimeReportCount <=
                DualSenseBluetoothAudioPacer.SingleAudioTransportSlotCount,
                "The one-time prime must fit atomically in the native transport's strict FIFO.");
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
        public void RealtimeCaptureDropsOnlyStaleHistoryAfterCeiling()
        {
            int targetFrames = 48000 *
                DualSenseBluetoothSpeakerPassthrough.TargetBufferMs / 1000;
            int retainFrames = 48000 *
                DualSenseBluetoothSpeakerPassthrough.
                    RealtimeCaptureRetainMs / 1000;
            int ceilingFrames = 48000 *
                DualSenseBluetoothSpeakerPassthrough.
                    RealtimeCaptureLatencyCeilingMs / 1000;
            const int callbackFrames = 480;

            Assert.AreEqual(0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateRealtimeCaptureFramesToDiscard(
                        targetFrames, callbackFrames));
            Assert.AreEqual(0,
                DualSenseBluetoothSpeakerPassthrough.
                    CalculateRealtimeCaptureFramesToDiscard(
                        ceilingFrames - callbackFrames, callbackFrames));

            int bufferedFrames = ceilingFrames - callbackFrames + 1;
            int discarded = DualSenseBluetoothSpeakerPassthrough.
                CalculateRealtimeCaptureFramesToDiscard(
                    bufferedFrames, callbackFrames);
            Assert.AreEqual(bufferedFrames -
                (retainFrames - callbackFrames), discarded);
            Assert.AreEqual(retainFrames,
                bufferedFrames - discarded + callbackFrames);
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
        public void LegacyProducerCannotAccumulateBeyondItsLatencyReservoir()
        {
            int target = DualSenseBluetoothSpeakerPassthrough.
                PacerReservoirTargetFrames;
            int prime = DualSenseBluetoothAudioPacer.NativePrimeReportCount;

            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, target - 1,
                    usesV5Source: false, presentedReports: prime));
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, target,
                    usesV5Source: false, presentedReports: prime));
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, target + 40,
                    usesV5Source: false, presentedReports: prime));
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(false, target,
                    usesV5Source: false, presentedReports: prime));

            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, prime - 1,
                    usesV5Source: false, presentedReports: 0));
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, prime - 1,
                    usesV5Source: false, presentedReports: 1));
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, prime - 1,
                    usesV5Source: false, presentedReports: prime - 1));
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, prime,
                    usesV5Source: false, presentedReports: prime - 1));
        }

        [TestMethod]
        public void V5SourceCatchesUpSevenGenerationsAfterSeventyTwoMillisecondStall()
        {
            const int retainedGenerations = 7;
            int target = DualSenseBluetoothSpeakerPassthrough.
                V5SourceReservoirTargetFrames;

            Assert.AreEqual(
                DualSenseBluetoothSpeakerPassthrough.StartupWarmupReportCount,
                target,
                "The post-stall live window must match the native transport's bounded " +
                "eight-generation source FIFO.");

            // A roughly 72 ms host-thread drought retains seven complete
            // 10.667 ms source generations. None may wait for a preceding
            // helper acknowledgement when the producer resumes.
            for (int pending = 0; pending < retainedGenerations; pending++)
            {
                Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                    ShouldBackpressurePacerProducer(true, pending,
                        usesV5Source: true, presentedReports: 1),
                    $"Retained generation {pending + 1} was not admitted " +
                    "during post-stall catch-up.");
            }

            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, retainedGenerations,
                    usesV5Source: true, presentedReports: 1),
                "Seven retained generations must remain admitted and ordered.");
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldBackpressurePacerProducer(true, target,
                    usesV5Source: true, presentedReports: 1),
                "Catch-up must remain bounded to the newest eight generations.");
        }

        [TestMethod]
        public void V5PrimeKeepsIdleCarrierUntilEightReportsCanBeBuilt()
        {
            const int sourceFramesPerBlock = 480;
            int bufferedFrames = 0;
            int requiredFrames =
                DualSenseBluetoothSpeakerPassthrough.
                    V5InitialSourceBufferFrames;
            int sourceBlocks = (requiredFrames + sourceFramesPerBlock - 1) /
                sourceFramesPerBlock;

            for (int block = 1;
                block <= sourceBlocks;
                block++)
            {
                bufferedFrames += sourceFramesPerBlock;
                bool captureReady = bufferedFrames >= requiredFrames;
                bool sourcePrimePending = !captureReady;

                Assert.AreEqual(block < sourceBlocks,
                    DualSenseBluetoothSpeakerPassthrough.
                        ShouldMaintainIdleCarrierDuringV5Prime(
                            usesV5Source: true, sourcePrimePending,
                            captureReady, sourceRecentlyActive: true),
                    $"V5 source block {block} broke the continuous " +
                    "carrier-to-content handoff.");
            }
        }

        [DataTestMethod]
        [DataRow(false, true, false)]
        [DataRow(true, false, false)]
        [DataRow(true, true, true)]
        public void LegacyAndActiveSourcesNeverUseV5PrimeCarrier(
            bool usesV5Source, bool sourcePrimePending,
            bool captureReady)
        {
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldMaintainIdleCarrierDuringV5Prime(
                    usesV5Source, sourcePrimePending, captureReady,
                    sourceRecentlyActive: true));
        }

        [TestMethod]
        public void V5CallbackDroughtUsesIdleCarrierWithoutEndingGeneration()
        {
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldEmitV5IdleCarrier(
                    usesV5Source: true,
                    sourceRecentlyActive: false),
                "A transient callback drought should use the armed idle " +
                "carrier without declaring the source generation ended.");
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldEmitV5IdleCarrier(
                    usesV5Source: true,
                    sourceRecentlyActive: true),
                "A callback that wins the freshness recheck must be retried " +
                "instead of being replaced by an idle carrier.");
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldEmitV5IdleCarrier(
                    usesV5Source: false,
                    sourceRecentlyActive: false),
                "Legacy capture transports must keep their existing policy.");
        }

        [TestMethod]
        public void V5ReturningCallbackResetsOnlyAfterHardGenerationGap()
        {
            const long timestampFrequency = 10_000_000;
            const long previousCallback = timestampFrequency;
            long transientReturn = previousCallback +
                timestampFrequency *
                DualSenseBluetoothSpeakerPassthrough.
                    TransientCaptureShortageLeaseMs / 1000;
            long lastContinuousReturn = previousCallback +
                timestampFrequency *
                DualSenseBluetoothSpeakerPassthrough.
                    V5HardSourceDiscontinuityMs / 1000 - 1;
            long hardGenerationReturn = previousCallback +
                timestampFrequency *
                DualSenseBluetoothSpeakerPassthrough.
                    V5HardSourceDiscontinuityMs / 1000;

            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough.
                    V5HardSourceDiscontinuityMs >
                DualSenseBluetoothSpeakerPassthrough.
                    TransientCaptureShortageLeaseMs,
                "The hard generation boundary must not reinterpret a normal " +
                "100 ms source shortage as a reset.");
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldResetV5SourceBeforeAppendingCallback(
                    usesV5Source: true, previousCallback,
                    transientReturn, timestampFrequency),
                "A 100 ms V5 callback drought must preserve the existing " +
                "ring and fractional resampler history.");
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldResetV5SourceBeforeAppendingCallback(
                    usesV5Source: true, previousCallback,
                    lastContinuousReturn, timestampFrequency),
                "V5 state must remain continuous until the hard generation " +
                "boundary is reached.");
            Assert.IsTrue(DualSenseBluetoothSpeakerPassthrough.
                ShouldResetV5SourceBeforeAppendingCallback(
                    usesV5Source: true, previousCallback,
                    hardGenerationReturn, timestampFrequency),
                "The first callback after a hard V5 gap must clear stale " +
                "source state before its new PCM is appended.");
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldResetV5SourceBeforeAppendingCallback(
                    usesV5Source: false, previousCallback,
                    hardGenerationReturn, timestampFrequency),
                "Legacy and non-V5 routes must not inherit the new reset " +
                "policy.");
            Assert.IsFalse(DualSenseBluetoothSpeakerPassthrough.
                ShouldResetV5SourceBeforeAppendingCallback(
                    usesV5Source: true, previousCallbackTimestamp: 0,
                    callbackTimestamp: hardGenerationReturn,
                    timestampFrequency),
                "The first callback has no prior generation to discard.");
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
