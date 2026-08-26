using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseBluetoothRealtimeWriterOwnershipTests
    {
        private const int ReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;

        [TestMethod]
        public void BlockingNativeSubmissionDoesNotOwnStateLockOrLifecycle()
        {
            using var native = new BlockingNativeIo(ReportLength);
            using var writer = new DualSenseBluetoothRealtimeWriter(
                ReportLength, slotCount: 3, audioInFlightLimit: 2, native);
            native.StateLockHeld = () =>
                writer.IsStateLockHeldByCurrentThread;
            byte[] report = new byte[ReportLength];

            Task<(bool Written, bool Fault)> write = Task.Run(() =>
            {
                bool written = writer.TryWrite(report, out bool fault);
                return (written, fault);
            });
            Assert.IsTrue(native.SubmissionStarted.Wait(1000));

            Task<(bool Disposed, bool Active, long Generation)> snapshot =
                Task.Run(() =>
                {
                    writer.GetOwnershipState(out bool disposed,
                        out bool active, out long generation);
                    return (disposed, active, generation);
                });
            Assert.IsTrue(snapshot.Wait(1000),
                "State observation waited behind physical HID submission.");
            Assert.IsFalse(snapshot.Result.Disposed);
            Assert.IsTrue(snapshot.Result.Active);
            Assert.IsFalse(native.ObservedStateLock,
                "Native HID submission ran while syncRoot was owned.");

            Task dispose = Task.Run(writer.Dispose);
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                writer.GetOwnershipState(out bool disposed, out _, out _);
                return disposed;
            }, 1000), "Lifecycle poison was blocked behind HID submission.");
            Assert.IsFalse(dispose.IsCompleted,
                "Disposal must retain pinned ownership until submission exits.");

            native.AllowSubmission.Set();
            Assert.IsTrue(write.Wait(1000));
            Assert.IsFalse(write.Result.Written,
                "An old-generation write cannot report success after poison.");
            Assert.IsTrue(write.Result.Fault);
            Assert.IsTrue(dispose.Wait(1000));
            Assert.IsTrue(writer.WaitForDisposal(1000));
            Assert.IsTrue(writer.NativeResourcesReleased);
            Assert.IsFalse(native.ObservedStateLock,
                "Completion/cancellation ran while syncRoot was owned.");
        }

        [TestMethod]
        public void SynchronousLoadedWriterPathAllocatesZeroAfterWarmup()
        {
            using var native = new ImmediateNativeIo(ReportLength);
            using var writer = new DualSenseBluetoothRealtimeWriter(
                ReportLength, slotCount: 3, audioInFlightLimit: 2, native);
            byte[] report = new byte[ReportLength];

            bool succeeded = true;
            for (int index = 0; index < 256; index++)
            {
                report[1] = (byte)index;
                succeeded &= writer.TryWrite(report, out bool fault) &&
                    !fault;
            }
            Assert.IsTrue(succeeded);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                report[1] = (byte)index;
                succeeded &= writer.TryWrite(report, out bool fault) &&
                    !fault;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(succeeded);
            Assert.AreEqual(0L, allocated,
                $"Realtime HID writer allocated {allocated} bytes.");
            Assert.IsFalse(native.ObservedStateLock);
        }

        private class ImmediateNativeIo :
            IDualSenseBluetoothRealtimeWriterNativeIo, IDisposable
        {
            private readonly uint completionLength;
            internal Func<bool> StateLockHeld;
            internal bool ObservedStateLock;

            internal ImmediateNativeIo(int completionLength)
            {
                this.completionLength = (uint)completionLength;
            }

            public virtual bool TrySubmit(IntPtr deviceHandle, IntPtr buffer,
                uint bytesToWrite, IntPtr overlapped, out bool pending,
                out int error)
            {
                ObserveStateLock();
                pending = false;
                error = 0;
                return true;
            }

            public bool TryGetCompletion(IntPtr deviceHandle,
                IntPtr overlapped, out uint bytesTransferred)
            {
                ObserveStateLock();
                bytesTransferred = completionLength;
                return true;
            }

            public void Cancel(IntPtr deviceHandle, IntPtr overlapped)
            {
                ObserveStateLock();
            }

            protected void ObserveStateLock()
            {
                if (StateLockHeld?.Invoke() == true)
                {
                    ObservedStateLock = true;
                }
            }

            public virtual void Dispose()
            {
            }
        }

        private sealed class BlockingNativeIo : ImmediateNativeIo
        {
            internal readonly ManualResetEventSlim SubmissionStarted =
                new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim AllowSubmission =
                new ManualResetEventSlim(false);

            internal BlockingNativeIo(int completionLength) :
                base(completionLength)
            {
            }

            public override bool TrySubmit(IntPtr deviceHandle, IntPtr buffer,
                uint bytesToWrite, IntPtr overlapped, out bool pending,
                out int error)
            {
                ObserveStateLock();
                SubmissionStarted.Set();
                AllowSubmission.Wait();
                pending = false;
                error = 0;
                return true;
            }

            public override void Dispose()
            {
                AllowSubmission.Set();
                SubmissionStarted.Dispose();
                AllowSubmission.Dispose();
            }
        }
    }
}
