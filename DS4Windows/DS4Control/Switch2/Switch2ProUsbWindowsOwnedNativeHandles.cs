/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows.Switch2;

internal sealed class Switch2ProUsbWindowsRetryableReleaseException :
    InvalidOperationException
{
    internal Switch2ProUsbWindowsRetryableReleaseException(string message) :
        base(message)
    {
    }
}

/// <summary>
/// One exact MI_00 SafeFileHandle and IOCP binding shared by independent input
/// and output operations. There is no secondary HID output handle.
/// </summary>
internal sealed unsafe class Switch2ProUsbWindowsOwnedHidHandle :
    ISwitch2ProUsbWindowsOwnedHidHandle
{
    private readonly object gate = new();
    private readonly SafeFileHandle file;
    private readonly ThreadPoolBoundHandle bound;
    private readonly Switch2ProUsbWindowsReadOperation read;
    private readonly Switch2ProUsbWindowsOwnedFileWriteOperation write;
    private bool disposalInProgress;
    private bool readDisposed;
    private bool writeDisposed;
    private bool boundDisposed;
    private bool fileDisposed;

    internal Switch2ProUsbWindowsOwnedHidHandle(SafeFileHandle file)
    {
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        if (file.IsInvalid || file.IsClosed)
        {
            throw new ArgumentException("Invalid owned HID handle.",
                nameof(file));
        }

        ThreadPoolBoundHandle candidate = null;
        try
        {
            candidate = ThreadPoolBoundHandle.BindHandle(file);
            read = new Switch2ProUsbWindowsReadOperation(file, candidate);
            write = new Switch2ProUsbWindowsOwnedFileWriteOperation(file,
                candidate);
            bound = candidate;
        }
        catch
        {
            if (!Switch2ProUsbWindowsExactHandleRelease.
                    TryDisposeBoundHandleQuiesced(candidate))
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Partial owned-HID IOCP binding cleanup is ambiguous.",
                    candidate);
            }
            throw;
        }
    }

    public bool TryBeginRead(byte[] destination, int offset, int count,
        Action<Switch2ProUsbWindowsReadCompletion> callback,
        out ISwitch2ProUsbWindowsReadOperation operation)
    {
        operation = null;
        if (destination == null || callback == null || offset < 0 ||
            count <= 0 || offset > destination.Length - count)
        {
            return false;
        }
        lock (gate)
        {
            if (disposalInProgress || fileDisposed)
            {
                return false;
            }

            Switch2ProUsbWindowsReadStartOutcome start = read.TryStart(
                destination, offset, count, callback);
            if (start == Switch2ProUsbWindowsReadStartOutcome.
                    RejectedSubmissionFenced)
            {
                throw new InvalidOperationException(
                    "Rejected owned-input storage remains retained.");
            }
            if (start != Switch2ProUsbWindowsReadStartOutcome.Started)
            {
                // TryStart cleans only a newly minted rejected submission. A
                // busy result may belong to an older completed-but-unretired
                // claim and must never release that prior submission.
                return false;
            }
            operation = read;
            return true;
        }
    }

    public bool TryBeginOutputWrite(byte[] source, int offset, int count,
        out ISwitch2ProUsbWindowsOwnedIoOperation operation)
    {
        operation = null;
        if (source == null || offset < 0 || count <= 0 ||
            offset > source.Length - count)
        {
            return false;
        }
        lock (gate)
        {
            if (disposalInProgress || fileDisposed ||
                !write.TryStart(source, offset, count))
            {
                return false;
            }
            operation = write;
            return true;
        }
    }

    public bool HasObservedDeviceDisconnection =>
        read.HasObservedDeviceDisconnection || write.HasObservedDeviceDisconnection;

    public void DisposeQuiesced()
    {
        lock (gate)
        {
            if (fileDisposed)
            {
                return;
            }
            if (disposalInProgress)
            {
                throw new InvalidOperationException(
                    "Owned HID disposal is already in progress.");
            }
            disposalInProgress = true;
        }

        Exception failure = null;
        try
        {
            if (!readDisposed)
            {
                read.DisposeOwnerQuiesced();
                readDisposed = true;
            }
            if (!writeDisposed)
            {
                write.DisposeOwnerQuiesced();
                writeDisposed = true;
            }
            if (!boundDisposed)
            {
                bound.Dispose();
                boundDisposed = true;
            }
            if (!fileDisposed)
            {
                if (!Switch2ProUsbWindowsExactHandleRelease.
                        TryReleaseFileQuiesced(file))
                {
                    throw new InvalidOperationException(
                        "The owned HID file handle was not released.");
                }
                fileDisposed = true;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (gate)
            {
                disposalInProgress = false;
                Monitor.PulseAll(gate);
            }
        }
        if (failure != null)
        {
            throw failure;
        }
    }
}

/// <summary>
/// One exact MI_01 SafeFileHandle/WinUSB lifetime. Bulk OUT and bulk IN own
/// separate preallocated overlapped operations while sharing the one retained
/// interface handle.
/// </summary>
internal sealed unsafe class Switch2ProUsbWindowsOwnedCommandHandle :
    ISwitch2ProUsbWindowsOwnedCommandHandle
{
    private readonly object gate = new();
    private readonly SafeFileHandle file;
    private readonly Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle
        winUsb;
    private readonly ThreadPoolBoundHandle bound;
    private readonly Switch2ProUsbWindowsOwnedWinUsbOperation write;
    private readonly Switch2ProUsbWindowsOwnedWinUsbOperation read;
    private bool disposalInProgress;
    private bool writeDisposed;
    private bool readDisposed;
    private bool boundDisposed;
    private bool winUsbDisposed;
    private bool fileDisposed;
    private string lastDisposeDiagnostic = "never-entered";

    internal Switch2ProUsbWindowsOwnedCommandHandle(SafeFileHandle file,
        Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle winUsb,
        byte bulkOutEndpoint, byte bulkInEndpoint)
    {
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        this.winUsb = winUsb ?? throw new ArgumentNullException(
            nameof(winUsb));
        if (file.IsInvalid || file.IsClosed || winUsb.IsInvalid ||
            winUsb.IsClosed || bulkOutEndpoint !=
                Switch2PhysicalDeviceFactory.CommandBulkOutEndpoint ||
            bulkInEndpoint !=
                Switch2PhysicalDeviceFactory.CommandBulkInEndpoint)
        {
            throw new ArgumentException("Invalid owned command handle.");
        }

        ThreadPoolBoundHandle candidate = null;
        try
        {
            candidate = ThreadPoolBoundHandle.BindHandle(file);
            write = new Switch2ProUsbWindowsOwnedWinUsbOperation(winUsb,
                bulkOutEndpoint, isRead: false, candidate);
            read = new Switch2ProUsbWindowsOwnedWinUsbOperation(winUsb,
                bulkInEndpoint, isRead: true, candidate);
            bound = candidate;
        }
        catch
        {
            if (!Switch2ProUsbWindowsExactHandleRelease.
                    TryDisposeBoundHandleQuiesced(candidate))
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Partial command IOCP binding cleanup is ambiguous.",
                    candidate);
            }
            throw;
        }
    }

    public bool TryBeginBulkWrite(byte[] source, int offset, int count,
        out ISwitch2ProUsbWindowsOwnedIoOperation operation) =>
        TryBegin(write, source, offset, count, out operation);

    public bool TryBeginBulkRead(byte[] destination, int offset, int count,
        out ISwitch2ProUsbWindowsOwnedIoOperation operation) =>
        TryBegin(read, destination, offset, count, out operation);

    public void DisposeQuiesced()
    {
        SetDisposeDiagnostic("entered");
        lock (gate)
        {
            if (fileDisposed)
            {
                return;
            }
            if (disposalInProgress)
            {
                throw new InvalidOperationException(
                    "Owned command disposal is already in progress.");
            }
            disposalInProgress = true;
        }

        Exception failure = null;
        try
        {
            if (!writeDisposed)
            {
                write.DisposeOwnerQuiesced();
                writeDisposed = true;
                SetDisposeDiagnostic("write-disposed");
            }
            if (!readDisposed)
            {
                read.DisposeOwnerQuiesced();
                readDisposed = true;
                SetDisposeDiagnostic("read-disposed");
            }
            if (!boundDisposed)
            {
                bound.Dispose();
                boundDisposed = true;
                SetDisposeDiagnostic("bound-disposed");
            }
            if (!winUsbDisposed)
            {
                if (!winUsb.TryDisposeQuiesced())
                {
                    throw winUsb.IsReleaseAmbiguous ?
                        new Switch2ProUsbWindowsCleanupAmbiguousException(
                            "Owned WinUSB release outcome is ambiguous.") :
                        new Switch2ProUsbWindowsRetryableReleaseException(
                            "The owned WinUSB lifetime was not released.");
                }
                winUsbDisposed = true;
                SetDisposeDiagnostic("winusb-disposed");
            }
            if (!fileDisposed)
            {
                if (!Switch2ProUsbWindowsExactHandleRelease.
                        TryReleaseFileQuiesced(file))
                {
                    throw Switch2ProUsbWindowsExactHandleRelease.
                        IsFileReleaseAmbiguous(file) ?
                        new Switch2ProUsbWindowsCleanupAmbiguousException(
                            "Owned command file release is ambiguous.") :
                        new Switch2ProUsbWindowsRetryableReleaseException(
                            "The owned command file was not released.");
                }
                fileDisposed = true;
                SetDisposeDiagnostic("file-disposed");
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            SetDisposeDiagnostic($"failed:{ex.GetType().Name}");
        }
        finally
        {
            lock (gate)
            {
                disposalInProgress = false;
                Monitor.PulseAll(gate);
            }
        }
        if (failure != null)
        {
            throw failure;
        }
    }

    internal string LastDisposeDiagnostic
    {
        get { lock (gate) { return lastDisposeDiagnostic; } }
    }

    private void SetDisposeDiagnostic(string diagnostic)
    {
        lock (gate)
        {
            lastDisposeDiagnostic = diagnostic;
        }
    }

    private bool TryBegin(Switch2ProUsbWindowsOwnedIoOperationBase target,
        byte[] buffer, int offset, int count,
        out ISwitch2ProUsbWindowsOwnedIoOperation operation)
    {
        operation = null;
        if (buffer == null || offset < 0 || count <= 0 ||
            offset > buffer.Length - count)
        {
            return false;
        }
        lock (gate)
        {
            if (disposalInProgress || fileDisposed ||
                !target.TryStart(buffer, offset, count))
            {
                return false;
            }
            operation = target;
            return true;
        }
    }
}

internal abstract unsafe class Switch2ProUsbWindowsOwnedIoOperationBase :
    ISwitch2ProUsbWindowsOwnedIoOperation
{
    private const int ErrorIoPending = 997;
    private readonly object gate = new();
    private readonly ThreadPoolBoundHandle bound;
    private readonly ManualResetEventSlim nativeCompleted = new(true);
    private byte[] retainedBuffer;
    private GCHandle bufferPin;
    private byte* buffer;
    private int bufferOffset;
    private int bufferCapacity;
    private int submittedCount;
    private PreAllocatedOverlapped preAllocated;
    private NativeOverlapped* overlapped;
    private Switch2ProUsbWindowsOwnedIoCompletion completion;
    private bool submissionActive;
    private bool terminal;
    private bool ownerDisposed;
    private int deviceDisconnected;
    internal bool HasObservedDeviceDisconnection =>
        Volatile.Read(ref deviceDisconnected) != 0;

    protected Switch2ProUsbWindowsOwnedIoOperationBase(
        ThreadPoolBoundHandle bound)
    {
        this.bound = bound ?? throw new ArgumentNullException(nameof(bound));
    }

    internal bool TryStart(byte[] candidate, int offset, int count)
    {
        lock (gate)
        {
            if (ownerDisposed || submissionActive ||
                !TryBindBufferNoLock(candidate, offset, count))
            {
                return false;
            }
            submissionActive = true;
            terminal = false;
            completion = default;
            submittedCount = count;
            nativeCompleted.Reset();
            try
            {
                overlapped = bound.AllocateNativeOverlapped(preAllocated);
                bool started = BeginNative(buffer, checked((uint)count),
                    overlapped);
                int error = started ? 0 : Marshal.GetLastWin32Error();
                if (Switch2ProUsbWindowsReadStatusMap.IsDefiniteDeviceRemoval((uint)error))
                {
                    Volatile.Write(ref deviceDisconnected, 1);
                }
                if (started || error == ErrorIoPending)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                // A thrown native begin has no accepted/not-accepted fact.
                // Retain the exact operation, pin, and OVERLAPPED; the shared
                // composite fence prevents reuse or disposal while a late
                // completion remains possible.
                throw new InvalidOperationException(
                    "Native owned-I/O start outcome is ambiguous.", ex);
            }
            terminal = true;
            if (!TryReleaseNativeNoLock())
            {
                throw new InvalidOperationException(
                    "Failed to retire rejected native submission storage.");
            }
            TrySignalNativeCompletion();
            submissionActive = false;
            terminal = false;
            completion = default;
            submittedCount = 0;
            return false;
        }
    }

    public bool TryCancelExact()
    {
        lock (gate)
        {
            return !ownerDisposed && submissionActive && !terminal &&
                overlapped != null && CancelNative(overlapped);
        }
    }

    public bool TryWaitForNativeQuiescence(int timeoutMilliseconds) =>
        timeoutMilliseconds >= 0 && !ownerDisposed &&
        nativeCompleted.Wait(timeoutMilliseconds);

    public bool TryGetCompletion(
        out Switch2ProUsbWindowsOwnedIoCompletion result)
    {
        lock (gate)
        {
            if (!submissionActive || !terminal || overlapped != null ||
                !nativeCompleted.IsSet)
            {
                result = default;
                return false;
            }
            result = completion;
            return true;
        }
    }

    public void ReleaseSubmissionQuiesced()
    {
        lock (gate)
        {
            if (!submissionActive)
            {
                return;
            }
            if (!terminal || overlapped != null || !nativeCompleted.IsSet)
            {
                throw new InvalidOperationException(
                    "Owned native operation is not quiescent.");
            }
            submissionActive = false;
            terminal = false;
            completion = default;
            submittedCount = 0;
        }
    }

    internal void DisposeOwnerQuiesced()
    {
        lock (gate)
        {
            if (ownerDisposed)
            {
                return;
            }
            if (submissionActive || overlapped != null)
            {
                throw new InvalidOperationException(
                    "Owned native operation is not quiescent.");
            }
            if (preAllocated != null)
            {
                preAllocated.Dispose();
                preAllocated = null;
            }
            if (bufferPin.IsAllocated)
            {
                bufferPin.Free();
            }
            retainedBuffer = null;
            buffer = null;
            nativeCompleted.Dispose();
            ownerDisposed = true;
        }
    }

    protected abstract bool BeginNative(byte* nativeBuffer, uint count,
        NativeOverlapped* nativeOverlapped);

    protected abstract bool CancelNative(
        NativeOverlapped* nativeOverlapped);

    private static void CompletionCallback(uint errorCode, uint numBytes,
        NativeOverlapped* nativeOverlapped)
    {
        Switch2ProUsbWindowsOwnedIoOperationBase operation = null;
        try
        {
            operation = ThreadPoolBoundHandle.GetNativeOverlappedState(
                nativeOverlapped) as
                Switch2ProUsbWindowsOwnedIoOperationBase;
            operation?.Finish(errorCode, numBytes, nativeOverlapped);
        }
        catch
        {
            // Exceptions must never escape an IOCP callback. If state lookup
            // itself failed, the retained operation never publishes
            // quiescence and the owning lease remains fenced. If lookup
            // succeeded, publish a terminal dependency failure if possible.
            operation?.FinishFaulted(nativeOverlapped);
        }
    }

    private void Finish(uint errorCode, uint numBytes,
        NativeOverlapped* completedOverlapped)
    {
        bool exactTransition = false;
        bool nativeStorageReleased = false;
        try
        {
            lock (gate)
            {
                if (terminal || completedOverlapped != overlapped)
                {
                    return;
                }
                terminal = true;
                exactTransition = true;
                if (Switch2ProUsbWindowsReadStatusMap.IsDefiniteDeviceRemoval(errorCode))
                {
                    Volatile.Write(ref deviceDisconnected, 1);
                }
                bool lengthValid = numBytes <= (uint)submittedCount;
                int boundedBytes = lengthValid ? (int)numBytes :
                    submittedCount;
                Switch2ProUsbNativeReadStatus status = lengthValid ?
                    Switch2ProUsbWindowsReadStatusMap.FromNativeError(
                        errorCode) : Switch2ProUsbNativeReadStatus.Failed;
                completion = new Switch2ProUsbWindowsOwnedIoCompletion(
                    boundedBytes, status);
                nativeStorageReleased = TryReleaseNativeNoLock();
                if (!nativeStorageReleased)
                {
                    completion = new Switch2ProUsbWindowsOwnedIoCompletion(
                        0, Switch2ProUsbNativeReadStatus.Failed);
                }
            }
        }
        catch
        {
            FinishFaulted(completedOverlapped);
            return;
        }
        finally
        {
            // No user callback exists on this path. Signalling is the final
            // operation/resource access made by the IOCP path; after Set
            // returns this frame only unwinds. A waiter may therefore reuse or
            // dispose the operation even though the callback's machine frame
            // has not necessarily returned. A failed native-storage release
            // never signals, so the higher lane remains permanently retained
            // and fenced rather than receiving a false quiescence proof.
            if (Switch2ProUsbWindowsOwnedCompletionPublication.
                    CanPublishQuiescence(exactTransition,
                        nativeStorageReleased))
            {
                TrySignalNativeCompletion();
            }
        }
    }

    private void FinishFaulted(NativeOverlapped* completedOverlapped)
    {
        bool exactTransition = false;
        bool nativeStorageReleased = false;
        try
        {
            lock (gate)
            {
                if (terminal || completedOverlapped != overlapped)
                {
                    return;
                }
                terminal = true;
                exactTransition = true;
                completion = new Switch2ProUsbWindowsOwnedIoCompletion(0,
                    Switch2ProUsbNativeReadStatus.Failed);
                nativeStorageReleased = TryReleaseNativeNoLock();
            }
        }
        catch
        {
        }
        finally
        {
            if (Switch2ProUsbWindowsOwnedCompletionPublication.
                    CanPublishQuiescence(exactTransition,
                        nativeStorageReleased))
            {
                TrySignalNativeCompletion();
            }
        }
    }

    private bool TryReleaseNativeNoLock()
    {
        if (overlapped == null)
        {
            return true;
        }
        try
        {
            bound.FreeNativeOverlapped(overlapped);
            overlapped = null;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TrySignalNativeCompletion()
    {
        try
        {
            nativeCompleted.Set();
        }
        catch
        {
        }
    }

    private bool TryBindBufferNoLock(byte[] candidate, int offset, int count)
    {
        if (candidate == null || offset < 0 || count <= 0 ||
            offset > candidate.Length - count)
        {
            return false;
        }
        if (retainedBuffer != null)
        {
            return ReferenceEquals(retainedBuffer, candidate) &&
                bufferOffset == offset && count <= bufferCapacity;
        }

        try
        {
            bufferPin = GCHandle.Alloc(candidate, GCHandleType.Pinned);
            buffer = (byte*)bufferPin.AddrOfPinnedObject() + offset;
            preAllocated = new PreAllocatedOverlapped(CompletionCallback,
                this, null);
            retainedBuffer = candidate;
            bufferOffset = offset;
            bufferCapacity = candidate.Length - offset;
            return true;
        }
        catch
        {
            preAllocated?.Dispose();
            preAllocated = null;
            if (bufferPin.IsAllocated)
            {
                bufferPin.Free();
            }
            buffer = null;
            return false;
        }
    }
}

/// <summary>
/// Pure publication gate shared by the normal and faulted IOCP paths. A stale
/// pointer transition or failed native-storage release must never wake a waiter
/// as though the exact submission were safe to reuse or dispose.
/// </summary>
internal static class Switch2ProUsbWindowsOwnedCompletionPublication
{
    internal static bool CanPublishQuiescence(bool exactPointerTransition,
        bool nativeStorageReleased) => exactPointerTransition &&
        nativeStorageReleased;
}

/// <summary>
/// Observable CloseHandle boundary for retained file lifetimes. SafeHandle's
/// void Dispose surface does not expose a false ReleaseHandle result, so exact
/// owners use this helper and retain the same handle for retry on failure.
/// </summary>
internal static class Switch2ProUsbWindowsExactHandleRelease
{
    private static readonly ConditionalWeakTable<SafeFileHandle,
        FileReleaseState> FileReleaseStates = new();

    internal static bool TryReleaseFileQuiesced(SafeFileHandle file) =>
        TryReleaseFileQuiesced(file, NativeMethods.CloseHandle);

    internal static bool TryReleaseFileQuiesced(SafeFileHandle file,
        Func<IntPtr, bool> closeHandle)
    {
        if (file == null || closeHandle == null)
        {
            return file == null;
        }
        FileReleaseState state = FileReleaseStates.GetValue(file,
            static _ => new FileReleaseState());
        lock (state.Gate)
        {
            if (state.ReleaseAmbiguous)
            {
                return false;
            }
            if (file.IsClosed)
            {
                FileReleaseStates.Remove(file);
                return true;
            }
            if (!state.NativeReleased)
            {
                if (file.IsInvalid)
                {
                    state.NativeReleased = true;
                }
                else
                {
                    try
                    {
                        if (!closeHandle(file.DangerousGetHandle()))
                        {
                            return false;
                        }
                        // Publish this before managed finalization. A later
                        // SetHandleAsInvalid/Dispose exception must never cause
                        // a retry to CloseHandle the already released numeric
                        // value, which Windows may have recycled.
                        state.NativeReleased = true;
                    }
                    catch
                    {
                        // A thrown close has no truthful consumed/not-consumed
                        // fact. Suppress SafeFileHandle's native finalizer
                        // before publishing ambiguity: it must never close the
                        // same numeric value later after Windows may have
                        // recycled it. The process-rooted reservation lifetime
                        // retains this exact handle/state for terminal
                        // attention even though native retry is forbidden.
                        state.ReleaseAmbiguous = true;
                        SuppressNativeRelease(file);
                        return false;
                    }
                }
            }

            try
            {
                file.SetHandleAsInvalid();
                file.Dispose();
                FileReleaseStates.Remove(file);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static bool TryDisposeBoundHandleQuiesced(
        ThreadPoolBoundHandle bound)
    {
        if (bound == null)
        {
            return true;
        }
        try
        {
            bound.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsFileReleaseAmbiguous(SafeFileHandle file)
    {
        if (file == null || !FileReleaseStates.TryGetValue(file,
                out FileReleaseState state))
        {
            return false;
        }
        lock (state.Gate)
        {
            return state.ReleaseAmbiguous;
        }
    }

    internal static bool IsFileNativeReleaseSuppressed(SafeFileHandle file)
    {
        if (file == null || !FileReleaseStates.TryGetValue(file,
                out FileReleaseState state))
        {
            return false;
        }
        lock (state.Gate)
        {
            return state.ReleaseAmbiguous &&
                (file.IsClosed || file.IsInvalid);
        }
    }

    private static void SuppressNativeRelease(SafeFileHandle file)
    {
        try
        {
            file.SetHandleAsInvalid();
        }
        catch
        {
            // A disposed SafeHandle is already outside native finalization.
            // Otherwise the registry's strong terminal root prevents GC from
            // reaching ReleaseHandle while this ambiguity is retained.
        }
        try
        {
            file.Dispose();
        }
        catch
        {
            // Native release is already permanently forbidden by the state
            // above; managed cleanup is best effort only.
        }
    }

    private sealed class FileReleaseState
    {
        internal object Gate { get; } = new();
        internal bool NativeReleased { get; set; }
        internal bool ReleaseAmbiguous { get; set; }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}

internal sealed unsafe class Switch2ProUsbWindowsOwnedFileWriteOperation :
    Switch2ProUsbWindowsOwnedIoOperationBase
{
    private readonly SafeFileHandle file;

    internal Switch2ProUsbWindowsOwnedFileWriteOperation(SafeFileHandle file,
        ThreadPoolBoundHandle bound) : base(bound)
    {
        this.file = file;
    }

    protected override bool BeginNative(byte* nativeBuffer, uint count,
        NativeOverlapped* nativeOverlapped) => NativeMethods.WriteFile(file,
        nativeBuffer, count, null, nativeOverlapped);

    protected override bool CancelNative(
        NativeOverlapped* nativeOverlapped) =>
        NativeMethods.CancelIoEx(file, nativeOverlapped);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteFile(SafeFileHandle file,
            byte* buffer, uint bytesToWrite, uint* bytesWritten,
            NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(SafeFileHandle file,
            NativeOverlapped* overlapped);
    }
}

internal sealed unsafe class Switch2ProUsbWindowsOwnedWinUsbOperation :
    Switch2ProUsbWindowsOwnedIoOperationBase
{
    private readonly Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle
        winUsb;
    private readonly byte pipe;
    private readonly bool isRead;

    internal Switch2ProUsbWindowsOwnedWinUsbOperation(
        Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle winUsb,
        byte pipe, bool isRead, ThreadPoolBoundHandle bound) : base(bound)
    {
        this.winUsb = winUsb;
        this.pipe = pipe;
        this.isRead = isRead;
    }

    protected override bool BeginNative(byte* nativeBuffer, uint count,
        NativeOverlapped* nativeOverlapped) => isRead ?
        NativeMethods.WinUsb_ReadPipe(winUsb.DangerousGetHandle(), pipe,
            nativeBuffer, count, null, nativeOverlapped) :
        NativeMethods.WinUsb_WritePipe(winUsb.DangerousGetHandle(), pipe,
            nativeBuffer, count, null, nativeOverlapped);

    protected override bool CancelNative(
        NativeOverlapped* nativeOverlapped) =>
        NativeMethods.WinUsb_AbortPipe(winUsb.DangerousGetHandle(), pipe);

    private static class NativeMethods
    {
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_WritePipe(IntPtr interfaceHandle,
            byte pipeId, byte* buffer, uint bufferLength,
            uint* lengthTransferred, NativeOverlapped* overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_ReadPipe(IntPtr interfaceHandle,
            byte pipeId, byte* buffer, uint bufferLength,
            uint* lengthTransferred, NativeOverlapped* overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_AbortPipe(IntPtr interfaceHandle,
            byte pipeId);
    }
}
