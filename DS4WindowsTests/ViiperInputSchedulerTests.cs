using System.Buffers.Binary;
using System.Diagnostics;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperInputSchedulerTests
    {
        [TestMethod]
        public void FinalMappedTriggersDriveClassificationAndWireBitsTogether()
        {
            DS4State source = new()
            {
                L2 = 0,
                L2Btn = true,
                R2 = 173,
                R2Btn = false,
            };

            ViiperMappedInputState mapped = ViiperStatePacketBuilder.
                BuildMappedState(source, -1);
            byte[] packet = new byte[ViiperStatePacketBuilder.GetPacketSize(
                ViiperVirtualDeviceType.DualSense)];
            ViiperStatePacketBuilder.BuildInto(mapped, packet);

            Assert.AreEqual(1, mapped.L2,
                "An explicit digital trigger maps to the smallest coherent analog press.");
            Assert.AreEqual(173, mapped.R2);
            Assert.IsTrue(mapped.L2Pressed);
            Assert.IsTrue(mapped.R2Pressed);
            Assert.AreEqual(mapped.L2, packet[9]);
            Assert.AreEqual(mapped.R2, packet[10]);
            uint wireButtons = BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4, 4));
            Assert.AreEqual(mapped.Buttons, wireButtons);
            Assert.AreNotEqual(0u,
                wireButtons & ViiperMappedInputState.L2ButtonMask);
            Assert.AreNotEqual(0u,
                wireButtons & ViiperMappedInputState.R2ButtonMask);

            ViiperInputScheduler scheduler = NewScheduler();
            ViiperInputPublication publication = scheduler.Publish(mapped, 2);
            Assert.IsTrue(publication.IsTransition,
                "Classification must consume the same final mapped trigger values.");
        }

        [TestMethod]
        public void RapidTriggerEpochStrengthensPendingPressBeforeRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(Trigger(1), 2);
            ViiperMappedInputState motion = Trigger(80);
            motion.GyroX = 101;
            scheduler.Publish(motion, 3);
            scheduler.Publish(Trigger(255), 4);
            scheduler.Publish(Trigger(0), 5);

            ViiperInputClaim peak = Claim(scheduler);
            Assert.IsTrue(peak.IsTransition);
            Assert.AreEqual(255, peak.State.R2);
            Assert.IsTrue(peak.State.R2Pressed);
            scheduler.CompleteSuccess(peak, 6);

            ViiperInputClaim release = Claim(scheduler);
            Assert.IsTrue(release.IsTransition);
            Assert.AreEqual(0, release.State.R2);
            Assert.IsFalse(release.State.R2Pressed);
            scheduler.CompleteSuccess(release, 7);

            Assert.IsFalse(scheduler.TryClaim(out _),
                "No stale continuous pressed state may follow release.");
            ViiperInputSchedulerSnapshot snapshot = scheduler.Snapshot();
            Assert.AreEqual(2, snapshot.TransitionHighWater);
            Assert.AreEqual(0, snapshot.OverflowCount);
            Assert.IsTrue(snapshot.ReplacementCount >= 1);
        }

        [TestMethod]
        public void PeakOnlyInContinuousSlotIsPromotedBeforeRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(Trigger(1), 2);
            ViiperInputClaim initial = Claim(scheduler);
            scheduler.CompleteSuccess(initial, 3);

            scheduler.Publish(Trigger(255), 4);
            scheduler.Publish(Trigger(0), 5);

            ViiperInputClaim peak = Claim(scheduler);
            Assert.AreEqual(255, peak.State.R2);
            scheduler.CompleteSuccess(peak, 6);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 7);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void WithinEpochTriggerMotionRemainsOneLatestUntilRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(Trigger(1), 2);
            ViiperInputClaim initial = Claim(scheduler);
            scheduler.CompleteSuccess(initial, 3);
            ViiperInputSchedulerSnapshot beforeMotion = scheduler.Snapshot();

            byte[] samples = { 40, 180, 75, 255, 210, 32 };
            for (int index = 0; index < samples.Length; index++)
            {
                ViiperMappedInputState motion = Trigger(samples[index]);
                motion.GyroX = (short)(100 + index);
                scheduler.Publish(motion, 4 + index);
            }

            ViiperInputSchedulerSnapshot afterMotion = scheduler.Snapshot();
            Assert.AreEqual(0, afterMotion.TransitionDepth,
                "Analog rise/fall within one press epoch must not become ordered FIFO work.");
            Assert.AreEqual(beforeMotion.TransitionHighWater,
                afterMotion.TransitionHighWater,
                "Within-epoch motion must not raise the ordered high-water mark.");
            Assert.AreEqual(beforeMotion.TransitionCount,
                afterMotion.TransitionCount,
                "Within-epoch motion must not count as a control transition.");
            Assert.IsTrue(afterMotion.ContinuousPending);

            ViiperInputClaim latest = Claim(scheduler);
            Assert.IsFalse(latest.IsTransition);
            Assert.AreEqual(32, latest.State.R2,
                "Only the newest replaceable trigger sample may remain.");
            Assert.AreEqual(105, latest.State.GyroX);
            scheduler.CompleteSuccess(latest, 11);

            scheduler.Publish(Trigger(0), 12);
            ViiperInputClaim peak = Claim(scheduler);
            Assert.IsTrue(peak.IsTransition);
            Assert.AreEqual(255, peak.State.R2);
            Assert.AreEqual(103, peak.State.GyroX,
                "The promoted peak must be the truthful complete snapshot from its receive cycle.");
            scheduler.CompleteSuccess(peak, 13);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 14);
            Assert.IsFalse(scheduler.TryClaim(out _),
                "No lower pressed sample may replay after release.");
        }

        [TestMethod]
        public void FirstReportTriggerPressStillOwnsPeakEpoch()
        {
            ViiperInputScheduler scheduler = new();
            scheduler.Reset(22);
            scheduler.Publish(Trigger(1), 1);
            scheduler.Publish(Trigger(255), 2);
            scheduler.Publish(Trigger(0), 3);

            ViiperInputClaim peak = Claim(scheduler);
            Assert.AreEqual(255, peak.State.R2);
            scheduler.CompleteSuccess(peak, 4);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 5);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void UnrelatedButtonEdgeCannotCausePeakSnapshotRepress()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(Trigger(1), 2);
            ViiperInputClaim initial = Claim(scheduler);
            scheduler.CompleteSuccess(initial, 3);

            scheduler.Publish(Trigger(255), 4);
            ViiperMappedInputState buttonDown = Trigger(80);
            buttonDown.Buttons |= 0x20;
            scheduler.Publish(buttonDown, 5);
            ViiperMappedInputState releasedWithButton = Trigger(0);
            releasedWithButton.Buttons |= 0x20;
            scheduler.Publish(releasedWithButton, 6);

            ViiperInputClaim peak = Claim(scheduler);
            Assert.AreEqual(255, peak.State.R2);
            Assert.AreEqual(0u, peak.State.Buttons & 0x20,
                "The historical peak must retain its truthful button state.");
            scheduler.CompleteSuccess(peak, 7);

            ViiperInputClaim button = Claim(scheduler);
            Assert.AreEqual(80, button.State.R2);
            Assert.AreNotEqual(0u, button.State.Buttons & 0x20);
            scheduler.CompleteSuccess(button, 8);

            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            Assert.AreNotEqual(0u, release.State.Buttons & 0x20,
                "Release must not replay the peak's older button state.");
            scheduler.CompleteSuccess(release, 9);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void IndependentLeftThenRightPeaksNeverSynthesizeCombinedMaximum()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(DualTrigger(1, 2), 2);
            scheduler.Publish(DualTrigger(240, 90), 3);
            scheduler.Publish(DualTrigger(80, 250), 4);
            scheduler.Publish(DualTrigger(0, 0), 5);

            AssertDualPeakSequence(scheduler,
                expectedFirstL2: 240, expectedFirstR2: 2,
                expectedSecondL2: 80, expectedSecondR2: 250);
        }

        [TestMethod]
        public void IndependentRightThenLeftPeaksNeverSynthesizeCombinedMaximum()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(DualTrigger(1, 2), 2);
            scheduler.Publish(DualTrigger(90, 240), 3);
            scheduler.Publish(DualTrigger(250, 80), 4);
            scheduler.Publish(DualTrigger(0, 0), 5);

            AssertDualPeakSequence(scheduler,
                expectedFirstL2: 1, expectedFirstR2: 240,
                expectedSecondL2: 250, expectedSecondR2: 80);
        }

        [TestMethod]
        public void CooccurringDualTriggerPeakMayStrengthenOnePendingPress()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(DualTrigger(1, 2), 2);
            scheduler.Publish(DualTrigger(240, 250), 3);
            scheduler.Publish(DualTrigger(0, 0), 4);

            ViiperInputClaim peak = Claim(scheduler);
            Assert.AreEqual(240, peak.State.L2);
            Assert.AreEqual(250, peak.State.R2);
            scheduler.CompleteSuccess(peak, 5);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 6);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void FailedClaimedDualPeakRetriesTruthfulOrderBeforeRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(DualTrigger(1, 2), 2);
            scheduler.Publish(DualTrigger(240, 90), 3);
            scheduler.Publish(DualTrigger(80, 250), 4);
            ViiperInputClaim claimedFirst = Claim(scheduler);
            Assert.AreEqual(240, claimedFirst.State.L2);
            Assert.AreEqual(2, claimedFirst.State.R2);

            scheduler.Publish(DualTrigger(0, 0), 5);
            scheduler.CompleteFailure(claimedFirst);
            ViiperInputClaim retry = Claim(scheduler);
            Assert.AreEqual(claimedFirst.PublicationId,
                retry.PublicationId);
            Assert.AreEqual(240, retry.State.L2);
            Assert.AreEqual(2, retry.State.R2);
            scheduler.CompleteSuccess(retry, 6);

            ViiperInputClaim second = Claim(scheduler);
            Assert.AreEqual(80, second.State.L2);
            Assert.AreEqual(250, second.State.R2);
            scheduler.CompleteSuccess(second, 7);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 8);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void FailedTransitionRetriesAheadOfLaterRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(Trigger(1), 2);
            ViiperInputClaim down = Claim(scheduler);
            scheduler.Publish(Trigger(0), 3);
            scheduler.CompleteFailure(down);

            ViiperInputClaim retry = Claim(scheduler);
            Assert.AreEqual(down.PublicationId, retry.PublicationId);
            Assert.AreNotEqual(0, retry.State.R2);
            scheduler.CompleteSuccess(retry, 4);

            ViiperInputClaim up = Claim(scheduler);
            Assert.AreEqual(0, up.State.R2);
            scheduler.CompleteSuccess(up, 5);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void PresentedPeakIsRememberedAfterLaterLowerTransition()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState peakWithButton = Trigger(255);
            peakWithButton.Buttons |= 0x20;
            scheduler.Publish(peakWithButton, 2);
            ViiperInputClaim peak = Claim(scheduler);
            scheduler.CompleteSuccess(peak, 3);

            ViiperMappedInputState lowerWithoutButton = Trigger(80);
            scheduler.Publish(lowerWithoutButton, 4);
            ViiperInputClaim lower = Claim(scheduler);
            Assert.AreEqual(80, lower.State.R2);
            Assert.AreEqual(0u, lower.State.Buttons & 0x20);
            scheduler.CompleteSuccess(lower, 5);

            scheduler.Publish(Trigger(0), 6);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            Assert.AreEqual(0u, release.State.Buttons & 0x20,
                "Release must not replay the old peak's button state.");
            scheduler.CompleteSuccess(release, 7);
            Assert.IsFalse(scheduler.TryClaim(out _),
                "A peak already presented in this epoch must not be replayed.");
        }

        [TestMethod]
        public void FailedClaimedContinuousCannotFollowQueuedRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(Trigger(255), 2);
            ViiperInputClaim initial = Claim(scheduler);
            scheduler.CompleteSuccess(initial, 3);

            scheduler.Publish(Trigger(80), 4);
            ViiperInputClaim continuous = Claim(scheduler);
            Assert.IsFalse(continuous.IsTransition);
            scheduler.Publish(Trigger(0), 5);
            scheduler.CompleteFailure(continuous);

            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 6);
            Assert.IsFalse(scheduler.TryClaim(out _),
                "A failed replaceable pressed state must not follow its contradictory release.");
        }

        [TestMethod]
        public void FailedClaimedEpochPeakRetriesOrderedBeforeRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            scheduler.Publish(Trigger(1), 2);
            ViiperInputClaim initial = Claim(scheduler);
            scheduler.CompleteSuccess(initial, 3);

            scheduler.Publish(Trigger(255), 4);
            ViiperInputClaim uncommittedPeak = Claim(scheduler);
            Assert.IsFalse(uncommittedPeak.IsTransition);
            scheduler.Publish(Trigger(0), 5);
            scheduler.CompleteFailure(uncommittedPeak);

            ViiperInputClaim peakRetry = Claim(scheduler);
            Assert.IsTrue(peakRetry.IsTransition,
                "The sole failed epoch peak must become an ordered retry.");
            Assert.AreEqual(255, peakRetry.State.R2);
            scheduler.CompleteSuccess(peakRetry, 6);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 7);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void TransitionOverflowStillConvergesToNewestCompleteState()
        {
            ViiperInputScheduler scheduler = new(4);
            scheduler.Reset(12);
            scheduler.Publish(ViiperMappedInputState.Neutral, 1);

            for (int index = 0; index < 4; index++)
            {
                ViiperMappedInputState edge = ViiperMappedInputState.Neutral;
                edge.Buttons = (index & 1) == 0 ? 0x01u : 0u;
                Assert.IsTrue(scheduler.Publish(edge, index + 2).Accepted);
            }

            ViiperMappedInputState rejected = ViiperMappedInputState.Neutral;
            rejected.Buttons = 0x02;
            Assert.IsTrue(scheduler.Publish(rejected, 7).Accepted);
            ViiperMappedInputState newest = ViiperMappedInputState.Neutral;
            newest.Buttons = 0x04;
            Assert.IsTrue(scheduler.Publish(newest, 8).Accepted);

            ViiperInputSchedulerSnapshot snapshot = scheduler.Snapshot();
            Assert.AreEqual(2, snapshot.OverflowCount);
            Assert.IsTrue(snapshot.ContinuousPending);
            for (int index = 0; index < 4; index++)
            {
                ViiperInputClaim ordered = Claim(scheduler);
                Assert.IsTrue(ordered.IsTransition);
                scheduler.CompleteSuccess(ordered, 9 + index);
            }
            ViiperInputClaim recovery = Claim(scheduler);
            Assert.IsFalse(recovery.IsTransition);
            Assert.AreEqual(0x04u, recovery.State.Buttons);
            scheduler.CompleteSuccess(recovery, 14);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void MotionReplacesOneContinuousSlotWithoutGrowingTransitionRing()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            for (short gyro = 1; gyro <= 100; gyro++)
            {
                ViiperMappedInputState motion = Trigger(0);
                motion.GyroX = gyro;
                scheduler.Publish(motion, gyro + 1);
            }

            ViiperInputSchedulerSnapshot snapshot = scheduler.Snapshot();
            Assert.AreEqual(0, snapshot.TransitionDepth);
            Assert.AreEqual(0, snapshot.TransitionHighWater);
            Assert.IsTrue(snapshot.ContinuousPending);
            ViiperInputClaim latest = Claim(scheduler);
            Assert.AreEqual(100, latest.State.GyroX);
        }

        [TestMethod]
        public void MappedBuildSerializeAndSchedulerCycleAllocateZeroAfterWarmup()
        {
            DS4State source = new()
            {
                R2 = 1,
                R2Btn = true,
            };
            byte[] packet = new byte[ViiperStatePacketBuilder.GetPacketSize(
                ViiperVirtualDeviceType.DualSense)];
            ViiperInputScheduler scheduler = NewScheduler();

            for (int index = 0; index < 512; index++)
            {
                RunAllocationCycle(scheduler, source, packet, index + 10);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                RunAllocationCycle(scheduler, source, packet, index + 1_000);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, allocated,
                $"Steady-state mapped build/classify/publish/serialize allocated {allocated} bytes.");
        }

        [TestMethod]
        public void FramedCrcAllocatesZeroAfterTableInitialization()
        {
            byte[] frame = new byte[16 + 33];
            frame[4] = 5;
            frame[5] = 1;
            frame[6] = 33;
            for (int index = 16; index < frame.Length; index++)
            {
                frame[index] = (byte)index;
            }
            _ = ViiperDeviceStream.ComputeFramedCrc(frame, frame.Length);

            long before = GC.GetAllocatedBytesForCurrentThread();
            uint checksum = 0;
            for (int index = 0; index < 10_000; index++)
            {
                checksum ^= ViiperDeviceStream.ComputeFramedCrc(frame,
                    frame.Length);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreNotEqual(0u, checksum | 1u);
            Assert.AreEqual(0, allocated);
        }

        [TestMethod]
        public void MicrophoneInterfaceEventRequiresExactPayloadAndGeneration()
        {
            byte[] payload = new byte[9];
            payload[0] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1), 73);

            Assert.IsTrue(ViiperOutDevice.
                TryParseMicrophoneInterfaceStateEvent(payload,
                    payload.Length, out bool active, out ulong generation));
            Assert.IsTrue(active);
            Assert.AreEqual(73ul, generation);
            payload[0] = 2;
            Assert.IsFalse(ViiperOutDevice.
                TryParseMicrophoneInterfaceStateEvent(payload,
                    payload.Length, out _, out _));
            payload[0] = 0;
            Assert.IsFalse(ViiperOutDevice.
                TryParseMicrophoneInterfaceStateEvent(payload, 8,
                    out _, out _));
        }

        [TestMethod]
        public void AliasFallbackRecognizesOnlyTypedExactUnknownDeviceError()
        {
            const string alias =
                "dualsensecombinedaudioduplexv5events";
            ViiperApiException exact = new(400, "Bad Request",
                "unknown device type: " + alias);
            Assert.IsTrue(exact.IsUnknownDeviceType(alias));
            Assert.IsFalse(new ViiperApiException(500, "Bad Request",
                exact.Detail).IsUnknownDeviceType(alias));
            Assert.IsFalse(new ViiperApiException(400, "Bad Request",
                "unknown device type: dualsensecombinedaudioduplexv5")
                .IsUnknownDeviceType(alias));
        }

        [TestMethod]
        public void ExplicitRateWaitCanBeInterruptedByMediaSignal()
        {
            using ViiperHighResolutionWaiter waiter = new();
            using ManualResetEvent stop = new(false);
            using AutoResetEvent media = new(true);
            ViiperDeadlineWaitResult result = waiter.WaitUntil(
                System.Diagnostics.Stopwatch.GetTimestamp() +
                    System.Diagnostics.Stopwatch.Frequency,
                stop, media);
            Assert.AreEqual(ViiperDeadlineWaitResult.Interrupted, result);
        }

        [TestMethod]
        public async Task RecoveryElectionDoesNotBlockInputOrReopenTwice()
        {
            ViiperStreamRecoveryGate recovery = new();
            ViiperInputScheduler scheduler = NewScheduler();
            using ManualResetEventSlim recoveryEntered = new(false);
            using ManualResetEventSlim releaseRecovery = new(false);
            using ManualResetEventSlim observerEntered = new(false);
            int reopenCount = 0;

            bool Recover()
            {
                Interlocked.Increment(ref reopenCount);
                recoveryEntered.Set();
                releaseRecovery.Wait();
                return true;
            }

            Task<bool> owner = Task.Factory.StartNew(
                () => recovery.ExecuteOrWait(73, Recover),
                CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Task<bool> observer = null;
            try
            {
                Assert.IsTrue(recoveryEntered.Wait(1000),
                    "The elected fake recovery owner did not start.");

                ViiperMappedInputState motion = ViiperMappedInputState.Neutral;
                motion.GyroX = 91;
                Stopwatch publishTimer = Stopwatch.StartNew();
                ViiperInputPublication publication = scheduler.Publish(
                    motion, 20);
                publishTimer.Stop();
                Assert.IsTrue(publication.Accepted);
                Assert.IsTrue(publishTimer.ElapsedMilliseconds < 100,
                    $"Input publication waited {publishTimer.ElapsedMilliseconds} ms for blocked transport recovery.");

                observer = Task.Factory.StartNew(() =>
                    {
                        observerEntered.Set();
                        return recovery.ExecuteOrWait(73, Recover);
                    }, CancellationToken.None, TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                Assert.IsTrue(observerEntered.Wait(1000),
                    "The concurrent recovery observer did not enter the gate.");
                Assert.AreNotSame(observer, await Task.WhenAny(observer,
                    Task.Delay(50)),
                    "A concurrent recovery caller did not wait for the elected owner.");
            }
            finally
            {
                // Never strand the fake owner if an assertion above fails.
                releaseRecovery.Set();
            }

            Assert.IsTrue(await owner);
            Assert.IsNotNull(observer);
            Assert.IsTrue(await observer);
            Assert.AreEqual(1, Volatile.Read(ref reopenCount),
                "Concurrent recovery callers reopened the same generation more than once.");
        }

        private static void RunAllocationCycle(ViiperInputScheduler scheduler,
            DS4State source, byte[] packet, int timestamp)
        {
            source.R2 = 1;
            ViiperMappedInputState pressed = ViiperStatePacketBuilder.
                BuildMappedState(source, -1);
            ViiperStatePacketBuilder.BuildInto(pressed, packet);
            scheduler.Publish(pressed, timestamp);
            source.R2 = 0;
            source.R2Btn = false;
            ViiperMappedInputState released = ViiperStatePacketBuilder.
                BuildMappedState(source, -1);
            ViiperStatePacketBuilder.BuildInto(released, packet);
            scheduler.Publish(released, timestamp + 1);
            source.R2Btn = true;

            while (scheduler.TryClaim(out ViiperInputClaim claim))
            {
                scheduler.CompleteSuccess(claim, timestamp + 2);
            }
        }

        private static ViiperInputScheduler NewScheduler()
        {
            ViiperInputScheduler scheduler = new();
            scheduler.Reset(11);
            scheduler.Publish(ViiperMappedInputState.Neutral, 1);
            return scheduler;
        }

        private static ViiperMappedInputState Trigger(byte r2)
        {
            ViiperMappedInputState state = ViiperMappedInputState.Neutral;
            state.R2 = r2;
            if (r2 != 0)
            {
                state.Buttons |= ViiperMappedInputState.R2ButtonMask;
            }
            return state;
        }

        private static ViiperMappedInputState DualTrigger(byte l2, byte r2)
        {
            ViiperMappedInputState state = Trigger(r2);
            state.L2 = l2;
            if (l2 != 0)
            {
                state.Buttons |= ViiperMappedInputState.L2ButtonMask;
            }
            return state;
        }

        private static void AssertDualPeakSequence(
            ViiperInputScheduler scheduler, byte expectedFirstL2,
            byte expectedFirstR2, byte expectedSecondL2,
            byte expectedSecondR2)
        {
            ViiperInputClaim first = Claim(scheduler);
            Assert.AreEqual(expectedFirstL2, first.State.L2);
            Assert.AreEqual(expectedFirstR2, first.State.R2);
            Assert.IsFalse(first.State.L2 == 240 && first.State.R2 == 250,
                "Independently timed trigger maxima were combined into a state that never existed.");
            scheduler.CompleteSuccess(first, 6);

            ViiperInputClaim second = Claim(scheduler);
            Assert.AreEqual(expectedSecondL2, second.State.L2);
            Assert.AreEqual(expectedSecondR2, second.State.R2);
            Assert.IsFalse(second.State.L2 == 240 && second.State.R2 == 250,
                "Independently timed trigger maxima were combined into a state that never existed.");
            scheduler.CompleteSuccess(second, 7);

            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 8);
            Assert.IsFalse(scheduler.TryClaim(out _),
                "No stale dual-trigger peak may follow release.");
        }

        private static ViiperInputClaim Claim(ViiperInputScheduler scheduler)
        {
            Assert.IsTrue(scheduler.TryClaim(out ViiperInputClaim claim));
            return claim;
        }
    }
}
