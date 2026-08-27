using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;

namespace DS4Windows
{
    /// <summary>
    /// Maintains one input read ahead of report processing. The completed
    /// buffer is returned only after the alternate buffer has been submitted,
    /// so mapping and virtual publication cannot leave the physical HID stack
    /// without an outstanding read.
    /// </summary>
    internal sealed class PipelinedInputReportReader : IDisposable
    {
        internal interface IReadBackend : IDisposable
        {
            bool TrySubmit(int bufferIndex, out bool completedSynchronously,
                out int winError);

            HidDevice.ReadStatus WaitForCompletion(uint timeout,
                out int winError);

            void CancelAndDrain();
        }

        private readonly byte[][] buffers;
        private readonly IReadBackend backend;
        private int activeBufferIndex;
        private bool operationAvailable;
        private bool operationCompletedSynchronously;
        private HidDevice.ReadStatus deferredStatus;
        private int deferredWinError;
        private bool disposed;

        internal PipelinedInputReportReader(byte[][] buffers,
            IReadBackend backend)
        {
            if (buffers == null || buffers.Length != 2 ||
                buffers[0] == null || buffers[1] == null ||
                buffers[0].Length == 0 ||
                buffers[0].Length != buffers[1].Length)
            {
                throw new ArgumentException(
                    "Exactly two equal, non-empty report buffers are required.",
                    nameof(buffers));
            }

            this.buffers = buffers;
            this.backend = backend ?? throw new ArgumentNullException(
                nameof(backend));
        }

        internal HidDevice.ReadStatus ReadNext(out byte[] report,
            out int winError, out long completionObservedAtQpc,
            out long rearmDurationTicks, uint timeout = uint.MaxValue)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            report = null;
            winError = 0;
            completionObservedAtQpc = 0;
            rearmDurationTicks = 0;

            if (deferredStatus != HidDevice.ReadStatus.Success)
            {
                HidDevice.ReadStatus status = deferredStatus;
                winError = deferredWinError;
                deferredStatus = HidDevice.ReadStatus.Success;
                deferredWinError = 0;
                return status;
            }

            if (!operationAvailable && !Submit(activeBufferIndex,
                    out winError))
            {
                return HidDevice.ReadStatus.ReadError;
            }

            if (!operationCompletedSynchronously)
            {
                HidDevice.ReadStatus status = backend.WaitForCompletion(
                    timeout, out winError);
                if (status != HidDevice.ReadStatus.Success)
                {
                    operationAvailable = false;
                    operationCompletedSynchronously = false;
                    return status;
                }
            }

            int completedBufferIndex = activeBufferIndex;
            operationAvailable = false;
            operationCompletedSynchronously = false;
            // Windows exposes completion state here, not the exact kernel
            // completion timestamp. If this ahead read finished while the
            // caller mapped the previous report, this is its observation time.
            completionObservedAtQpc = Stopwatch.GetTimestamp();

            // This is the latency-critical ownership handoff. Submit into the
            // alternate pinned buffer before exposing the completed report to
            // parsing, mapping, callbacks, or the virtual transport.
            long rearmStartedAt = Stopwatch.GetTimestamp();
            activeBufferIndex ^= 1;
            if (!Submit(activeBufferIndex, out int rearmWinError))
            {
                deferredStatus = HidDevice.ReadStatus.ReadError;
                deferredWinError = rearmWinError;
            }
            rearmDurationTicks = Stopwatch.GetTimestamp() - rearmStartedAt;

            report = buffers[completedBufferIndex];
            return HidDevice.ReadStatus.Success;
        }

        private bool Submit(int bufferIndex, out int winError)
        {
            bool submitted = backend.TrySubmit(bufferIndex,
                out bool completedSynchronously, out winError);
            if (submitted)
            {
                operationAvailable = true;
                operationCompletedSynchronously = completedSynchronously;
            }
            return submitted;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (operationAvailable && !operationCompletedSynchronously)
            {
                backend.CancelAndDrain();
            }
            operationAvailable = false;
            operationCompletedSynchronously = false;
            backend.Dispose();
        }
    }

    /// <summary>
    /// Owns one reusable OVERLAPPED, one manual-reset completion event, and
    /// two permanently pinned report buffers for a single input generation.
    /// No buffer or kernel wait handle is created in the report loop.
    /// </summary>
    internal sealed unsafe class NativePipelinedInputReadBackend :
        PipelinedInputReportReader.IReadBackend
    {
        private readonly HidDevice owner;
        private readonly SafeFileHandle safeHandle;
        private readonly IntPtr nativeHandle;
        private readonly long transferEpoch;
        private readonly GCHandle[] pinnedBuffers = new GCHandle[2];
        private readonly IntPtr[] bufferPointers = new IntPtr[2];
        private EventWaitHandle completionEvent;
        private IntPtr overlappedStorage;
        private readonly uint reportLength;
        private bool handleReferenceAdded;
        private bool operationPending;
        private bool disposed;

        internal NativePipelinedInputReadBackend(HidDevice owner,
            SafeFileHandle safeHandle, long transferEpoch, byte[][] buffers)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.safeHandle = safeHandle ?? throw new ArgumentNullException(
                nameof(safeHandle));
            this.transferEpoch = transferEpoch;
            if (!owner.IsTransferEpochCurrent(transferEpoch) ||
                safeHandle.IsClosed || safeHandle.IsInvalid)
            {
                throw new InvalidOperationException(
                    "The physical HID input handle is not open.");
            }
            if (buffers == null || buffers.Length != 2 ||
                buffers[0] == null || buffers[1] == null ||
                buffers[0].Length == 0 ||
                buffers[0].Length != buffers[1].Length)
            {
                throw new ArgumentException(
                    "Exactly two equal, non-empty report buffers are required.",
                    nameof(buffers));
            }

            try
            {
                bool added = false;
                safeHandle.DangerousAddRef(ref added);
                handleReferenceAdded = added;
                nativeHandle = safeHandle.DangerousGetHandle();
                reportLength = (uint)buffers[0].Length;

                for (int index = 0; index < pinnedBuffers.Length; index++)
                {
                    pinnedBuffers[index] = GCHandle.Alloc(buffers[index],
                        GCHandleType.Pinned);
                    bufferPointers[index] = pinnedBuffers[index].
                        AddrOfPinnedObject();
                }

                completionEvent = new EventWaitHandle(false,
                    EventResetMode.ManualReset);
                overlappedStorage = Marshal.AllocHGlobal(
                    sizeof(NativeOverlapped));
                PrepareOverlapped();
            }
            catch
            {
                ReleaseOwnedResources();
                throw;
            }
        }

        public bool TrySubmit(int bufferIndex,
            out bool completedSynchronously, out int winError)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if ((uint)bufferIndex >= (uint)bufferPointers.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferIndex));
            }
            if (operationPending)
            {
                throw new InvalidOperationException(
                    "A physical input read is already pending.");
            }
            if (!owner.IsTransferEpochCurrent(transferEpoch) ||
                safeHandle.IsClosed || safeHandle.IsInvalid)
            {
                completedSynchronously = false;
                winError = (int)WIN32_ERROR.ERROR_OPERATION_ABORTED;
                return false;
            }

            completionEvent.Reset();
            PrepareOverlapped();
            bool completed = NativeMethods.ReadFilePinned(nativeHandle,
                (byte*)bufferPointers[bufferIndex], reportLength, null,
                (NativeOverlapped*)overlappedStorage);
            if (completed)
            {
                if (!owner.IsTransferEpochCurrent(transferEpoch) ||
                    safeHandle.IsClosed)
                {
                    completedSynchronously = false;
                    winError = (int)WIN32_ERROR.ERROR_OPERATION_ABORTED;
                    return false;
                }
                completedSynchronously = true;
                winError = 0;
                return true;
            }

            winError = Marshal.GetLastWin32Error();
            if (winError != (int)WIN32_ERROR.ERROR_IO_PENDING)
            {
                completedSynchronously = false;
                return false;
            }

            operationPending = true;
            if (!owner.IsTransferEpochCurrent(transferEpoch) ||
                safeHandle.IsClosed)
            {
                // CloseDevice advances the transfer epoch before its
                // handle-wide cancellation. If the close crossed this exact
                // submit, cancel and drain our own OVERLAPPED as well so
                // neither side depends on a favorable interleaving.
                CancelAndDrain();
                completedSynchronously = false;
                winError = (int)WIN32_ERROR.ERROR_OPERATION_ABORTED;
                return false;
            }
            completedSynchronously = false;
            winError = 0;
            return true;
        }

        public HidDevice.ReadStatus WaitForCompletion(uint timeout,
            out int winError)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!operationPending)
            {
                winError = 0;
                return HidDevice.ReadStatus.Success;
            }

            bool completed = NativeMethods.GetOverlappedResultExPinned(
                nativeHandle, (NativeOverlapped*)overlappedStorage, out _,
                timeout, false);
            if (completed)
            {
                operationPending = false;
                winError = 0;
                return HidDevice.ReadStatus.Success;
            }

            winError = Marshal.GetLastWin32Error();
            if (winError == NativeMethods.WAIT_TIMEOUT)
            {
                CancelAndDrain();
                return HidDevice.ReadStatus.WaitTimedOut;
            }

            // Any non-timeout false result represents completion with an
            // error (most commonly ERROR_OPERATION_ABORTED during shutdown).
            operationPending = false;
            return HidDevice.ReadStatus.ReadError;
        }

        public void CancelAndDrain()
        {
            if (!operationPending || disposed)
            {
                return;
            }

            NativeMethods.CancelIoEx(nativeHandle, overlappedStorage);
            NativeMethods.GetOverlappedResultPinned(nativeHandle,
                (NativeOverlapped*)overlappedStorage, out _, true);
            operationPending = false;
        }

        private void PrepareOverlapped()
        {
            *(NativeOverlapped*)overlappedStorage = new NativeOverlapped
            {
                EventHandle = completionEvent.SafeWaitHandle.
                    DangerousGetHandle(),
            };
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            CancelAndDrain();
            disposed = true;
            ReleaseOwnedResources();
        }

        private void ReleaseOwnedResources()
        {
            if (overlappedStorage != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(overlappedStorage);
                overlappedStorage = IntPtr.Zero;
            }
            completionEvent?.Dispose();
            completionEvent = null;

            for (int index = 0; index < pinnedBuffers.Length; index++)
            {
                if (pinnedBuffers[index].IsAllocated)
                {
                    pinnedBuffers[index].Free();
                }
                bufferPointers[index] = IntPtr.Zero;
            }

            if (handleReferenceAdded)
            {
                safeHandle.DangerousRelease();
                handleReferenceAdded = false;
            }
        }
    }
}
