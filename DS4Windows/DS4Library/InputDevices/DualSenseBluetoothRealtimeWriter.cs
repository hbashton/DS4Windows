using System;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Sends time-sensitive Bluetooth audio reports without blocking the
    /// controller input thread. A bounded in-flight pool drops a late frame
    /// rather than allowing a backlog to become audible.
    /// </summary>
    internal sealed class DualSenseBluetoothRealtimeWriter : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_FAILED = 0xFFFFFFFF;
        private const uint INFINITE = 0xFFFFFFFF;
        private const int ERROR_IO_PENDING = 997;
        private const int LateSubmissionMilliseconds = 15;
        private const int SlowCompletionMilliseconds = 20;

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

        private readonly object syncRoot = new object();
        private readonly IntPtr deviceHandle;
        private readonly SafeFileHandle sharedDeviceHandle;
        private readonly bool ownsDeviceHandle;
        private bool sharedHandleReferenceAdded;
        private readonly WriteSlot[] slots;
        private int nextSlot;
        private bool disposed;
        private bool nativeResourcesReleased;
        private long completedWrites;
        private long slowCompletionCount;
        private long maximumCompletionTicks;
        private long lateSubmissionCount;
        private long maximumSubmissionGapTicks;
        private long lastSubmissionTimestamp;

        public long CompletedWrites => Interlocked.Read(ref completedWrites);
        public long SlowCompletionCount => Interlocked.Read(ref slowCompletionCount);
        public long LateSubmissionCount => Interlocked.Read(ref lateSubmissionCount);
        public double MaximumCompletionMilliseconds =>
            Interlocked.Read(ref maximumCompletionTicks) * 1000.0 / Stopwatch.Frequency;
        public double MaximumSubmissionGapMilliseconds =>
            Interlocked.Read(ref maximumSubmissionGapTicks) * 1000.0 / Stopwatch.Frequency;

        public void ResetSubmissionClock()
        {
            Interlocked.Exchange(ref lastSubmissionTimestamp, 0);
        }

        private DualSenseBluetoothRealtimeWriter(IntPtr deviceHandle, int reportLength, int slotCount)
        {
            this.deviceHandle = deviceHandle;
            ownsDeviceHandle = true;
            slots = new WriteSlot[slotCount];
            try
            {
                for (int index = 0; index < slots.Length; index++)
                {
                    slots[index] = new WriteSlot(reportLength);
                }
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

        private DualSenseBluetoothRealtimeWriter(SafeFileHandle deviceHandle, int reportLength, int slotCount)
        {
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
                createdSlots = new WriteSlot[slotCount];
                slots = createdSlots;
                for (int index = 0; index < slots.Length; index++)
                {
                    slots[index] = new WriteSlot(reportLength);
                }
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

        public static bool TryCreate(string devicePath, int reportLength,
            out DualSenseBluetoothRealtimeWriter writer, out int error)
        {
            writer = null;
            error = 0;
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                return false;
            }

            IntPtr handle = CreateFileW(devicePath, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                writer = new DualSenseBluetoothRealtimeWriter(handle, reportLength, slotCount: 8);
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
                    Math.Max(1, slotCount));
                return true;
            }
            catch
            {
                error = Marshal.GetLastWin32Error();
                return false;
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
                report.Length > slots[0].Buffer.Length)
            {
                transportFault = true;
                return false;
            }

            lock (syncRoot)
            {
                if (disposed)
                {
                    transportFault = true;
                    return false;
                }

                ObserveCompletedWrites();
                long now = Stopwatch.GetTimestamp();
                WriteSlot slot = slots[nextSlot];
                uint waitResult = WaitForSingleObject(slot.EventHandle, 0);
                if (waitResult != WAIT_OBJECT_0)
                {
                    transportFault = waitResult == WAIT_FAILED;
                    return false;
                }

                if (slot.Pending)
                {
                    if (!GetOverlappedResult(deviceHandle, slot.Overlapped, out _, false))
                    {
                        transportFault = true;
                        return false;
                    }

                    RecordCompletion(now - slot.SubmittedTimestamp);
                    slot.Pending = false;
                    slot.SubmittedTimestamp = 0;
                }

                Buffer.BlockCopy(report, 0, slot.Buffer, 0, report.Length);
                ResetEvent(slot.EventHandle);
                var overlapped = new NativeOverlappedData { EventHandle = slot.EventHandle };
                Marshal.StructureToPtr(overlapped, slot.Overlapped, false);

                bool completed = WriteFile(deviceHandle, slot.BufferHandle.AddrOfPinnedObject(),
                    (uint)report.Length, IntPtr.Zero, slot.Overlapped);
                if (!completed)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ERROR_IO_PENDING)
                    {
                        SetEvent(slot.EventHandle);
                        transportFault = true;
                        return false;
                    }

                    slot.Pending = true;
                    slot.SubmittedTimestamp = now;
                }
                else
                {
                    SetEvent(slot.EventHandle);
                    slot.Pending = false;
                    slot.SubmittedTimestamp = 0;
                    Interlocked.Increment(ref completedWrites);
                }

                RecordSubmissionGap(now);
                nextSlot = (nextSlot + 1) % slots.Length;
                return true;
            }
        }

        /// <summary>
        /// Writes a one-shot control report and waits for its exact OVERLAPPED
        /// completion. DS4 audio gives this writer a dedicated HID handle, so
        /// the wait cannot consume or cancel the input reader's IRP.
        /// </summary>
        public bool TryWriteAndWait(byte[] report, uint timeoutMilliseconds,
            out bool transportFault)
        {
            transportFault = false;
            if (report == null || report.Length == 0 ||
                report.Length > slots[0].Buffer.Length)
            {
                transportFault = true;
                return false;
            }

            lock (syncRoot)
            {
                if (disposed)
                {
                    transportFault = true;
                    return false;
                }

                ObserveCompletedWrites();
                WriteSlot slot = null;
                foreach (WriteSlot candidate in slots)
                {
                    if (WaitForSingleObject(candidate.EventHandle, 0) !=
                        WAIT_OBJECT_0)
                    {
                        continue;
                    }

                    if (candidate.Pending)
                    {
                        if (!GetOverlappedResult(deviceHandle,
                            candidate.Overlapped, out _, false))
                        {
                            transportFault = true;
                            return false;
                        }

                        RecordCompletion(Stopwatch.GetTimestamp() -
                            candidate.SubmittedTimestamp);
                        candidate.Pending = false;
                        candidate.SubmittedTimestamp = 0;
                    }

                    slot = candidate;
                    break;
                }

                if (slot == null)
                {
                    transportFault = true;
                    return false;
                }

                Buffer.BlockCopy(report, 0, slot.Buffer, 0, report.Length);
                ResetEvent(slot.EventHandle);
                Marshal.StructureToPtr(new NativeOverlappedData
                {
                    EventHandle = slot.EventHandle,
                }, slot.Overlapped, false);

                long submitted = Stopwatch.GetTimestamp();
                bool completed = WriteFile(deviceHandle,
                    slot.BufferHandle.AddrOfPinnedObject(), (uint)report.Length,
                    IntPtr.Zero, slot.Overlapped);
                if (!completed)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ERROR_IO_PENDING)
                    {
                        SetEvent(slot.EventHandle);
                        transportFault = true;
                        return false;
                    }

                    slot.Pending = true;
                    slot.SubmittedTimestamp = submitted;
                    if (WaitForSingleObject(slot.EventHandle,
                        timeoutMilliseconds) != WAIT_OBJECT_0)
                    {
                        CancelIoEx(deviceHandle, slot.Overlapped);
                        WaitForSingleObject(slot.EventHandle, INFINITE);
                        slot.Pending = false;
                        slot.SubmittedTimestamp = 0;
                        transportFault = true;
                        return false;
                    }

                    if (!GetOverlappedResult(deviceHandle, slot.Overlapped,
                        out _, false))
                    {
                        slot.Pending = false;
                        slot.SubmittedTimestamp = 0;
                        transportFault = true;
                        return false;
                    }

                    RecordCompletion(Stopwatch.GetTimestamp() - submitted);
                    slot.Pending = false;
                    slot.SubmittedTimestamp = 0;
                }
                else
                {
                    SetEvent(slot.EventHandle);
                    Interlocked.Increment(ref completedWrites);
                }

                RecordSubmissionGap(submitted);
                return true;
            }
        }

        private void ObserveCompletedWrites()
        {
            long now = Stopwatch.GetTimestamp();
            foreach (WriteSlot slot in slots)
            {
                if (!slot.Pending || WaitForSingleObject(slot.EventHandle, 0) != WAIT_OBJECT_0)
                {
                    continue;
                }

                if (!GetOverlappedResult(deviceHandle, slot.Overlapped, out _, false))
                {
                    continue;
                }

                RecordCompletion(now - slot.SubmittedTimestamp);
                slot.Pending = false;
                slot.SubmittedTimestamp = 0;
            }
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
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (!CancelAndWaitForPendingWrites(1000))
                {
                    // Do not free a pinned buffer or OVERLAPPED structure until
                    // Windows has completed its I/O. A Bluetooth disconnect can
                    // delay that completion; freeing it early corrupts native
                    // memory and can terminate CoreCLR later on reconnect.
                    ThreadPool.QueueUserWorkItem(_ => FinishDeferredDispose());
                    return;
                }

                ReleaseNativeResources();
            }
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
                CancelIoEx(deviceHandle, slot.Overlapped);
                if (WaitForSingleObject(slot.EventHandle, timeoutMilliseconds) != WAIT_OBJECT_0)
                {
                    allCompleted = false;
                    continue;
                }

                slot.Pending = false;
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

                // The UI and controller shutdown path must not hang on a bad
                // Bluetooth stack. This background cleanup may wait, but it is
                // the only safe place to release native overlapped buffers.
                if (CancelAndWaitForPendingWrites(INFINITE))
                {
                    ReleaseNativeResources();
                }
            }
        }

        private void ReleaseNativeResources()
        {
            if (nativeResourcesReleased)
            {
                return;
            }

            nativeResourcesReleased = true;
            foreach (WriteSlot slot in slots)
            {
                slot.Dispose();
            }

            if (ownsDeviceHandle)
            {
                CloseHandle(deviceHandle);
            }
            else if (sharedHandleReferenceAdded)
            {
                sharedHandleReferenceAdded = false;
                sharedDeviceHandle.DangerousRelease();
            }
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
        private static extern bool CloseHandle(IntPtr handle);
    }
}
