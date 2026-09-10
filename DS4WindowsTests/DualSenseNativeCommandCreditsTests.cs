using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Runtime.CompilerServices;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseNativeCommandCreditsTests
    {
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(-1)]
        public void CapacityMustBePositive(int capacity)
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                new DualSenseNativeCommandCredits(capacity));
        }

        [TestMethod]
        public void CapacityPlusOneFailsWithoutEvictingAnyAcceptedCommand()
        {
            var credits = new DualSenseNativeCommandCredits(32);
            for (long id = 1; id <= 32; id++)
                Assert.IsTrue(credits.TryReserve(id, 7));

            Assert.AreEqual(32, credits.Count);
            Assert.IsFalse(credits.TryReserve(33, 7));
            Assert.AreEqual(32, credits.Count);
            // ACK order need not match the slot order.
            for (long id = 32; id >= 1; id--)
                Assert.IsTrue(credits.TryRelease(id, 7));
            Assert.AreEqual(0, credits.Count);
        }

        [TestMethod]
        public void InvalidIdentityCannotReserveOrReleaseCapacity()
        {
            var credits = new DualSenseNativeCommandCredits(2);
            Assert.IsFalse(credits.TryReserve(0, 1));
            Assert.IsFalse(credits.TryReserve(1, 0));
            Assert.AreEqual(0, credits.Count);
            Assert.IsTrue(credits.TryReserve(1, 1));
            Assert.IsFalse(credits.TryRelease(0, 1));
            Assert.IsFalse(credits.TryRelease(1, 0));
            Assert.IsFalse(credits.TryRelease(1, -1));
            Assert.AreEqual(1, credits.Count);
            Assert.IsTrue(credits.TryRelease(1, 1));
        }

        [TestMethod]
        public void DuplicateReservationAndWrongAcknowledgementsDoNotSpendCredit()
        {
            var credits = new DualSenseNativeCommandCredits(3);
            Assert.IsTrue(credits.TryReserve(11, 3));
            Assert.IsFalse(credits.TryReserve(11, 3));
            Assert.IsFalse(credits.TryReserve(11, 4),
                "A still-outstanding shared report ID cannot enter two generations.");
            Assert.IsFalse(credits.TryRelease(12, 3));
            Assert.IsFalse(credits.TryRelease(11, 4));
            Assert.AreEqual(1, credits.Count);
            Assert.IsTrue(credits.TryRelease(11, 3));
            Assert.IsFalse(credits.TryRelease(11, 3));
            Assert.AreEqual(0, credits.Count);
        }

        [TestMethod]
        public void LateAcknowledgementCannotReleaseAReusedSlotOrId()
        {
            var credits = new DualSenseNativeCommandCredits(1);
            Assert.IsTrue(credits.TryReserve(11, 3));
            Assert.IsTrue(credits.TryRelease(11, 3));
            Assert.IsTrue(credits.TryReserve(12, 3));
            Assert.IsFalse(credits.TryRelease(11, 3));
            Assert.AreEqual(1, credits.Count);
            Assert.IsTrue(credits.TryRelease(12, 3));

            Assert.IsTrue(credits.TryReserve(11, 4));
            Assert.IsFalse(credits.TryRelease(11, 3),
                "An old generation ACK must not match the same ID in a new generation.");
            Assert.AreEqual(1, credits.Count);
            Assert.IsTrue(credits.TryRelease(11, 4));
        }

        [TestMethod]
        public void NewGenerationDoesNotImplicitlyReleaseSentCommands()
        {
            var credits = new DualSenseNativeCommandCredits(2);
            Assert.IsTrue(credits.TryReserve(1, 1));
            Assert.IsTrue(credits.TryReserve(2, 2));
            for (int generation = 3; generation < 100; generation++)
                Assert.IsFalse(credits.TryReserve(generation, generation),
                    "Repeated reset generations cannot manufacture ACK reservoir capacity.");
            Assert.AreEqual(2, credits.Count);
            Assert.IsTrue(credits.TryRelease(1, 1));
            Assert.IsTrue(credits.TryReserve(100, 100));
            Assert.AreEqual(2, credits.Count);
            Assert.IsTrue(credits.TryRelease(2, 2));
            Assert.IsTrue(credits.TryRelease(100, 100));
        }

        [TestMethod]
        public void DefinitePreSendRollbackReleasesOnlyItsOwnReservation()
        {
            var credits = new DualSenseNativeCommandCredits(2);
            Assert.IsTrue(credits.TryReserve(1, 2));
            Assert.IsTrue(credits.TryReserve(2, 2));
            Assert.IsTrue(credits.TryRelease(2, 2));
            Assert.AreEqual(1, credits.Count);
            Assert.IsTrue(credits.TryReserve(3, 2));
            Assert.IsFalse(credits.TryRelease(2, 2));
            Assert.AreEqual(2, credits.Count);
            Assert.IsTrue(credits.TryRelease(1, 2));
            Assert.IsTrue(credits.TryRelease(3, 2));
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(int.MaxValue)]
        [DataRow(int.MinValue)]
        [DataRow(-1)]
        public void NonzeroSignedReportIdsAndGenerationsRemainExact(int generation)
        {
            var credits = new DualSenseNativeCommandCredits(3);
            Assert.IsTrue(credits.TryReserve(long.MinValue, generation));
            Assert.IsTrue(credits.TryReserve(-1, 1));
            Assert.IsTrue(credits.TryReserve(long.MaxValue, 2));
            Assert.IsFalse(credits.TryRelease(long.MinValue, unchecked(generation - 1)));
            Assert.IsTrue(credits.TryRelease(-1, 1));
            Assert.IsTrue(credits.TryRelease(long.MaxValue, 2));
            Assert.IsTrue(credits.TryRelease(long.MinValue, generation));
            Assert.AreEqual(0, credits.Count);
        }

        [TestMethod]
        public void TerminalClearIsIdempotentAndRejectsOldAcknowledgements()
        {
            var credits = new DualSenseNativeCommandCredits(2);
            Assert.IsTrue(credits.TryReserve(1, 1));
            Assert.IsTrue(credits.TryReserve(2, 2));
            credits.Clear();
            credits.Clear();
            Assert.AreEqual(0, credits.Count);
            Assert.IsFalse(credits.TryRelease(1, 1));
            Assert.IsFalse(credits.TryRelease(2, 2));
            // The owner seals terminal admission; this pure storage helper
            // itself resets slots rather than owning process lifecycle state.
            Assert.IsTrue(credits.TryReserve(3, 3));
            Assert.AreEqual(1, credits.Count);
            Assert.IsTrue(credits.TryRelease(3, 3));
        }

        [TestMethod]
        public void RepeatedEpochsReuseOnlyExactlyAcknowledgedSlots()
        {
            var credits = new DualSenseNativeCommandCredits(1);
            for (int generation = 1; generation <= 1000; generation++)
            {
                Assert.IsTrue(credits.TryReserve(1, generation));
                Assert.IsFalse(credits.TryRelease(1, generation - 1));
                Assert.AreEqual(1, credits.Count);
                Assert.IsTrue(credits.TryRelease(1, generation));
                Assert.IsFalse(credits.TryRelease(1, generation));
            }
            Assert.AreEqual(0, credits.Count);
        }

        [TestMethod]
        public void HotReservationAcknowledgementAndRollbackAllocateZeroWithPositiveControl()
        {
            var credits = new DualSenseNativeCommandCredits(4);
            bool succeeded = true;
            for (int index = 0; index < 128; index++)
                succeeded &= RunCycle(credits, index * 8L + 1, index + 1);
            GC.KeepAlive(AllocatePositiveControl());
            Assert.IsTrue(succeeded);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
                succeeded &= RunCycle(credits, index * 8L + 10_000, index + 1000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            long positiveBefore = GC.GetAllocatedBytesForCurrentThread();
            object control = AllocatePositiveControl();
            long positiveAllocated = GC.GetAllocatedBytesForCurrentThread() - positiveBefore;
            GC.KeepAlive(control);

            Assert.IsTrue(succeeded, "Warm and measured cycles must exercise every credit branch.");
            Assert.AreEqual(0, credits.Count);
            Assert.IsTrue(positiveAllocated >= 128,
                "The allocation counter must detect an actual retained allocation.");
            Assert.AreEqual(0L, allocated, "The fixed credit ledger must not allocate per command or ACK.");
        }

        private static bool RunCycle(DualSenseNativeCommandCredits credits, long id, int generation)
        {
            bool ok = credits.TryReserve(id, generation);
            ok &= !credits.TryReserve(id, generation);
            ok &= !credits.TryReserve(id, generation + 1);
            ok &= credits.TryReserve(id + 1, generation);
            ok &= credits.TryReserve(id + 2, generation);
            ok &= credits.TryReserve(id + 3, generation);
            ok &= !credits.TryReserve(id + 4, generation);
            ok &= !credits.TryRelease(id, generation + 1);
            ok &= credits.TryRelease(id, generation);
            ok &= !credits.TryRelease(id, generation);
            ok &= credits.TryReserve(id + 4, generation);
            ok &= credits.TryRelease(id + 1, generation);
            ok &= credits.TryRelease(id + 2, generation);
            ok &= credits.TryRelease(id + 3, generation);
            ok &= credits.TryRelease(id + 4, generation);
            return ok && credits.Count == 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object AllocatePositiveControl() => new byte[128];
    }
}
