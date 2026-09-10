using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public class DualSenseBluetoothNativeIdleRetryTests
{
    [TestMethod]
    public void ActualHelperRetriesIdleNativeCommandWhenPhysicalCreditReturns()
    {
        using var fixture = new Fixture();
        fixture.ReceiveNative(91, 173);
        fixture.StartBusy();
        fixture.WaitForBusy();
        fixture.Native.ReturnCredit();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count == 1, 2000),
            "An idle native command must retry without a new media frame or command.");
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.AreEqual(1, reports.Length);
        AssertNative(reports[0], 91, 173);
    }

    [TestMethod]
    public void ActualHelperYieldsToQueuedMediaAfterControlBusy()
    {
        using var fixture = new Fixture();
        fixture.ReceiveNative(91, 173);
        fixture.Native.DuringCompletionProbe = () => fixture.QueueSpeakerReports(8);
        fixture.StartBusy();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.ReportsAhead == 1, 2000),
            "An actual queued media frame retains its one-frame fairness credit.");
        fixture.Native.ReturnCredit();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= 2, 2000));
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.AreEqual((byte)0x36, reports[0][0]);
        Assert.AreEqual(0, reports[0][13] & 3,
            "The first yielded media frame must not spend the waiting native pulse.");
        Assert.IsTrue(reports.Skip(1).Any(report => report[0] == 0x36 &&
            (report[13] & 3) == 3 && report[15] == 91 && report[16] == 173),
            "The exact retained pulse must follow the media fairness boundary.");
    }

    [TestMethod]
    public void ActualHelperClearCancelsOldBusyCommandBeforeNewEpoch()
    {
        using var fixture = new Fixture();
        fixture.ReceiveNative(91, 173);
        fixture.StartBusy();
        fixture.WaitForBusy();
        fixture.Clear();
        fixture.ReceiveNative(17, 41);
        fixture.Native.ReturnCredit();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count == 1, 2000));
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        Assert.AreEqual(1, reports.Length);
        AssertNative(reports[0], 17, 41);
    }

    [TestMethod]
    public void ActualHelperStopJoinsBeforeLatePhysicalCreditReturns()
    {
        using var fixture = new Fixture();
        fixture.ReceiveNative(91, 173);
        fixture.StartBusy();
        fixture.WaitForBusy();
        fixture.Stop();
        fixture.Native.ReturnCredit();
        Assert.AreEqual(0, fixture.Native.Reports.Count,
            "No retired helper remains alive to retry the old command.");
    }

    [TestMethod]
    public void ActualHelperBusyDoesNotEraseConcurrentMicrophoneBoundary()
    {
        using var fixture = new Fixture();
        fixture.ReceiveNative(91, 173);
        // Establish the already accepted microphone-on state without native
        // hardware; the concurrent disable enters the actual receiver below.
        fixture.SetCommittedMicrophoneEnabled();
        fixture.Native.DuringCompletionProbe = fixture.ReceiveMicrophoneDisable;
        fixture.StartBusy();
        fixture.WaitForBusy();
        Assert.AreEqual(2, fixture.ReportsAhead,
            "The old Busy result cannot erase a newer two-media-frame microphone boundary.");
        fixture.Stop();
        fixture.Native.ReturnCredit();
        Assert.AreEqual(0, fixture.Native.Reports.Count);
    }

    private static void AssertNative(byte[] report, byte light, byte heavy)
    {
        Assert.AreEqual((byte)0x31, report[0]);
        Assert.AreEqual(3, report[3] & 3);
        Assert.AreEqual(light, report[5]);
        Assert.AreEqual(heavy, report[6]);
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type HostType = typeof(DualSenseBluetoothAudioPacer).
            GetNestedType("HelperHost", BindingFlags.NonPublic);
        private readonly MemoryMappedFile clock = MemoryMappedFile.CreateNew(null, 4096);
        private readonly MemoryMappedViewAccessor clockView;
        private readonly EventWaitHandle inputArrival = new AutoResetEvent(false);
        private readonly DualSenseRealtimeHapticsSharedRing realtime =
            DualSenseRealtimeHapticsSharedRing.CreateOwner("Local\\DS4NativeIdleRetry-" + Guid.NewGuid().ToString("N"), 4);
        private readonly DualSenseBluetoothRealtimeWriter writer;
        private readonly object host;
        private readonly Thread pacer;
        private bool stopped;
        internal readonly SyntheticNativeIo Native = new();

        internal Fixture()
        {
            clockView = clock.CreateViewAccessor();
            writer = new DualSenseBluetoothRealtimeWriter(DualSenseBluetoothAudioPacer.ReportLength, 1, 1, Native);
            host = Activator.CreateInstance(HostType, BindingFlags.Public | Flags, null,
                new object[] { Stream.Null, Stream.Null, writer, Environment.ProcessId, inputArrival, clockView, realtime }, null);
            pacer = (Thread)HostType.GetField("pacerThread", Flags).GetValue(host);
        }

        internal int ReportsAhead
        {
            get
            {
                object stateLock = HostType.GetField("stateLock", Flags).GetValue(host);
                lock (stateLock) return (int)HostType.GetField("controllerStateReportsAhead", Flags).GetValue(host);
            }
        }

        internal void ReceiveNative(byte light, byte heavy)
        {
            const int stateLength = DualSenseBluetoothPhysicalOutputSequence.ControllerStatePayloadLength;
            byte[] payload = new byte[DualSenseBluetoothAudioPacer.GameStateAndTemplatePayloadLength];
            payload[0] = 3;
            payload[2] = light;
            payload[3] = heavy;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(stateLength), long.MaxValue);
            Template().CopyTo(payload, stateLength + sizeof(long));
            Receive("ReceiveGameStateAndTemplate", payload);
        }

        internal void QueueSpeakerReports(int count)
        {
            for (int index = 0; index < count; index++)
            {
                byte[] payload = new byte[20 + DualSenseBluetoothAudioPacer.ReportLength];
                BinaryPrimitives.WriteInt64LittleEndian(payload, index + 1);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 1);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), long.MaxValue);
                byte[] report = Template();
                report[142] = 0x93;
                report[143] = 200;
                report.CopyTo(payload, 20);
                Receive("ReceiveQueuedReport", payload);
            }
        }

        private static byte[] Template()
        {
            byte[] report = new byte[DualSenseBluetoothAudioPacer.ReportLength];
            report[0] = 0x36;
            report[11] = 0x90;
            report[12] = 63;
            report[76] = 0x92;
            report[77] = 64;
            return report;
        }

        internal void StartBusy()
        {
            Native.HoldInitialSubmission = true;
            byte[] placeholder = Template();
            placeholder[0] = 0x31;
            Assert.IsTrue(writer.TryWrite(placeholder, out bool fault) && !fault);
            Assert.IsTrue(Native.Reports.TryDequeue(out _));
            Native.ArmBusyProbe();
            pacer.Start();
        }

        internal void WaitForBusy()
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                writer.GetOwnershipState(out _, out bool active, out _);
                return Volatile.Read(ref Native.ProbeReturned) != 0 && !active;
            }, 2000), "The real writer must finish its first deliberate Busy attempt.");
            Assert.AreEqual(0, Native.Reports.Count);
        }

        internal void Clear()
        {
            byte[] payload = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(payload, 2);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 2);
            Receive("ReceiveClear", payload);
        }

        internal void SetCommittedMicrophoneEnabled() =>
            HostType.GetField("committedMicrophoneEnabled", Flags).SetValue(host, true);
        internal void ReceiveMicrophoneDisable() => Receive("ReceiveMicrophoneStatus", new byte[] { 0 });
        private void Receive(string method, byte[] payload) =>
            HostType.GetMethod(method, Flags).Invoke(host, new object[] { payload, payload.Length });

        internal void Stop()
        {
            if (stopped) return;
            ((IDisposable)host).Dispose();
            Assert.IsFalse(pacer.IsAlive, "Retain dependencies if the bounded helper join fails.");
            stopped = true;
        }

        public void Dispose()
        {
            Native.ReturnCredit();
            Stop();
            writer.Dispose();
            Assert.IsTrue(writer.WaitForDisposal(2000) && writer.NativeResourcesReleased);
            realtime.Dispose();
            clockView.Dispose();
            clock.Dispose();
            inputArrival.Dispose();
        }
    }

    private sealed class SyntheticNativeIo : IDualSenseBluetoothRealtimeWriterNativeIo
    {
        internal readonly ConcurrentQueue<byte[]> Reports = new();
        internal bool HoldInitialSubmission;
        internal Action DuringCompletionProbe;
        internal int ProbeReturned;
        private IntPtr slotEvent;
        private int probeOnce;

        public bool TrySubmit(IntPtr deviceHandle, IntPtr buffer, uint length, IntPtr overlapped,
            out bool pending, out int error)
        {
            byte[] report = new byte[checked((int)length)];
            Marshal.Copy(buffer, report, 0, report.Length);
            Reports.Enqueue(report);
            pending = HoldInitialSubmission;
            HoldInitialSubmission = false;
            if (pending)
                slotEvent = Marshal.ReadIntPtr(overlapped, IntPtr.Size * 2 + sizeof(uint) * 2);
            error = 0;
            return true;
        }

        internal void ArmBusyProbe()
        {
            Volatile.Write(ref probeOnce, 1);
            Signal(set: true);
        }

        public bool TryGetCompletion(IntPtr deviceHandle, IntPtr overlapped, out uint bytesTransferred)
        {
            bytesTransferred = DualSenseBluetoothAudioPacer.ReportLength;
            if (Interlocked.Exchange(ref probeOnce, 0) != 0)
            {
                // Complete the synthetic predecessor, but deliberately withhold
                // its event credit for the following real TryWrite slot check.
                Signal(set: false);
                DuringCompletionProbe?.Invoke();
                Volatile.Write(ref ProbeReturned, 1);
            }
            return true;
        }

        internal void ReturnCredit() => Signal(set: true);
        private void Signal(bool set)
        {
            if (slotEvent == IntPtr.Zero) return;
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
            signal.SafeWaitHandle = new SafeWaitHandle(slotEvent, ownsHandle: false);
            if (set) signal.Set(); else signal.Reset();
        }
        public void Cancel(IntPtr deviceHandle, IntPtr overlapped) => ReturnCredit();
    }
}
