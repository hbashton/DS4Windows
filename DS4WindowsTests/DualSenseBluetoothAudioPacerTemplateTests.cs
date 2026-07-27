using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseBluetoothAudioPacerTemplateTests
    {
        private const int ReportLength = 398;
        private const int HapticsOffset = 78;
        private const int HapticsLength = 64;

        [TestMethod]
        public void FreshLatestTemplateHapticsSurviveOldQueuedAudioFrame()
        {
            const long qpcFrequency = 10_000_000;
            const long nowQpc = qpcFrequency * 10;
            long queuedAtQpc = nowQpc - qpcFrequency * 85 / 1000;
            long oldQueuedHapticsExpiryQpc = queuedAtQpc +
                qpcFrequency * 30 / 1000;
            long latestTemplateHapticsExpiryQpc = nowQpc +
                qpcFrequency * 30 / 1000;
            Assert.IsTrue(oldQueuedHapticsExpiryQpc < nowQpc,
                "The fixture must represent an 85 ms-old queued audio frame.");

            byte[] queued = CreateReport(0x31);
            byte[] latestTemplate = CreateReport(0x72);
            FillHaptics(latestTemplate, 0x40);
            byte expectedSequence = queued[1];
            byte expectedPacketCounter = queued[10];
            byte[] expectedSpeaker = CopyRange(queued, 142, 202);

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, oldQueuedHapticsExpiryQpc, latestTemplate,
                latestTemplateHapticsExpiryQpc, nowQpc);

            for (int index = 0; index < HapticsLength; index++)
            {
                Assert.AreEqual(latestTemplate[HapticsOffset + index],
                    queued[HapticsOffset + index],
                    $"Fresh latest-template haptics changed at byte {index}.");
            }

            Assert.AreEqual(expectedSequence, queued[1]);
            Assert.AreEqual(expectedPacketCounter, queued[10]);
            CollectionAssert.AreEqual(expectedSpeaker,
                CopyRange(queued, 142, 202));
            AssertCrcIsValid(queued);
        }

        [TestMethod]
        public void StaleLatestTemplateHapticsAreZeroed()
        {
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);
            FillHaptics(latestTemplate, 0x55);
            long stillFreshQueuedHapticsExpiryQpc = nowQpc + 1_000_000;

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, stillFreshQueuedHapticsExpiryQpc, latestTemplate,
                nowQpc - 1, nowQpc);

            for (int index = 0; index < HapticsLength; index++)
            {
                Assert.AreEqual((byte)0, queued[HapticsOffset + index],
                    $"Stale latest-template haptics survived at byte {index}.");
            }

            AssertCrcIsValid(queued);
        }

        [TestMethod]
        public void NativeLatestTemplateHapticsRemainUntilGameSendsSilence()
        {
            const long nowQpc = 500_000_000;
            byte[] queued = CreateReport(0x34);
            byte[] latestTemplate = CreateReport(0x79);
            FillHaptics(latestTemplate, 0x2C);

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, long.MaxValue, nowQpc);

            for (int index = 0; index < HapticsLength; index++)
            {
                Assert.AreEqual(latestTemplate[HapticsOffset + index],
                    queued[HapticsOffset + index],
                    $"Native haptics were muted by wall-clock age at byte {index}.");
            }

            AssertCrcIsValid(queued);
        }

        [TestMethod]
        public void QueuedSpeakerBufferDepthSurvivesControlTemplateOverlay()
        {
            const byte speakerBufferDepth = 64;
            const byte controlBufferDepth = 16;
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);
            for (int index = 5; index <= 9; index++)
            {
                queued[index] = speakerBufferDepth;
                latestTemplate[index] = controlBufferDepth;
            }

            byte[] expectedBufferDepths = CopyRange(queued, 5, 5);

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, nowQpc + 1, nowQpc);

            CollectionAssert.AreEqual(expectedBufferDepths,
                CopyRange(queued, 5, 5),
                "Presentation replaced the queued speaker buffer depth with the control template depth.");
            Assert.AreEqual(latestTemplate[2], queued[2]);
            Assert.AreEqual(latestTemplate[3], queued[3]);
            Assert.AreEqual(latestTemplate[4], queued[4]);
            Assert.AreEqual(latestTemplate[11], queued[11]);
            Assert.AreEqual(latestTemplate[141], queued[141]);
            AssertCrcIsValid(queued);
        }

        [TestMethod]
        public void SpeakerMicrophoneSnapshotSurvivesControlTemplateOverlay()
        {
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);

            // The ordinary speaker snapshot has already stripped these
            // one-shot controller-state fields. The periodically refreshed
            // template still contains them.
            queued[13] &= unchecked((byte)~0x40);
            queued[14] &= unchecked((byte)~0x01);
            queued[19] = 0;
            queued[21] = 0;
            latestTemplate[13] |= 0x40;
            latestTemplate[14] |= 0x01;
            latestTemplate[19] = 0x40;
            latestTemplate[21] = 0x01;
            latestTemplate[57] = 0xA5;

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, nowQpc + 1, nowQpc);

            Assert.AreEqual((byte)0, (byte)(queued[13] & 0x40));
            Assert.AreEqual((byte)0, (byte)(queued[14] & 0x01));
            Assert.AreEqual((byte)0, queued[19]);
            Assert.AreEqual((byte)0, queued[21]);
            Assert.AreEqual((byte)0xA5, queued[57],
                "Unrelated current controller state must still be overlaid.");
            AssertCrcIsValid(queued);
        }

        [TestMethod]
        public void PendingMicrophoneControlSurvivesControlTemplateOverlay()
        {
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);
            queued[13] |= 0x40;
            queued[14] |= 0x01;
            queued[19] = 0x2F;
            queued[21] = 0x01;
            latestTemplate[13] &= unchecked((byte)~0x40);
            latestTemplate[14] &= unchecked((byte)~0x01);
            latestTemplate[19] = 0;
            latestTemplate[21] = 0;

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, nowQpc + 1, nowQpc);

            Assert.AreEqual((byte)0x40, (byte)(queued[13] & 0x40));
            Assert.AreEqual((byte)0x01, (byte)(queued[14] & 0x01));
            Assert.AreEqual((byte)0x2F, queued[19]);
            Assert.AreEqual((byte)0x01, queued[21]);
            AssertCrcIsValid(queued);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(500)]
        [DataRow(1000)]
        public void SchedulerRetainsPhaseForAtMostOneMillisecondLateness(
            int latenessMicroseconds)
        {
            const long qpcFrequency = 10_000_000;
            const long firstCadenceTicks = 106_666;
            const long maximumCatchUpTicks = qpcFrequency / 1000;
            const long startQpc = 1_000_000;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.Start(startQpc);
            long presentationQpc = startQpc +
                latenessMicroseconds * qpcFrequency / 1_000_000;

            long nextDeadline = scheduler.AdvanceAfterSend(presentationQpc);

            Assert.AreEqual(startQpc + firstCadenceTicks, nextDeadline,
                "Normal scheduler jitter must remain locked to the rational phase.");
            Assert.IsTrue(nextDeadline - presentationQpc >=
                firstCadenceTicks - maximumCatchUpTicks,
                "Phase lock must never compress catch-up by more than one millisecond.");
        }

        [DataTestMethod]
        [DataRow(5)]
        [DataRow(10)]
        [DataRow(20)]
        public void SchedulerRetainsSourceClockAcrossSendReadinessLateness(
            int latenessMilliseconds)
        {
            const long qpcFrequency = 10_000_000;
            const long firstCadenceTicks = 106_666;
            const long startQpc = 1_000_000;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.Start(startQpc);
            long presentationQpc = startQpc +
                latenessMilliseconds * qpcFrequency / 1000;

            long nextDeadline = scheduler.AdvanceAfterSend(presentationQpc);

            Assert.AreEqual(startQpc + firstCadenceTicks, nextDeadline,
                "A send-readiness stall changed the absolute source clock.");
            Assert.AreEqual(firstCadenceTicks -
                latenessMilliseconds * qpcFrequency / 1000,
                nextDeadline - presentationQpc,
                "The scheduler did not retain the measured readiness debt for bounded catch-up.");
        }

        [TestMethod]
        public void PairedWriterUsesDs5DongleQueueDepth()
        {
            Assert.AreEqual(10,
                DualSenseBluetoothAudioPacer.PairedWriterQueueDepth,
                "DS5Dongle keeps ten complete L2CAP reports in its send FIFO.");
        }

        [TestMethod]
        public void SchedulerPreservesExactRationalCadenceThroughSubMillisecondJitter()
        {
            const long qpcFrequency = 10_000_000;
            const int intervals = 3000;
            long[] deterministicJitterTicks =
            {
                0,
                qpcFrequency / 10_000,
                qpcFrequency * 3 / 10_000,
                qpcFrequency * 5 / 10_000,
                qpcFrequency * 9 / 10_000,
            };
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.Start(0);

            for (int index = 0; index < intervals; index++)
            {
                long presentationQpc = scheduler.NextDeadlineQpc +
                    deterministicJitterTicks[index %
                        deterministicJitterTicks.Length];
                scheduler.AdvanceAfterSend(presentationQpc);
            }

            Assert.AreEqual(qpcFrequency * 32L,
                scheduler.NextDeadlineQpc,
                "Sub-millisecond jitter must not accumulate phase or average-cadence error.");
        }

        [TestMethod]
        public void SchedulerTracksFractionalControllerClockWithoutDrift()
        {
            const long qpcFrequency = 10_000_000;
            const double controllerClockRatio = 0.999800;
            const int intervals = 3000;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.SetRateRatio(controllerClockRatio);
            scheduler.Start(0);

            for (int index = 0; index < intervals; index++)
            {
                scheduler.AdvanceAfterSend(scheduler.NextDeadlineQpc);
            }

            double expected = qpcFrequency * 32.0 /
                controllerClockRatio;
            Assert.AreEqual(expected, scheduler.NextDeadlineQpc, 1.0,
                "Fractional correction accumulated report-clock drift.");
        }

        [TestMethod]
        public void PairedSchedulerDoesNotReplayLegacyReserveTransfer()
        {
            const long qpcFrequency = 10_000_000;
            const long startQpc = 1_000_000;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.Start(startQpc,
                DualSenseBluetoothAudioPacer.ControllerLinkWarmupIntervals,
                DualSenseBluetoothAudioPacer.ControllerReserveTransferIntervals);

            Assert.AreEqual(0,
                DualSenseBluetoothAudioPacer.ControllerLinkWarmupIntervals);
            Assert.AreEqual(0,
                DualSenseBluetoothAudioPacer.ControllerReserveTransferIntervals);
            for (int index = 0; index < 128; index++)
            {
                long previous = scheduler.NextDeadlineQpc;
                scheduler.AdvanceAfterSend(previous);
                long interval = scheduler.NextDeadlineQpc - previous;
                Assert.IsTrue(interval == 106_666 || interval == 106_667,
                    "The paired path replayed a 10 ms startup burst.");
            }
        }

        [TestMethod]
        public void PairedSchedulerStartsAtNativeCadenceImmediately()
        {
            const long qpcFrequency = 10_000_000;
            const long startQpc = 1_000_000;
            int warmupIntervals =
                DualSenseBluetoothAudioPacer.ControllerLinkWarmupIntervals;
            int transferIntervals =
                DualSenseBluetoothAudioPacer.ControllerReserveTransferIntervals;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.Start(startQpc, warmupIntervals, transferIntervals);

            Assert.AreEqual(0, warmupIntervals);
            Assert.AreEqual(0, transferIntervals);
            long previous = scheduler.NextDeadlineQpc;
            scheduler.AdvanceAfterSend(previous);
            long nominalInterval = scheduler.NextDeadlineQpc - previous;
            Assert.IsTrue(nominalInterval == 106_666 ||
                nominalInterval == 106_667);
        }

        [TestMethod]
        public void SchedulerKeepsContinuousAudioClockWhenInputArrivalPhaseMoves()
        {
            const long qpcFrequency = 10_000_000;
            const long startQpc = 20_000_000;
            const int intervals = 3000;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.Start(startQpc);

            for (int index = 0; index < intervals; index++)
            {
                // Model the latest HID arrival wandering across the audio
                // cadence. It is useful for measuring the controller clock,
                // but must not move an already scheduled audio deadline.
                scheduler.SetInputPhaseReference(startQpc + index * 12_500L +
                    ((index % 7) - 3) * 997L);
                long idealDeadline = scheduler.NextDeadlineQpc;
                long presentationDeadline =
                    scheduler.PresentationDeadlineQpc;
                Assert.AreEqual(idealDeadline, presentationDeadline,
                    "HID arrival jitter leaked into the audio presentation clock.");
                scheduler.AdvanceAfterSend(presentationDeadline);
            }

            Assert.AreEqual(startQpc + qpcFrequency * 32L,
                scheduler.NextDeadlineQpc,
                "The continuous scheduler changed the exact average audio cadence.");
        }

        [TestMethod]
        public void CleanStopBarrierRequiresExplicitReleasedOwnershipAck()
        {
            Assert.IsFalse(DualSenseBluetoothAudioPacer.IsCleanStopBarrier(
                stopSignalReceived: false, cleanStopAcknowledged: false));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.IsCleanStopBarrier(
                stopSignalReceived: true, cleanStopAcknowledged: false));
            Assert.IsTrue(DualSenseBluetoothAudioPacer.IsCleanStopBarrier(
                stopSignalReceived: true, cleanStopAcknowledged: true));
        }

        [TestMethod]
        public void HelperCannotPublishStoppedBeforeWorkersAndWriterRetire()
        {
            Assert.IsFalse(DualSenseBluetoothAudioPacer.CanPublishStopped(
                pacerThreadStopped: false, acknowledgementThreadStopped: true,
                transportReleased: true));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.CanPublishStopped(
                pacerThreadStopped: true, acknowledgementThreadStopped: false,
                transportReleased: true));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.CanPublishStopped(
                pacerThreadStopped: true, acknowledgementThreadStopped: true,
                transportReleased: false));
            Assert.IsTrue(DualSenseBluetoothAudioPacer.CanPublishStopped(
                pacerThreadStopped: true, acknowledgementThreadStopped: true,
                transportReleased: true));
        }

        [TestMethod]
        public void OnlyTransportFaultAcknowledgementIsFatal()
        {
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.IsFatalAcknowledgementDisposition(
                    DualSenseBluetoothAudioPacer.AcknowledgementDisposition
                        .TransportFault));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.IsFatalAcknowledgementDisposition(
                    DualSenseBluetoothAudioPacer.AcknowledgementDisposition
                        .Rejected));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.IsFatalAcknowledgementDisposition(
                    DualSenseBluetoothAudioPacer.AcknowledgementDisposition
                        .Presented));
        }

        [TestMethod]
        public void SpeakerFreeControlReportBypassesAudioPrimeGate()
        {
            byte[] control = CreateReport(0x25);
            control[142] = 0;
            control[143] = 0;

            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(control));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true, speakerReportCount: 0,
                    nextReport: control),
                "A completion-aware mic/haptics control must not wait for a paired audio report.");
        }

        [TestMethod]
        public void SpeakerAudioRequiresExactlyOneCompletePairedReport()
        {
            byte[] speaker = CreateReport(0x52);

            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(speaker));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.PrimeReportCount - 1,
                    nextReport: speaker));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.PrimeReportCount,
                    nextReport: speaker));
        }

        [TestMethod]
        public void PairedSpeakerAudioNeverPresentsAnUnpairedHalf()
        {
            byte[] speaker = CreateReport(0x52);

            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.CanPresentFromTransportGate(
                    primeRequired: false, speakerReportCount: 1,
                    nextReport: speaker));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromTransportGate(
                    primeRequired: false, speakerReportCount: 2,
                    nextReport: speaker));
        }

        [TestMethod]
        public void HeadsetAudioUsesTheSamePrimeReservoirAsSpeakerAudio()
        {
            byte[] headset = CreateReport(0x52);
            headset[142] = 0x96;

            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(headset));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.IsHeadsetAudioReport(headset));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.PrimeReportCount - 1,
                    nextReport: headset));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.PrimeReportCount,
                    nextReport: headset));
        }

        [TestMethod]
        public void LowLatencyProducerReservoirCoversMeasuredWindowsCallbackStall()
        {
            double presentationReserveMilliseconds =
                DualSenseBluetoothSpeakerPassthrough
                    .PacerReservoirTargetFrames * 1000.0 *
                DualSenseBluetoothAudioPacerScheduler.CadenceNumerator /
                DualSenseBluetoothAudioPacerScheduler.CadenceDenominator;
            double protectedMilliseconds = presentationReserveMilliseconds +
                DualSenseBluetoothSpeakerPassthrough.TargetBufferMs;

            Assert.IsTrue(protectedMilliseconds >= 100.0,
                $"The combined reserve covers only " +
                $"{protectedMilliseconds:F1} ms; the measured callback stall " +
                "was 86.7 ms and requires scheduling margin.");
            Assert.IsTrue(protectedMilliseconds <= 130.0,
                $"The low-latency path still buffers " +
                $"{protectedMilliseconds:F1} ms before presentation.");
            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough.InitialBufferMs <= 32,
                "Startup must not add a second long media-style prebuffer.");
            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough
                    .LowLatencyCaptureBufferMs <= 5,
                "Loopback capture should request a sub-10 ms period.");
            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough
                    .PacerReservoirTargetFrames >
                DualSenseBluetoothAudioPacer.PrimeReportCount,
                "The steady-state reserve must survive a stall without " +
                "falling through the helper's empty-reservoir re-prime gate.");
        }

        [TestMethod]
        public void ControlReportDoesNotCountAsSpeakerPrimeCredit()
        {
            byte[] speaker = CreateReport(0x41);

            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.PrimeReportCount - 1,
                    nextReport: speaker),
                "One speaker frame plus a control report must not satisfy the two-frame pair gate.");
        }

        [TestMethod]
        public void ControlPrimeBypassKeepsAudioPrimeArmed()
        {
            byte[] control = CreateReport(0x37);
            control[142] = 0;
            control[143] = 0;

            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true, speakerReportCount: 0,
                    nextReport: control));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.
                    ShouldRequireAudioPrimeAfterPresentation(
                        presentedControlReport: true,
                        remainingReportCount:
                            DualSenseBluetoothAudioPacer.PrimeReportCount),
                "A control commit must hard re-prime audio even when speaker reports remain queued.");
        }

        [TestMethod]
        public void EmptySourceQueueDoesNotRestartPairedAudioClock()
        {
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldRequireAudioPrimeAfterPresentation(
                        presentedControlReport: false,
                        remainingReportCount: 0),
                "DS5Dongle waits for the next complete pair without replaying startup pacing.");
        }

        [TestMethod]
        public void PrimeCountsOnlyConsecutiveHeadSpeakerReports()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<byte[]>(9);
            for (int index = 0;
                index < DualSenseBluetoothAudioPacer.PrimeReportCount - 1;
                index++)
            {
                Assert.IsTrue(ring.TryEnqueue(CreateReport((byte)index)));
            }
            byte[] control = CreateReport(0x60);
            control[142] = 0;
            control[143] = 0;
            Assert.IsTrue(ring.TryEnqueue(control));
            Assert.IsTrue(ring.TryEnqueue(CreateReport(0x61)));

            int leadingSpeakers = ring.CountLeading(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport);
            Assert.AreEqual(DualSenseBluetoothAudioPacer.PrimeReportCount - 1,
                leadingSpeakers);
            Assert.IsTrue(ring.TryPeek(out byte[] first));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount: leadingSpeakers,
                    nextReport: first),
                "A speaker after an intervening control must not complete the leading audio prime.");
        }

        [TestMethod]
        public void PartialSpeakerPrimeCanYieldToControlWithoutReorderingControls()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<byte[]>(8);
            for (int index = 0; index < 4; index++)
            {
                Assert.IsTrue(ring.TryEnqueue(CreateReport((byte)index)));
            }
            byte[] firstControl = CreateReport(0x70);
            firstControl[142] = 0;
            firstControl[143] = 0;
            Assert.IsTrue(ring.TryEnqueue(firstControl));
            byte[] secondControl = CreateReport(0x71);
            secondControl[142] = 0;
            secondControl[143] = 0;
            Assert.IsTrue(ring.TryEnqueue(secondControl));

            var removed = ring.RemoveWhere(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport);

            Assert.AreEqual(4, removed.Count);
            Assert.IsTrue(ring.TryDequeue(out byte[] retainedFirst));
            Assert.AreSame(firstControl, retainedFirst);
            Assert.IsTrue(ring.TryDequeue(out byte[] retainedSecond));
            Assert.AreSame(secondControl, retainedSecond);
        }

        private static byte[] CreateReport(byte seed)
        {
            byte[] report = new byte[ReportLength];
            for (int index = 0; index < ReportLength - sizeof(uint); index++)
            {
                report[index] = (byte)(seed + index * 17);
            }

            report[0] = 0x36;
            report[142] = 0x93;
            report[143] = 200;
            return report;
        }

        private static void FillHaptics(byte[] report, byte seed)
        {
            report[76] = 0x92;
            report[77] = HapticsLength;
            for (int index = 0; index < HapticsLength; index++)
            {
                report[HapticsOffset + index] = (byte)(seed + index);
            }
        }

        private static byte[] CopyRange(byte[] source, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static void AssertCrcIsValid(byte[] report)
        {
            uint expected = DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                report, ReportLength - sizeof(uint));
            uint actual = BitConverter.ToUInt32(report,
                ReportLength - sizeof(uint));
            Assert.AreEqual(expected, actual);
        }
    }
}
