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
        public void EnhancedPacketCarriesSameReportRawStatusAndLegacyStaysExact()
        {
            DS4State source = new()
            {
                L2 = 91,
                R2 = 173,
                Cross = true,
                DualSenseRawInputStatus = DualSenseRawInputStatusTests.
                    CreateStatus(0x62, 0x73, 0xA5, 0x44332211u,
                        hostTimestamp: 0x88776655u,
                        deviceTimestamp: 0xCCBBAA99u,
                        touchTimestamp: 0x51, battery: 0xDD,
                        connection: 0xEE, raw55: 0xF0),
            };
            ViiperMappedInputState mapped = ViiperStatePacketBuilder.
                BuildMappedState(source, -1);
            byte[] legacy = new byte[ViiperStatePacketBuilder.
                GetDualSenseInputPacketSize(false)];
            byte[] enhanced = new byte[ViiperStatePacketBuilder.
                GetDualSenseInputPacketSize(true)];

            ViiperStatePacketBuilder.BuildInto(mapped, legacy);
            ViiperStatePacketBuilder.BuildInto(mapped, enhanced,
                includeRawInputStatus: true);

            Assert.AreEqual(33, legacy.Length);
            Assert.AreEqual(53, enhanced.Length);
            CollectionAssert.AreEqual(legacy, enhanced[..legacy.Length],
                "Negotiation may extend, but never reinterpret, the legacy state bytes.");
            Assert.AreEqual(0x01, enhanced[33]);
            Assert.AreEqual(0x44332211u,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    enhanced.AsSpan(34, 4)));
            byte[] expectedStatus =
            {
                0x51, 0x62, 0x73, 0x55, 0x66, 0x77, 0x88,
                0xA5, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xF0,
            };
            CollectionAssert.AreEqual(expectedStatus, enhanced[38..53]);
            Assert.AreEqual(0xF0, enhanced[^1],
                "Physical raw55 is transported; only raw56..63 CMAC bytes remain outside the payload.");

            mapped.RawInputStatus.IsEdgeLayout = true;
            ViiperStatePacketBuilder.BuildInto(mapped, enhanced,
                includeRawInputStatus: true);
            Assert.AreEqual(0x03, enhanced[33],
                "A valid Edge-layout observation needs its distinct layout flag.");
            Assert.AreEqual(33, ViiperStatePacketBuilder.Build(
                ViiperVirtualDeviceType.DualSense, source, -1).Length,
                "The allocating compatibility wrapper must remain legacy-sized.");
        }

        [TestMethod]
        public void EnhancedPacketClearsReusedExtensionWhenStatusIsInvalid()
        {
            byte[] packet = new byte[ViiperStatePacketBuilder.
                GetDualSenseInputPacketSize(true)];
            Array.Fill(packet, (byte)0xCC);
            ViiperMappedInputState invalid = ViiperMappedInputState.Neutral;
            invalid.RawInputStatus.IsEdgeLayout = true;
            ViiperStatePacketBuilder.BuildInto(invalid, packet,
                includeRawInputStatus: true);

            CollectionAssert.AreEqual(new byte[20], packet[33..53],
                "A reused writer slot must not leak a prior report's metadata.");
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
        public void StrengthenedRightPeakCarriesOnlyRightCoupledStatus()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState initial = Trigger(1);
            initial.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x10, 0x70, 0xA1, 0x11111111u,
                hostTimestamp: 0x12121212u, raw55: 0x11);
            scheduler.Publish(initial, 2);

            ViiperMappedInputState peak = Trigger(255);
            peak.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x28, 0x81, 0xC2, 0x22222222u,
                hostTimestamp: 0x23232323u, raw55: 0x22);
            scheduler.Publish(peak, 3);

            ViiperMappedInputState falling = Trigger(80);
            falling.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x24, 0x88, 0xE4, 0x25252525u, raw55: 0x33);
            scheduler.Publish(falling, 4);

            ViiperMappedInputState settledPeak = Trigger(255);
            settledPeak.RawInputStatus = DualSenseRawInputStatusTests.
                CreateStatus(0x29, 0x92, 0xD3, 0x33333333u,
                    hostTimestamp: 0x34343434u, raw55: 0x44);
            scheduler.Publish(settledPeak, 5);
            scheduler.Publish(Trigger(0), 6);

            ViiperInputClaim strengthened = Claim(scheduler);
            Assert.AreEqual(255, strengthened.State.R2);
            Assert.AreEqual(0x29,
                strengthened.State.RawInputStatus.RightTriggerFeedback);
            Assert.AreEqual(0x70,
                strengthened.State.RawInputStatus.LeftTriggerFeedback,
                "Strengthening R2 must not import L2 status.");
            Assert.AreEqual(0xA3,
                strengthened.State.RawInputStatus.TriggerEffectModes,
                "Only the R2 low effect nibble may follow the saved peak.");
            Assert.AreEqual(0x11111111u,
                strengthened.State.RawInputStatus.SensorTimestamp);
            Assert.AreEqual(0x12121212u,
                strengthened.State.RawInputStatus.HostTimestamp);
            Assert.AreEqual(0x11,
                strengthened.State.RawInputStatus.Raw55,
                "Trigger peak coupling must not import unrelated raw55 from another report.");
            scheduler.CompleteSuccess(strengthened, 7);

            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.R2);
            scheduler.CompleteSuccess(release, 8);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void EqualLeftPeakStatusSurvivesOrderedTransportRetry()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState initial = DualTrigger(1, 0);
            initial.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x50, 0x10, 0xA1, 0x10101010u);
            scheduler.Publish(initial, 2);
            ViiperInputClaim presentedInitial = Claim(scheduler);
            scheduler.CompleteSuccess(presentedInitial, 3);

            ViiperMappedInputState peak = DualTrigger(255, 0);
            peak.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x61, 0x28, 0x2B, 0x20202020u,
                hostTimestamp: 0x21212121u, raw55: 0x22);
            scheduler.Publish(peak, 4);
            ViiperMappedInputState settledPeak = DualTrigger(255, 0);
            settledPeak.RawInputStatus = DualSenseRawInputStatusTests.
                CreateStatus(0x72, 0x29, 0x3C, 0x30303030u,
                    hostTimestamp: 0x31313131u, raw55: 0x33);
            scheduler.Publish(settledPeak, 5);
            scheduler.Publish(DualTrigger(0, 0), 6);

            ViiperInputClaim promoted = Claim(scheduler);
            Assert.AreEqual(255, promoted.State.L2);
            Assert.AreEqual(0x29,
                promoted.State.RawInputStatus.LeftTriggerFeedback);
            Assert.AreEqual(0x61,
                promoted.State.RawInputStatus.RightTriggerFeedback,
                "Equal-peak refresh must retain unrelated R2 status from the peak snapshot.");
            Assert.AreEqual(0x3B,
                promoted.State.RawInputStatus.TriggerEffectModes);
            Assert.AreEqual(0x20202020u,
                promoted.State.RawInputStatus.SensorTimestamp,
                "Equal-peak refresh must not change the peak receive-cycle clock.");
            Assert.AreEqual(0x21212121u,
                promoted.State.RawInputStatus.HostTimestamp);
            Assert.AreEqual(0x22, promoted.State.RawInputStatus.Raw55,
                "Promoting an equal peak retains raw55 from the saved complete peak snapshot.");

            scheduler.CompleteFailure(promoted);
            ViiperInputClaim retry = Claim(scheduler);
            Assert.AreEqual(promoted.PublicationId, retry.PublicationId);
            Assert.AreEqual(promoted.State.RawInputStatus,
                retry.State.RawInputStatus);
            scheduler.CompleteSuccess(retry, 7);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            scheduler.CompleteSuccess(release, 8);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void PresentedEqualPeakStatusIsPromotedAndRetriedBeforeRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState physicalPeak = DualTrigger(255, 0);
            physicalPeak.RawInputStatus = DualSenseRawInputStatusTests.
                CreateStatus(0x61, 0x28, 0x2B, 0x20202020u,
                    hostTimestamp: 0x21212121u);
            scheduler.Publish(physicalPeak, 2);

            ViiperInputClaim firstPresentation = Claim(scheduler);
            Assert.AreEqual(0x28,
                firstPresentation.State.RawInputStatus.LeftTriggerFeedback);
            scheduler.CompleteSuccess(firstPresentation, 3);

            ViiperMappedInputState settledPeak = DualTrigger(255, 0);
            settledPeak.RawInputStatus = DualSenseRawInputStatusTests.
                CreateStatus(0x72, 0x29, 0x3C, 0x30303030u,
                    hostTimestamp: 0x31313131u);
            scheduler.Publish(settledPeak, 4);
            scheduler.Publish(DualTrigger(0, 0), 5);

            ViiperInputClaim promoted = Claim(scheduler);
            Assert.AreEqual(255, promoted.State.L2);
            Assert.AreEqual(0x29,
                promoted.State.RawInputStatus.LeftTriggerFeedback,
                "The first analog-255 report did not present the later settled trigger status.");
            Assert.AreEqual(0x61,
                promoted.State.RawInputStatus.RightTriggerFeedback,
                "Promoting settled L2 status must retain the saved peak's unrelated R2 byte.");
            Assert.AreEqual(0x3B,
                promoted.State.RawInputStatus.TriggerEffectModes);
            Assert.AreEqual(0x20202020u,
                promoted.State.RawInputStatus.SensorTimestamp,
                "Equal-peak settlement must not synthesize unrelated clock fields.");

            scheduler.CompleteFailure(promoted);
            ViiperInputClaim retry = Claim(scheduler);
            Assert.AreEqual(promoted.PublicationId, retry.PublicationId);
            Assert.AreEqual(promoted.State.RawInputStatus,
                retry.State.RawInputStatus);
            scheduler.CompleteSuccess(retry, 6);

            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            scheduler.CompleteSuccess(release, 7);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void EqualPeakLayoutChangeRemainsASeparateTruthfulSnapshot()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState basePeak = DualTrigger(255, 0);
            basePeak.RawInputStatus = DualSenseRawInputStatusTests.
                CreateStatus(0x61, 0x28, 0x2B, 0x20202020u,
                    deviceTimestamp: 0x24232221u);
            scheduler.Publish(basePeak, 2);

            ViiperMappedInputState edgePeak = DualTrigger(255, 0);
            edgePeak.RawInputStatus = DualSenseRawInputStatusTests.
                CreateStatus(0x72, 0x29, 0x3C, 0x30303030u,
                    deviceTimestamp: 0x34333231u,
                    isEdgeLayout: true);
            scheduler.Publish(edgePeak, 3);
            scheduler.Publish(DualTrigger(0, 0), 4);

            ViiperInputClaim original = Claim(scheduler);
            Assert.IsFalse(original.State.RawInputStatus.IsEdgeLayout);
            Assert.AreEqual(0x28,
                original.State.RawInputStatus.LeftTriggerFeedback,
                "The pending base-layout report must not be mutated with Edge metadata.");
            Assert.AreEqual(0x24232221u,
                original.State.RawInputStatus.DeviceTimestamp);
            scheduler.CompleteSuccess(original, 5);

            ViiperInputClaim separateEdge = Claim(scheduler);
            Assert.IsTrue(separateEdge.State.RawInputStatus.IsEdgeLayout);
            Assert.AreEqual(255, separateEdge.State.L2);
            Assert.AreEqual(0x29,
                separateEdge.State.RawInputStatus.LeftTriggerFeedback);
            Assert.AreEqual(0x34333231u,
                separateEdge.State.RawInputStatus.DeviceTimestamp,
                "A layout change must retain the complete newer physical snapshot.");
            scheduler.CompleteSuccess(separateEdge, 6);

            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            scheduler.CompleteSuccess(release, 7);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void EqualPeakStatusDoesNotRewriteEarlierButtonTransitions()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState initial = DualTrigger(255, 0);
            initial.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x61, 0x28, 0x2B, 0x20202020u);
            scheduler.Publish(initial, 2);

            ViiperMappedInputState firstButton = initial;
            firstButton.Buttons |= 0x01;
            scheduler.Publish(firstButton, 3);
            ViiperMappedInputState secondButton = firstButton;
            secondButton.Buttons |= 0x02;
            scheduler.Publish(secondButton, 4);

            ViiperMappedInputState settled = secondButton;
            settled.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x72, 0x29, 0x3C, 0x30303030u);
            scheduler.Publish(settled, 5);
            ViiperMappedInputState release = DualTrigger(0, 0);
            release.Buttons |= 0x03;
            scheduler.Publish(release, 6);

            ViiperInputClaim original = Claim(scheduler);
            Assert.AreEqual(0x28,
                original.State.RawInputStatus.LeftTriggerFeedback);
            scheduler.CompleteSuccess(original, 7);
            ViiperInputClaim firstOrderedButton = Claim(scheduler);
            Assert.AreEqual(0x01u,
                firstOrderedButton.State.Buttons & 0x03u);
            Assert.AreEqual(0x28, firstOrderedButton.State.RawInputStatus.
                LeftTriggerFeedback);
            scheduler.CompleteSuccess(firstOrderedButton, 8);
            ViiperInputClaim secondOrderedButton = Claim(scheduler);
            Assert.AreEqual(0x03u,
                secondOrderedButton.State.Buttons & 0x03u);
            Assert.AreEqual(0x28, secondOrderedButton.State.RawInputStatus.
                LeftTriggerFeedback,
                "A later raw-status observation must not be written into an earlier button state.");
            scheduler.CompleteSuccess(secondOrderedButton, 9);

            ViiperInputClaim settledPeak = Claim(scheduler);
            Assert.AreEqual(255, settledPeak.State.L2);
            Assert.AreEqual(0x03u, settledPeak.State.Buttons & 0x03u,
                "The promoted status must retain the complete latest button state.");
            Assert.AreEqual(0x29,
                settledPeak.State.RawInputStatus.LeftTriggerFeedback);
            scheduler.CompleteSuccess(settledPeak, 10);
            ViiperInputClaim released = Claim(scheduler);
            Assert.AreEqual(0, released.State.L2);
            scheduler.CompleteSuccess(released, 11);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void LowInitialWithLaterButtonsPromotesSettledPeakInChronology()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState initial = DualTrigger(1, 0);
            initial.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x61, 0x02, 0x1B, 0x10101010u);
            scheduler.Publish(initial, 2);

            ViiperMappedInputState buttonDown = DualTrigger(255, 0);
            buttonDown.Buttons |= 0x01;
            buttonDown.RawInputStatus = DualSenseRawInputStatusTests.
                CreateStatus(0x62, 0x28, 0x2B, 0x20202020u);
            scheduler.Publish(buttonDown, 3);
            ViiperMappedInputState buttonUp = buttonDown;
            buttonUp.Buttons &= ~0x01u;
            scheduler.Publish(buttonUp, 4);

            ViiperMappedInputState settled = buttonUp;
            settled.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x73, 0x29, 0x3C, 0x30303030u);
            scheduler.Publish(settled, 5);
            scheduler.Publish(DualTrigger(0, 0), 6);

            ViiperInputClaim original = Claim(scheduler);
            Assert.AreEqual(1, original.State.L2,
                "The initial press must stay ahead of later button chronology without peak mutation.");
            Assert.AreEqual(0x02,
                original.State.RawInputStatus.LeftTriggerFeedback);
            scheduler.CompleteSuccess(original, 7);

            ViiperInputClaim orderedDown = Claim(scheduler);
            Assert.AreEqual(255, orderedDown.State.L2);
            Assert.AreEqual(0x01u, orderedDown.State.Buttons & 0x01u);
            Assert.AreEqual(0x28,
                orderedDown.State.RawInputStatus.LeftTriggerFeedback);
            scheduler.CompleteSuccess(orderedDown, 8);
            ViiperInputClaim orderedUp = Claim(scheduler);
            Assert.AreEqual(0u, orderedUp.State.Buttons & 0x01u);
            Assert.AreEqual(0x28,
                orderedUp.State.RawInputStatus.LeftTriggerFeedback);
            scheduler.CompleteSuccess(orderedUp, 9);

            ViiperInputClaim settledPeak = Claim(scheduler);
            Assert.AreEqual(255, settledPeak.State.L2);
            Assert.AreEqual(0u, settledPeak.State.Buttons & 0x01u);
            Assert.AreEqual(0x29,
                settledPeak.State.RawInputStatus.LeftTriggerFeedback,
                "The complete settled peak must follow the button transitions and precede release.");
            scheduler.CompleteSuccess(settledPeak, 10);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            scheduler.CompleteSuccess(release, 11);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void FailedInitialRetryRemainsExactBeforeSavedPeakAndRelease()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            ViiperMappedInputState initial = DualTrigger(1, 0);
            initial.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x61, 0x10, 0x2B, 0x20202020u);
            scheduler.Publish(initial, 2);
            ViiperInputClaim failedInitial = Claim(scheduler);
            scheduler.CompleteFailure(failedInitial);

            ViiperMappedInputState peak = DualTrigger(255, 0);
            peak.RawInputStatus = DualSenseRawInputStatusTests.CreateStatus(
                0x72, 0x29, 0x3C, 0x30303030u);
            scheduler.Publish(peak, 3);
            scheduler.Publish(DualTrigger(0, 0), 4);

            ViiperInputClaim exactRetry = Claim(scheduler);
            Assert.AreEqual(failedInitial.PublicationId,
                exactRetry.PublicationId);
            Assert.AreEqual(1, exactRetry.State.L2,
                "A previously claimed failed transition must not be peak-strengthened in retry storage.");
            Assert.AreEqual(0x10,
                exactRetry.State.RawInputStatus.LeftTriggerFeedback);
            Assert.AreEqual(failedInitial.State.RawInputStatus,
                exactRetry.State.RawInputStatus);
            scheduler.CompleteSuccess(exactRetry, 5);

            ViiperInputClaim savedPeak = Claim(scheduler);
            Assert.AreEqual(255, savedPeak.State.L2);
            Assert.AreEqual(0x29,
                savedPeak.State.RawInputStatus.LeftTriggerFeedback);
            scheduler.CompleteSuccess(savedPeak, 6);
            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            scheduler.CompleteSuccess(release, 7);
            Assert.IsFalse(scheduler.TryClaim(out _));
        }

        [TestMethod]
        public void ConstantLeftTriggerStatusSettlesLatestWithoutFifoGrowth()
        {
            ViiperInputScheduler scheduler = NewScheduler();
            byte[] feedbackSequence = { 0x02, 0x12, 0x22, 0x28, 0x29 };
            for (int index = 0; index < feedbackSequence.Length; index++)
            {
                ViiperMappedInputState state = DualTrigger(255, 0);
                state.RawInputStatus = DualSenseRawInputStatusTests.
                    CreateStatus(0x61, feedbackSequence[index], 0x3B,
                        (uint)(0x40000000 + index));
                ViiperInputPublication publication = scheduler.Publish(state,
                    2 + index);
                Assert.AreEqual(index == 0, publication.IsTransition,
                    "Status-only evolution at a constant analog value is replaceable state.");
            }

            ViiperInputSchedulerSnapshot beforeRelease = scheduler.Snapshot();
            Assert.AreEqual(1, beforeRelease.TransitionDepth);
            Assert.AreEqual(1, beforeRelease.TransitionHighWater);
            Assert.AreEqual(1L, beforeRelease.TransitionCount);
            Assert.IsTrue(beforeRelease.ContinuousPending);

            scheduler.Publish(DualTrigger(0, 0), 8);
            ViiperInputClaim pressed = Claim(scheduler);
            Assert.AreEqual(255, pressed.State.L2);
            Assert.AreEqual(0x29,
                pressed.State.RawInputStatus.LeftTriggerFeedback,
                "The settled physical trigger status must be presented before release.");
            Assert.AreEqual(0x40000000u,
                pressed.State.RawInputStatus.SensorTimestamp,
                "Status settlement must not rewrite the saved report's clock fields.");
            scheduler.CompleteSuccess(pressed, 9);

            ViiperInputClaim release = Claim(scheduler);
            Assert.AreEqual(0, release.State.L2);
            scheduler.CompleteSuccess(release, 10);
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
                DualSenseRawInputStatus = DualSenseRawInputStatusTests.
                    CreateStatus(0x20, 0x30, 0x41, 0x12345678u),
            };
            byte[] packet = new byte[ViiperStatePacketBuilder.
                GetDualSenseInputPacketSize(true)];
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
        public void RawInputAliasNamesAreDistinctFromShippedV5EventsContracts()
        {
            Assert.AreEqual(
                "dualsensecombinedaudioduplexv5rawinputevents",
                ViiperOutDevice.GetV5RawInputDeviceName(
                    ViiperVirtualDeviceType.DualSense,
                    audioOnlySidecar: false, gamepadOnly: false));
            Assert.AreEqual(
                "dualsenseaudioonlyduplexv5rawinputevents",
                ViiperOutDevice.GetV5RawInputDeviceName(
                    ViiperVirtualDeviceType.DualSense,
                    audioOnlySidecar: true, gamepadOnly: false));
            Assert.AreEqual("dualsensegamepadv5rawinput",
                ViiperOutDevice.GetV5RawInputDeviceName(
                    ViiperVirtualDeviceType.DualSense,
                    audioOnlySidecar: false, gamepadOnly: true));
            Assert.AreEqual(
                "dualsenseedgecombinedaudioduplexv5rawinputevents",
                ViiperOutDevice.GetV5RawInputDeviceName(
                    ViiperVirtualDeviceType.DualSenseEdge,
                    audioOnlySidecar: false, gamepadOnly: false));
            Assert.AreEqual("dualsenseedgegamepadv5rawinput",
                ViiperOutDevice.GetV5RawInputDeviceName(
                    ViiperVirtualDeviceType.DualSenseEdge,
                    audioOnlySidecar: false, gamepadOnly: true));
            Assert.AreEqual("dualsensecombinedaudioduplexv5events",
                ViiperOutDevice.GetV5EventDeviceName(
                    ViiperVirtualDeviceType.DualSense,
                    audioOnlySidecar: false));
        }

        [TestMethod]
        public void CompositeRawInputNegotiationUsesThreeExactTiers()
        {
            const string raw =
                "dualsensecombinedaudioduplexv5rawinputevents";
            const string events =
                "dualsensecombinedaudioduplexv5events";
            const string legacy =
                "dualsensecombinedaudioduplexv5";

            AssertNegotiationTier(successfulAttempt: 1,
                expectedAliases: new[] { raw },
                expectedRaw: true, expectedEvents: true);
            AssertNegotiationTier(successfulAttempt: 2,
                expectedAliases: new[] { raw, events },
                expectedRaw: false, expectedEvents: true);
            AssertNegotiationTier(successfulAttempt: 3,
                expectedAliases: new[] { raw, events, legacy },
                expectedRaw: false, expectedEvents: false);

            void AssertNegotiationTier(int successfulAttempt,
                string[] expectedAliases, bool expectedRaw,
                bool expectedEvents)
            {
                List<string> attempts = new();
                ViiperDeviceStream Open(string alias)
                {
                    attempts.Add(alias);
                    if (attempts.Count < successfulAttempt)
                    {
                        throw UnknownAlias(alias);
                    }
                    return null;
                }

                _ = ViiperOutDevice.OpenRawInputV5StreamWithFallback(Open,
                    raw, events, legacy,
                    supportsMicrophoneInterfaceEvents: true,
                    out bool rawStatus, out bool microphoneEvents);
                CollectionAssert.AreEqual(expectedAliases, attempts);
                Assert.AreEqual(expectedRaw, rawStatus);
                Assert.AreEqual(expectedEvents, microphoneEvents);
            }
        }

        [TestMethod]
        public void GamepadRawInputFallbackSkipsIncompatibleEventsAlias()
        {
            const string raw = "dualsensegamepadv5rawinput";
            const string legacy = "dualsensegamepadv5";
            List<string> attempts = new();
            ViiperDeviceStream Open(string alias)
            {
                attempts.Add(alias);
                if (alias == raw)
                {
                    throw UnknownAlias(alias);
                }
                return null;
            }

            _ = ViiperOutDevice.OpenRawInputV5StreamWithFallback(Open, raw,
                eventDeviceName: null, legacyDeviceName: legacy,
                supportsMicrophoneInterfaceEvents: false,
                out bool rawStatus, out bool microphoneEvents);

            CollectionAssert.AreEqual(new[] { raw, legacy }, attempts);
            Assert.IsFalse(rawStatus);
            Assert.IsFalse(microphoneEvents);
        }

        [TestMethod]
        public void RawInputNegotiationDoesNotHideNonCapabilityFailure()
        {
            const string raw = "dualsensegamepadv5rawinput";
            int attempts = 0;
            ViiperApiException failure = Assert.ThrowsException<
                ViiperApiException>(() => ViiperOutDevice.
                    OpenRawInputV5StreamWithFallback(alias =>
                    {
                        attempts++;
                        throw new ViiperApiException(500, "Server Error",
                            "create failed");
                    }, raw, eventDeviceName: null,
                    legacyDeviceName: "dualsensegamepadv5",
                    supportsMicrophoneInterfaceEvents: false,
                    out _, out _));
            Assert.AreEqual(500, failure.Status);
            Assert.AreEqual(1, attempts);
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
            ViiperStatePacketBuilder.BuildInto(pressed, packet,
                includeRawInputStatus: true);
            scheduler.Publish(pressed, timestamp);
            source.R2 = 0;
            source.R2Btn = false;
            ViiperMappedInputState released = ViiperStatePacketBuilder.
                BuildMappedState(source, -1);
            ViiperStatePacketBuilder.BuildInto(released, packet,
                includeRawInputStatus: true);
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

        private static ViiperApiException UnknownAlias(string alias) =>
            new(400, "Bad Request", "unknown device type: " + alias);
    }
}
