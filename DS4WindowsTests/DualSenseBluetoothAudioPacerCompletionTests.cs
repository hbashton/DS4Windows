using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseBluetoothAudioPacerCompletionTests
    {
        [TestMethod]
        public void TimeoutDetachesLateAcknowledgementFromReusedSlot()
        {
            object stateLock = new object();
            using var pool = new DualSenseBluetoothAudioPacer.
                ControlReportCompletionPool(stateLock, capacity: 1);

            Assert.IsTrue(pool.TryAcquire(reportId: 11, epoch: 3,
                out var first));
            Assert.IsFalse(pool.WaitAndRelease(first,
                timeoutMilliseconds: 1, out var firstDisposition));
            Assert.AreEqual(DualSenseBluetoothAudioPacer.
                AcknowledgementDisposition.Rejected, firstDisposition);

            Assert.IsTrue(pool.TryAcquire(reportId: 12, epoch: 4,
                out var replacement));
            Assert.IsFalse(pool.Complete(reportId: 11,
                DualSenseBluetoothAudioPacer.AcknowledgementDisposition.
                    Presented),
                "A late acknowledgement matched a reused completion slot.");
            Assert.IsFalse(pool.WaitAndRelease(replacement,
                timeoutMilliseconds: 1, out var replacementDisposition));
            Assert.AreEqual(DualSenseBluetoothAudioPacer.
                AcknowledgementDisposition.Rejected,
                replacementDisposition);

            Assert.IsTrue(pool.TryAcquire(reportId: 13, epoch: 5,
                out var completed));
            Assert.IsTrue(pool.Complete(reportId: 13,
                DualSenseBluetoothAudioPacer.AcknowledgementDisposition.
                    Presented));
            Assert.IsTrue(pool.WaitAndRelease(completed,
                timeoutMilliseconds: 100, out var completedDisposition));
            Assert.AreEqual(DualSenseBluetoothAudioPacer.
                AcknowledgementDisposition.Presented,
                completedDisposition);
            Assert.IsFalse(pool.WaitAndRelease(completed,
                timeoutMilliseconds: 100, out _),
                "A copied token released the same completion lease twice.");
        }

        [TestMethod]
        public void LifecycleCompletionPreservesEachBoundDisposition()
        {
            object stateLock = new object();
            using var pool = new DualSenseBluetoothAudioPacer.
                ControlReportCompletionPool(stateLock, capacity: 2);

            Assert.IsTrue(pool.TryAcquire(reportId: 21, epoch: 7,
                out var cleared));
            Assert.IsTrue(pool.TryAcquire(reportId: 22, epoch: 8,
                out var faulted));
            Assert.IsTrue(pool.Complete(reportId: 21,
                DualSenseBluetoothAudioPacer.AcknowledgementDisposition.
                    Cleared));
            pool.CompleteAll(DualSenseBluetoothAudioPacer.
                AcknowledgementDisposition.TransportFault);

            Assert.IsFalse(pool.WaitAndRelease(cleared,
                timeoutMilliseconds: 100, out var clearedDisposition));
            Assert.AreEqual(DualSenseBluetoothAudioPacer.
                AcknowledgementDisposition.Cleared,
                clearedDisposition);
            Assert.IsFalse(pool.WaitAndRelease(faulted,
                timeoutMilliseconds: 100, out var faultDisposition));
            Assert.AreEqual(DualSenseBluetoothAudioPacer.
                AcknowledgementDisposition.TransportFault,
                faultDisposition);
        }

        [TestMethod]
        public void WarmCompletionLeaseCycleAllocatesZero()
        {
            object stateLock = new object();
            using var pool = new DualSenseBluetoothAudioPacer.
                ControlReportCompletionPool(stateLock, capacity: 1);

            bool succeeded = true;
            for (long reportId = 1; reportId <= 256; reportId++)
            {
                succeeded &= CompletePresented(pool, reportId);
            }
            Assert.IsTrue(succeeded);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (long reportId = 257; reportId <= 10_256; reportId++)
            {
                succeeded &= CompletePresented(pool, reportId);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(succeeded);
            Assert.AreEqual(0L, allocated,
                $"Control completion leases allocated {allocated} bytes.");
        }

        private static bool CompletePresented(
            DualSenseBluetoothAudioPacer.ControlReportCompletionPool pool,
            long reportId)
        {
            return pool.TryAcquire(reportId, epoch: 1, out var token) &&
                pool.Complete(reportId, DualSenseBluetoothAudioPacer.
                    AcknowledgementDisposition.Presented) &&
                pool.WaitAndRelease(token, timeoutMilliseconds: 1,
                    out var disposition) &&
                disposition == DualSenseBluetoothAudioPacer.
                    AcknowledgementDisposition.Presented;
        }
    }
}
