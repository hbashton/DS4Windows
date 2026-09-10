using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using DS4Windows.InputDevices;
using DS4WindowsTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DualSenseBluetoothAcknowledgementReceiverTests
{
    private const int MediaLength = sizeof(long) + sizeof(byte) + sizeof(long) + 17 * sizeof(long);
    private const int NativeLength = sizeof(long) + sizeof(int) + sizeof(byte);
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(5)]
    public void FramedNativeAcknowledgementReleasesExactCreditAndSignalsOutsideStateLock(int disposition)
    {
        using Fixture f = new();
        Assert.IsTrue(f.Credits.TryReserve(11, 3));
        byte[] frame = Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged,
            NativePayload(11, 3, (byte)disposition));
        Assert.IsTrue(f.Receive(frame));
        Assert.AreEqual(0, f.Credits.Count);
        Assert.AreEqual(1, f.CapacitySignals);
        Assert.IsFalse(f.SignalHeldStateLock);
        Assert.IsTrue(f.Receive(frame));
        Assert.AreEqual(1, f.CapacitySignals, "A duplicate ACK cannot wake the owner twice.");
    }

    [TestMethod]
    public void FramedStaleGenerationDoesNotReturnNewCredit()
    {
        using Fixture f = new();
        Assert.IsTrue(f.Credits.TryReserve(11, 4));
        Assert.IsTrue(f.Receive(Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged,
            NativePayload(11, 3, 1))));
        Assert.AreEqual(1, f.Credits.Count);
        Assert.AreEqual(0, f.CapacitySignals);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(int.MinValue)]
    [DataRow(-1)]
    public void FramedNativeAcknowledgementPreservesSignedNonzeroGeneration(int generation)
    {
        using Fixture f = new();
        Assert.IsTrue(f.Credits.TryReserve(11, generation));
        Assert.IsTrue(f.Receive(Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged,
            NativePayload(11, unchecked(generation - 1), 1))));
        Assert.AreEqual(1, f.Credits.Count, "A stale generation cannot release the wrapped reservation.");
        Assert.AreEqual(0, f.CapacitySignals);
        byte[] exact = Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged,
            NativePayload(11, generation, 1));
        Assert.IsTrue(f.Receive(exact));
        Assert.AreEqual(0, f.Credits.Count);
        Assert.AreEqual(1, f.CapacitySignals);
        Assert.IsTrue(f.Receive(exact));
        Assert.AreEqual(1, f.CapacitySignals);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void FramedMediaAcknowledgementPreservesExistingDispositionAndMetrics(int disposition)
    {
        using Fixture f = new();
        f.Media.Add(22, 0);
        byte[] payload = MediaPayload(22, (byte)disposition);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(17), 123);
        Assert.IsTrue(f.Receive(Frame(DualSenseBluetoothAudioPacer.MessageKind.ReportAcknowledged, payload)));
        Assert.AreEqual(0, f.Media.Count);
        Assert.AreEqual(1L, f.Pacer.AcknowledgedReports);
        Assert.AreEqual(123L, Get<long>(f.Pacer, "helperInFlightLimitWaitCount"));
        Assert.AreEqual(disposition == 4, f.Pacer.IsFaulted);
    }

    [TestMethod]
    public void MixedNativeAndMediaFramesUseExactLengthsInOneReusedBuffer()
    {
        using Fixture f = new();
        byte[] native = Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged, NativePayload(11, 3, 1));
        byte[] media = Frame(DualSenseBluetoothAudioPacer.MessageKind.ReportAcknowledged, MediaPayload(22, 1));
        byte[] both = Join(media, native);
        Assert.IsTrue(f.Credits.TryReserve(11, 3));
        f.Media.Add(22, 0);
        using MemoryStream stream = new(both, writable: false);
        Assert.IsTrue(f.ReadOne(stream));
        Assert.IsTrue(f.ReadOne(stream));
        Assert.AreEqual(stream.Length, stream.Position);
        Assert.AreEqual(0, f.Credits.Count);
        Assert.AreEqual(0, f.Media.Count);
    }

    [TestMethod]
    public void ErrorDecodingExcludesThePreviousLongerFrameTail()
    {
        using Fixture f = new();
        f.Media.Add(22, 0);
        byte[] media = MediaPayload(22, 1);
        Array.Fill(media, (byte)'Z', 17, media.Length - 17);
        Assert.IsTrue(f.Receive(Frame(DualSenseBluetoothAudioPacer.MessageKind.ReportAcknowledged, media)));
        Assert.IsFalse(f.Receive(Frame(DualSenseBluetoothAudioPacer.MessageKind.Error, Encoding.UTF8.GetBytes("test"))));
        Assert.AreEqual("DualSense audio pacer helper: test", f.Pacer.LastError);
        Assert.IsTrue(f.Ready.IsSet);
        Assert.IsTrue(f.Stopped.IsSet);
    }

    [TestMethod]
    public void EmptyLifecycleMessagesPreserveReadyAndCleanStop()
    {
        using Fixture f = new();
        Assert.IsTrue(f.Receive(Frame(DualSenseBluetoothAudioPacer.MessageKind.Ready, Array.Empty<byte>())));
        Assert.IsTrue(f.Ready.IsSet);
        Set(f.Pacer, "stopping", 1);
        Assert.IsFalse(f.Receive(Frame(DualSenseBluetoothAudioPacer.MessageKind.Stopped, Array.Empty<byte>())));
        Assert.AreEqual(1, Get<int>(f.Pacer, "cleanStopAcknowledged"));
        Assert.IsTrue(f.Stopped.IsSet);
        Assert.IsFalse(f.Pacer.IsFaulted);
    }

    [DataTestMethod]
    [DataRow(0x83, 0)]
    [DataRow(0x83, NativeLength - 1)]
    [DataRow(0x83, NativeLength + 1)]
    [DataRow(0x81, 0)]
    [DataRow(0x81, MediaLength - 1)]
    [DataRow(0x81, MediaLength + 1)]
    [DataRow(0x80, 1)]
    [DataRow(0x82, 1)]
    [DataRow(0x7f, 0)]
    public void MalformedMessageCannotReturnAcceptedCredit(int kind, int length)
    {
        using Fixture f = new();
        Assert.IsTrue(f.Credits.TryReserve(11, 3));
        f.Media.Add(22, 0);
        byte[] payload = new byte[length];
        Assert.ThrowsException<InvalidDataException>(() => f.Receive(Frame(
            (DualSenseBluetoothAudioPacer.MessageKind)kind, payload)));
        Assert.AreEqual(1, f.Credits.Count);
        Assert.AreEqual(1, f.Media.Count);
        Assert.AreEqual(0, f.CapacitySignals);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(255)]
    public void InvalidNativeDispositionCannotReturnCredit(int disposition)
    {
        using Fixture f = new();
        Assert.IsTrue(f.Credits.TryReserve(11, 3));
        byte[] frame = Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged,
            NativePayload(11, 3, (byte)disposition));
        Assert.ThrowsException<InvalidDataException>(() => f.Receive(frame));
        Assert.AreEqual(1, f.Credits.Count);
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(4097)]
    public void FrameLengthOutsideProtocolBoundIsRejected(int length)
    {
        using Fixture f = new();
        byte[] header = new byte[5];
        header[0] = 0x83;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), length);
        Assert.ThrowsException<InvalidDataException>(() => f.Receive(header));
    }

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(7)]
    public void TruncatedHeaderOrBodyIsNotAccepted(int availableBytes)
    {
        using Fixture f = new();
        byte[] frame = Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged, NativePayload(11, 3, 1));
        Array.Resize(ref frame, availableBytes);
        Assert.ThrowsException<EndOfStreamException>(() => f.Receive(frame));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualFramedAckReceiverAllocatesZeroAfterWarmupWithPositiveControl(bool positiveControl)
    {
        using Fixture f = new();
        using MemoryStream stream = new(Join(
            Frame(DualSenseBluetoothAudioPacer.MessageKind.ReportAcknowledged, MediaPayload(22, 1)),
            Frame(DualSenseBluetoothAudioPacer.MessageKind.NativeStateAcknowledged, NativePayload(11, 3, 1))), writable: false);
        bool succeeded = true;
        for (int index = 0; index < 2000; index++) succeeded &= f.Cycle(stream);
        Assert.IsTrue(succeeded);
        long allocated;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 20_000; index++) succeeded &= f.Cycle(stream);
            if (positiveControl) GC.KeepAlive(new byte[128]);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.IsTrue(succeeded);
        Assert.IsFalse(f.SignalHeldStateLock);
        if (positiveControl) Assert.IsTrue(allocated >= 128);
        else Assert.AreEqual(0L, allocated);
    }

    private static byte[] NativePayload(long id, int generation, byte disposition)
    {
        byte[] payload = new byte[NativeLength];
        BinaryPrimitives.WriteInt64LittleEndian(payload, id);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), generation);
        payload[12] = disposition;
        return payload;
    }

    private static byte[] MediaPayload(long id, byte disposition)
    {
        byte[] payload = new byte[MediaLength];
        BinaryPrimitives.WriteInt64LittleEndian(payload, id);
        payload[8] = disposition;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(9), 1000);
        return payload;
    }

    private static byte[] Frame(DualSenseBluetoothAudioPacer.MessageKind kind, byte[] payload)
    {
        byte[] frame = new byte[5 + payload.Length];
        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame, 5);
        return frame;
    }

    private static byte[] Join(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static void Set(object value, string name, object fieldValue) =>
        value.GetType().GetField(name, InstancePrivate)!.SetValue(value, fieldValue);
    private static T Get<T>(object value, string name) =>
        (T)value.GetType().GetField(name, InstancePrivate)!.GetValue(value)!;

    // No pacer constructor, transport, pipe connection, process, or worker is
    // started. Only the actual receiver's state and disposal-owned test signals
    // are supplied. The real shared ring is used solely by error RequestStop.
    private sealed class Fixture : IDisposable
    {
        internal readonly DualSenseBluetoothAudioPacer Pacer =
            (DualSenseBluetoothAudioPacer)RuntimeHelpers.GetUninitializedObject(typeof(DualSenseBluetoothAudioPacer));
        internal readonly object StateLock = new();
        internal readonly DualSenseNativeCommandCredits Credits = new(32);
        internal readonly Dictionary<long, byte> Media = new(64);
        internal readonly ManualResetEventSlim Ready = new(false), Stopped = new(false);
        private readonly DualSenseBluetoothAudioPacer.ControlReportCompletionPool completions;
        private readonly DualSenseRealtimeHapticsSharedRing realtime;
        private readonly byte[] header = new byte[5], payload = new byte[4096];
        private readonly Func<Stream, byte[], byte[], bool> receive;
        internal int CapacitySignals;
        internal bool SignalHeldStateLock;

        internal Fixture()
        {
            completions = new(StateLock, 8);
            realtime = DualSenseRealtimeHapticsSharedRing.CreateOwner(
                "DS4Windows.Tests.AckReceiver." + Guid.NewGuid().ToString("N"), 8);
            Set(Pacer, "stateLock", StateLock);
            Set(Pacer, "nativeCommandCredits", Credits);
            Set(Pacer, "outstandingReports", Media);
            Set(Pacer, "controlReportCompletions", completions);
            Set(Pacer, "readyEvent", Ready);
            Set(Pacer, "stoppedEvent", Stopped);
            Set(Pacer, "realtimeHaptics", realtime);
            Set(Pacer, "outboundCommands", new DualSenseBluetoothAudioPacerRing<DualSenseBluetoothAudioPacer.OutboundCommand>(80));
            Set(Pacer, "removedOutboundCommands", new DualSenseBluetoothAudioPacer.OutboundCommand[80]);
            receive = (Func<Stream, byte[], byte[], bool>)typeof(DualSenseBluetoothAudioPacer)
                .GetMethod("ReceiveResponseFrame", InstancePrivate)!
                .CreateDelegate(typeof(Func<Stream, byte[], byte[], bool>), Pacer);
            Pacer.NativeCommandCapacityAvailable += OnCapacity;
        }

        private void OnCapacity(DualSenseBluetoothAudioPacer sender)
        {
            CapacitySignals++;
            SignalHeldStateLock |= Monitor.IsEntered(StateLock) || !ReferenceEquals(sender, Pacer);
        }

        internal bool Receive(byte[] bytes)
        {
            using MemoryStream stream = new(bytes, writable: false);
            return ReadOne(stream);
        }

        internal bool ReadOne(Stream stream) => receive(stream, header, payload);

        internal bool Cycle(MemoryStream stream)
        {
            bool ok = Credits.TryReserve(11, 3);
            Media.Add(22, 0);
            stream.Position = 0;
            ok &= ReadOne(stream);
            ok &= ReadOne(stream);
            return ok && Credits.Count == 0 && Media.Count == 0;
        }

        public void Dispose()
        {
            Pacer.NativeCommandCapacityAvailable -= OnCapacity;
            completions.Dispose();
            realtime.Dispose();
            Ready.Dispose();
            Stopped.Dispose();
        }
    }
}
