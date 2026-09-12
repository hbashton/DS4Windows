using DS4Windows.InputDevices;
using DS4WindowsTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseBluetoothNativeWriteCreditTests
    {
        private const int PhysicalLength = 398;
        public TestContext TestContext { get; set; }

        [DataTestMethod]
        [DataRow(0x31, 78)]
        [DataRow(0x36, 398)]
        public void ExplicitNativeCreditGatesStandaloneAndPiggybackWithoutBlockingMedia(
            int reportId, int logicalLength)
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            byte[] command = Report((byte)reportId, logicalLength);
            Assert.IsTrue(writer.CanSubmitNativeCommand(out bool fault));
            Assert.IsFalse(fault);
            Assert.IsTrue(writer.TryWrite(command, nativeCommand: true, out fault));
            Assert.IsFalse(fault);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out fault));
            Assert.IsFalse(fault);
            Assert.IsFalse(writer.TryWrite(command, nativeCommand: true, out fault));
            Assert.IsFalse(fault, "Credit exhaustion is not a transport failure.");

            byte[] media = Report(0x36);
            for (int index = 0; index < 31; index++)
            {
                Assert.IsTrue(writer.TryWrite(media, out fault),
                    "Pending native I/O must not shrink the existing media ring.");
                Assert.IsFalse(fault);
            }
            Assert.AreEqual(32, io.Submitted);
            Assert.IsFalse(writer.TryWrite(media, out fault));
            Assert.IsFalse(fault);
            Assert.AreEqual(PhysicalLength, writer.PhysicalWriteLength);
            CollectionAssert.AreEqual(command,
                io.History[0].Bytes.AsSpan(0, logicalLength).ToArray());
            foreach (byte padding in io.History[0].Bytes.AsSpan(logicalLength))
                Assert.AreEqual((byte)0, padding);
        }

        [TestMethod]
        public void DefaultNativeBudgetPreservesTheFullExistingRing()
        {
            var io = new ControlledNativeIo();
            using var writer = new DualSenseBluetoothRealtimeWriter(
                PhysicalLength, 32, 32, io);
            Assert.AreEqual(32, writer.NativeCommandInFlightLimit);
            for (int index = 0; index < 32; index++)
            {
                Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out bool fault));
                Assert.IsFalse(fault);
            }
            Assert.AreEqual(32, io.Submitted);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out bool busyFault));
            Assert.IsFalse(busyFault);
        }

        [TestMethod]
        public void ConfiguredBudgetTwoIsNotHardcodedToOne()
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io, nativeLimit: 2);
            byte[] command = Report(0x31, 78);
            Assert.IsTrue(writer.TryWrite(command, true, out _));
            Assert.IsTrue(writer.CanSubmitNativeCommand(out _));
            Assert.IsTrue(writer.TryWrite(command, true, out _));
            Assert.IsFalse(writer.TryWrite(command, true, out bool fault));
            Assert.IsFalse(fault);
            io.Complete(0);
            Assert.IsTrue(writer.TryWrite(command, true, out fault));
            Assert.IsFalse(fault);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out fault));
            Assert.IsFalse(fault);
        }

        [DataTestMethod]
        [DataRow(-1)]
        [DataRow(33)]
        public void InvalidNativeBudgetIsRejected(int limit)
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                Create(new ControlledNativeIo(), limit));
        }

        [TestMethod]
        public void ExistingOptOutOverloadDoesNotInferOwnershipFromReportId()
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            byte[] command = Report(0x31, 78);
            Assert.IsTrue(writer.TryWrite(command, out _));
            Assert.IsTrue(writer.TryWrite(command, out _));
            Assert.IsTrue(writer.CanSubmitNativeCommand(out bool fault));
            Assert.IsFalse(fault);
            Assert.IsTrue(writer.TryWrite(command, true, out fault));
            Assert.IsFalse(fault);
            Assert.AreEqual(3, io.Submitted);
        }

        [TestMethod]
        public void UnusedNativeCreditDoesNotPollUnrelatedMediaCompletions()
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            Assert.IsTrue(writer.TryWrite(Report(0x36), out _));
            io.Complete(0);
            Assert.IsTrue(writer.CanSubmitNativeCommand(out bool fault));
            Assert.IsFalse(fault);
            Assert.AreEqual(0, io.CompletionProbes,
                "A native readiness probe must not consume an unrelated media event.");
            Assert.AreEqual(0L, writer.CompletedWrites);
            Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out fault));
            Assert.IsFalse(fault);
            Assert.AreEqual(1, io.CompletionProbes,
                "Normal physical admission still observes completed media.");
        }

        [TestMethod]
        public void CompletingMediaDoesNotReturnAnotherRequestsNativeCredit()
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out _));
            Assert.IsTrue(writer.TryWrite(Report(0x36), out _));
            io.Complete(1);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out bool fault));
            Assert.IsFalse(fault);
            io.Complete(0);
            Assert.IsTrue(writer.CanSubmitNativeCommand(out fault));
            Assert.IsFalse(fault);
            Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out fault));
            Assert.IsFalse(fault);
            Assert.AreEqual(3, io.Submitted);
        }

        [TestMethod]
        public void SuccessfulReadinessProbeDoesNotReserveOrBypassTheWriteGate()
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            Assert.IsTrue(writer.CanSubmitNativeCommand(out _));
            Assert.IsTrue(writer.CanSubmitNativeCommand(out _));
            Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out _));
            Assert.IsFalse(writer.TryWrite(Report(0x36), true, out bool fault));
            Assert.IsFalse(fault);
            Assert.AreEqual(1, io.Submitted);
        }

        [TestMethod]
        public void SynchronousSuccessImmediatelyReturnsTheConfiguredCredit()
        {
            var io = new ControlledNativeIo { Asynchronous = false };
            using var writer = Create(io);
            for (int index = 0; index < 64; index++)
            {
                Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out bool fault));
                Assert.IsFalse(fault);
                Assert.IsTrue(writer.CanSubmitNativeCommand(out fault));
                Assert.IsFalse(fault);
            }
            Assert.AreEqual(64, io.Submitted);
            Assert.AreEqual(64L, writer.CompletedWrites);
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, true)]
        public void FailedOrShortNativeCompletionPoisonsCreditAndLaterWrites(
            bool successfulCompletion, bool shortCompletion)
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out _));
            io.Complete(0, successfulCompletion, shortCompletion ? 77u : PhysicalLength);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out bool fault));
            Assert.IsTrue(fault);
            // A short completion cleared its pending slot, but that must not
            // make the following probe silently healthy again.
            Assert.IsFalse(writer.CanSubmitNativeCommand(out fault));
            Assert.IsTrue(fault);
            Assert.IsFalse(writer.TryWrite(Report(0x36), out fault));
            Assert.IsTrue(fault);
            Assert.AreEqual(1, io.Submitted);
        }

        [TestMethod]
        public void SynchronousShortNativeCompletionDoesNotGrantHealthyCredit()
        {
            var io = new ControlledNativeIo
            {
                Asynchronous = false,
                SynchronousLength = 78
            };
            using var writer = Create(io);
            Assert.IsFalse(writer.TryWrite(Report(0x31, 78), true, out bool fault));
            Assert.IsTrue(fault);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out fault));
            Assert.IsTrue(fault);
            Assert.AreEqual(1L, writer.ShortCompletionCount);
        }

        [TestMethod]
        public void RejectedNativeSubmissionCannotBeRetriedAsHealthy()
        {
            var io = new ControlledNativeIo { RejectNext = true };
            using var writer = Create(io);
            Assert.IsFalse(writer.TryWrite(Report(0x31, 78), true, out bool fault));
            Assert.IsTrue(fault);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out fault));
            Assert.IsTrue(fault);
            Assert.IsFalse(writer.TryWrite(Report(0x31, 78), true, out fault));
            Assert.IsTrue(fault);
            Assert.AreEqual(1, io.Submitted);
        }

        [TestMethod]
        public void ExistingCompletionBarrierDrainsTheNativeCredit()
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out _));
            io.Complete(0);
            io.Asynchronous = false;
            Assert.IsTrue(writer.TryWriteAndWait(Report(0x31, 78), 100, out bool fault));
            Assert.IsFalse(fault);
            Assert.IsTrue(writer.CanSubmitNativeCommand(out fault));
            Assert.IsFalse(fault);
            Assert.AreEqual(2, io.Submitted);
        }

        [TestMethod]
        public void DisposalCancelsTheExactNativeWriteAndNeverReopensCredit()
        {
            var io = new ControlledNativeIo();
            using var writer = Create(io);
            Assert.IsTrue(writer.TryWrite(Report(0x31, 78), true, out _));
            writer.Dispose();
            Assert.IsTrue(writer.WaitForDisposal(1000));
            Assert.IsTrue(writer.NativeResourcesReleased);
            Assert.AreEqual(1, io.Cancelled);
            Assert.AreEqual(io.History[0].Overlapped, io.LastCancelled);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out bool fault));
            Assert.IsTrue(fault);
        }

        [TestMethod]
        public void ConcurrentProbeCannotPassAnUnfinishedSubmissionLease()
        {
            var io = new ControlledNativeIo();
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            using var writer = Create(io);
            io.BeforeSubmit = () => { entered.Set(); proceed.Wait(); };
            io.StateLockHeld = () => writer.IsStateLockHeldByCurrentThread;
            Task<(bool accepted, bool fault)> task = Task.Run(() =>
            {
                bool accepted = writer.TryWrite(Report(0x31, 78), true, out bool fault);
                return (accepted, fault);
            });
            try
            {
                Assert.IsTrue(entered.Wait(1000));
                Assert.IsFalse(writer.CanSubmitNativeCommand(out bool fault));
                Assert.IsFalse(fault);
                Assert.IsFalse(writer.TryWrite(Report(0x31, 78), true, out fault));
                Assert.IsFalse(fault);
            }
            finally
            {
                proceed.Set();
            }
            Assert.IsTrue(task.Wait(1000));
            Assert.IsTrue(task.Result.accepted);
            Assert.IsFalse(task.Result.fault);
            Assert.IsFalse(io.ObservedStateLock);
            Assert.IsFalse(writer.CanSubmitNativeCommand(out bool pendingFault));
            Assert.IsFalse(pendingFault);
        }

        [TestMethod]
        public void NativeCreditProbeAndSynchronousWriteAllocateZeroAfterWarmup()
        {
            var io = new ControlledNativeIo { Asynchronous = false, RecordHistory = false };
            using var writer = Create(io);
            byte[] command = Report(0x31, 78);
            string traceMode = Environment.GetEnvironmentVariable("DS4W_NATIVE_CREDIT_ALLOCATION_TRACE");
            if (!string.IsNullOrEmpty(traceMode))
            {
                TraceAllocationWindows(io, writer, command, traceMode);
                return;
            }
            bool successful = true;
            for (int index = 0; index < 256; index++)
            {
                successful &= writer.CanSubmitNativeCommand(out bool fault) && !fault;
                successful &= writer.TryWrite(command, true, out fault) && !fault;
            }
            Assert.IsTrue(successful);
            Assert.AreEqual(32, io.RequestCount);
            long allocated;
            // Same-workload object callbacks corroborate background-GC
            // allocation-context repair without a managed object. Isolate
            // only the warmed counter pair; entry/exit failures still fail.
            using (StrictAllocationMeasurementScope.Begin())
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 10_000; index++)
                {
                    successful &= writer.CanSubmitNativeCommand(out bool fault) && !fault;
                    successful &= writer.TryWrite(command, true, out fault) && !fault;
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.IsTrue(successful);
            Assert.AreEqual(0L, allocated);
        }

        [TestMethod]
        public void NativeCreditRealReportCloneFailsTheExactZeroGate()
        {
            var io = new ControlledNativeIo { Asynchronous = false, RecordHistory = false };
            using var writer = Create(io);
            byte[] command = Report(0x31, 78);
            bool successful = true;
            for (int index = 0; index < 256; index++)
            {
                successful &= writer.CanSubmitNativeCommand(out bool fault) && !fault;
                successful &= writer.TryWrite(command, true, out fault) && !fault;
            }
            bool capture = NativeAllocationMeasurement.IsEnabled;
            if (capture) { NativeAllocationMeasurement.Begin(); NativeAllocationMeasurement.End(0); }
            io.CloneReport = true;
            long allocated;
            uint nativeObjects = 0;
            using (StrictAllocationMeasurementScope.Begin())
            {
                if (capture) NativeAllocationMeasurement.Begin();
                long before = GC.GetAllocatedBytesForCurrentThread();
                successful &= writer.CanSubmitNativeCommand(out bool fault) && !fault;
                successful &= writer.TryWrite(command, true, out fault) && !fault;
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (capture) nativeObjects = NativeAllocationMeasurement.End(allocated);
            }
            Assert.IsTrue(successful);
            Assert.AreEqual(PhysicalLength, io.LastClone.Length);
            CollectionAssert.AreEqual(command, io.LastClone.AsSpan(0, command.Length).ToArray());
            Assert.IsTrue(allocated >= PhysicalLength);
            Assert.ThrowsException<AssertFailedException>(() => Assert.AreEqual(0L, allocated));
            if (capture) Assert.IsTrue(nativeObjects >= 1);
            TestContext?.WriteLine($"Native credit positive control: bytes={allocated} nativeCapture={capture} nativeObjects={nativeObjects}");
        }

        private void TraceAllocationWindows(ControlledNativeIo io,
            DualSenseBluetoothRealtimeWriter writer, byte[] command, string mode)
        {
            if (mode is not ("boundary" or "phases"))
                throw new ArgumentException("Unknown native credit allocation trace mode.");
            bool pressure = Environment.GetEnvironmentVariable("DS4W_NATIVE_CREDIT_ALLOCATION_PRESSURE") == "1";
            bool isolate = Environment.GetEnvironmentVariable("DS4W_NATIVE_CREDIT_ALLOCATION_ISOLATE") == "1";
            bool capture = NativeAllocationMeasurement.IsEnabled;
            if (capture) { NativeAllocationMeasurement.Begin(); NativeAllocationMeasurement.End(0); }
            object[][] graph = null;
            if (pressure)
            {
                graph = new object[250_000][];
                for (int index = 0; index < graph.Length; index++)
                    graph[index] = new object[] { new object(), index == 0 ? graph : graph[index - 1] };
            }
            for (int batch = 0; batch < (pressure ? 256 : 1); batch++)
            {
                // Pressure is outside the original 256/10,000-call workload.
                // Preserve the first failed strict-zero window; never retry it.
                long[] first = pressure ? new long[24_576] : null;
                long[] second = pressure ? new long[24_576] : null;
                byte[] padding = pressure ? new byte[512] : null;
                Window(batch);
                GC.KeepAlive(first); GC.KeepAlive(second); GC.KeepAlive(padding);
            }
            GC.KeepAlive(graph);

            void Window(int batch)
            {
                var records = new (int Iteration, int Phase, long Delta)[256];
                var phaseBytes = new long[3];
                int recordCount = 0, omitted = 0;
                bool successful = true;
                for (int index = 0; index < 256; index++)
                {
                    successful &= writer.CanSubmitNativeCommand(out bool fault) && !fault;
                    successful &= writer.TryWrite(command, true, out fault) && !fault;
                }
                int threadBefore = Environment.CurrentManagedThreadId;
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                long before, after, last = 0;
                uint objects = 0;
                using (isolate ? StrictAllocationMeasurementScope.Begin() : null)
                {
                    if (capture) NativeAllocationMeasurement.Begin();
                    before = GC.GetAllocatedBytesForCurrentThread();
                    last = before;
                    for (int index = 0; index < 10_000; index++)
                    {
                        if (mode == "phases") Probe(index, 0);
                        successful &= writer.CanSubmitNativeCommand(out bool fault) && !fault;
                        if (mode == "phases") Probe(index, 1);
                        successful &= writer.TryWrite(command, true, out fault) && !fault;
                        if (mode == "phases") Probe(index, 2);
                    }
                    after = GC.GetAllocatedBytesForCurrentThread();
                    if (capture) objects = NativeAllocationMeasurement.End(after - before);
                }
                int threadAfter = Environment.CurrentManagedThreadId;
                string details = $"Native credit allocation trace mode={mode} batch={batch} warm=256 measured=10000 " +
                    $"before={before} after={after} bytes={after-before} nativeCapture={capture} nativeObjects={objects} isolated={isolate} " +
                    $"thread={threadBefore}->{threadAfter} gc={g0},{g1},{g2}->{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)} " +
                    $"requests={io.RequestCount} omitted={omitted} phaseBytes={phaseBytes[0]},{phaseBytes[1]},{phaseBytes[2]}";
                TestContext?.WriteLine(details);
                for (int index = 0; index < recordCount; index++)
                    TestContext?.WriteLine($"iteration={records[index].Iteration} phase={records[index].Phase} bytes={records[index].Delta}");
                Assert.IsTrue(successful, details);
                Assert.AreEqual(32, io.RequestCount, details);
                Assert.AreEqual(threadBefore, threadAfter, details);
                Assert.AreEqual(0L, after - before, details);

                void Probe(int iteration, int phase)
                {
                    long current = GC.GetAllocatedBytesForCurrentThread();
                    long delta = current - last;
                    last = current;
                    phaseBytes[phase] += delta;
                    if (delta == 0) return;
                    if (recordCount < records.Length) records[recordCount++] = (iteration, phase, delta);
                    else omitted++;
                }
            }
        }

        private static DualSenseBluetoothRealtimeWriter Create(
            ControlledNativeIo io, int nativeLimit = 1) =>
            new(PhysicalLength, 32, 32, io, nativeLimit);

        private static byte[] Report(byte reportId, int length = PhysicalLength)
        {
            var result = new byte[length];
            result[0] = reportId;
            result[length - 1] = 0xA7;
            return result;
        }

        private sealed class ControlledNativeIo : IDualSenseBluetoothRealtimeWriterNativeIo
        {
            internal sealed class Request
            {
                internal readonly byte[] Bytes = new byte[PhysicalLength];
                internal IntPtr Overlapped;
                internal IntPtr Event;
                internal bool Successful = true;
                internal uint CompletionLength = PhysicalLength;
            }

            private readonly Dictionary<IntPtr, Request> requests = new();
            internal readonly List<Request> History = new();
            internal bool Asynchronous = true;
            internal bool RecordHistory = true;
            internal bool CloneReport;
            internal byte[] LastClone;
            internal int RequestCount => requests.Count;
            internal bool RejectNext;
            internal uint SynchronousLength = PhysicalLength;
            internal int Submitted;
            internal int Cancelled;
            internal int CompletionProbes;
            internal IntPtr LastCancelled;
            internal Action BeforeSubmit;
            internal Func<bool> StateLockHeld;
            internal bool ObservedStateLock;

            public bool TrySubmit(IntPtr deviceHandle, IntPtr buffer,
                uint bytesToWrite, IntPtr overlapped, out bool pending, out int error)
            {
                ObserveLock();
                BeforeSubmit?.Invoke();
                Submitted++;
                pending = Asynchronous;
                error = 0;
                if (RejectNext)
                {
                    RejectNext = false;
                    pending = false;
                    error = 31;
                    return false;
                }
                if (!requests.TryGetValue(overlapped, out Request request))
                {
                    request = new Request { Overlapped = overlapped };
                    requests.Add(overlapped, request);
                }
                request.Event = Marshal.ReadIntPtr(overlapped,
                    IntPtr.Size * 2 + sizeof(uint) * 2);
                request.Successful = true;
                request.CompletionLength = pending ? bytesToWrite : SynchronousLength;
                Marshal.Copy(buffer, request.Bytes, 0, checked((int)bytesToWrite));
                if (CloneReport)
                {
                    // Explicit test-owned allocation/copy keeps the native
                    // profiler's JIT allocation callback visible as well as
                    // exercising real report retention in this writer seam.
                    byte[] copy = new byte[request.Bytes.Length];
                    Buffer.BlockCopy(request.Bytes, 0, copy, 0, copy.Length);
                    LastClone = copy;
                }
                if (RecordHistory)
                    History.Add(request);
                return true;
            }

            internal void Complete(int index, bool success = true,
                uint length = PhysicalLength)
            {
                Request request = History[index];
                request.Successful = success;
                request.CompletionLength = length;
                SetEvent(request.Event);
            }

            public bool TryGetCompletion(IntPtr deviceHandle, IntPtr overlapped,
                out uint bytesTransferred)
            {
                ObserveLock();
                CompletionProbes++;
                Request request = requests[overlapped];
                bytesTransferred = request.CompletionLength;
                return request.Successful;
            }

            public void Cancel(IntPtr deviceHandle, IntPtr overlapped)
            {
                ObserveLock();
                Cancelled++;
                LastCancelled = overlapped;
                SetEvent(requests[overlapped].Event);
            }

            private void ObserveLock() =>
                ObservedStateLock |= StateLockHeld?.Invoke() == true;

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetEvent(IntPtr handle);
        }
    }
}
