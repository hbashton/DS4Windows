using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseInputReadPipelineTests
    {
        [TestMethod]
        public void AlternateReadIsSubmittedBeforeCompletedReportReturns()
        {
            byte[][] buffers = CreateBuffers();
            using var backend = new FakeReadBackend(buffers,
                completesSynchronously: true);
            using var reader = new PipelinedInputReportReader(buffers,
                backend);

            Assert.AreEqual(HidDevice.ReadStatus.Success,
                reader.ReadNext(out byte[] first, out int firstError,
                    out long firstCompletion, out _));
            Assert.AreSame(buffers[0], first);
            Assert.AreEqual(1, first[0]);
            Assert.AreEqual(2, backend.SubmitCount,
                "The alternate buffer was not armed before ReadNext returned.");
            Assert.AreEqual(0, firstError);
            Assert.IsTrue(firstCompletion > 0);

            Assert.AreEqual(HidDevice.ReadStatus.Success,
                reader.ReadNext(out byte[] second, out int secondError,
                    out _, out _));
            Assert.AreSame(buffers[1], second);
            Assert.AreEqual(2, second[0]);
            Assert.AreEqual(3, backend.SubmitCount);
            Assert.AreEqual(0, secondError);
        }

        [TestMethod]
        public void EveryCompletedReportIsReturnedInSubmissionOrder()
        {
            byte[][] buffers = CreateBuffers();
            using var backend = new FakeReadBackend(buffers,
                completesSynchronously: true);
            using var reader = new PipelinedInputReportReader(buffers,
                backend);

            for (int reportNumber = 1; reportNumber <= 1_000;
                reportNumber++)
            {
                Assert.AreEqual(HidDevice.ReadStatus.Success,
                    reader.ReadNext(out byte[] report, out int winError,
                        out _, out _));
                Assert.AreEqual(0, winError);
                Assert.AreEqual((byte)reportNumber, report[0],
                    $"Report {reportNumber} was skipped or reordered.");
            }
        }

        [TestMethod]
        public void FailedRearmIsDeferredUntilCompletedReportIsConsumed()
        {
            byte[][] buffers = CreateBuffers();
            using var backend = new FakeReadBackend(buffers,
                completesSynchronously: true)
            {
                FailSubmitNumber = 2,
                SubmitFailureError = 1234,
            };
            using var reader = new PipelinedInputReportReader(buffers,
                backend);

            Assert.AreEqual(HidDevice.ReadStatus.Success,
                reader.ReadNext(out byte[] completed, out int firstError,
                    out _, out _));
            Assert.AreSame(buffers[0], completed);
            Assert.AreEqual(0, firstError);

            Assert.AreEqual(HidDevice.ReadStatus.ReadError,
                reader.ReadNext(out byte[] missing, out int deferredError,
                    out _, out _));
            Assert.IsNull(missing);
            Assert.AreEqual(1234, deferredError);
            Assert.AreEqual(2, backend.SubmitCount);
        }

        [TestMethod]
        public void CloseBetweenCompletionAndRearmCannotStrandARead()
        {
            byte[][] buffers = CreateBuffers();
            using var backend = new FakeReadBackend(buffers,
                completesSynchronously: true)
            {
                CloseBeforeSubmitNumber = 2,
            };
            using var reader = new PipelinedInputReportReader(buffers,
                backend);

            Assert.AreEqual(HidDevice.ReadStatus.Success,
                reader.ReadNext(out byte[] completed, out int firstError,
                    out _, out _));
            Assert.AreSame(buffers[0], completed,
                "The already-completed report was lost at the close boundary.");
            Assert.AreEqual(0, firstError);
            Assert.AreEqual(HidDevice.ReadStatus.ReadError,
                reader.ReadNext(out _, out int closeError, out _, out _));
            Assert.AreEqual(995, closeError,
                "The close-crossing rearm was not surfaced as cancellation.");
            Assert.AreEqual(0, backend.PendingOperationCount,
                "A read remained pending behind the close boundary.");
        }

        [TestMethod]
        public void DisposalCancelsAndDrainsTheExactPendingRead()
        {
            byte[][] buffers = CreateBuffers();
            using var backend = new FakeReadBackend(buffers,
                completesSynchronously: false);
            var reader = new PipelinedInputReportReader(buffers, backend);

            Assert.AreEqual(HidDevice.ReadStatus.Success,
                reader.ReadNext(out _, out _, out _, out _));
            Assert.AreEqual(1, backend.WaitCount);
            Assert.AreEqual(2, backend.SubmitCount);

            reader.Dispose();
            Assert.AreEqual(1, backend.CancelAndDrainCount);
            Assert.AreEqual(1, backend.DisposeCount);
        }

        [TestMethod]
        public void WarmSynchronousReadAndRearmCycleAllocatesZero()
        {
            byte[][] buffers = CreateBuffers();
            using var backend = new FakeReadBackend(buffers,
                completesSynchronously: true);
            using var reader = new PipelinedInputReportReader(buffers,
                backend);

            for (int index = 0; index < 512; index++)
            {
                Assert.AreEqual(HidDevice.ReadStatus.Success,
                    reader.ReadNext(out _, out _, out _, out _));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                HidDevice.ReadStatus status = reader.ReadNext(out _, out _,
                    out _, out _);
                if (status != HidDevice.ReadStatus.Success)
                {
                    Assert.Fail($"Unexpected read status {status}.");
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                "The warmed input completion/rearm cycle allocated.");
        }

        [TestMethod]
        public void PhysicalRumbleKeepaliveSignalsOnlyOnceWhenDue()
        {
            long dueAt = 1_000;
            Assert.IsFalse(DualSenseDevice.TryClaimPhysicalOutputKeepalive(
                ref dueAt, 999));
            Assert.AreEqual(1_000L, dueAt);

            Assert.IsTrue(DualSenseDevice.TryClaimPhysicalOutputKeepalive(
                ref dueAt, 1_000));
            Assert.AreEqual(-1L, dueAt);
            Assert.IsFalse(DualSenseDevice.TryClaimPhysicalOutputKeepalive(
                ref dueAt, 2_000),
                "A due keepalive was admitted more than once.");
        }

        [TestMethod]
        public void PhysicalOutputFailureSchedulesRetryWithoutRumble()
        {
            long dueAt = -1;
            DualSenseDevice.SchedulePhysicalOutputDue(ref dueAt,
                nowQpc: 10_000, frequency: 1_000,
                delayMilliseconds: 100);

            Assert.AreEqual(10_100L, dueAt,
                "A non-rumble output failure did not publish a retry deadline.");
            Assert.IsFalse(DualSenseDevice.TryClaimPhysicalOutputKeepalive(
                ref dueAt, 10_099));
            Assert.IsTrue(DualSenseDevice.TryClaimPhysicalOutputKeepalive(
                ref dueAt, 10_100));
            Assert.AreEqual(-1L, dueAt);
        }

        private static byte[][] CreateBuffers() =>
            new[] { new byte[64], new byte[64] };

        private sealed class FakeReadBackend :
            PipelinedInputReportReader.IReadBackend
        {
            private readonly byte[][] buffers;
            private readonly bool completesSynchronously;

            internal FakeReadBackend(byte[][] buffers,
                bool completesSynchronously)
            {
                this.buffers = buffers;
                this.completesSynchronously = completesSynchronously;
            }

            internal int SubmitCount { get; private set; }
            internal int WaitCount { get; private set; }
            internal int CancelAndDrainCount { get; private set; }
            internal int DisposeCount { get; private set; }
            internal int FailSubmitNumber { get; init; }
            internal int SubmitFailureError { get; init; }
            internal int CloseBeforeSubmitNumber { get; init; }
            internal int PendingOperationCount { get; private set; }

            public bool TrySubmit(int bufferIndex,
                out bool completedSynchronously, out int winError)
            {
                SubmitCount++;
                if (SubmitCount == CloseBeforeSubmitNumber)
                {
                    completedSynchronously = false;
                    winError = 995; // ERROR_OPERATION_ABORTED
                    PendingOperationCount = 0;
                    return false;
                }
                if (SubmitCount == FailSubmitNumber)
                {
                    completedSynchronously = false;
                    winError = SubmitFailureError;
                    return false;
                }

                buffers[bufferIndex][0] = (byte)SubmitCount;
                completedSynchronously = completesSynchronously;
                PendingOperationCount = completesSynchronously ? 0 : 1;
                winError = 0;
                return true;
            }

            public HidDevice.ReadStatus WaitForCompletion(uint timeout,
                out int winError)
            {
                WaitCount++;
                PendingOperationCount = 0;
                winError = 0;
                return HidDevice.ReadStatus.Success;
            }

            public void CancelAndDrain()
            {
                CancelAndDrainCount++;
                PendingOperationCount = 0;
            }

            public void Dispose()
            {
                DisposeCount++;
            }
        }
    }
}
