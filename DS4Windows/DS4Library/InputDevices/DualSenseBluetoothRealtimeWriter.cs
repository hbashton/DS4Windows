using System;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Narrow native submission seam. Tests can stall the physical boundary
    /// without requiring a controller; production still owns the real HID
    /// handle and OVERLAPPED storage in <see cref="DualSenseBluetoothRealtimeWriter"/>.
    /// </summary>
    internal interface IDualSenseBluetoothRealtimeWriterNativeIo
    {
        bool TrySubmit(IntPtr deviceHandle, IntPtr buffer, uint bytesToWrite,
            IntPtr overlapped, out bool pending, out int error);

        bool TryGetCompletion(IntPtr deviceHandle, IntPtr overlapped,
            out uint bytesTransferred);

        void Cancel(IntPtr deviceHandle, IntPtr overlapped);
    }

    /// <summary>
    /// Sends time-sensitive Bluetooth audio reports without blocking the
    /// controller input thread. A bounded in-flight pool exposes completion
    /// backpressure so its caller can retain the oldest logical audio frame.
    /// </summary>
    internal sealed class DualSenseBluetoothRealtimeWriter :
        IDualSenseBluetoothAudioPacerPhysicalWriter, IDisposable
    {
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 258;
        private const uint WAIT_FAILED = 0xFFFFFFFF;
        private const uint INFINITE = 0xFFFFFFFF;
        private const int ERROR_IO_PENDING = 997;
        private const uint CancellationCompletionGraceMilliseconds = 100;
        private const int LateSubmissionMilliseconds = 15;
        private const int SlowCompletionMilliseconds = 20;
        private const int SevereCompletionMilliseconds = 40;
        private const int SlowNativeSubmissionMilliseconds = 2;
        private const int NormalAudioInFlightLimit = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOverlappedData
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        private sealed class WriteSlot
        {
            public readonly byte[] Buffer;
            public GCHandle BufferHandle;
            public readonly IntPtr EventHandle;
            public readonly IntPtr Overlapped;
            public bool Pending;
            public long SubmittedTimestamp;

            public WriteSlot(int reportLength)
            {
                Buffer = new byte[reportLength];
                BufferHandle = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
                EventHandle = CreateEventW(IntPtr.Zero, true, true, null);
                if (EventHandle == IntPtr.Zero)
                {
                    BufferHandle.Free();
                    throw new InvalidOperationException("Could not create a Bluetooth audio write event.");
                }

                Overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
                Marshal.StructureToPtr(default(NativeOverlappedData), Overlapped, false);
            }

            public void Dispose()
            {
                if (Overlapped != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Overlapped);
                }

                if (EventHandle != IntPtr.Zero)
                {
                    CloseHandle(EventHandle);
                }

                if (BufferHandle.IsAllocated)
                {
                    BufferHandle.Free();
                }
            }
        }

        private sealed class NativeIo :
            IDualSenseBluetoothRealtimeWriterNativeIo
        {
            internal static readonly NativeIo Instance = new NativeIo();

            public bool TrySubmit(IntPtr deviceHandle, IntPtr buffer,
                uint bytesToWrite, IntPtr overlapped, out bool pending,
                out int error)
            {
                bool completed = WriteFile(deviceHandle, buffer, bytesToWrite,
                    IntPtr.Zero, overlapped);
                error = completed ? 0 : Marshal.GetLastWin32Error();
                pending = !completed && error == ERROR_IO_PENDING;
                return completed || pending;
            }

            public bool TryGetCompletion(IntPtr deviceHandle,
                IntPtr overlapped, out uint bytesTransferred)
            {
                return GetOverlappedResult(deviceHandle, overlapped,
                    out bytesTransferred, false);
            }

            public void Cancel(IntPtr deviceHandle, IntPtr overlapped)
            {
                CancelIoEx(deviceHandle, overlapped);
            }
        }

        private readonly object syncRoot = new object();
        private readonly ManualResetEvent operationIdle =
            new ManualResetEvent(true);
        private readonly IntPtr deviceHandle;
        private readonly SafeFileHandle sharedDeviceHandle;
        private readonly bool ownsDeviceHandle;
        private bool sharedHandleReferenceAdded;
        private readonly WriteSlot[] slots;
        private readonly IntPtr[] slotEventHandles;
        private readonly IDualSenseBluetoothRealtimeWriterNativeIo nativeIo;
        private readonly int maximumLogicalReportLength;
        private readonly int physicalWriteLength;
        private readonly int audioInFlightLimit;
        private int nextSlot;
        private bool operationInFlight;
        private long lifecycleGeneration = 1;
        private bool disposed;
        private bool nativeResourcesReleased;
        private int deferredDisposeStarted;
        private long completedWrites;
        private long slowCompletionCount;
        private long severeCompletionCount;
        private long maximumCompletionTicks;
        private long lateSubmissionCount;
        private long maximumSubmissionGapTicks;
        private long slowNativeSubmissionCount;
        private long maximumNativeSubmissionTicks;
        private long lastSubmissionTimestamp;
        private long inFlightLimitWaitCount;
        private long inFlightLimitEscapeCount;
        private long maximumInFlightLimitWaitTicks;
        private long maximumAudioPendingBeforeSubmission;
        private long shallowAudioSubmissionCount;
        private long fullAudioSubmissionCount;
        private long shortCompletionCount;
        private long lastCompletionBytes;
        private readonly TaskCompletionSource<bool> disposalCompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public long CompletedWrites => Interlocked.Read(ref completedWrites);
        public long SlowCompletionCount => Interlocked.Read(ref slowCompletionCount);
        public long SevereCompletionCount =>
            Interlocked.Read(ref severeCompletionCount);
        public long LateSubmissionCount => Interlocked.Read(ref lateSubmissionCount);
        public long MaximumCompletionTicks =>
            Interlocked.Read(ref maximumCompletionTicks);
        public long MaximumSubmissionGapTicks =>
            Interlocked.Read(ref maximumSubmissionGapTicks);
        public long SlowNativeSubmissionCount =>
            Interlocked.Read(ref slowNativeSubmissionCount);
        public long MaximumNativeSubmissionTicks =>
            Interlocked.Read(ref maximumNativeSubmissionTicks);
        public double MaximumCompletionMilliseconds =>
            MaximumCompletionTicks * 1000.0 / Stopwatch.Frequency;
        public double MaximumSubmissionGapMilliseconds =>
            MaximumSubmissionGapTicks * 1000.0 / Stopwatch.Frequency;
        public long InFlightLimitWaitCount =>
            Interlocked.Read(ref inFlightLimitWaitCount);
        public long InFlightLimitEscapeCount =>
            Interlocked.Read(ref inFlightLimitEscapeCount);
        public long MaximumInFlightLimitWaitTicks =>
            Interlocked.Read(ref maximumInFlightLimitWaitTicks);
        public long MaximumAudioPendingBeforeSubmission =>
            Interlocked.Read(ref maximumAudioPendingBeforeSubmission);
        public long ShallowAudioSubmissionCount =>
            Interlocked.Read(ref shallowAudioSubmissionCount);
        public long FullAudioSubmissionCount =>
            Interlocked.Read(ref fullAudioSubmissionCount);
        public long ShortCompletionCount =>
            Interlocked.Read(ref shortCompletionCount);
        public long LastCompletionBytes =>
            Interlocked.Read(ref lastCompletionBytes);
        public int PhysicalWriteLength => physicalWriteLength;
        public bool NativeResourcesReleased =>
            disposalCompletion.Task.IsCompletedSuccessfully;

        public void ResetSubmissionClock()
        {
            Interlocked.Exchange(ref lastSubmissionTimestamp, 0);
        }

        private DualSenseBluetoothRealtimeWriter(IntPtr deviceHandle,
            int reportLength, int slotCount, int audioInFlightLimit)
        {
            nativeIo = NativeIo.Instance;
            this.deviceHandle = deviceHandle;
            ownsDeviceHandle = true;
            maximumLogicalReportLength = reportLength;
            physicalWriteLength = ResolvePhysicalWriteLength(deviceHandle,
                reportLength);
            this.audioInFlightLimit = Math.Max(1, audioInFlightLimit);
            slots = new WriteSlot[slotCount];
            try
            {
                for (int index = 0; index < slots.Length; index++)
                {
                    slots[index] = new WriteSlot(physicalWriteLength);
                }
                slotEventHandles = CreateSlotEventHandleSnapshot(slots);
            }
            catch
            {
                foreach (WriteSlot slot in slots)
                {
                    slot?.Dispose();
                }

                throw;
            }
        }

        private DualSenseBluetoothRealtimeWriter(SafeFileHandle deviceHandle,
            int reportLength, int slotCount, int audioInFlightLimit)
        {
            nativeIo = NativeIo.Instance;
            if (deviceHandle == null || deviceHandle.IsInvalid || deviceHandle.IsClosed)
            {
                throw new InvalidOperationException("The active controller HID handle is unavailable.");
            }

            bool addedReference = false;
            WriteSlot[] createdSlots = null;
            deviceHandle.DangerousAddRef(ref addedReference);
            try
            {
                sharedDeviceHandle = deviceHandle;
                sharedHandleReferenceAdded = addedReference;
                this.deviceHandle = deviceHandle.DangerousGetHandle();
                maximumLogicalReportLength = reportLength;
                physicalWriteLength = ResolvePhysicalWriteLength(
                    this.deviceHandle, reportLength);
                this.audioInFlightLimit = Math.Max(1, audioInFlightLimit);
                createdSlots = new WriteSlot[slotCount];
                slots = createdSlots;
                for (int index = 0; index < slots.Length; index++)
                {
                    slots[index] = new WriteSlot(physicalWriteLength);
                }
                slotEventHandles = CreateSlotEventHandleSnapshot(slots);
            }
            catch
            {
                foreach (WriteSlot slot in createdSlots ?? Array.Empty<WriteSlot>())
                {
                    slot?.Dispose();
                }

                if (addedReference)
                {
                    deviceHandle.DangerousRelease();
                }

                throw;
            }
        }

        /// <summary>
        /// Controller-free constructor for deterministic native-boundary tests.
        /// It still uses real event and pinned OVERLAPPED storage, but delegates
        /// HID submission/completion/cancellation to the supplied seam.
        /// </summary>
        internal DualSenseBluetoothRealtimeWriter(int reportLength,
            int slotCount, int audioInFlightLimit,
            IDualSenseBluetoothRealtimeWriterNativeIo nativeIo)
        {
            if (reportLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(reportLength));
            }

            this.nativeIo = nativeIo ??
                throw new ArgumentNullException(nameof(nativeIo));
            deviceHandle = new IntPtr(1);
            maximumLogicalReportLength = reportLength;
            physicalWriteLength = reportLength;
            this.audioInFlightLimit = Math.Max(1, audioInFlightLimit);
            slots = new WriteSlot[Math.Max(1, slotCount)];
            try
            {
                for (int index = 0; index < slots.Length; index++)
                {
                    slots[index] = new WriteSlot(physicalWriteLength);
                }
                slotEventHandles = CreateSlotEventHandleSnapshot(slots);
            }
            catch
            {
                foreach (WriteSlot slot in slots)
                {
                    slot?.Dispose();
                }
                throw;
            }
        }

        private static IntPtr[] CreateSlotEventHandleSnapshot(
            WriteSlot[] source)
        {
            IntPtr[] handles = new IntPtr[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                handles[index] = source[index].EventHandle;
            }
            return handles;
        }

        public static bool TryCreate(string devicePath, int reportLength,
            out DualSenseBluetoothRealtimeWriter writer, out int error)
        {
            return TryCreate(devicePath, reportLength, out writer, out error,
                slotCount: 8, audioInFlightLimit: NormalAudioInFlightLimit);
        }

        public static bool TryCreate(string devicePath, int reportLength,
            out DualSenseBluetoothRealtimeWriter writer, out int error,
            int slotCount, int audioInFlightLimit)
        {
            writer = null;
            error = 0;
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                return false;
            }

            IntPtr handle = CreateFileW(devicePath, GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                writer = new DualSenseBluetoothRealtimeWriter(handle,
                    reportLength, Math.Max(1, slotCount),
                    Math.Max(1, audioInFlightLimit));
                return true;
            }
            catch
            {
                CloseHandle(handle);
                throw;
            }
        }

        /// <summary>
        /// Builds the bounded writer around an already-open exclusive HID
        /// handle. This is the only safe way to stream alongside DS4Windows'
        /// reader: opening a second handle loses to the exclusive share mode.
        /// </summary>
        public static bool TryCreate(SafeFileHandle deviceHandle, int reportLength,
            out DualSenseBluetoothRealtimeWriter writer, out int error,
            int slotCount = 3)
        {
            return TryCreate(deviceHandle, reportLength, out writer,
                out error, slotCount, audioInFlightLimit:
                    NormalAudioInFlightLimit);
        }

        public static bool TryCreate(SafeFileHandle deviceHandle, int reportLength,
            out DualSenseBluetoothRealtimeWriter writer, out int error,
            int slotCount, int audioInFlightLimit)
        {
            writer = null;
            error = 0;
            if (deviceHandle == null || deviceHandle.IsInvalid || deviceHandle.IsClosed)
            {
                error = 6; // ERROR_INVALID_HANDLE
                return false;
            }

            try
            {
                writer = new DualSenseBluetoothRealtimeWriter(deviceHandle, reportLength,
                    Math.Max(1, slotCount),
                    Math.Max(1, audioInFlightLimit));
                return true;
            }
            catch
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
        }

        /// <summary>
        /// Waits for the strict next OVERLAPPED slot to become reusable without
        /// consuming it. Windows does not expose HidBth/L2CAP send credits, so
        /// completion of the oldest slot is the closest lossless credit proxy.
        /// The pacer calls this before removing a logical audio report from its
        /// FIFO; a temporary service drought therefore delays that report
        /// instead of dropping it.
        /// </summary>
        public bool WaitForNextWriteSlot(uint timeoutMilliseconds,
            out bool transportFault)
        {
            if (!TryBeginOperation(out long generation,
                    out transportFault))
            {
                return false;
            }

            try
            {
                if (!ObserveCompletedWrites())
                {
                    transportFault = true;
                    return false;
                }
                WriteSlot slot;
                lock (syncRoot)
                {
                    slot = slots[nextSlot];
                }

                uint waitResult = WaitForEvent(slot.EventHandle, 0);
                if (waitResult == WAIT_TIMEOUT && timeoutMilliseconds != 0)
                {
                    long waitStarted = Stopwatch.GetTimestamp();
                    waitResult = WaitForEvent(slot.EventHandle,
                        timeoutMilliseconds);
                    long waitedTicks = Stopwatch.GetTimestamp() - waitStarted;
                    Interlocked.Increment(ref inFlightLimitWaitCount);
                    UpdateMaximum(ref maximumInFlightLimitWaitTicks,
                        waitedTicks);
                }

                if (waitResult == WAIT_TIMEOUT)
                {
                    Interlocked.Increment(ref inFlightLimitEscapeCount);
                    return false;
                }

                if (waitResult != WAIT_OBJECT_0)
                {
                    transportFault = true;
                    return false;
                }

                bool pending;
                lock (syncRoot)
                {
                    pending = slot.Pending;
                }
                if (!pending)
                {
                    return IsOperationCurrent(generation, out transportFault);
                }

                if (!CompletePendingSlot(slot, Stopwatch.GetTimestamp()))
                {
                    transportFault = true;
                    return false;
                }
                return IsOperationCurrent(generation, out transportFault);
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>
        /// Returns true when the report was accepted. A false result with no
        /// transport fault means the bounded writer is currently saturated.
        /// </summary>
        public bool TryWrite(byte[] report, out bool transportFault)
        {
            transportFault = false;
            if (report == null || report.Length == 0 ||
                report.Length > maximumLogicalReportLength)
            {
                transportFault = true;
                return false;
            }

            if (!TryBeginOperation(out long generation,
                    out transportFault))
            {
                return false;
            }

            try
            {
                if (!ObserveCompletedWrites())
                {
                    transportFault = true;
                    return false;
                }
                long now = Stopwatch.GetTimestamp();
                int slotIndex;
                WriteSlot slot;
                lock (syncRoot)
                {
                    slotIndex = nextSlot;
                    slot = slots[slotIndex];
                }
                uint waitResult = WaitForEvent(slot.EventHandle, 0);
                if (waitResult == WAIT_FAILED)
                {
                    transportFault = true;
                    return false;
                }

                if (waitResult != WAIT_OBJECT_0)
                {
                    // MeasuredTransport advances a strict oldest-slot ring and
                    // CombinedReportReference removes exactly one oldest FIFO entry for each
                    // CAN_SEND_NOW event. Never scan past this slot: doing so
                    // can admit a newer Opus pair around an older pending HID
                    // IRP on stacks whose OVERLAPPED completions are delayed.
                    return false;
                }

                bool pendingBeforeSubmission;
                lock (syncRoot)
                {
                    pendingBeforeSubmission = slot.Pending;
                }
                if (pendingBeforeSubmission)
                {
                    if (!CompletePendingSlot(slot, now))
                    {
                        transportFault = true;
                        return false;
                    }
                }

                RecordAudioPendingBeforeSubmission(CountPendingSlots());
                Array.Clear(slot.Buffer, 0, slot.Buffer.Length);
                Buffer.BlockCopy(report, 0, slot.Buffer, 0, report.Length);
                ResetEvent(slot.EventHandle);
                PrepareNativeOverlapped(slot);

                long nativeSubmissionStarted = Stopwatch.GetTimestamp();
                bool submitted = TrySubmit(
                    slot.BufferHandle.AddrOfPinnedObject(), slot.Overlapped,
                    out bool pending);
                RecordNativeSubmission(Stopwatch.GetTimestamp() -
                    nativeSubmissionStarted);
                if (!submitted)
                {
                    SetEvent(slot.EventHandle);
                    transportFault = true;
                    return false;
                }

                if (pending)
                {
                    lock (syncRoot)
                    {
                        slot.Pending = true;
                        slot.SubmittedTimestamp = now;
                    }
                }
                else
                {
                    if (!ValidateSynchronousCompletion(slot))
                    {
                        SetEvent(slot.EventHandle);
                        transportFault = true;
                        return false;
                    }

                    SetEvent(slot.EventHandle);
                    lock (syncRoot)
                    {
                        slot.Pending = false;
                        slot.SubmittedTimestamp = 0;
                    }
                    Interlocked.Increment(ref completedWrites);
                }

                RecordSubmissionGap(now);
                lock (syncRoot)
                {
                    nextSlot = (slotIndex + 1) % slots.Length;
                }
                return IsOperationCurrent(generation, out transportFault);
            }
            finally
            {
                EndOperation();
            }
        }

        private bool TryBeginOperation(out long generation,
            out bool transportFault)
        {
            lock (syncRoot)
            {
                generation = lifecycleGeneration;
                if (disposed || nativeResourcesReleased)
                {
                    transportFault = true;
                    return false;
                }

                // The helper pacer is the sole physical writer. Fail closed if
                // a future caller violates that ownership instead of queuing
                // behind an in-flight HID operation under this state monitor.
                if (operationInFlight)
                {
                    transportFault = false;
                    return false;
                }

                operationInFlight = true;
                operationIdle.Reset();
                transportFault = false;
                return true;
            }
        }

        private bool IsOperationCurrent(long generation,
            out bool transportFault)
        {
            lock (syncRoot)
            {
                bool current = !disposed && !nativeResourcesReleased &&
                    lifecycleGeneration == generation;
                transportFault = !current;
                return current;
            }
        }

        private void EndOperation()
        {
            bool signalIdle = false;
            lock (syncRoot)
            {
                if (operationInFlight)
                {
                    operationInFlight = false;
                    signalIdle = true;
                }
            }

            if (signalIdle)
            {
                operationIdle.Set();
            }
        }

        internal void GetOwnershipState(out bool isDisposed,
            out bool operationActive, out long generation)
        {
            lock (syncRoot)
            {
                isDisposed = disposed;
                operationActive = operationInFlight;
                generation = lifecycleGeneration;
            }
        }

        internal bool IsStateLockHeldByCurrentThread =>
            Monitor.IsEntered(syncRoot);

        private void VerifyNoStateLockAtNativeBoundary()
        {
            if (Monitor.IsEntered(syncRoot))
            {
                throw new InvalidOperationException(
                    "DualSense realtime HID I/O cannot run under its state lock.");
            }
        }

        private uint WaitForEvent(IntPtr handle, uint milliseconds)
        {
            VerifyNoStateLockAtNativeBoundary();
            return WaitForSingleObject(handle, milliseconds);
        }

        private uint WaitForEvents(IntPtr[] handles, uint milliseconds)
        {
            VerifyNoStateLockAtNativeBoundary();
            return WaitForMultipleObjects((uint)handles.Length, handles,
                false, milliseconds);
        }

        private bool TrySubmit(IntPtr buffer, IntPtr overlapped,
            out bool pending)
        {
            VerifyNoStateLockAtNativeBoundary();
            return nativeIo.TrySubmit(deviceHandle, buffer,
                (uint)physicalWriteLength, overlapped, out pending, out _);
        }

        private bool TryGetCompletion(IntPtr overlapped,
            out uint bytesTransferred)
        {
            VerifyNoStateLockAtNativeBoundary();
            return nativeIo.TryGetCompletion(deviceHandle, overlapped,
                out bytesTransferred);
        }

        private void Cancel(IntPtr overlapped)
        {
            VerifyNoStateLockAtNativeBoundary();
            nativeIo.Cancel(deviceHandle, overlapped);
        }

        private int CountPendingSlots()
        {
            int pendingCount = 0;
            foreach (WriteSlot candidate in slots)
            {
                if (candidate.Pending)
                {
                    pendingCount++;
                }
            }

            return pendingCount;
        }

        private void RecordAudioPendingBeforeSubmission(int pendingCount)
        {
            UpdateMaximum(ref maximumAudioPendingBeforeSubmission,
                pendingCount);
            if (pendingCount <= 1)
            {
                Interlocked.Increment(ref shallowAudioSubmissionCount);
            }
            else if (pendingCount >= audioInFlightLimit - 1)
            {
                Interlocked.Increment(ref fullAudioSubmissionCount);
            }
        }

        /// <summary>
        /// Writes a one-shot control report and waits for its exact OVERLAPPED
        /// completion. This writer owns the exact OVERLAPPED pointer even when
        /// the HID handle is shared, so its wait/cancel cannot consume the input
        /// reader's IRP.
        /// </summary>
        public bool TryWriteAndWait(byte[] report, uint timeoutMilliseconds,
            out bool transportFault)
        {
            transportFault = false;
            if (report == null || report.Length == 0 ||
                report.Length > maximumLogicalReportLength)
            {
                transportFault = true;
                return false;
            }

            if (!TryBeginOperation(out long generation,
                    out transportFault))
            {
                return false;
            }

            try
            {
                long waitStarted = Stopwatch.GetTimestamp();
                if (!ObserveCompletedWrites())
                {
                    transportFault = true;
                    return false;
                }
                // A completion acknowledgement for a control report is also a
                // physical ordering barrier. Do not submit it into any free
                // slot while an older audio IRP can still complete afterwards
                // and restore stale microphone/light/haptics state.
                if (!TryDrainPendingWrites(waitStarted, timeoutMilliseconds,
                    out transportFault))
                {
                    return false;
                }

                WriteSlot slot = null;
                int slotIndex = -1;
                for (int offset = 0; offset < slots.Length; offset++)
                {
                    int firstSlot;
                    lock (syncRoot)
                    {
                        firstSlot = nextSlot;
                    }
                    int candidateIndex = (firstSlot + offset) % slots.Length;
                    WriteSlot candidate = slots[candidateIndex];
                    if (WaitForEvent(candidate.EventHandle, 0) !=
                        WAIT_OBJECT_0)
                    {
                        continue;
                    }

                    bool candidatePending;
                    lock (syncRoot)
                    {
                        candidatePending = candidate.Pending;
                    }
                    if (candidatePending)
                    {
                        if (!CompletePendingSlot(candidate,
                            Stopwatch.GetTimestamp()))
                        {
                            transportFault = true;
                            return false;
                        }
                    }

                    slot = candidate;
                    slotIndex = candidateIndex;
                    break;
                }

                if (slot == null)
                {
                    uint waitResult = WaitForEvents(slotEventHandles,
                        RemainingTimeoutMilliseconds(waitStarted,
                            timeoutMilliseconds));
                    if (waitResult == WAIT_FAILED || waitResult == WAIT_TIMEOUT ||
                        waitResult >= WAIT_OBJECT_0 +
                            (uint)slotEventHandles.Length)
                    {
                        transportFault = true;
                        return false;
                    }

                    slotIndex = (int)(waitResult - WAIT_OBJECT_0);
                    slot = slots[slotIndex];
                    bool slotPending;
                    lock (syncRoot)
                    {
                        slotPending = slot.Pending;
                    }
                    if (slotPending)
                    {
                        if (!CompletePendingSlot(slot,
                            Stopwatch.GetTimestamp()))
                        {
                            transportFault = true;
                            return false;
                        }
                    }
                }

                Array.Clear(slot.Buffer, 0, slot.Buffer.Length);
                Buffer.BlockCopy(report, 0, slot.Buffer, 0, report.Length);
                ResetEvent(slot.EventHandle);
                PrepareNativeOverlapped(slot);

                long submitted = Stopwatch.GetTimestamp();
                long nativeSubmissionStarted = Stopwatch.GetTimestamp();
                bool submittedToHid = TrySubmit(
                    slot.BufferHandle.AddrOfPinnedObject(), slot.Overlapped,
                    out bool pending);
                RecordNativeSubmission(Stopwatch.GetTimestamp() -
                    nativeSubmissionStarted);
                if (!submittedToHid)
                {
                    SetEvent(slot.EventHandle);
                    transportFault = true;
                    return false;
                }

                if (pending)
                {
                    lock (syncRoot)
                    {
                        slot.Pending = true;
                        slot.SubmittedTimestamp = submitted;
                    }
                    if (WaitForEvent(slot.EventHandle,
                        RemainingTimeoutMilliseconds(waitStarted,
                            timeoutMilliseconds)) != WAIT_OBJECT_0)
                    {
                        Cancel(slot.Overlapped);
                        uint cancellationWait = WaitForEvent(
                            slot.EventHandle,
                            CancellationCompletionGraceMilliseconds);
                        if (cancellationWait == WAIT_OBJECT_0)
                        {
                            lock (syncRoot)
                            {
                                slot.Pending = false;
                                slot.SubmittedTimestamp = 0;
                            }
                        }
                        else
                        {
                            // A wedged HID stack must not hold syncRoot (and its
                            // caller) forever. Poison this writer and retain all
                            // pinned/native ownership until the existing
                            // deferred retirement barrier observes completion.
                            MarkDisposedForDeferredDisposal();
                            ScheduleDeferredDispose();
                        }

                        transportFault = true;
                        return false;
                    }

                    if (!CompletePendingSlot(slot,
                        Stopwatch.GetTimestamp()))
                    {
                        transportFault = true;
                        return false;
                    }
                }
                else
                {
                    if (!ValidateSynchronousCompletion(slot))
                    {
                        SetEvent(slot.EventHandle);
                        transportFault = true;
                        return false;
                    }

                    SetEvent(slot.EventHandle);
                    Interlocked.Increment(ref completedWrites);
                }

                RecordSubmissionGap(submitted);
                lock (syncRoot)
                {
                    nextSlot = (slotIndex + 1) % slots.Length;
                }
                return IsOperationCurrent(generation, out transportFault);
            }
            finally
            {
                EndOperation();
            }
        }

        private bool TryDrainPendingWrites(long waitStarted,
            uint timeoutMilliseconds, out bool transportFault)
        {
            transportFault = false;
            foreach (WriteSlot slot in slots)
            {
                if (!slot.Pending)
                {
                    continue;
                }

                uint waitResult = WaitForEvent(slot.EventHandle,
                    RemainingTimeoutMilliseconds(waitStarted,
                        timeoutMilliseconds));
                if (waitResult != WAIT_OBJECT_0)
                {
                    transportFault = true;
                    return false;
                }

                if (!CompletePendingSlot(slot, Stopwatch.GetTimestamp()))
                {
                    transportFault = true;
                    return false;
                }
            }

            return true;
        }

        private static uint RemainingTimeoutMilliseconds(long started,
            uint timeoutMilliseconds)
        {
            if (timeoutMilliseconds == INFINITE)
            {
                return INFINITE;
            }

            long elapsedTicks = Math.Max(0,
                Stopwatch.GetTimestamp() - started);
            ulong elapsedMilliseconds = (ulong)elapsedTicks * 1000UL /
                (ulong)Stopwatch.Frequency;
            return elapsedMilliseconds >= timeoutMilliseconds ? 0 :
                timeoutMilliseconds - (uint)elapsedMilliseconds;
        }

        private static unsafe void PrepareNativeOverlapped(WriteSlot slot)
        {
            // Marshal.StructureToPtr boxes this value on the loaded path.
            // The slot owns fixed unmanaged storage for its whole lifetime, so
            // assign the blittable structure directly without GC activity.
            *(NativeOverlappedData*)slot.Overlapped = new NativeOverlappedData
            {
                EventHandle = slot.EventHandle,
            };
        }

        private bool ObserveCompletedWrites()
        {
            long now = Stopwatch.GetTimestamp();
            foreach (WriteSlot slot in slots)
            {
                if (!slot.Pending || WaitForEvent(slot.EventHandle, 0) !=
                    WAIT_OBJECT_0)
                {
                    continue;
                }

                if (!CompletePendingSlot(slot, now))
                {
                    return false;
                }
            }

            return true;
        }

        private bool CompletePendingSlot(WriteSlot slot,
            long completedTimestamp)
        {
            if (!TryGetCompletion(slot.Overlapped,
                out uint bytesTransferred))
            {
                return false;
            }

            long submittedTimestamp;
            lock (syncRoot)
            {
                submittedTimestamp = slot.SubmittedTimestamp;
                slot.Pending = false;
                slot.SubmittedTimestamp = 0;
            }
            if (!ValidateCompletionLength(bytesTransferred))
            {
                return false;
            }

            RecordCompletion(completedTimestamp - submittedTimestamp);
            return true;
        }

        private bool ValidateSynchronousCompletion(WriteSlot slot)
        {
            return TryGetCompletion(slot.Overlapped,
                       out uint bytesTransferred) &&
                ValidateCompletionLength(bytesTransferred);
        }

        private bool ValidateCompletionLength(uint bytesTransferred)
        {
            Interlocked.Exchange(ref lastCompletionBytes,
                bytesTransferred);
            if (bytesTransferred == (uint)physicalWriteLength)
            {
                return true;
            }

            Interlocked.Increment(ref shortCompletionCount);
            return false;
        }

        private static int ResolvePhysicalWriteLength(IntPtr handle,
            int logicalReportLength)
        {
            IntPtr preparsedData = IntPtr.Zero;
            try
            {
                if (handle != IntPtr.Zero &&
                    handle != new IntPtr(-1) &&
                    DS4Windows.NativeMethods.HidD_GetPreparsedData(handle,
                        ref preparsedData))
                {
                    var capabilities =
                        default(DS4Windows.NativeMethods.HIDP_CAPS);
                    DS4Windows.NativeMethods.HidP_GetCaps(preparsedData,
                        ref capabilities);
                    if (capabilities.OutputReportByteLength > 0)
                    {
                        return Math.Max(logicalReportLength,
                            capabilities.OutputReportByteLength);
                    }
                }
            }
            finally
            {
                if (preparsedData != IntPtr.Zero)
                {
                    DS4Windows.NativeMethods.HidD_FreePreparsedData(
                        preparsedData);
                }
            }

            return logicalReportLength;
        }

        private void RecordSubmissionGap(long now)
        {
            long previous = Interlocked.Exchange(ref lastSubmissionTimestamp, now);
            if (previous == 0)
            {
                return;
            }

            long gap = Math.Max(0, now - previous);
            UpdateMaximum(ref maximumSubmissionGapTicks, gap);
            if (gap > Stopwatch.Frequency * LateSubmissionMilliseconds / 1000)
            {
                Interlocked.Increment(ref lateSubmissionCount);
            }
        }

        private void RecordCompletion(long elapsedTicks)
        {
            Interlocked.Increment(ref completedWrites);
            elapsedTicks = Math.Max(0, elapsedTicks);
            UpdateMaximum(ref maximumCompletionTicks, elapsedTicks);
            if (elapsedTicks > Stopwatch.Frequency * SlowCompletionMilliseconds / 1000)
            {
                Interlocked.Increment(ref slowCompletionCount);
            }
            if (elapsedTicks > Stopwatch.Frequency * SevereCompletionMilliseconds / 1000)
            {
                Interlocked.Increment(ref severeCompletionCount);
            }
        }

        private void RecordNativeSubmission(long elapsedTicks)
        {
            elapsedTicks = Math.Max(0, elapsedTicks);
            UpdateMaximum(ref maximumNativeSubmissionTicks, elapsedTicks);
            if (elapsedTicks > Stopwatch.Frequency *
                SlowNativeSubmissionMilliseconds / 1000)
            {
                Interlocked.Increment(ref slowNativeSubmissionCount);
            }
        }

        private static void UpdateMaximum(ref long target, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref target);
                if (value <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }

        public void Dispose()
        {
            bool ownsDisposal = false;
            lock (syncRoot)
            {
                if (disposed || nativeResourcesReleased)
                {
                    return;
                }

                disposed = true;
                lifecycleGeneration++;
                ownsDisposal = true;
            }

            if (!ownsDisposal)
            {
                return;
            }

            // Publish the poison under the short monitor, then wait outside it.
            // The active owner retains every slot/pin until it releases this
            // reusable barrier; no replacement writer is published until the
            // helper's WaitForDisposal ownership barrier succeeds.
            bool operationStopped = operationIdle.WaitOne(1000);
            if (!operationStopped || !CancelAndWaitForPendingWrites(1000))
            {
                // Do not free a pinned buffer or OVERLAPPED structure until
                // Windows has completed its I/O. A Bluetooth disconnect can
                // delay that completion; freeing it early corrupts native
                // memory and can terminate CoreCLR later on reconnect.
                ScheduleDeferredDispose();
                return;
            }

            ReleaseNativeResources();
        }

        /// <summary>
        /// Waits until every OVERLAPPED write has completed or been cancelled
        /// and this writer has released its pinned buffers, native slots, and
        /// HID-handle ownership/reference. Call <see cref="Dispose"/> first.
        /// A false result is not a transport-ownership barrier: the caller must
        /// not start another writer against this controller yet.
        /// </summary>
        public bool WaitForDisposal(uint timeoutMilliseconds)
        {
            if (timeoutMilliseconds == INFINITE)
            {
                disposalCompletion.Task.GetAwaiter().GetResult();
                return true;
            }

            int boundedTimeout = timeoutMilliseconds > int.MaxValue ?
                int.MaxValue : (int)timeoutMilliseconds;
            return disposalCompletion.Task.Wait(boundedTimeout);
        }

        private bool CancelAndWaitForPendingWrites(uint timeoutMilliseconds)
        {
            bool allCompleted = true;
            foreach (WriteSlot slot in slots)
            {
                if (!slot.Pending)
                {
                    continue;
                }

                // This writer always supplies its own OVERLAPPED pointer, so
                // targeted cancellation is safe even when the HID handle is
                // shared with DS4Windows' input reader.
                Cancel(slot.Overlapped);
                if (WaitForEvent(slot.EventHandle, timeoutMilliseconds) !=
                    WAIT_OBJECT_0)
                {
                    allCompleted = false;
                    continue;
                }

                lock (syncRoot)
                {
                    slot.Pending = false;
                    slot.SubmittedTimestamp = 0;
                }
            }

            return allCompleted;
        }

        private void FinishDeferredDispose()
        {
            lock (syncRoot)
            {
                if (nativeResourcesReleased)
                {
                    return;
                }
            }

            // The UI and controller shutdown path must not hang on a bad
            // Bluetooth stack. This background cleanup may wait, but must not
            // hold syncRoot while doing so: Dispose/health checks still need to
            // observe the poisoned writer without joining the native wait.
            operationIdle.WaitOne();
            bool completed = CancelAndWaitForPendingWrites(INFINITE);
            if (!completed)
            {
                return;
            }

            ReleaseNativeResources();
        }

        private void ReleaseNativeResources()
        {
            bool releaseSharedReference;
            lock (syncRoot)
            {
                if (nativeResourcesReleased)
                {
                    return;
                }

                nativeResourcesReleased = true;
                releaseSharedReference = sharedHandleReferenceAdded;
                sharedHandleReferenceAdded = false;
            }

            // Slot, handle, and completion ownership is destroyed only after
            // the state publication lock is released.
            foreach (WriteSlot slot in slots)
            {
                slot.Dispose();
            }

            if (ownsDeviceHandle)
            {
                CloseHandle(deviceHandle);
            }
            else if (releaseSharedReference)
            {
                sharedDeviceHandle.DangerousRelease();
            }

            disposalCompletion.TrySetResult(true);
            operationIdle.Dispose();
        }

        private void MarkDisposedForDeferredDisposal()
        {
            lock (syncRoot)
            {
                if (!disposed)
                {
                    disposed = true;
                    lifecycleGeneration++;
                }
            }
        }

        private void ScheduleDeferredDispose()
        {
            if (Interlocked.CompareExchange(ref deferredDisposeStarted, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ => FinishDeferredDispose());
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr file, IntPtr buffer, uint bytesToWrite,
            IntPtr bytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped,
            out uint bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset,
            bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(uint count,
            IntPtr[] handles, bool waitAll, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
