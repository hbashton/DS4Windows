using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;

namespace DS4Windows.Tests
{
    [TestClass]
    public class ViiperFeedbackDispatchBufferTests
    {
        [TestMethod]
        public void SpeakerQueuePreservesPayloadOrderAndGeneration()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(3, 16, 12);
            byte[] first = { 1, 2, 3, 4 };
            byte[] second = { 8, 7, 6, 5, 4, 3 };
            byte[] destination = new byte[16];

            Assert.IsTrue(buffer.TryEnqueueSpeaker(first, first.Length, 41));
            Assert.IsTrue(buffer.TryEnqueueSpeaker(second, second.Length, 42));

            Assert.IsTrue(buffer.TryDequeueSpeaker(destination,
                out int firstLength, out long firstGeneration));
            Assert.AreEqual(first.Length, firstLength);
            Assert.AreEqual(41L, firstGeneration);
            CollectionAssert.AreEqual(first,
                destination[..firstLength]);

            Array.Clear(destination);
            Assert.IsTrue(buffer.TryDequeueSpeaker(destination,
                out int secondLength, out long secondGeneration));
            Assert.AreEqual(second.Length, secondLength);
            Assert.AreEqual(42L, secondGeneration);
            CollectionAssert.AreEqual(second,
                destination[..secondLength]);
            Assert.IsFalse(buffer.TryDequeueSpeaker(destination,
                out _, out _));
            Assert.AreEqual(2L, buffer.SpeakerEnqueued);
            Assert.AreEqual(2L, buffer.SpeakerDequeued);
            Assert.AreEqual(2L, buffer.SpeakerHighWater);
        }

        [TestMethod]
        public void SpeakerQueueKeepsAtomicKindAndTargetBesidePayload()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 32, 12);
            byte[] atomicGeneration = { 0xda, 0x01, 0x36, 0x92, 1, 2, 3, 4 };
            byte[] destination = new byte[32];

            Assert.IsTrue(buffer.TryEnqueueSpeaker(atomicGeneration,
                atomicGeneration.Length, generation: 77, kind: 1,
                deviceIndex: 3));
            Assert.IsTrue(buffer.TryDequeueSpeaker(destination,
                out int length, out long generation, out byte kind,
                out int deviceIndex));

            CollectionAssert.AreEqual(atomicGeneration,
                destination[..length]);
            Assert.AreEqual(77L, generation);
            Assert.AreEqual((byte)1, kind);
            Assert.AreEqual(3, deviceIndex);
        }

        [TestMethod]
        public void FullSpeakerQueueDropsOldestAndKeepsLiveAudioCurrent()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 8);
            byte[] first = { 1, 1 };
            byte[] second = { 2, 2 };
            byte[] newest = { 9, 9 };
            byte[] destination = new byte[8];

            Assert.IsTrue(buffer.TryEnqueueSpeaker(first, first.Length, 1));
            Assert.IsTrue(buffer.TryEnqueueSpeaker(second, second.Length, 2));
            Assert.IsTrue(buffer.TryEnqueueSpeaker(newest,
                newest.Length, 3));
            Assert.AreEqual(1L, buffer.SpeakerDropped);

            Assert.IsTrue(buffer.TryDequeueSpeaker(destination,
                out int length, out long generation));
            Assert.AreEqual(2L, generation);
            CollectionAssert.AreEqual(second, destination[..length]);
            Assert.IsTrue(buffer.TryDequeueSpeaker(destination,
                out length, out generation));
            Assert.AreEqual(3L, generation);
            CollectionAssert.AreEqual(newest, destination[..length]);
        }

        [TestMethod]
        public void OrderedControlFifoPreservesTimeBearingHaptics()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 16, 3);
            byte[] first = { 1, 10 };
            byte[] second = { 2, 20 };
            byte[] third = { 3, 30 };
            byte[] destination = new byte[16];

            Assert.IsTrue(buffer.TryEnqueueOrderedControl(first,
                first.Length, 21, 0));
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(second,
                second.Length, 22, 1));
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(third,
                third.Length, 23, 2));

            byte[][] expected = { first, second, third };
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.IsTrue(buffer.TryDequeueOrderedControl(destination,
                    out int length, out long generation,
                    out int deviceIndex));
                CollectionAssert.AreEqual(expected[index],
                    destination[..length]);
                Assert.AreEqual(21L + index, generation);
                Assert.AreEqual(index, deviceIndex);
            }

            Assert.AreEqual(3L, buffer.OrderedControlEnqueued);
            Assert.AreEqual(3L, buffer.OrderedControlDequeued);
            Assert.AreEqual(0L, buffer.OrderedControlDropped);
            Assert.AreEqual(3L, buffer.OrderedControlHighWater);
        }

        [TestMethod]
        public void OrderedControlOverflowKeepsNewestBoundedWindow()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 16, 2);
            byte[] destination = new byte[16];
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 1 }, 1, 1, 0));
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 2 }, 1, 2, 0));
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 3 }, 1, 3, 0));

            Assert.AreEqual(1L, buffer.OrderedControlDropped);
            Assert.IsTrue(buffer.TryDequeueOrderedControl(destination,
                out _, out long generation, out _));
            Assert.AreEqual(2L, generation);
            Assert.IsTrue(buffer.TryDequeueOrderedControl(destination,
                out _, out generation, out _));
            Assert.AreEqual(3L, generation);
        }

        [TestMethod]
        public void ProductionFeedbackWindowsStayInsideLiveLatencyBudgets()
        {
            const double dualSenseFeedbackPacketsPerSecond = 150.0;
            const double speakerPacketMilliseconds = 10.0;

            double hapticsWindowMilliseconds =
                ViiperOutDevice.FeedbackOrderedControlQueueCapacity * 1000.0 /
                dualSenseFeedbackPacketsPerSecond;
            double speakerWindowMilliseconds =
                ViiperOutDevice.FeedbackSpeakerQueueCapacity *
                speakerPacketMilliseconds;

            Assert.IsTrue(hapticsWindowMilliseconds <= 30.0,
                $"The native-haptics FIFO can retain {hapticsWindowMilliseconds:F1} ms of stale effects.");
            Assert.IsTrue(speakerWindowMilliseconds <= 80.0,
                $"The direct-speaker FIFO can retain {speakerWindowMilliseconds:F1} ms of stale audio.");
            Assert.IsTrue(
                ViiperOutDevice.FeedbackOrderedControlMaximumAgeMilliseconds <=
                    20,
                "A paused haptics frame can outlive the presentation budget.");
            Assert.IsTrue(
                ViiperOutDevice.FeedbackSpeakerMaximumAgeMilliseconds <= 80,
                "A paused direct-speaker frame can outlive the handoff budget.");
        }

        [TestMethod]
        public void ExpiredLiveFeedbackIsNeverReplayedAfterAStall()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 16, 16, 2,
                speakerMaximumAgeMilliseconds: 1,
                orderedControlMaximumAgeMilliseconds: 1);
            byte[] destination = new byte[16];

            Assert.IsTrue(buffer.TryEnqueueSpeaker(
                new byte[] { 1, 2 }, 2, 1));
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 3, 4 }, 2, 1, 0));
            Thread.Sleep(15);

            Assert.IsFalse(buffer.TryDequeueSpeaker(destination,
                out _, out _));
            Assert.IsFalse(buffer.TryDequeueOrderedControl(destination,
                out _, out _, out _));
            Assert.AreEqual(1L, buffer.SpeakerExpired);
            Assert.AreEqual(1L, buffer.OrderedControlExpired);
            Assert.IsTrue(buffer.SpeakerMaximumQueueAgeMilliseconds >= 1.0);
            Assert.IsTrue(
                buffer.OrderedControlMaximumQueueAgeMilliseconds >= 1.0);
        }

        [TestMethod]
        public void ClearPendingCreatesHardBoundaryForEveryFeedbackLane()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 16, 2);
            byte[] destination = new byte[16];

            Assert.IsTrue(buffer.TryEnqueueSpeaker(
                new byte[] { 1, 2 }, 2, 10));
            Assert.IsTrue(buffer.QueueControl(
                new byte[] { 3, 4 }, 2, 11, 1));
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 5, 6 }, 2, 12, 2));

            buffer.ClearPending();

            Assert.IsFalse(buffer.TryDequeueSpeaker(destination,
                out _, out _));
            Assert.IsFalse(buffer.TryTakeControl(destination,
                out _, out _, out _));
            Assert.IsFalse(buffer.TryDequeueOrderedControl(destination,
                out _, out _, out _));

            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 9 }, 1, 20, 3));
            Assert.IsTrue(buffer.TryDequeueOrderedControl(destination,
                out int length, out long generation, out int deviceIndex));
            Assert.AreEqual(1, length);
            Assert.AreEqual(9, destination[0]);
            Assert.AreEqual(20L, generation);
            Assert.AreEqual(3, deviceIndex);
        }

        [TestMethod]
        public void ResetClearsFullOrderedControlQueueBeforeCurrentGeneration()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 16, 2);
            byte[] destination = new byte[16];

            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 1 }, 1, 1, 0));
            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 2 }, 1, 1, 0));

            buffer.Reset();

            Assert.IsTrue(buffer.TryEnqueueOrderedControl(
                new byte[] { 3 }, 1, 2, 1));
            Assert.IsTrue(buffer.TryDequeueOrderedControl(destination,
                out int length, out long generation, out int deviceIndex));
            Assert.AreEqual(1, length);
            Assert.AreEqual(3, destination[0]);
            Assert.AreEqual(2L, generation);
            Assert.AreEqual(1, deviceIndex);
            Assert.IsFalse(buffer.TryDequeueOrderedControl(destination,
                out _, out _, out _));
            Assert.AreEqual(1L, buffer.OrderedControlEnqueued);
            Assert.AreEqual(1L, buffer.OrderedControlDequeued);
            Assert.AreEqual(0L, buffer.OrderedControlDropped);
        }

        [TestMethod]
        public void ControlMailboxCoalescesToNewestStateAndTarget()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 8, 16);
            byte[] oldState = { 1, 2, 3 };
            byte[] newestState = { 9, 8, 7, 6 };
            byte[] destination = new byte[16];

            Assert.IsTrue(buffer.QueueControl(oldState, oldState.Length,
                11, 0));
            Assert.IsTrue(buffer.QueueControl(newestState,
                newestState.Length, 12, 3));

            Assert.IsTrue(buffer.TryTakeControl(destination, out int length,
                out long generation, out int deviceIndex));
            Assert.AreEqual(12L, generation);
            Assert.AreEqual(3, deviceIndex);
            CollectionAssert.AreEqual(newestState, destination[..length]);
            Assert.IsFalse(buffer.TryTakeControl(destination, out _, out _,
                out _));
            Assert.AreEqual(2L, buffer.ControlEnqueued);
            Assert.AreEqual(1L, buffer.ControlDequeued);
            Assert.AreEqual(1L, buffer.ControlCoalesced);
        }

        [TestMethod]
        public void HotSpeakerHandoffDoesNotAllocateAfterWarmup()
        {
            var buffer = new ViiperFeedbackDispatchBuffer(2, 32, 16);
            byte[] source = new byte[32];
            byte[] destination = new byte[32];

            Assert.IsTrue(buffer.TryEnqueueSpeaker(source, source.Length, 1));
            Assert.IsTrue(buffer.TryDequeueSpeaker(destination, out _, out _));

            bool valid = true;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 4096; index++)
            {
                valid &= buffer.TryEnqueueSpeaker(source,
                    source.Length, index);
                valid &= buffer.TryDequeueSpeaker(destination,
                    out int length, out long generation);
                valid &= source.Length == length && index == generation;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(valid, "The speaker FIFO changed data in flight.");
            Assert.AreEqual(0L, allocated,
                "The VIIPER feedback hot hand-off allocated memory.");
        }
    }
}
