using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseBluetoothAudioPacerTemplateTests
    {
        private const int ReportLength = 398;
        private const int HapticsOffset = 78;
        private const int HapticsLength = 64;

        [TestMethod]
        public void GameStateTemplateMergePreservesOwnedMediaLanes()
        {
            byte[] selectedAppMedia = CreateReport(0x31);
            byte[] gameStateTemplate = CreateReport(0x72);
            for (int index = 0; index < ReportLength; index++)
            {
                selectedAppMedia[index] = (byte)(index * 17 + 3);
                gameStateTemplate[index] = (byte)(index * 29 + 7);
            }

            byte[] expectedMedia = (byte[])selectedAppMedia.Clone();
            DualSenseBluetoothAudioPacer.MergeControllerStateIntoTemplate(
                gameStateTemplate, selectedAppMedia);

            const int stateOffset =
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStateSourceOffset;
            const int stateLength =
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength;
            CollectionAssert.AreEqual(
                CopyRange(gameStateTemplate, stateOffset, stateLength),
                CopyRange(selectedAppMedia, stateOffset, stateLength));
            CollectionAssert.AreEqual(CopyRange(expectedMedia, 0, stateOffset),
                CopyRange(selectedAppMedia, 0, stateOffset),
                "Game state replaced the active media header.");
            CollectionAssert.AreEqual(
                CopyRange(expectedMedia, stateOffset + stateLength,
                    ReportLength - stateOffset - stateLength),
                CopyRange(selectedAppMedia, stateOffset + stateLength,
                    ReportLength - stateOffset - stateLength),
                "Game state replaced app-owned haptics or speaker media.");
        }

        [TestMethod]
        public void RealtimeHapticsPreserveAttackAndZeroTailInOrder()
        {
            using DualSenseRealtimeHapticsSharedRing producer =
                CreateSharedRing(2, out DualSenseRealtimeHapticsSharedRing queue);
            using (queue)
            {
                byte[] attack = CreateHapticsGeneration(0x31);
                byte[] release = new byte[HapticsLength];
                byte[] report = CreateReport(0x70);
                Assert.IsTrue(producer.Publish(attack, 0, 1,
                    long.MaxValue, 1));
                Assert.IsTrue(producer.Publish(release, 0, 1,
                    long.MaxValue, 2));

                Assert.IsTrue(queue.PrepareForPresentation(report, 100));
                CollectionAssert.AreEqual(attack,
                    CopyRange(report, HapticsOffset, HapticsLength));
                queue.CommitPrepared();
                FillHaptics(report, 0x7A);
                Assert.IsTrue(queue.PrepareForPresentation(report, 200));
                CollectionAssert.AreEqual(release,
                    CopyRange(report, HapticsOffset, HapticsLength));
                queue.CommitPrepared();
                FillHaptics(report, 0x55);
                Assert.IsFalse(queue.PrepareForPresentation(report, 300));
                CollectionAssert.AreEqual(release,
                    CopyRange(report, HapticsOffset, HapticsLength));
            }
        }

        [TestMethod]
        public void RealtimeHapticsRetryRetainsBoundGeneration()
        {
            using DualSenseRealtimeHapticsSharedRing producer =
                CreateSharedRing(4, out DualSenseRealtimeHapticsSharedRing queue);
            using (queue)
            {
                byte[] first = CreateHapticsGeneration(0x11);
                byte[] second = CreateHapticsGeneration(0x22);
                byte[] newest = CreateHapticsGeneration(0x33);
                byte[] report = CreateReport(0x60);
                Assert.IsTrue(producer.Publish(first, 0, 1,
                    long.MaxValue, 1));
                Assert.IsTrue(producer.Publish(second, 0, 1,
                    long.MaxValue, 2));
                Assert.IsTrue(queue.PrepareForPresentation(report, 100));
                Assert.IsTrue(producer.Publish(newest, 0, 1,
                    long.MaxValue, 3));
                FillHaptics(report, 0x70);
                Assert.IsTrue(queue.PrepareForPresentation(report, 200));
                CollectionAssert.AreEqual(first,
                    CopyRange(report, HapticsOffset, HapticsLength));
                queue.CommitPrepared();
                Assert.IsTrue(queue.PrepareForPresentation(report, 300));
                CollectionAssert.AreEqual(second,
                    CopyRange(report, HapticsOffset, HapticsLength));
                queue.CommitPrepared();
                Assert.IsTrue(queue.PrepareForPresentation(report, 400));
                CollectionAssert.AreEqual(newest,
                    CopyRange(report, HapticsOffset, HapticsLength));
            }
        }

        [TestMethod]
        public void RealtimeHapticsMetricsObserveLatencyWithoutChangingFifo()
        {
            using DualSenseRealtimeHapticsSharedRing producer =
                CreateSharedRing(4, out DualSenseRealtimeHapticsSharedRing queue);
            using (queue)
            {
                byte[] first = CreateHapticsGeneration(0x21);
                byte[] second = CreateHapticsGeneration(0x42);
                byte[] report = CreateReport(0x60);
                Assert.IsTrue(producer.Publish(first, 0, 1,
                    long.MaxValue, 100));
                Assert.IsTrue(producer.Publish(second, 0, 1,
                    long.MaxValue, 120));
                Assert.AreEqual(2, queue.Count);
                Assert.IsTrue(queue.PrepareForPresentation(report, 175));
                Assert.AreEqual(2, queue.MaximumQueueDepth);
                Assert.AreEqual(75L, queue.MaximumQueueAgeTicks);
                CollectionAssert.AreEqual(first,
                    CopyRange(report, HapticsOffset, HapticsLength));
                queue.CommitPrepared();
                Assert.AreEqual(1L, queue.PresentedCount);
                Assert.AreEqual(1, queue.Count);
            }
        }

        [TestMethod]
        public void RealtimeHapticsFullRingBackpressuresWithoutReplacement()
        {
            using DualSenseRealtimeHapticsSharedRing producer =
                CreateSharedRing(2, out DualSenseRealtimeHapticsSharedRing queue);
            using (queue)
            {
                byte[] old = CreateHapticsGeneration(0x10);
                byte[] middle = CreateHapticsGeneration(0x20);
                byte[] newest = CreateHapticsGeneration(0x30);
                byte[] report = CreateReport(0x50);
                Assert.IsTrue(producer.Publish(old, 0, 1,
                    long.MaxValue, 1));
                Assert.IsTrue(producer.Publish(middle, 0, 1,
                    long.MaxValue, 2));
                Task<bool> blocked = Task.Run(() => producer.Publish(newest,
                    0, 1, long.MaxValue, 3));
                Assert.IsFalse(blocked.Wait(30));
                Assert.IsTrue(queue.PrepareForPresentation(report, 100));
                CollectionAssert.AreEqual(old,
                    CopyRange(report, HapticsOffset, HapticsLength));
                queue.CommitPrepared();
                Assert.IsTrue(blocked.Wait(500));
                Assert.IsTrue(blocked.Result);
                Assert.IsTrue(queue.PrepareForPresentation(report, 200));
                CollectionAssert.AreEqual(middle,
                    CopyRange(report, HapticsOffset, HapticsLength));
            }
        }

        [TestMethod]
        public void RealtimeHapticsLifecycleResetSilencesStaleGeneration()
        {
            using DualSenseRealtimeHapticsSharedRing producer =
                CreateSharedRing(2, out DualSenseRealtimeHapticsSharedRing queue);
            using (queue)
            {
                byte[] pulse = CreateHapticsGeneration(0x41);
                byte[] report = CreateReport(0x20);
                Assert.IsTrue(producer.Publish(pulse, 0, 1,
                    long.MaxValue, 1));
                queue.AcceptGeneration(2, silenceFutureReports: true);
                FillHaptics(report, 0x66);
                Assert.IsFalse(queue.PrepareForPresentation(report, 100));
                CollectionAssert.AreEqual(new byte[HapticsLength],
                    CopyRange(report, HapticsOffset, HapticsLength));
                Assert.AreEqual((byte)0x92, report[HapticsOffset - 2]);
                Assert.AreEqual((byte)HapticsLength,
                    report[HapticsOffset - 1]);
            }
        }

        private static DualSenseRealtimeHapticsSharedRing CreateSharedRing(
            int capacity, out DualSenseRealtimeHapticsSharedRing consumer)
        {
            string prefix = "DS4Windows.Tests.Haptics." +
                Guid.NewGuid().ToString("N");
            DualSenseRealtimeHapticsSharedRing producer =
                DualSenseRealtimeHapticsSharedRing.CreateOwner(prefix,
                    capacity);
            consumer = DualSenseRealtimeHapticsSharedRing.OpenConsumer(
                producer.MapName, producer.SpaceAvailableName,
                producer.StopRequestedName, producer.Capacity);
            return producer;
        }

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
        public void SteadyMediaTemplateKeepsActiveRegularRumbleSelected()
        {
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);
            latestTemplate[13] = 0xF3;
            latestTemplate[15] = 0x44;
            latestTemplate[16] = 0x55;

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, nowQpc + 1, nowQpc);

            Assert.AreEqual((byte)0xF3, queued[13],
                "The steady 0x36 carrier switched away from active regular rumble.");
            Assert.AreEqual((byte)0x44, queued[15]);
            Assert.AreEqual((byte)0x55, queued[16]);
            AssertCrcIsValid(queued);

            byte[] gameState = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength];
            gameState[0] = 0x03;
            DualSenseBluetoothAudioReportPatcher.
                ApplyControllerStateForPresentation(queued, gameState);
            Assert.AreEqual((byte)0x03, queued[13],
                "The media guard consumed an explicitly composed game rumble transition.");
        }

        [TestMethod]
        public void SteadyMediaTemplateDoesNotReplayCompletedRumbleStop()
        {
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);
            latestTemplate[13] = 0xF3;
            latestTemplate[15] = 0;
            latestTemplate[16] = 0;

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, nowQpc + 1, nowQpc);

            Assert.AreEqual((byte)0xF1, queued[13],
                "A completed zero-rumble transition was replayed by steady media.");
            AssertCrcIsValid(queued);
        }

        [TestMethod]
        public void LatestV5SnapshotReplacesQueuedAudioStateAtomically()
        {
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);

            // The native reference repeats one complete state snapshot on
            // every 0x36. Presentation must never retain a stale subset from
            // the queued audio frame.
            queued[13] = 0x12;
            queued[14] = 0x34;
            queued[17] = 0x56;
            queued[18] = 0x78;
            queued[19] = 0x9A;
            queued[20] = 0xBC;
            queued[22] = 0xDE;
            queued[50] = 0xF0;
            SetV5AudioContract(latestTemplate);
            latestTemplate[15] = 0xA1;
            latestTemplate[16] = 0xB2;
            latestTemplate[21] = 0x01;
            for (int index = 23; index <= 49; index++)
            {
                latestTemplate[index] = (byte)(0x30 + index - 23);
            }
            latestTemplate[56] = 0x1D;
            latestTemplate[57] = 0xA5;
            latestTemplate[58] = 0xB6;
            latestTemplate[59] = 0xC7;

            byte expectedSequence = queued[1];
            byte[] expectedDepths = CopyRange(queued, 5, 5);
            byte expectedPacketCounter = queued[10];
            byte[] expectedSpeaker = CopyRange(queued, 142, 202);

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, nowQpc + 1, nowQpc);

            AssertV5AudioContract(queued);
            CollectionAssert.AreEqual(CopyRange(latestTemplate, 15, 2),
                CopyRange(queued, 15, 2),
                "Presentation lost the latest rumble motors.");
            CollectionAssert.AreEqual(CopyRange(latestTemplate, 23, 27),
                CopyRange(queued, 23, 27),
                "Presentation lost the latest trigger/effect state.");
            Assert.AreEqual((byte)0x01, queued[21]);
            CollectionAssert.AreEqual(CopyRange(latestTemplate, 56, 4),
                CopyRange(queued, 56, 4),
                "Presentation lost the latest player LED/lightbar state.");
            Assert.AreEqual(expectedSequence, queued[1]);
            CollectionAssert.AreEqual(expectedDepths,
                CopyRange(queued, 5, 5));
            Assert.AreEqual(expectedPacketCounter, queued[10]);
            CollectionAssert.AreEqual(expectedSpeaker,
                CopyRange(queued, 142, 202));
            AssertCrcIsValid(queued);
        }

        [TestMethod]
        public void LatestTemplateWinsOverStalePendingMicrophoneSnapshot()
        {
            const long nowQpc = 50_000_000;
            byte[] queued = CreateReport(0x28);
            byte[] latestTemplate = CreateReport(0x63);
            queued[13] = 0xFF;
            queued[14] = 0xFF;
            queued[17] = 0x11;
            queued[18] = 0x22;
            queued[19] = 0x2F;
            queued[20] = 0x44;
            queued[21] = 0x01;
            queued[22] = 0x55;
            queued[50] = 0x66;
            SetV5AudioContract(latestTemplate);
            latestTemplate[21] = 0;

            byte expectedSequence = queued[1];
            byte[] expectedDepths = CopyRange(queued, 5, 5);
            byte expectedPacketCounter = queued[10];
            byte[] expectedSpeaker = CopyRange(queued, 142, 202);

            DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                queued, latestTemplate, nowQpc + 1, nowQpc);

            AssertV5AudioContract(queued);
            Assert.AreEqual((byte)0, queued[21]);
            Assert.AreEqual(expectedSequence, queued[1]);
            CollectionAssert.AreEqual(expectedDepths,
                CopyRange(queued, 5, 5));
            Assert.AreEqual(expectedPacketCounter, queued[10]);
            CollectionAssert.AreEqual(expectedSpeaker,
                CopyRange(queued, 142, 202));
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
        public void SchedulerReanchorsAfterLargeLateness(
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

            Assert.AreEqual(firstCadenceTicks,
                nextDeadline - presentationQpc,
                "A true stall must re-anchor and leave one full cadence before the next presentation.");
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
        public void NativeSchedulerMatchesV5UniformHostDeadline()
        {
            const long qpcFrequency = 1_000_000;
            const long startQpc = 3_000_000;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.Start(startQpc);

            long previous = scheduler.NextDeadlineQpc;
            for (int interval = 1; interval <= 30; interval++)
            {
                scheduler.AdvanceAfterSend(previous);
                long next = scheduler.NextDeadlineQpc;
                long expected = interval % 3 == 1 ? 10_666 : 10_667;
                Assert.AreEqual(expected, next - previous,
                    $"V5 host interval {interval} was incorrect.");
                previous = next;
            }

            Assert.AreEqual(startQpc + 320_000,
                scheduler.NextDeadlineQpc,
                "Thirty uniform native reports must span exactly 320 ms.");
        }

        [TestMethod]
        public void V5NativeLatticeMatchesObservedTenTwentyPattern()
        {
            const long qpcFrequency = 10_000_000;
            const long startQpc = 3_000_000;
            long[] expectedOffsetsMilliseconds =
            {
                0, 10, 20, 30, 40, 50, 60, 70,
                80, 90, 100, 110, 120, 130, 140, 160,
            };
            var scheduler =
                new DualSenseV5NativePresentationScheduler(
                    qpcFrequency);
            scheduler.Start(startQpc);

            foreach (long expectedOffsetMilliseconds in
                expectedOffsetsMilliseconds)
            {
                Assert.AreEqual(startQpc + expectedOffsetMilliseconds *
                    qpcFrequency / 1000, scheduler.NextDeadlineQpc);
                scheduler.AdvanceAfterSend(scheduler.NextDeadlineQpc);
            }
        }

        [TestMethod]
        public void V5NativeLatticePreservesExactLongWindowCadence()
        {
            const long qpcFrequency = 10_000_000;
            var scheduler =
                new DualSenseV5NativePresentationScheduler(
                    qpcFrequency);
            scheduler.Start(0);

            for (int report = 0; report < 150; report++)
            {
                scheduler.AdvanceAfterSend(scheduler.NextDeadlineQpc);
            }

            Assert.AreEqual(qpcFrequency * 16L / 10,
                scheduler.NextDeadlineQpc,
                "One hundred fifty reports must consume exactly ten " +
                "V5 160 ms host cycles.");
        }

        [TestMethod]
        public void V5NativeLatticeAppliesClockRatioWithoutPhaseReset()
        {
            const long qpcFrequency = 10_000_000;
            const double controllerClockRatio = 0.999800;
            var scheduler =
                new DualSenseV5NativePresentationScheduler(
                    qpcFrequency);
            scheduler.Start(0);
            for (int report = 0; report < 75; report++)
            {
                scheduler.AdvanceAfterSend(scheduler.NextDeadlineQpc);
            }

            long boundaryBeforeRatioUpdate = scheduler.NextDeadlineQpc;
            scheduler.SetRateRatio(controllerClockRatio);
            Assert.AreEqual(boundaryBeforeRatioUpdate,
                scheduler.NextDeadlineQpc,
                "A clock-ratio update must not move the already-published " +
                "V5 deadline.");
            for (int report = 0; report < 1500; report++)
            {
                scheduler.AdvanceAfterSend(scheduler.NextDeadlineQpc);
            }

            double expected = boundaryBeforeRatioUpdate +
                qpcFrequency * 16.0 / controllerClockRatio;
            Assert.AreEqual(expected, scheduler.NextDeadlineQpc, 1.0,
                "Fractional clock correction drifted across the V5 " +
                "10/20 ms lattice.");
        }

        [TestMethod]
        public void V5NativeLatticeReanchorsAfterLongHostStall()
        {
            const long qpcFrequency = 10_000_000;
            var scheduler =
                new DualSenseV5NativePresentationScheduler(
                    qpcFrequency);
            scheduler.Start(qpcFrequency);
            scheduler.AdvanceAfterSend(scheduler.NextDeadlineQpc);

            long stalledPresentation = scheduler.NextDeadlineQpc +
                qpcFrequency * 97 / 1000;
            scheduler.AdvanceAfterSend(stalledPresentation);

            Assert.AreEqual(stalledPresentation + qpcFrequency / 100,
                scheduler.NextDeadlineQpc,
                "A long host stall must schedule one future 10 ms interval, " +
                "not replay missed V5 ticks in a catch-up burst.");
        }

        [TestMethod]
        public void NativeSchedulerAppliesClockRatioAcrossUniformDeadlines()
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
                "Fractional correction drifted across V5 host deadlines.");
        }

        [TestMethod]
        public void NativeUniformCadencePrimeDoesNotDrain()
        {
            // Model the native transport's one produced and consumed frame each 32/3 ms.
            const double producerIntervalMilliseconds = 32.0 / 3.0;
            double nextProductionMilliseconds = producerIntervalMilliseconds;
            double presentationMilliseconds = 0.0;
            int queuedFrames =
                DualSenseBluetoothAudioPacer.NativePrimeReportCount;

            for (int presentation = 1; presentation <= 1500;
                presentation++)
            {
                Assert.IsTrue(queuedFrames > 0,
                    $"Native lattice underflowed before report {presentation}.");
                queuedFrames--;
                presentationMilliseconds += producerIntervalMilliseconds;
                while (nextProductionMilliseconds <=
                    presentationMilliseconds + 1.0e-9)
                {
                    queuedFrames++;
                    nextProductionMilliseconds +=
                        producerIntervalMilliseconds;
                }
            }

            Assert.IsTrue(queuedFrames >= 1,
                "The uniform V5 host cadence drained its startup reserve.");
            Assert.AreEqual(1,
                DualSenseBluetoothAudioPacer.GetPrimeReportCount(
                    useMeasuredTransportAudioTransport: true));
            Assert.AreEqual(8,
                DualSenseBluetoothAudioPacer.GetPrimeReportCount(
                    useMeasuredTransportAudioTransport: false));
            Assert.AreEqual(8,
                DualSenseBluetoothAudioPacer.NativePrimeReportCount);
            Assert.AreEqual(2,
                DualSenseBluetoothAudioPacer.PairedPrimeReportCount);
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
        public void PairedSchedulerCarriesFractionalClockAcrossBothMediaIntervals()
        {
            const long qpcFrequency = 10_000_000;
            const double controllerClockRatio = 0.999800;
            const int physicalPairs = 1500;
            var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                qpcFrequency);
            scheduler.SetRateRatio(controllerClockRatio);
            scheduler.Start(0);

            for (int pair = 0; pair < physicalPairs; pair++)
            {
                // One physical 0x39 packages two ordered 10.667 ms media
                // generations. Both share one Windows presentation timestamp,
                // but each must spend its own fractional cadence interval.
                long presentedAt = scheduler.NextDeadlineQpc;
                scheduler.AdvanceAfterSend(presentedAt);
                scheduler.AdvanceAfterSend(presentedAt);
            }

            double expected = qpcFrequency * 32.0 /
                controllerClockRatio;
            Assert.AreEqual(expected, scheduler.NextDeadlineQpc, 1.0,
                "A paired 0x39 write lost fractional media-clock correction.");
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
        public void SchedulerBoundsInputPhaseBiasWithoutMovingRationalClock()
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
                // cadence. Presentation may receive the deliberately bounded
                // sub-millisecond bias, while the rational clock itself must
                // remain continuous and exact.
                scheduler.SetInputPhaseReference(startQpc + index * 12_500L +
                    ((index % 7) - 3) * 997L);
                long idealDeadline = scheduler.NextDeadlineQpc;
                long presentationDeadline =
                    scheduler.PresentationDeadlineQpc;
                long maximumBiasTicks = qpcFrequency *
                    DualSenseBluetoothAudioPacerScheduler.
                        MaximumInputPhaseCorrectionMicroseconds / 1_000_000;
                Assert.IsTrue(Math.Abs(
                        presentationDeadline - idealDeadline) <=
                    maximumBiasTicks,
                    "The controller-input phase bias exceeded its hard cap.");
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
        public void SourceDrivenPresentationIsExclusiveToNativeSpeakerFrames()
        {
            byte[] speaker = CreateReport(0x52);
            byte[] control = CreateReport(0x25);
            control[142] = 0;
            control[143] = 0;

            Assert.IsTrue(DualSenseBluetoothAudioPacer.
                UsesSourceDrivenNativePresentation(
                    useNativeAudioTransport: true, nextReport: speaker));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UsesSourceDrivenNativePresentation(
                    useNativeAudioTransport: true, nextReport: control));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UsesSourceDrivenNativePresentation(
                    useNativeAudioTransport: false, nextReport: speaker),
                "Compact transport must remain helper-clocked.");
        }

        [DataTestMethod]
        [DataRow(true, true, true)]
        [DataRow(true, false, false)]
        [DataRow(false, true, true)]
        [DataRow(false, false, false)]
        public void V5PresentationCadenceIsAlwaysNative(
            bool requested, bool nativeTransport, bool expected)
        {
            Assert.AreEqual(expected,
                DualSenseBluetoothAudioPacer.
                    ShouldUseV5PresentationCadence(requested,
                        nativeTransport));
        }

        [TestMethod]
        public void NativeSpeakerAudioRequiresEightCompleteReports()
        {
            byte[] speaker = CreateReport(0x52);

            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(speaker));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.NativePrimeReportCount - 1,
                    nextReport: speaker));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.NativePrimeReportCount,
                    nextReport: speaker));
        }

        [TestMethod]
        public void SingleSpeakerAudioPresentsWithoutWaitingForAPair()
        {
            byte[] speaker = CreateReport(0x52);

            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromTransportGate(
                    primeRequired: false, speakerReportCount: 1,
                    nextReport: speaker));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromTransportGate(
                    primeRequired: false, speakerReportCount: 2,
                    nextReport: speaker));
        }

        [TestMethod]
        public void PairedSpeakerDequeueIsAtomicAcrossFinalGateRevalidation()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<byte[]>(4);
            byte[] first = CreateReport(0x51);
            byte[] second = CreateReport(0x52);
            Assert.IsTrue(ring.TryEnqueue(first));

            Assert.IsFalse(ring.TryDequeuePair(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport,
                out _, out _));
            Assert.AreEqual(1, ring.Count,
                "A failed paired dequeue must not consume its first half.");
            Assert.IsTrue(ring.TryPeek(out byte[] retained));
            Assert.AreSame(first, retained);

            Assert.IsTrue(ring.TryEnqueue(second));
            Assert.IsTrue(ring.TryDequeuePair(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport,
                out byte[] dequeuedFirst, out byte[] dequeuedSecond));
            Assert.AreSame(first, dequeuedFirst);
            Assert.AreSame(second, dequeuedSecond);
            Assert.AreEqual(0, ring.Count);
        }

        [TestMethod]
        public void PairedSpeakerDequeueRejectsControlWithoutMutatingFifo()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<byte[]>(4);
            byte[] speaker = CreateReport(0x61);
            byte[] control = CreateReport(0x62);
            control[142] = 0;
            control[143] = 0;
            Assert.IsTrue(ring.TryEnqueue(speaker));
            Assert.IsTrue(ring.TryEnqueue(control));

            Assert.IsFalse(ring.TryDequeuePair(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport,
                out _, out _));
            Assert.AreEqual(2, ring.Count);
            Assert.IsTrue(ring.TryDequeue(out byte[] retainedSpeaker));
            Assert.AreSame(speaker, retainedSpeaker);
            Assert.IsTrue(ring.TryDequeue(out byte[] retainedControl));
            Assert.AreSame(control, retainedControl);
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
                        DualSenseBluetoothAudioPacer.NativePrimeReportCount - 1,
                    nextReport: headset));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.NativePrimeReportCount,
                    nextReport: headset));
        }

        [TestMethod]
        public void ProducerUsesOneTimePrimeAndSingleFrameSteadyLead()
        {
            Assert.AreEqual(1,
                DualSenseBluetoothSpeakerPassthrough
                    .PacerReservoirTargetFrames,
                "Steady source production must not preserve a burstable lead.");
            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough.InitialBufferMs <= 32,
                "Startup must not add a second long media-style prebuffer.");
            Assert.IsTrue(
                DualSenseBluetoothSpeakerPassthrough
                    .LowLatencyCaptureBufferMs <= 5,
                "Loopback capture should request a sub-10 ms period.");
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.NativePrimeReportCount >
                DualSenseBluetoothSpeakerPassthrough
                    .PacerReservoirTargetFrames,
                "The eight-report requirement is a one-time source prime, " +
                "not the steady-state media lead.");
        }

        [TestMethod]
        public void ControlReportDoesNotCountAsSpeakerPrimeCredit()
        {
            byte[] speaker = CreateReport(0x41);

            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount:
                        DualSenseBluetoothAudioPacer.NativePrimeReportCount - 1,
                    nextReport: speaker),
                "A control report must not count toward the eight-frame native prime.");
        }

        [TestMethod]
        public void SingleFrameControlDoesNotRestartAudioPrime()
        {
            byte[] control = CreateReport(0x37);
            control[142] = 0;
            control[143] = 0;

            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true, speakerReportCount: 0,
                    nextReport: control));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldRequireAudioPrimeAfterPresentation(
                        presentedControlReport: true,
                        remainingReportCount:
                            DualSenseBluetoothAudioPacer.NativePrimeReportCount),
                "A control commit must not restart the native 0x36 cadence.");
        }

        [TestMethod]
        public void NativeSourceBoundaryDoesNotReplayStartupPrime()
        {
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldReprimeAfterEmptyReservoir(
                        useNativeAudioTransport: true),
                "V5 primes eight blocks once and waits for one next " +
                "source block at an ordinary empty boundary.");
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.
                    ShouldReprimeAfterEmptyReservoir(
                        useNativeAudioTransport: false));
        }

        [TestMethod]
        public void EmptySourceQueueDoesNotRestartPairedAudioClock()
        {
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldRequireAudioPrimeAfterPresentation(
                        presentedControlReport: false,
                        remainingReportCount: 0),
                "CombinedReportReference waits for the next complete pair without replaying startup pacing.");
        }

        [TestMethod]
        public void NativeControlBypassesPrimeThenEightSpeakersStartPresentation()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<byte[]>(9);
            byte[] control = CreateReport(0x60);
            control[142] = 0;
            control[143] = 0;
            Assert.IsTrue(ring.TryEnqueue(control));
            for (int index = 0;
                index < DualSenseBluetoothAudioPacer.NativePrimeReportCount;
                index++)
            {
                Assert.IsTrue(ring.TryEnqueue(
                    CreateReport((byte)(0x61 + index))));
            }

            int leadingSpeakers = ring.CountLeading(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport);
            Assert.AreEqual(0, leadingSpeakers);
            Assert.IsTrue(ring.TryPeek(out byte[] first));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromPrimeGate(
                    primeRequired: true,
                    speakerReportCount: leadingSpeakers,
                    nextReport: first),
                "A control report must bypass the single-frame audio prime.");
            Assert.IsTrue(ring.TryDequeue(out _));
            leadingSpeakers = ring.CountLeading(
                DualSenseBluetoothAudioPacer.IsSpeakerAudioReport);
            Assert.AreEqual(
                DualSenseBluetoothAudioPacer.NativePrimeReportCount,
                leadingSpeakers);
            Assert.IsTrue(ring.TryPeek(out first));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.CanPresentFromTransportGate(
                    primeRequired: true,
                    speakerReportCount: leadingSpeakers,
                    nextReport: first),
                "Eight queued 0x36 frames must satisfy the native presentation prime.");
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

        [TestMethod]
        public void CoalescedTelemetryReplacesNewestMatchWithoutReordering()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<int>(4);
            Assert.IsTrue(ring.TryEnqueue(1));
            Assert.IsTrue(ring.TryEnqueue(20));
            Assert.IsTrue(ring.TryEnqueue(3));

            Assert.IsTrue(ring.TryReplaceNewestOrEnqueue(
                value => value == 20, 21));
            Assert.AreEqual(3, ring.Count);
            Assert.IsTrue(ring.TryDequeue(out int first));
            Assert.IsTrue(ring.TryDequeue(out int replacement));
            Assert.IsTrue(ring.TryDequeue(out int last));
            Assert.AreEqual(1, first);
            Assert.AreEqual(21, replacement);
            Assert.AreEqual(3, last);

            Assert.IsTrue(ring.TryReplaceNewestOrEnqueue(
                value => value == 99, 4));
            Assert.IsTrue(ring.TryDequeue(out int appended));
            Assert.AreEqual(4, appended);
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

        private static void SetV5AudioContract(byte[] report)
        {
            report[13] = 0xFD;
            report[14] = 0xF7;
            report[17] = 0x64;
            report[18] = 0x64;
            report[19] = 0xFF;
            report[20] = 0x09;
            report[22] = 0x0F;
            report[50] = 0x0A;
        }

        private static void AssertV5AudioContract(byte[] report)
        {
            Assert.AreEqual((byte)0xFD, report[13]);
            Assert.AreEqual((byte)0xF7, report[14]);
            Assert.AreEqual((byte)0x64, report[17]);
            Assert.AreEqual((byte)0x64, report[18]);
            Assert.AreEqual((byte)0xFF, report[19]);
            Assert.AreEqual((byte)0x09, report[20]);
            Assert.AreEqual((byte)0x0F, report[22]);
            Assert.AreEqual((byte)0x0A, report[50]);
        }

        private static byte[] CopyRange(byte[] source, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static byte[] CreateHapticsGeneration(byte seed)
        {
            byte[] result = new byte[HapticsLength];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = (byte)(seed + index);
            }
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
