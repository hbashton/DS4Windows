using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseMicrophonePresentationClockTests
    {
        private const long QpcFrequency = 10_000_000;
        private const long PeriodTicks = QpcFrequency / 100;
        private const long PhaseTicks = 81_000;

        [TestMethod]
        public void LocksOnFourCompleteBlocksAndTargetsEightPointOneMsPhase()
        {
            const long origin = 50_000_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);

            for (long sequence = 0; sequence <= 64; sequence++)
            {
                long positiveArrivalJitter = sequence % 5 * 300;
                clock.Observe((byte)sequence,
                    origin + sequence * PeriodTicks +
                    positiveArrivalJitter);
            }

            Assert.IsTrue(clock.IsLocked);
            var snapshot = clock.GetSnapshot();
            Assert.AreEqual(64L, snapshot.LatestLogicalSequence);
            Assert.AreEqual(4, snapshot.ReliableBlockCount);
            Assert.AreEqual(10.0, snapshot.PeriodMilliseconds, 0.000001);

            const long presentationIndex = 75;
            Assert.IsTrue(clock.TryGetPresentationDeadline(
                presentationIndex,
                out long deadline));
            // Presentation 75 maps to logical microphone coordinate 81:
            // 1 + 75 * 16/15.
            long expected = origin + 81 * PeriodTicks + PhaseTicks;
            Assert.AreEqual(expected, deadline, 500,
                "The model must target the per-block lower envelope, not " +
                "mean Bluetooth arrival jitter.");
        }

        [TestMethod]
        public void BatchingAndSequenceWrapPreserveTheLogicalClock()
        {
            const long firstSequence = 240;
            const long origin = 100_000_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);

            // Four reports arrive together at the end of each 40 ms batch.
            // min(arrival - 10 ms * sequence) is nevertheless the correct
            // logical epoch for every complete block.
            for (long sequence = firstSequence;
                sequence <= firstSequence + 80; sequence++)
            {
                long batchEnd = ((sequence - firstSequence) / 4) * 4 + 3;
                long arrival = origin + batchEnd * PeriodTicks;
                clock.Observe((byte)sequence, arrival);
            }

            Assert.IsTrue(clock.IsLocked);
            var snapshot = clock.GetSnapshot();
            Assert.AreEqual(firstSequence + 80,
                snapshot.LatestLogicalSequence);

            const long requested = 225;
            Assert.IsTrue(clock.TryGetPresentationDeadline(requested,
                out long deadline));
            // Presentation 225 maps to microphone coordinate 241, one
            // logical tick after this capture's sequence-240 origin.
            long expected = origin + PeriodTicks + PhaseTicks;
            Assert.AreEqual(expected, deadline, 1_000);
        }

        [TestMethod]
        public void MissingReportsRemainMonotonicOnRationalLattice()
        {
            const long firstSequence = 248;
            const long origin = 200_000_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);
            var omitted = new HashSet<long>
            {
                253, 267, 268, 285, 301, 316
            };

            for (long sequence = firstSequence;
                sequence <= firstSequence + 88; sequence++)
            {
                if (!omitted.Contains(sequence))
                {
                    clock.Observe((byte)sequence, origin +
                        (sequence - firstSequence) * PeriodTicks);
                }
            }

            var snapshot = clock.GetSnapshot();
            Assert.IsTrue(snapshot.IsLocked);
            Assert.AreEqual(firstSequence + 88,
                snapshot.LatestLogicalSequence);
            Assert.IsTrue(
                DualSenseMicrophonePresentationClock.
                    IsScheduledPresentationSlot(320));
            Assert.IsTrue(
                DualSenseMicrophonePresentationClock.
                    IsScheduledPresentationSlot(321));

            long now = origin + 89 * PeriodTicks;
            Assert.IsTrue(clock.TryGetNextSlot(now,
                out long slot, out long firstDeadline));
            Assert.AreEqual(315L, slot,
                "Presentation 315 maps exactly to microphone coordinate 337.");
            clock.Advance(slot);
            Assert.IsTrue(clock.TryGetNextSlot(now,
                out long nextSlot, out long nextDeadline));
            Assert.IsTrue(nextSlot > slot);
            Assert.IsTrue(nextDeadline > firstDeadline);
            Assert.AreEqual(106_667L, nextDeadline - firstDeadline, 1L);
        }

        [TestMethod]
        public void RationalLatticeHasNoTwentyMillisecondOmission()
        {
            const long origin = 250_000_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);
            FeedClean(clock, 0, 96, origin);
            Assert.IsTrue(clock.IsLocked);

            long previous = 0;
            long minimumInterval = long.MaxValue;
            long maximumInterval = 0;
            for (long index = 60; index <= 75; index++)
            {
                Assert.IsTrue(clock.TryGetPresentationDeadline(index,
                    out long deadline));
                if (previous != 0)
                {
                    long interval = deadline - previous;
                    minimumInterval = Math.Min(minimumInterval, interval);
                    maximumInterval = Math.Max(maximumInterval, interval);
                }

                previous = deadline;
            }

            Assert.AreEqual(106_666L, minimumInterval);
            Assert.AreEqual(106_667L, maximumInterval);
            Assert.IsTrue(clock.TryGetPresentationDeadline(60,
                out long first));
            Assert.IsTrue(clock.TryGetPresentationDeadline(75,
                out long last));
            Assert.AreEqual(16 * PeriodTicks, last - first,
                "Fifteen speaker intervals must exactly span one 160 ms " +
                "microphone superframe without a 20 ms hole.");
        }

        [TestMethod]
        public void AnySpentDeadlineResumesInPhaseWithoutCatchUpBurst()
        {
            const long origin = 275_000_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);
            FeedClean(clock, 0, 96, origin);

            long now = origin + 97 * PeriodTicks;
            Assert.IsTrue(clock.TryGetNextSlot(now,
                out long firstIndex, out long firstDeadline));

            long lateNow = firstDeadline + 1;
            Assert.IsTrue(clock.TryGetNextSlot(lateNow,
                out long resumedIndex, out long resumedDeadline));
            Assert.AreEqual(firstIndex + 1, resumedIndex,
                "Even a just-spent deadline must not be returned for an " +
                "immediate catch-up write.");
            Assert.IsTrue(resumedDeadline >= lateNow,
                "A missed interval must advance to a future lattice point " +
                "instead of returning several immediately-due slots.");
            Assert.IsTrue(resumedDeadline - lateNow < 106_668L);
        }

        [TestMethod]
        public void SilentMicrophoneClockOutageFailsOpenAndResetsModel()
        {
            const long origin = 287_500_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);
            FeedClean(clock, 0, 64, origin);
            Assert.IsTrue(clock.IsLocked);
            int generation = clock.Generation;

            long lastObservation = origin + 64 * PeriodTicks;
            Assert.IsFalse(clock.TryGetNextSlot(
                lastObservation + 3 * QpcFrequency,
                out _, out _));
            Assert.IsFalse(clock.IsLocked);
            Assert.AreEqual(generation + 1, clock.Generation);
        }

        [TestMethod]
        public void LateWholeBlockDoesNotPullTheLowerEnvelopeLate()
        {
            const long origin = 300_000_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);

            for (long sequence = 0; sequence <= 192; sequence++)
            {
                long block = sequence / 16;
                long lateBatch = block == 5 ? 8 * PeriodTicks / 10 : 0;
                clock.Observe((byte)sequence,
                    origin + sequence * PeriodTicks + lateBatch);
            }

            const long presentationIndex = 195;
            Assert.IsTrue(clock.TryGetPresentationDeadline(
                presentationIndex,
                out long deadline));
            // Presentation 195 maps to microphone coordinate 209.
            long expected = origin + 209 * PeriodTicks + PhaseTicks;
            Assert.AreEqual(expected, deadline, 3_000,
                "One entirely delayed input block must be rejected rather " +
                "than steering presentation toward Bluetooth batching.");
            Assert.IsTrue(clock.GetSnapshot().AcceptedAnchorCount <
                clock.GetSnapshot().ReliableBlockCount);
        }

        [TestMethod]
        public void LongWindowTracksSmallClockErrorWithoutAbruptPhaseSteps()
        {
            const long origin = 400_000_000;
            const double truePeriodTicks = PeriodTicks * 1.000200;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);
            long previousDeadline = 0;
            long maximumDeadlineStepError = 0;

            for (long sequence = 0; sequence <= 640; sequence++)
            {
                long arrival = origin + (long)Math.Round(
                    sequence * truePeriodTicks);
                clock.Observe((byte)sequence, arrival);

                if (clock.IsLocked && sequence % 16 == 0 &&
                    clock.TryGetPresentationDeadline(
                        sequence / 16 * 15,
                        out long deadline))
                {
                    if (previousDeadline != 0)
                    {
                        long expectedBlockTicks = (long)Math.Round(
                            16 * truePeriodTicks);
                        maximumDeadlineStepError = Math.Max(
                            maximumDeadlineStepError,
                            Math.Abs((deadline - previousDeadline) -
                                expectedBlockTicks));
                    }

                    previousDeadline = deadline;
                }
            }

            var snapshot = clock.GetSnapshot();
            Assert.IsTrue(snapshot.IsLocked);
            Assert.AreEqual(10.002, snapshot.PeriodMilliseconds, 0.00003);
            Assert.IsTrue(maximumDeadlineStepError <= 300,
                "Each completed 160 ms block may slew presentation phase by " +
                "at most about 20 microseconds.");
        }

        [TestMethod]
        public void ResetAndAmbiguousBackstepRequireFreshReliableBlocks()
        {
            const long origin = 500_000_000;
            var clock = new DualSenseMicrophonePresentationClock(
                QpcFrequency);
            FeedClean(clock, 0, 64, origin);
            Assert.IsTrue(clock.IsLocked);

            int generation = clock.Generation;
            clock.Reset();
            Assert.IsFalse(clock.IsLocked);
            Assert.AreEqual(generation + 1, clock.Generation);
            FeedClean(clock, 80, 32, origin + 800 * PeriodTicks);
            Assert.IsFalse(clock.IsLocked);

            // A raw backward jump is an ambiguous +255 modulo delta, so it
            // starts over instead of pretending that 2.55 seconds vanished.
            clock.Observe(10, origin + 2_000 * PeriodTicks);
            Assert.IsFalse(clock.IsLocked);
            Assert.IsTrue(clock.Generation >= generation + 2);
        }

        private static void FeedClean(
            DualSenseMicrophonePresentationClock clock,
            long firstSequence, int count, long origin)
        {
            for (long offset = 0; offset <= count; offset++)
            {
                long sequence = firstSequence + offset;
                clock.Observe((byte)sequence,
                    origin + offset * PeriodTicks);
            }
        }
    }
}
