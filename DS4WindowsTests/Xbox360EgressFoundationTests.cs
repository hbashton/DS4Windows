using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class Xbox360EgressFoundationTests
    {
        private const long TestMaximumOrderedAge = 1_000_000;
        private const uint A = 0x00001000;
        private const uint B = 0x00002000;
        private const uint X = 0x00004000;
        private const uint Y = 0x00008000;

        [TestMethod]
        public void OrderedAgePolicyRequiresAnExplicitBoundedWholeMillisecond()
        {
            Assert.IsTrue(Xbox360PresentationPolicy.
                TryParseMaximumOrderedAgeMilliseconds(null, out int unset,
                    out string unsetError));
            Assert.AreEqual(0, unset);
            Assert.IsNull(unsetError);

            Assert.IsTrue(Xbox360PresentationPolicy.
                TryParseMaximumOrderedAgeMilliseconds("17", out int declared,
                    out string declaredError));
            Assert.AreEqual(17, declared);
            Assert.IsNull(declaredError);
            Assert.IsTrue(Xbox360PresentationPolicy.ToStopwatchTicks(declared) >
                0);

            foreach (string invalid in new[] { "0", "-1", "1.5", "60001" })
            {
                Assert.IsFalse(Xbox360PresentationPolicy.
                    TryParseMaximumOrderedAgeMilliseconds(invalid, out _,
                        out string error), invalid);
                StringAssert.Contains(error,
                    Xbox360PresentationPolicy.
                        MaximumOrderedAgeEnvironmentVariable);
            }
        }

        [TestMethod]
        public void DeclaredAgePolicyIsSerializedAsDeviceSpecificDuration()
        {
            string strict = ViiperClient.SerializeDeviceCreateRequest(
                "xbox360", deviceSpecific:
                    new ViiperClient.Xbox360CreateOptions
                    {
                        MaximumOrderedAgeMilliseconds = 17,
                    });
            using JsonDocument strictDocument = JsonDocument.Parse(strict);
            JsonElement root = strictDocument.RootElement;
            Assert.AreEqual("xbox360", root.GetProperty("type").GetString());
            Assert.AreEqual(17, root.GetProperty("deviceSpecific").
                GetProperty("maximumOrderedAgeMilliseconds").GetInt32());

            string compatibility = ViiperClient.SerializeDeviceCreateRequest(
                "xbox360");
            using JsonDocument compatibilityDocument =
                JsonDocument.Parse(compatibility);
            Assert.IsFalse(compatibilityDocument.RootElement.TryGetProperty(
                "deviceSpecific", out _));
        }

        [TestMethod]
        public void CanonicalStateMatchesAuditVectorAndAlwaysZerosTail()
        {
            Xbox360EgressState state = new(
                Xbox360EgressState.ValidButtonsMask, 37, 219,
                short.MinValue, short.MaxValue, short.MaxValue,
                short.MinValue);
            byte[] wire = new byte[Xbox360EgressState.WireSize];
            Array.Fill(wire, (byte)0xCC);

            state.BuildInto(wire);

            Assert.AreEqual(20, wire.Length);
            Assert.AreEqual(0x0000F7FFu,
                BinaryPrimitives.ReadUInt32LittleEndian(wire));
            Assert.AreEqual(37, wire[4]);
            Assert.AreEqual(219, wire[5]);
            Assert.AreEqual(short.MinValue,
                BinaryPrimitives.ReadInt16LittleEndian(wire.AsSpan(6, 2)));
            Assert.AreEqual(short.MaxValue,
                BinaryPrimitives.ReadInt16LittleEndian(wire.AsSpan(8, 2)));
            Assert.AreEqual(short.MaxValue,
                BinaryPrimitives.ReadInt16LittleEndian(wire.AsSpan(10, 2)));
            Assert.AreEqual(short.MinValue,
                BinaryPrimitives.ReadInt16LittleEndian(wire.AsSpan(12, 2)));
            CollectionAssert.AreEqual(new byte[6], wire[14..20],
                "Reserved bytes are not caller-controlled semantic state.");
        }

        [TestMethod]
        public void Ds4ProjectionAndLegacyBuilderRemainByteExact()
        {
            DS4State source = new()
            {
                DpadUp = true,
                DpadLeft = true,
                Options = true,
                Share = true,
                L1 = true,
                R1 = true,
                PS = true,
                Cross = true,
                Triangle = true,
                L2 = 17,
                R2 = 231,
                LX = 0,
                LY = 255,
                RX = 128,
                RY = 64,
            };

            Xbox360EgressState projected = ViiperStatePacketBuilder.
                BuildXbox360State(source, -1);
            byte[] projectedWire = new byte[Xbox360EgressState.WireSize];
            projected.BuildInto(projectedWire);
            byte[] legacyWire = ViiperStatePacketBuilder.Build(
                ViiperVirtualDeviceType.Xbox360, source, -1);

            CollectionAssert.AreEqual(legacyWire, projectedWire);
            Assert.AreEqual(0x00009735u, projected.Buttons);
            Assert.AreEqual((short)-32768, projected.LeftStickX);
            Assert.AreEqual((short)-32768, projected.LeftStickY);
            Assert.AreEqual((short)0, projected.RightStickX);
            Assert.AreEqual((short)16383, projected.RightStickY);
            CollectionAssert.AreEqual(new byte[6], projectedWire[14..20]);
        }

        [TestMethod]
        public void OpposingDPadBitsAreValidAtTheCanonicalBoundary()
        {
            Xbox360EgressState state = State(buttons: 0x0000000F);
            Span<byte> wire = stackalloc byte[Xbox360EgressState.WireSize];

            state.BuildInto(wire);

            Assert.AreEqual(0x0000000Fu,
                BinaryPrimitives.ReadUInt32LittleEndian(wire));
        }

        [TestMethod]
        public void StateRejectsReservedButtonsAndNonExactWireStorage()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                State(buttons: 0x00000800));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                State(buttons: 0x00010000));

            Xbox360EgressState neutral = Xbox360EgressState.Neutral;
            Assert.ThrowsException<ArgumentException>(() =>
                neutral.BuildInto(new byte[19]));
            Assert.ThrowsException<ArgumentException>(() =>
                neutral.BuildInto(new byte[21]));
        }

        [TestMethod]
        public void StateAndClaimAreImmutableValueOwnedTypes()
        {
            AssertImmutableValueOwned<Xbox360EgressState>();
            AssertImmutableValueOwned<OrderedEgressClaim<Xbox360EgressState>>();
        }

        [TestMethod]
        public void OnlyButtonsAndTriggerZeroCrossingsAreOrdered()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;

            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedContinuous,
                scheduler.Publish(epoch, State(lx: 100), 10));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, State(buttons: A, lx: 100), 11));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch,
                    State(buttons: A, leftTrigger: 1, lx: 100), 12));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedContinuous,
                scheduler.Publish(epoch,
                    State(buttons: A, leftTrigger: 255, lx: -100), 13));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch,
                    State(buttons: A, leftTrigger: 0, lx: -100), 14));
        }

        [TestMethod]
        public void CompatibilityPolicyHasNoAgeDeadlineButOverflowFailsClosed()
        {
            Xbox360EgressScheduler noAge = new(0, orderedCapacity: 2);
            OrderedEgressProducerEpoch noAgeEpoch =
                noAge.CurrentProducerEpoch;

            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                noAge.Publish(noAgeEpoch, State(buttons: A), 10));
            OrderedEgressClaim<Xbox360EgressState> retained = Claim(noAge,
                long.MaxValue - 1);
            Assert.AreEqual(A, retained.State.Buttons,
                "Compatibility mode must not invent an ordered-age deadline.");
            Commit(noAge, retained, long.MaxValue - 1);

            Xbox360EgressScheduler overflow = new(0, orderedCapacity: 2);
            OrderedEgressProducerEpoch oldEpoch =
                overflow.CurrentProducerEpoch;
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                overflow.Publish(oldEpoch, State(buttons: A), 10));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                overflow.Publish(oldEpoch, State(), 11));
            Assert.AreEqual(OrderedEgressPublishDisposition.FaultedOverflow,
                overflow.Publish(oldEpoch, State(buttons: A), 12));

            OrderedEgressSchedulerSnapshot snapshot = overflow.Snapshot();
            Assert.AreEqual(OrderedEgressAgePolicy.CompatibilityNoAgeLimit,
                snapshot.OrderedAgePolicy);
            Assert.AreEqual(0L, snapshot.MaximumOrderedAge);
            Assert.AreEqual(1L, snapshot.OverflowFaults);
            Assert.IsTrue(snapshot.MandatoryNeutralPending);
            Assert.IsFalse(snapshot.ResynchronizationRequired);
            Assert.AreEqual(0, snapshot.OrderedDepth);
            Assert.AreNotEqual(oldEpoch.Value, snapshot.ProducerEpoch);

            OrderedEgressProducerEpoch recoveryEpoch =
                overflow.CurrentProducerEpoch;
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedFaultNeutralPending,
                overflow.Publish(recoveryEpoch, State(), 13),
                "The release after a rejected press was silently treated as continuous state.");
            OrderedEgressClaim<Xbox360EgressState> neutral = Claim(overflow, 13);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutral.Kind);
            Assert.IsTrue(neutral.State.IsNeutral);
            Commit(overflow, neutral, 13);
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedResynchronization,
                overflow.Resynchronize(recoveryEpoch, State(), 13));
        }

        [TestMethod]
        public void ShortPressReleaseCannotCollapseIntoContinuousLatest()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, State(buttons: A), 10));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, State(), 11));

            OrderedEgressClaim<Xbox360EgressState> press = Claim(scheduler, 11);
            Assert.AreEqual(A, press.State.Buttons);
            Assert.IsTrue(press.IsOrdered);
            Assert.AreEqual(OrderedEgressClaimKind.Ordered, press.Kind);
            Commit(scheduler, press, 11);

            OrderedEgressClaim<Xbox360EgressState> release = Claim(scheduler, 11);
            Assert.IsTrue(release.State.IsNeutral);
            Assert.IsTrue(release.IsOrdered);
            Commit(scheduler, release, 11);

            OrderedEgressClaim<Xbox360EgressState> idle = Claim(scheduler, 11);
            Assert.AreEqual(OrderedEgressClaimKind.Idle, idle.Kind);
            Assert.IsTrue(idle.State.IsNeutral);
            Commit(scheduler, idle, 11);
        }

        [TestMethod]
        public void StreamWriterClaimDoesNotCreateAnIdleBusyLoop()
        {
            Xbox360EgressScheduler scheduler = new(0);
            Assert.IsFalse(scheduler.TryClaim(1, out _, includeIdle: false));

            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, State(buttons: A), 2));
            Assert.IsTrue(scheduler.TryClaim(2, out OrderedEgressClaim<Xbox360EgressState> claim,
                includeIdle: false));
            Commit(scheduler, claim, 2);
            Assert.IsFalse(scheduler.TryClaim(3, out _, includeIdle: false));
        }

        [TestMethod]
        public void TriggerPeakIsPromotedWithItsOriginalTimestampBeforeRelease()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(rightTrigger: 1), 10);
            scheduler.Publish(epoch, State(rightTrigger: 255), 20);
            scheduler.Publish(epoch, State(), 30);

            OrderedEgressClaim<Xbox360EgressState> initial = Claim(scheduler, 30);
            Assert.AreEqual(1, initial.State.RightTrigger);
            Commit(scheduler, initial, 30);

            OrderedEgressClaim<Xbox360EgressState> peak = Claim(scheduler, 30);
            Assert.AreEqual(255, peak.State.RightTrigger);
            Assert.AreEqual(20L, peak.ReceivedTimestamp,
                "Promotion must retain the truthful report timestamp.");
            Assert.IsTrue(peak.IsOrdered);
            Commit(scheduler, peak, 30);

            OrderedEgressClaim<Xbox360EgressState> release = Claim(scheduler, 30);
            Assert.AreEqual(0, release.State.RightTrigger);
            Assert.AreEqual(30L, release.ReceivedTimestamp);
            Commit(scheduler, release, 30);

            OrderedEgressSchedulerSnapshot snapshot = scheduler.Snapshot();
            Assert.AreEqual(1L, snapshot.ContinuousPromotions);
            Assert.AreEqual(
                OrderedEgressAgePolicy.CallerSuppliedMonotonicLimit,
                snapshot.OrderedAgePolicy);
            Assert.AreEqual(TestMaximumOrderedAge,
                snapshot.MaximumOrderedAge);
        }

        [TestMethod]
        public void PromotionAndEdgeReservationFaultAtomicallyToNeutral()
        {
            Xbox360EgressScheduler scheduler = NewScheduler(
                orderedCapacity: 2);
            OrderedEgressProducerEpoch faultedEpoch =
                scheduler.CurrentProducerEpoch;
            ulong presentationGeneration = scheduler.PresentationGeneration;
            scheduler.Publish(faultedEpoch, State(buttons: A), 10);
            scheduler.Publish(faultedEpoch, State(buttons: A, lx: 1234), 20);

            Assert.AreEqual(OrderedEgressPublishDisposition.FaultedOverflow,
                scheduler.Publish(faultedEpoch, State(lx: 1234), 30));

            OrderedEgressSchedulerSnapshot fault = scheduler.Snapshot();
            Assert.AreEqual(0, fault.OrderedDepth,
                "Neither half of a failed promotion+edge reservation may leak.");
            Assert.IsFalse(fault.ContinuousPending);
            Assert.IsTrue(fault.MandatoryNeutralPending);
            Assert.AreEqual(1L, fault.OverflowFaults);
            Assert.AreEqual(presentationGeneration,
                fault.PresentationGeneration,
                "Producer overflow must not masquerade as presentation retirement.");
            Assert.AreNotEqual(faultedEpoch.Value, fault.ProducerEpoch);

            OrderedEgressProducerEpoch recoveryEpoch =
                scheduler.CurrentProducerEpoch;
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedStaleProducerEpoch,
                scheduler.Publish(faultedEpoch, State(buttons: B), 31));
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedFaultNeutralPending,
                scheduler.Publish(recoveryEpoch, State(buttons: B), 32));

            OrderedEgressClaim<Xbox360EgressState> neutral = Claim(scheduler, 30);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutral.Kind);
            Assert.IsTrue(neutral.IsOrdered);
            Assert.IsTrue(neutral.State.IsNeutral);
            byte[] neutralWire = new byte[Xbox360EgressState.WireSize];
            neutral.BuildInto(neutralWire);
            CollectionAssert.AreEqual(new byte[20], neutralWire);

            Assert.IsTrue(scheduler.TryAdmit(neutral, 30));
            Assert.IsTrue(scheduler.Complete(neutral,
                OrderedEgressCompletion.Defer));
            OrderedEgressClaim<Xbox360EgressState> neutralRetry = Claim(scheduler, 30);
            Assert.AreNotEqual(neutral.Token, neutralRetry.Token);
            Assert.AreEqual(neutral.Ordinal, neutralRetry.Ordinal);
            Assert.AreEqual(neutral.ReceivedTimestamp,
                neutralRetry.ReceivedTimestamp);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutralRetry.Kind);
            Commit(scheduler, neutralRetry, 30);

            Assert.AreEqual(
                OrderedEgressPublishDisposition.
                    RejectedResynchronizationRequired,
                scheduler.Publish(recoveryEpoch, State(buttons: B), 40));
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedResynchronization,
                scheduler.Resynchronize(recoveryEpoch,
                    State(buttons: B, lx: 2222), 41));
            OrderedEgressClaim<Xbox360EgressState> fresh = Claim(scheduler, 41);
            Assert.AreEqual(OrderedEgressClaimKind.Continuous, fresh.Kind);
            Assert.AreEqual(B, fresh.State.Buttons);
            Assert.AreEqual(2222, fresh.State.LeftStickX);
            Commit(scheduler, fresh, 41);
        }

        [DataTestMethod]
        [DataRow((byte)OrderedEgressCompletion.Commit)]
        [DataRow((byte)OrderedEgressCompletion.Defer)]
        public void PreFaultActiveClaimCannotDisplaceOrRetryPastNeutral(
            byte completionValue)
        {
            OrderedEgressCompletion completion =
                (OrderedEgressCompletion)completionValue;
            Xbox360EgressScheduler scheduler = NewScheduler(
                orderedCapacity: 2);
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(buttons: A), 10);
            OrderedEgressClaim<Xbox360EgressState> active = Claim(scheduler, 10);
            Assert.IsTrue(scheduler.TryAdmit(active, 10));

            scheduler.Publish(epoch, State(buttons: B), 20);
            scheduler.Publish(epoch, State(buttons: X), 21);
            Assert.AreEqual(OrderedEgressPublishDisposition.FaultedOverflow,
                scheduler.Publish(epoch, State(buttons: Y), 22));

            OrderedEgressSchedulerSnapshot fault = scheduler.Snapshot();
            Assert.IsTrue(fault.MandatoryNeutralPending);
            Assert.IsFalse(fault.HasOrderedDependency,
                "A pre-fault admitted claim was mislabeled as retryable history.");

            Assert.IsTrue(scheduler.Complete(active, completion),
                "The already-owned claim has exactly one terminal completion.");
            Assert.IsFalse(scheduler.Complete(active, completion));
            OrderedEgressClaim<Xbox360EgressState> next = Claim(scheduler, 22);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                next.Kind,
                "A defer must not resurrect pre-fault bytes; a commit must not postpone neutral.");
            Assert.IsTrue(next.State.IsNeutral);
            Commit(scheduler, next, 22);
        }

        [TestMethod]
        public void OrderedRetryPreservesStateBytesTimestampAndOrdinal()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            Xbox360EgressState state = State(buttons: A | B,
                leftTrigger: 73, rightTrigger: 201, lx: -2345, ly: 4567,
                rx: short.MinValue, ry: short.MaxValue);
            scheduler.Publish(epoch, state, 123456);
            OrderedEgressClaim<Xbox360EgressState> first = Claim(scheduler, 123456);
            byte[] firstWire = Build(first);

            Assert.IsTrue(scheduler.TryAdmit(first, 123456));
            Assert.IsTrue(scheduler.Complete(first,
                OrderedEgressCompletion.Defer));
            OrderedEgressClaim<Xbox360EgressState> retry = Claim(scheduler, 123456);
            CollectionAssert.AreEqual(firstWire, Build(retry));
            Assert.AreEqual(first.State, retry.State);
            Assert.AreEqual(first.ReceivedTimestamp, retry.ReceivedTimestamp);
            Assert.AreEqual(first.Ordinal, retry.Ordinal);
            Assert.AreEqual(first.ProducerEpoch, retry.ProducerEpoch);
            Assert.AreEqual(first.PresentationGeneration,
                retry.PresentationGeneration);
            Assert.AreNotEqual(first.Token, retry.Token);
            Assert.AreEqual(OrderedEgressClaimKind.Retry, retry.Kind);
            Assert.IsTrue(retry.IsOrdered);

            Assert.IsTrue(scheduler.TryAdmit(retry, 123456));
            Assert.IsTrue(scheduler.Complete(retry,
                OrderedEgressCompletion.Defer));
            OrderedEgressClaim<Xbox360EgressState> secondRetry = Claim(scheduler, 123456);
            CollectionAssert.AreEqual(firstWire, Build(secondRetry));
            Assert.AreEqual(OrderedEgressClaimKind.Retry,
                secondRetry.Kind);
            Commit(scheduler, secondRetry, 123456);
        }

        [TestMethod]
        public void InFlightContinuousPredecessorRetriesAheadOfLaterEdge()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch,
                State(buttons: A, rightTrigger: 1), 10);
            Commit(scheduler, Claim(scheduler, 10), 10);

            Xbox360EgressState peakState = State(buttons: A,
                rightTrigger: 255);
            scheduler.Publish(epoch, peakState, 20);
            OrderedEgressClaim<Xbox360EgressState> peak = Claim(scheduler, 20);
            Assert.AreEqual(OrderedEgressClaimKind.Continuous, peak.Kind);
            scheduler.Publish(epoch, State(), 30);
            Assert.IsTrue(scheduler.TryAdmit(peak, 30));
            Assert.IsTrue(scheduler.Complete(peak,
                OrderedEgressCompletion.Defer));

            OrderedEgressClaim<Xbox360EgressState> retry = Claim(scheduler, 30);
            Assert.AreEqual(OrderedEgressClaimKind.Retry, retry.Kind);
            Assert.AreEqual(peakState, retry.State);
            Assert.IsTrue(retry.IsOrdered);
            Commit(scheduler, retry, 30);
            OrderedEgressClaim<Xbox360EgressState> release = Claim(scheduler, 30);
            Assert.AreEqual(OrderedEgressClaimKind.Ordered, release.Kind);
            Assert.IsTrue(release.State.IsNeutral);
            Commit(scheduler, release, 30);
        }

        [TestMethod]
        public void CommittedInFlightContinuousPredecessorIsNotDuplicated()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(buttons: A), 10);
            Commit(scheduler, Claim(scheduler, 10), 10);
            scheduler.Publish(epoch, State(buttons: A, lx: 32767), 20);
            OrderedEgressClaim<Xbox360EgressState> predecessor = Claim(scheduler, 20);
            scheduler.Publish(epoch, State(), 30);
            Commit(scheduler, predecessor, 30);

            OrderedEgressClaim<Xbox360EgressState> release = Claim(scheduler, 30);
            Assert.AreEqual(OrderedEgressClaimKind.Ordered, release.Kind);
            Assert.IsTrue(release.State.IsNeutral);
            Commit(scheduler, release, 30);
            OrderedEgressClaim<Xbox360EgressState> idle = Claim(scheduler, 30);
            Assert.AreEqual(OrderedEgressClaimKind.Idle, idle.Kind);
            Assert.IsTrue(idle.State.IsNeutral);
            Commit(scheduler, idle, 30);
        }

        [TestMethod]
        public void TokensRejectWrongDoubleAndCrossGenerationCompletion()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch oldProducerEpoch =
                scheduler.CurrentProducerEpoch;
            scheduler.Publish(oldProducerEpoch, State(buttons: A), 10);
            OrderedEgressClaim<Xbox360EgressState> active = Claim(scheduler, 10);
            OrderedEgressClaim<Xbox360EgressState> forged = new(active.State, active.Token + 1,
                active.PresentationGeneration, active.ProducerEpoch,
                active.Ordinal, active.ReceivedTimestamp, active.IsOrdered,
                active.Kind);
            Assert.IsFalse(scheduler.Complete(forged,
                OrderedEgressCompletion.Commit));
            Assert.IsFalse(scheduler.TryClaim(10, out _),
                "An invalid completion must not release ownership.");

            ulong oldGeneration = active.PresentationGeneration;
            Assert.IsTrue(scheduler.RetirePresentationGeneration(
                oldGeneration, 11));
            Assert.IsFalse(scheduler.Complete(active,
                OrderedEgressCompletion.Commit));
            Assert.AreNotEqual(oldGeneration,
                scheduler.PresentationGeneration);
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedStaleProducerEpoch,
                scheduler.Publish(oldProducerEpoch, State(buttons: B), 20));

            OrderedEgressProducerEpoch newProducerEpoch =
                scheduler.CurrentProducerEpoch;
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(newProducerEpoch, State(buttons: B), 21));
            OrderedEgressClaim<Xbox360EgressState> successor = Claim(scheduler, 21);
            Commit(scheduler, successor, 21);
            Assert.IsFalse(scheduler.Complete(successor,
                OrderedEgressCompletion.Commit));
        }

        [TestMethod]
        public void ProducerEpochCaptureIsBoundToExactPresentationGeneration()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            ulong retiredGeneration = scheduler.PresentationGeneration;
            Assert.IsTrue(scheduler.TryCaptureProducerEpoch(
                retiredGeneration, out OrderedEgressProducerEpoch retired));

            Assert.IsTrue(scheduler.RetirePresentationGeneration(
                retiredGeneration, 10));
            Assert.IsFalse(scheduler.TryCaptureProducerEpoch(
                retiredGeneration, out OrderedEgressProducerEpoch rejected));
            Assert.IsFalse(rejected.IsValid);

            ulong successorGeneration = scheduler.PresentationGeneration;
            Assert.AreNotEqual(retiredGeneration, successorGeneration);
            Assert.IsTrue(scheduler.TryCaptureProducerEpoch(
                successorGeneration,
                out OrderedEgressProducerEpoch successor));
            Assert.AreNotEqual(retired.Value, successor.Value);
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedStaleProducerEpoch,
                scheduler.Publish(retired, State(buttons: A), 11));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(successor, State(buttons: A), 11));
        }

        [TestMethod]
        public void OrderedHeadAtAgeLimitFaultsToOneNeutralAndFreshResync()
        {
            Xbox360EgressScheduler scheduler = NewScheduler(
                maximumOrderedAge: 10);
            OrderedEgressProducerEpoch oldEpoch =
                scheduler.CurrentProducerEpoch;
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(oldEpoch, State(buttons: A), 100));
            OrderedEgressSchedulerSnapshot queued = scheduler.Snapshot();
            Assert.IsTrue(queued.HasOrderedDependency);
            Assert.AreEqual(100L, queued.OldestOrderedTimestamp);

            OrderedEgressClaim<Xbox360EgressState> neutral = Claim(scheduler, 110);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutral.Kind);
            Assert.IsTrue(neutral.State.IsNeutral);
            OrderedEgressSchedulerSnapshot fault = scheduler.Snapshot();
            Assert.AreEqual(1L, fault.OrderedAgeFaults);
            Assert.AreEqual(10L, fault.LastOldestAgeAtFault);
            Assert.AreEqual(0, fault.OrderedDepth);
            Assert.AreNotEqual(oldEpoch.Value, fault.ProducerEpoch);
            Assert.IsTrue(fault.MandatoryNeutralPending);
            Commit(scheduler, neutral, 110);

            OrderedEgressProducerEpoch recoveryEpoch =
                scheduler.CurrentProducerEpoch;
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedInvalidTimestamp,
                scheduler.Resynchronize(recoveryEpoch,
                    State(buttons: B), 109),
                "A snapshot captured before the fault was accepted as fresh resynchronization.");
            Assert.AreEqual(
                OrderedEgressPublishDisposition.
                    RejectedResynchronizationRequired,
                scheduler.Publish(recoveryEpoch, State(buttons: B), 111));
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedResynchronization,
                scheduler.Resynchronize(recoveryEpoch,
                    State(buttons: B), 111));
            OrderedEgressClaim<Xbox360EgressState> fresh = Claim(scheduler, 111);
            Assert.AreEqual(B, fresh.State.Buttons);
            Commit(scheduler, fresh, 111);
        }

        [TestMethod]
        public void AgedRetryPurgesLaterEdgesInsteadOfSelectiveDropping()
        {
            Xbox360EgressScheduler scheduler = NewScheduler(
                maximumOrderedAge: 10);
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(buttons: A), 100);
            OrderedEgressClaim<Xbox360EgressState> press = Claim(scheduler, 100);
            Assert.IsTrue(scheduler.TryAdmit(press, 100));
            Assert.IsTrue(scheduler.Complete(press,
                OrderedEgressCompletion.Defer));
            scheduler.Publish(epoch, State(), 101);

            OrderedEgressClaim<Xbox360EgressState> neutral = Claim(scheduler, 110);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutral.Kind);
            Assert.IsTrue(neutral.State.IsNeutral);
            OrderedEgressSchedulerSnapshot fault = scheduler.Snapshot();
            Assert.AreEqual(1L, fault.OrderedAgeFaults);
            Assert.AreEqual(10L, fault.LastOldestAgeAtFault);
            Assert.IsFalse(fault.RetryPending);
            Assert.AreEqual(0, fault.OrderedDepth,
                "A later release survived an aged-retry fault.");
            Commit(scheduler, neutral, 110);
        }

        [TestMethod]
        public void AgedInflightDependencyCannotReachWriterAdmission()
        {
            Xbox360EgressScheduler scheduler = NewScheduler(
                maximumOrderedAge: 10);
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(buttons: A), 100);
            Commit(scheduler, Claim(scheduler, 100), 100);
            scheduler.Publish(epoch, State(buttons: A, lx: 32767), 101);
            OrderedEgressClaim<Xbox360EgressState> peak = Claim(scheduler, 101);
            scheduler.Publish(epoch, State(), 102);

            Assert.IsFalse(scheduler.TryAdmit(peak, 111));
            Assert.IsFalse(scheduler.Complete(peak,
                OrderedEgressCompletion.Commit));
            OrderedEgressSchedulerSnapshot fault = scheduler.Snapshot();
            Assert.AreEqual(1L, fault.OrderedAgeFaults);
            Assert.AreEqual(10L, fault.LastOldestAgeAtFault);
            Assert.IsTrue(fault.MandatoryNeutralPending);
            Assert.IsFalse(fault.ClaimPending);
            OrderedEgressClaim<Xbox360EgressState> neutral = Claim(scheduler, 111);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutral.Kind);
            Commit(scheduler, neutral, 111);
        }

        [TestMethod]
        public void InvalidProducerAndClaimLocalTimestampsFailClosed()
        {
            Assert.AreEqual(OrderedEgressAgePolicy.CompatibilityNoAgeLimit,
                new Xbox360EgressScheduler(0).OrderedAgePolicy);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                new Xbox360EgressScheduler(-1));

            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedInvalidTimestamp,
                scheduler.Publish(epoch, State(buttons: A), -1));
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, State(buttons: A), 10));
            Assert.AreEqual(
                OrderedEgressPublishDisposition.RejectedInvalidTimestamp,
                scheduler.Publish(epoch, State(buttons: B), 9));
            Assert.IsTrue(scheduler.TryClaim(9,
                out OrderedEgressClaim<Xbox360EgressState> neutral));
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutral.Kind,
                "A future-dated selected edge escaped instead of faulting the whole history.");
            Assert.IsFalse(scheduler.TryAdmit(neutral, 8));
            Assert.IsTrue(scheduler.TryAdmit(neutral, 9));
            Assert.IsTrue(scheduler.Complete(neutral,
                OrderedEgressCompletion.Commit));
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedResynchronization,
                scheduler.Resynchronize(scheduler.CurrentProducerEpoch,
                    State(lx: 1), 10),
                "A future timestamp from the retired producer epoch poisoned immediate resynchronization.");
            ulong generation = scheduler.PresentationGeneration;
            Assert.IsTrue(scheduler.RetirePresentationGeneration(
                generation, 9),
                "A matching lifecycle retirement wedged behind a newer producer timestamp.");
            Assert.IsFalse(scheduler.RetirePresentationGeneration(
                generation, 10));
            Assert.AreEqual(5L,
                scheduler.Snapshot().InvalidTimestampCount);
        }

        [TestMethod]
        public void NewerProducerCannotWedgeClaimAdmissionOrRetirement()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(epoch, State(buttons: A), 10));
            OrderedEgressClaim<Xbox360EgressState> press = Claim(scheduler, 11);
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedContinuous,
                scheduler.Publish(epoch, State(buttons: A, lx: 7), 14));

            Assert.IsTrue(scheduler.TryAdmit(press, 12),
                "A newer producer callback poisoned this claim's older admission boundary.");
            Assert.IsTrue(scheduler.Complete(press,
                OrderedEgressCompletion.Commit));
            OrderedEgressClaim<Xbox360EgressState> latest = Claim(scheduler, 15);
            Assert.AreEqual(7, latest.State.LeftStickX);
            Assert.IsTrue(scheduler.TryAdmit(latest, 15));
            Assert.IsTrue(scheduler.Complete(latest,
                OrderedEgressCompletion.Commit));

            ulong generation = scheduler.PresentationGeneration;
            Assert.IsTrue(scheduler.RetirePresentationGeneration(
                generation, 13),
                "A matching retirement was rejected after a newer producer won the lock.");
            Assert.AreNotEqual(generation,
                scheduler.PresentationGeneration);
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedContinuous,
                scheduler.Publish(scheduler.CurrentProducerEpoch,
                    State(lx: 8), 13),
                "The old producer epoch poisoned the successor timestamp domain.");
            Assert.AreEqual(1L,
                scheduler.Snapshot().InvalidTimestampCount);
        }

        [TestMethod]
        public void FutureDatedContinuousSelectionFaultsToNeutral()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedContinuous,
                scheduler.Publish(epoch, State(lx: 7), 20));

            OrderedEgressClaim<Xbox360EgressState> neutral = Claim(scheduler, 19);
            Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
                neutral.Kind);
            Assert.IsTrue(neutral.State.IsNeutral);
            OrderedEgressSchedulerSnapshot snapshot = scheduler.Snapshot();
            Assert.AreEqual(1L, snapshot.InvalidTimestampCount);
            Assert.AreEqual(1L, snapshot.OrderedAgeFaults);
            Assert.IsFalse(snapshot.ContinuousPending);
            Commit(scheduler, neutral, 19);
            Assert.AreEqual(
                OrderedEgressPublishDisposition.AcceptedResynchronization,
                scheduler.Resynchronize(scheduler.CurrentProducerEpoch,
                    State(lx: 8), 20),
                "A future continuous sample poisoned the recovery epoch.");
        }

        [TestMethod]
        public void PausedWriterCannotAdmitClaimAfterRetirementReturns()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(buttons: A), 10);
            OrderedEgressClaim<Xbox360EgressState> oldPress = Claim(scheduler, 10);
            int writes = 0;
            using ManualResetEventSlim beforeAdmission = new(false);
            using ManualResetEventSlim resume = new(false);
            Task<bool> writer = Task.Run(() =>
            {
                beforeAdmission.Set();
                resume.Wait();
                if (!scheduler.TryAdmit(oldPress, 12))
                {
                    return false;
                }
                Interlocked.Increment(ref writes);
                scheduler.Complete(oldPress,
                    OrderedEgressCompletion.Commit);
                return true;
            });

            Assert.IsTrue(beforeAdmission.Wait(1_000));
            Assert.IsTrue(scheduler.RetirePresentationGeneration(
                oldPress.PresentationGeneration, 11));
            resume.Set();
            Assert.IsTrue(writer.Wait(1_000));
            Assert.IsFalse(writer.Result);
            Assert.AreEqual(0, Volatile.Read(ref writes));

            OrderedEgressProducerEpoch successorEpoch =
                scheduler.CurrentProducerEpoch;
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedContinuous,
                scheduler.Publish(successorEpoch, State(lx: 1), 12));
            OrderedEgressClaim<Xbox360EgressState> successor = Claim(scheduler, 12);
            Assert.AreEqual(1, successor.State.LeftStickX);
            Commit(scheduler, successor, 12);
        }

        [TestMethod]
        public void PausedBeforeFinalAdmissionCannotWriteAfterDisconnectFence()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            var gate = new OrderedEgressWriterAdmissionGate();
            const long writerGeneration = 17;
            const long admissionGeneration = 23;
            gate.Activate(writerGeneration,
                unchecked((long)scheduler.PresentationGeneration),
                admissionGeneration);
            scheduler.Publish(scheduler.CurrentProducerEpoch,
                State(buttons: A), 10);
            Assert.IsTrue(gate.TryClaim(writerGeneration, scheduler, 10,
                out OrderedEgressClaim<Xbox360EgressState> claim,
                out OrderedEgressWriterAdmissionLease lease,
                includeIdle: false));

            int writes = 0;
            using ManualResetEventSlim beforeFinalAdmission = new(false);
            using ManualResetEventSlim resume = new(false);
            Task<bool> writer = Task.Run(() =>
            {
                beforeFinalAdmission.Set();
                resume.Wait();
                if (!gate.TryAdmit(lease, scheduler, claim, 11))
                {
                    scheduler.Complete(claim,
                        OrderedEgressCompletion.Defer);
                    return false;
                }
                Interlocked.Increment(ref writes);
                scheduler.Complete(claim,
                    OrderedEgressCompletion.Commit);
                return true;
            });

            Assert.IsTrue(beforeFinalAdmission.Wait(1_000));
            gate.Invalidate();
            Assert.AreEqual(1UL, scheduler.PresentationGeneration,
                "The regression requires Disconnect's outer fence to win " +
                "before deferred scheduler retirement.");
            resume.Set();
            Assert.IsTrue(writer.Wait(1_000));
            Assert.IsFalse(writer.Result);
            Assert.AreEqual(0, Volatile.Read(ref writes));
            Assert.IsTrue(scheduler.Snapshot().RetryPending,
                "Rejected final admission must retain immutable retry state.");
        }

        [TestMethod]
        public void PreAdmissionIdentityIsExactAndSingleUse()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(buttons: A), 10);
            OrderedEgressClaim<Xbox360EgressState> claim = Claim(scheduler, 10);
            OrderedEgressClaim<Xbox360EgressState> wrong = new(claim.State, claim.Token + 1,
                claim.PresentationGeneration, claim.ProducerEpoch,
                claim.Ordinal, claim.ReceivedTimestamp, claim.IsOrdered,
                claim.Kind);
            Assert.IsFalse(scheduler.TryAdmit(wrong, 10));
            Assert.IsTrue(scheduler.TryAdmit(claim, 10));
            Assert.IsFalse(scheduler.TryAdmit(claim, 10));
            Assert.IsTrue(scheduler.Complete(claim,
                OrderedEgressCompletion.Commit));
            Assert.IsFalse(scheduler.TryAdmit(claim, 10));
        }

        [TestMethod]
        public void UnadmittedDeferRecoversOrderedDependencyBeforeRelease()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(buttons: A), 10);
            OrderedEgressClaim<Xbox360EgressState> press = Claim(scheduler, 10);
            scheduler.Publish(epoch, State(), 11);

            Assert.IsTrue(scheduler.Complete(press,
                OrderedEgressCompletion.Defer));
            OrderedEgressClaim<Xbox360EgressState> retry = Claim(scheduler, 11);
            Assert.AreEqual(OrderedEgressClaimKind.Retry, retry.Kind);
            Assert.AreEqual(press.State, retry.State);
            Assert.AreEqual(press.Ordinal, retry.Ordinal);
            Commit(scheduler, retry, 11);
            OrderedEgressClaim<Xbox360EgressState> release = Claim(scheduler, 11);
            Assert.IsTrue(release.State.IsNeutral);
            Commit(scheduler, release, 11);
        }

        [TestMethod]
        public void UnadmittedContinuousDeferRestoresOrCoalescesTruthfully()
        {
            Xbox360EgressScheduler restored = NewScheduler();
            OrderedEgressProducerEpoch restoredEpoch =
                restored.CurrentProducerEpoch;
            restored.Publish(restoredEpoch, State(lx: 1), 10);
            OrderedEgressClaim<Xbox360EgressState> first = Claim(restored, 10);
            Assert.AreEqual(OrderedEgressClaimKind.Continuous, first.Kind);
            Assert.IsTrue(restored.Complete(first,
                OrderedEgressCompletion.Defer));
            OrderedEgressClaim<Xbox360EgressState> restoredClaim = Claim(restored, 10);
            Assert.AreEqual(first.State, restoredClaim.State);
            Commit(restored, restoredClaim, 10);

            Xbox360EgressScheduler replaced = NewScheduler();
            OrderedEgressProducerEpoch replacedEpoch =
                replaced.CurrentProducerEpoch;
            replaced.Publish(replacedEpoch, State(lx: 1), 20);
            OrderedEgressClaim<Xbox360EgressState> older = Claim(replaced, 20);
            replaced.Publish(replacedEpoch, State(lx: 2), 21);
            Assert.IsTrue(replaced.Complete(older,
                OrderedEgressCompletion.Defer));
            OrderedEgressClaim<Xbox360EgressState> newest = Claim(replaced, 21);
            Assert.AreEqual(2, newest.State.LeftStickX);
            Commit(replaced, newest, 21);
        }

        [TestMethod]
        public void ConsumerBoundaryDoesNotRejectDelayedProducerTimestamp()
        {
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
            scheduler.Publish(epoch, State(lx: 1), 100);
            OrderedEgressClaim<Xbox360EgressState> first = Claim(scheduler, 200);

            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedContinuous,
                scheduler.Publish(epoch, State(lx: 2), 150));
            Assert.IsTrue(scheduler.Complete(first,
                OrderedEgressCompletion.Defer));
            OrderedEgressClaim<Xbox360EgressState> delayed = Claim(scheduler, 201);
            Assert.AreEqual(2, delayed.State.LeftStickX);
            Commit(scheduler, delayed, 201);
            Assert.AreEqual(0L,
                scheduler.Snapshot().InvalidTimestampCount);
        }

        [TestMethod]
        public void CanonicalBuildAndSchedulerHotPathsDoNotAllocate()
        {
            Xbox360EgressState state = State(lx: 123, ly: -456,
                rightTrigger: 99);
            Span<byte> wire = stackalloc byte[Xbox360EgressState.WireSize];
            Xbox360EgressScheduler scheduler = NewScheduler();
            OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;

            for (int index = 0; index < 128; index++)
            {
                state.BuildInto(wire);
                Xbox360EgressState sample = State(lx: (short)index);
                scheduler.Publish(epoch, sample, index + 1);
                OrderedEgressClaim<Xbox360EgressState> claim = Claim(scheduler, index + 1);
                Commit(scheduler, claim, index + 1);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                state.BuildInto(wire);
                Xbox360EgressState sample = State(
                    lx: (short)(index & 0x7FFF));
                OrderedEgressPublishDisposition disposition =
                    scheduler.Publish(epoch, sample, index + 1000);
                if (disposition !=
                    OrderedEgressPublishDisposition.AcceptedContinuous ||
                    !scheduler.TryClaim(index + 1000,
                        out OrderedEgressClaim<Xbox360EgressState> claim) ||
                    !scheduler.TryAdmit(claim, index + 1000) ||
                    !scheduler.Complete(claim,
                        OrderedEgressCompletion.Commit))
                {
                    Assert.Fail("Unexpected hot-path state.");
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated);
        }

        private static Xbox360EgressState State(uint buttons = 0,
            byte leftTrigger = 0, byte rightTrigger = 0, short lx = 0,
            short ly = 0, short rx = 0, short ry = 0) =>
            new(buttons, leftTrigger, rightTrigger, lx, ly, rx, ry);

        private static Xbox360EgressScheduler NewScheduler(
            int orderedCapacity = Xbox360EgressScheduler.
                DefaultOrderedCapacity,
            long maximumOrderedAge = TestMaximumOrderedAge) =>
            new(maximumOrderedAge, orderedCapacity);

        private static OrderedEgressClaim<Xbox360EgressState> Claim(
            Xbox360EgressScheduler scheduler, long selectedTimestamp)
        {
            Assert.IsTrue(scheduler.TryClaim(selectedTimestamp,
                out OrderedEgressClaim<Xbox360EgressState> claim));
            Assert.IsTrue(claim.IsValid);
            return claim;
        }

        private static void Commit(Xbox360EgressScheduler scheduler,
            in OrderedEgressClaim<Xbox360EgressState> claim, long admittedTimestamp)
        {
            Assert.IsTrue(scheduler.TryAdmit(claim, admittedTimestamp));
            Assert.IsTrue(scheduler.Complete(claim,
                OrderedEgressCompletion.Commit));
        }

        private static byte[] Build(in OrderedEgressClaim<Xbox360EgressState> claim)
        {
            byte[] wire = new byte[Xbox360EgressState.WireSize];
            claim.BuildInto(wire);
            return wire;
        }

        private static void AssertImmutableValueOwned<T>() where T : struct
        {
            Type type = typeof(T);
            Assert.IsTrue(type.IsValueType);
            Assert.IsTrue(type.IsDefined(typeof(IsReadOnlyAttribute),
                inherit: false));
            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic))
            {
                Assert.IsTrue(field.IsInitOnly,
                    $"{type.Name}.{field.Name} is mutable.");
                Assert.IsFalse(field.FieldType.IsArray,
                    $"{type.Name}.{field.Name} owns a mutable array.");
                Assert.IsFalse(field.FieldType.IsClass,
                    $"{type.Name}.{field.Name} owns a mutable reference.");
            }
        }
    }
}
