using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WindowsTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DualSenseBluetoothNativeBackpressureTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FinalNativeCommandRetriesAfterCapacityReturnsWithoutNewPublication(bool nativeCredits)
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits);
        fixture.QueueNative(91, 173);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        fixture.AssertNoRecovery();
        fixture.AssertPending(91, 173);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Retry,
            fixture.Device.ProcessNextPhysicalOutputCommand(),
            "The final admitted transaction must remain serviced without a later command.");
        fixture.AssertNoRecovery();

        Assert.IsTrue(fixture.Pacer.Clear());
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        Assert.AreEqual(0L, Get<long>(fixture.Device, "pendingBluetoothNativeGameRevision"));
        Assert.AreEqual(1, fixture.NativeCommandCount);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.None,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        fixture.AssertNoRecovery();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LaterNativeCommandCannotReplaceBusyExactTransaction(bool nativeCredits)
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits);
        fixture.QueueNative(91, 173);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        fixture.QueueNative(0, 0);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Retry,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        fixture.AssertPending(91, 173);
        Assert.AreEqual(1, Get<int>(fixture.Device, "physicalOutputCommandCount"));
        fixture.AssertNoRecovery();

        Assert.IsTrue(fixture.Pacer.Clear());
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        Assert.AreEqual(2, fixture.NativeCommandCount);
        Assert.AreEqual(0, Get<int>(fixture.Device, "physicalOutputCommandCount"));
        fixture.AssertNoRecovery();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CachedTemplateFailureDoesNotPromoteNativeBusyToRecovery(bool nativeCredits)
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits);
        fixture.QueueNative(37, 83);
        fixture.Device.ProcessNextPhysicalOutputCommand();
        MethodInfo refresh = typeof(DualSenseDevice).GetMethods(InstancePrivate)
            .Single(method => method.Name == "RefreshBluetoothAudioPacerTemplateFromCache" &&
                method.GetParameters()[0].ParameterType == typeof(bool));
        Assert.IsFalse((bool)refresh.Invoke(fixture.Device, new object[] { false, false }));
        fixture.AssertPending(37, 83);
        fixture.AssertNoRecovery();
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void FaultedOrMissingPacerStillRequestsRecoveryAfterEarlierBusy(bool missing, bool nativeCredits)
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits);
        fixture.QueueNative(37, 83);
        fixture.Device.ProcessNextPhysicalOutputCommand();
        fixture.AssertNoRecovery();
        if (missing)
            fixture.InstallPacer(null);
        else
            Set(fixture.Pacer, "lastError", "Synthetic test-only transport fault");

        Assert.IsFalse((bool)Invoke(fixture.Device,
            "TryPublishPendingBluetoothNativeGameTransition"));
        Assert.IsTrue(fixture.RecoveryEntered.WaitOne(2000),
            "A real unavailable/faulted transport was incorrectly classified as capacity pressure.");
        fixture.AssertPending(37, 83);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarmedNativeBusyRetryPreservesStrictAllocationGate(bool positiveControl)
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits: true);
        fixture.QueueNative(37, 83);
        fixture.Device.ProcessNextPhysicalOutputCommand();
        for (int index = 0; index < 2000; index++)
            fixture.Device.ProcessNextPhysicalOutputCommand();
        long allocated;
        DualSenseDevice.PhysicalOutputCommandProcessResult result = default;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 20000; index++)
                result = fixture.Device.ProcessNextPhysicalOutputCommand();
            if (positiveControl) GC.KeepAlive(new byte[128]);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Retry, result);
        if (positiveControl) Assert.IsTrue(allocated >= 128);
        else Assert.AreEqual(0L, allocated);
        fixture.AssertNoRecovery();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OldBusyClassificationCannotSuppressRecoveryAfterOwnershipBoundary(bool replacePacer)
    {
        using Fixture fixture = new();
        using Fixture replacement = new();
        fixture.FillCapacity(nativeCredits: true);
        fixture.QueueNative(37, 83);
        fixture.Device.ProcessNextPhysicalOutputCommand();
        fixture.AssertNoRecovery();
        if (replacePacer)
            fixture.InstallPacer(replacement.Pacer);
        else
            Invoke(fixture.Device, "ClearPendingBluetoothNativeGameTransition");

        Invoke(fixture.Device, "RequestBluetoothOutputRecoveryUnlessNativeAdmissionBusy");
        Assert.IsTrue(fixture.RecoveryEntered.WaitOne(2000),
            "An old transaction/helper capacity classification leaked across an ownership boundary.");
    }

    [TestMethod]
    public void ExactCreditAckWakesActualPhysicalOwnerWithoutInputOrKeepalive()
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits: true);
        fixture.QueueNative(37, 83);
        fixture.Device.ProcessNextPhysicalOutputCommand();
        fixture.AssertPending(37, 83);
        byte[] acknowledgement = fixture.TakeSentNativeAcknowledgement();
        fixture.DrainOutputSignal();
        Set(fixture.Device, "physicalOutputKeepaliveDueQpc", long.MaxValue);
        fixture.StartPhysicalOwner();

        fixture.Acknowledge(acknowledgement);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
            Get<long>(fixture.Device, "pendingBluetoothNativeGameRevision") == 0, 2000),
            "A returned native credit did not wake the real physical owner; no input or keepalive was published.");
        fixture.StopPhysicalOwner();
        Assert.AreEqual(32, fixture.NativeCommandCount);
        fixture.AssertNoRecovery();
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void LateCapacityNotificationCannotWakeRetiredOrStoppedOwner(int boundary)
    {
        using Fixture fixture = new();
        using Fixture replacement = new();
        fixture.FillCapacity(nativeCredits: true);
        byte[] acknowledgement = fixture.TakeSentNativeAcknowledgement();
        Set(fixture.Device, "physicalOutputStopRequested", 0);
        if (boundary == 0) Set(fixture.Device, "physicalOutputStopRequested", 1);
        else if (boundary == 1) Set(fixture.Device, "bluetoothOutputTransportStopping", 1);
        else fixture.InstallPacer(boundary == 2 ? replacement.Pacer : null);
        fixture.DrainOutputSignal();
        fixture.Acknowledge(acknowledgement);
        // Also cover a delegate already copied by the ACK publisher before detach.
        Invoke(fixture.Device, "OnNativeCommandCapacityAvailable", fixture.Pacer);
        Assert.IsFalse(fixture.OutputSignal.WaitOne(0));
    }

    [TestMethod]
    public void CapacityReturnBeforePendingPublicationStillSignalsAndDuplicateAckDoesNot()
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits: true);
        byte[] acknowledgement = fixture.TakeSentNativeAcknowledgement();
        Set(fixture.Device, "physicalOutputStopRequested", 0);
        fixture.DrainOutputSignal();
        Assert.AreEqual(0L, Get<long>(fixture.Device, "pendingBluetoothNativeGameRevision"));
        fixture.Acknowledge(acknowledgement);
        Assert.IsTrue(fixture.OutputSignal.WaitOne(0),
            "Credit returned before the pending flag was published must not be lost.");
        fixture.Acknowledge(acknowledgement);
        Assert.IsFalse(fixture.OutputSignal.WaitOne(0),
            "Duplicate ACK must not create a second capacity notification.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RejectedResetPreservesGenerationAndValidStateLatches(bool exhaustPayloads)
    {
        using Fixture fixture = new();
        fixture.SeedStateLatches();
        Set(fixture.Pacer, "realtimeHapticsGeneration", 17);
        byte[] stateBefore = (byte[])Get<byte[]>(fixture.Pacer, "latestControllerState").Clone();
        byte localBefore = Get<byte>(fixture.Pacer, "latestLocalRumbleLightFast");
        List<DualSenseBluetoothAudioPacerPayloadLease> held = new();
        var pool = Get<DualSenseBluetoothAudioPacerPayloadPool>(fixture.Pacer, "outboundPayloads");
        try
        {
            if (exhaustPayloads)
            {
                lock (Get<object>(fixture.Pacer, "stateLock"))
                    while (pool.TryRent(0, out var lease)) held.Add(lease);
            }
            else fixture.FillNonStateOutboundQueue();

            Assert.IsFalse(fixture.Pacer.ResetControllerStateTransitions());
            Assert.AreEqual(17, Get<int>(fixture.Pacer, "realtimeHapticsGeneration"));
            Assert.IsTrue(Get<bool>(fixture.Pacer, "latestControllerStateAvailable"));
            Assert.IsTrue(Get<bool>(fixture.Pacer, "latestLocalRumbleAvailable"));
            Assert.AreEqual(localBefore, Get<byte>(fixture.Pacer, "latestLocalRumbleLightFast"));
            CollectionAssert.AreEqual(stateBefore, Get<byte[]>(fixture.Pacer, "latestControllerState"));
        }
        finally
        {
            lock (Get<object>(fixture.Pacer, "stateLock"))
                foreach (var lease in held) pool.Return(lease);
        }
    }

    [TestMethod]
    public void SuccessfulResetCancelsUnsentCreditsButKeepsSentCreditUntilExactAck()
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits: true);
        byte[] sentAcknowledgement = fixture.TakeSentNativeAcknowledgement();
        Assert.AreEqual(32, fixture.NativeCreditCount);
        Assert.IsTrue(fixture.Pacer.ResetControllerStateTransitions());
        Assert.AreEqual(2, Get<int>(fixture.Pacer, "realtimeHapticsGeneration"));
        Assert.AreEqual(1, fixture.NativeCreditCount,
            "Reset may cancel unsent leases, not a native command already owned by the helper.");
        Assert.AreEqual(0, fixture.NativeCommandCount);
        fixture.Acknowledge(sentAcknowledgement);
        Assert.AreEqual(0, fixture.NativeCreditCount);
        fixture.Acknowledge(sentAcknowledgement);
        Assert.AreEqual(0, fixture.NativeCreditCount);
    }

    [DataTestMethod]
    [DataRow(int.MaxValue, int.MinValue)]
    [DataRow(-1, 1)]
    public void AcceptedResetPreservesSignedSerialGenerationAndSkipsZero(int previous, int next)
    {
        using Fixture fixture = new();
        fixture.SeedStateLatches();
        Set(fixture.Pacer, "realtimeHapticsGeneration", previous);
        Assert.IsTrue(fixture.Pacer.ResetControllerStateTransitions());
        Assert.AreEqual(next, Get<int>(fixture.Pacer, "realtimeHapticsGeneration"));
        Assert.IsFalse(Get<bool>(fixture.Pacer, "latestControllerStateAvailable"));
        Assert.IsFalse(Get<bool>(fixture.Pacer, "latestLocalRumbleAvailable"));
    }

    [TestMethod]
    public void ActualSenderDrainWakesNativeAdmissionWithoutAnyNativeCreditOutstanding()
    {
        using Fixture fixture = new();
        fixture.FillCapacity(nativeCredits: false);
        fixture.QueueNative(37, 83);
        fixture.Device.ProcessNextPhysicalOutputCommand();
        fixture.AssertPending(37, 83);
        Assert.AreEqual(0, fixture.NativeCreditCount);
        fixture.DrainOutputSignal();
        Set(fixture.Device, "physicalOutputKeepaliveDueQpc", long.MaxValue);
        int capacityNotifications = 0;
        fixture.Pacer.NativeCommandCapacityAvailable += _ => Interlocked.Increment(ref capacityNotifications);
        fixture.StartPhysicalOwner();
        fixture.StartSenderWithSyntheticPipeReader();
        Assert.IsTrue(SpinWait.SpinUntil(() =>
            Volatile.Read(ref fixture.NativeFramesRead) == 1, 2000),
            "Real sender capacity return did not wake the native transaction into the IPC stream.");
        fixture.StopPhysicalOwner();
        Assert.AreEqual(0L, Get<long>(fixture.Device, "pendingBluetoothNativeGameRevision"));
        Assert.AreEqual(1, Volatile.Read(ref capacityNotifications),
            "Ordinary sender drain must only notify the blocked native admission once.");
        fixture.AssertNoRecovery();
    }

    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static FieldInfo Field(object value, string name)
    {
        for (Type type = value.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, InstancePrivate | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(value.GetType().FullName, name);
    }

    private static T Get<T>(object value, string name) => (T)Field(value, name).GetValue(value);
    private static void Set(object value, string name, object fieldValue) =>
        Field(value, name).SetValue(value, fieldValue);
    private static object Invoke(object value, string name, params object[] args) =>
        value.GetType().GetMethod(name, InstancePrivate).Invoke(value, args);

    // Real parent admission and device transactions, with no helper worker,
    // controller handle, HID call, app/broker launch, or connected IPC peer.
    private sealed class Fixture : IDisposable
    {
        internal readonly DualSenseDevice Device;
        internal readonly DualSenseBluetoothAudioPacer Pacer;
        internal readonly ManualResetEvent RecoveryEntered = new(false);
        private readonly ManualResetEvent releaseRecovery = new(false);
        private readonly Process exitedProcess;
        private readonly string commandPipeName;
        private Thread physicalOwner;
        private Exception physicalOwnerFailure;
        private NamedPipeClientStream syntheticReader;
        private Thread pipeReader;
        private Exception pipeReaderFailure;
        internal int NativeFramesRead;

        internal Fixture()
        {
            string prefix = "DS4Windows.Tests.NativeBusy." + Guid.NewGuid().ToString("N");
            commandPipeName = prefix + ".cmd";
            NamedPipeServerStream command = new(commandPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            NamedPipeServerStream response = new(prefix + ".rsp", PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            EventWaitHandle input = new(false, EventResetMode.AutoReset);
            MemoryMappedFile map = MemoryMappedFile.CreateNew(null, 24);
            MemoryMappedViewAccessor view = map.CreateViewAccessor();
            DualSenseRealtimeHapticsSharedRing realtime =
                DualSenseRealtimeHapticsSharedRing.CreateOwner(prefix, 8);
            try
            {
                // The real pacer disposal contract needs an already-exited
                // owned process, as in QueueOnlyPacerFixture. Never give it the
                // test host or an actual app process that it could terminate.
                ProcessStartInfo start = new(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("exit 0");
                exitedProcess = Process.Start(start);
                Assert.IsNotNull(exitedProcess);
                Assert.IsTrue(exitedProcess.WaitForExit(2000));
                Pacer = (DualSenseBluetoothAudioPacer)typeof(DualSenseBluetoothAudioPacer)
                    .GetConstructors(InstancePrivate).Single().Invoke(new object[]
                    { command, response, exitedProcess, input, map, view, realtime, true });
                HidDevice hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
                Device = new DualSenseDevice(hid, "Native capacity test");
                Set(Device, "conType", ConnectionType.BT);
                InstallPacer(Pacer);
                Device.BluetoothOutputRecoveryIterationTestHook = _ =>
                {
                    RecoveryEntered.Set();
                    releaseRecovery.WaitOne();
                    return true;
                };
            }
            catch
            {
                if (Pacer != null) Pacer.Dispose();
                else
                {
                    realtime.Dispose();
                    view.Dispose();
                    map.Dispose();
                    input.Dispose();
                    response.Dispose();
                    command.Dispose();
                    exitedProcess?.Dispose();
                }
                RecoveryEntered.Dispose();
                releaseRecovery.Dispose();
                throw;
            }
        }

        internal void FillCapacity(bool nativeCredits)
        {
            byte[] template = new byte[DualSenseBluetoothAudioPacer.ReportLength];
            template[0] = 0x36;
            template[13] = 0x03;
            int admitted = 0;
            for (int index = 0; index < 256; index++)
            {
                template[15] = (byte)index;
                bool accepted = nativeCredits ?
                    Pacer.UpdateGameStateAndTemplate(template, template, long.MaxValue) :
                    Pacer.UpdateControllerState(template);
                if (!accepted) break;
                admitted++;
            }
            Assert.IsTrue(admitted > 0 && admitted < 256);
            if (nativeCredits) Assert.AreEqual(32, admitted,
                "Exercise the real native outstanding-credit limit, not only the IPC queue.");
            Assert.IsTrue(Pacer.IsRunning);
        }

        internal int NativeCreditCount => Get<DualSenseNativeCommandCredits>(Pacer,
            "nativeCommandCredits").Count;

        internal void SeedStateLatches()
        {
            byte[] template = new byte[DualSenseBluetoothAudioPacer.ReportLength];
            template[0] = 0x36;
            template[13] = 0x03;
            template[15] = 37;
            template[16] = 83;
            Assert.IsTrue(Pacer.UpdateControllerState(template));
            Assert.IsTrue(Pacer.UpdateLocalRumbleState(template));
        }

        internal void FillNonStateOutboundQueue()
        {
            lock (Get<object>(Pacer, "stateLock"))
            {
                var ring = Get<DualSenseBluetoothAudioPacerRing<
                    DualSenseBluetoothAudioPacer.OutboundCommand>>(Pacer, "outboundCommands");
                var pool = Get<DualSenseBluetoothAudioPacerPayloadPool>(Pacer, "outboundPayloads");
                // Remove seeded state commands without clearing their valid
                // latches. Reset must now face a queue it cannot make room in
                // by cancelling an earlier controller-state command.
                while (ring.TryDequeue(out var removedCommand))
                    Invoke(Pacer, "ReleaseOutboundCommandLocked", removedCommand);
                int admitted = 0;
                while (pool.TryRent(sizeof(long) * 2, out var payload))
                {
                    BinaryPrimitives.WriteInt64LittleEndian(payload.Buffer,
                        BitConverter.DoubleToInt64Bits(1.0));
                    var command = new DualSenseBluetoothAudioPacer.OutboundCommand(
                        DualSenseBluetoothAudioPacer.MessageKind.UpdateCadence, payload,
                        sizeof(long) * 2);
                    if (!ring.TryEnqueue(command))
                    {
                        pool.Return(payload);
                        break;
                    }
                    admitted++;
                }
                Assert.IsTrue(admitted > 0);
                Assert.IsTrue(pool.AvailableCount > 0,
                    "The full-queue test must leave payload space for the attempted reset.");
            }
        }

        internal void QueueNative(byte light, byte heavy)
        {
            byte[] report = new byte[48];
            report[0] = 0x02;
            report[1] = 0x03;
            report[3] = light;
            report[4] = heavy;
            Assert.IsTrue(Device.WriteRawOutputReportFromGame(report, 0, report.Length));
        }

        internal AutoResetEvent OutputSignal => Get<AutoResetEvent>(Device, "physicalOutputSignal");

        internal void InstallPacer(DualSenseBluetoothAudioPacer owner)
        {
            lock (Get<object>(Device, "bluetoothAudioPacerLock"))
                Invoke(Device, "SetBluetoothAudioPacerUnderLock", owner);
        }

        internal void DrainOutputSignal()
        {
            while (OutputSignal.WaitOne(0)) { }
        }

        internal byte[] TakeSentNativeAcknowledgement()
        {
            // Model the sender's existing dequeue/lease-return boundary without
            // connecting IPC. Only the real ACK parser below releases credit.
            lock (Get<object>(Pacer, "stateLock"))
            {
                var ring = Get<DualSenseBluetoothAudioPacerRing<
                    DualSenseBluetoothAudioPacer.OutboundCommand>>(Pacer, "outboundCommands");
                Assert.IsTrue(ring.TryDequeue(out var command));
                Assert.AreEqual(DualSenseBluetoothAudioPacer.MessageKind.UpdateGameStateAndTemplate,
                    command.Kind);
                byte[] acknowledgement = new byte[sizeof(long) + sizeof(int) + 1];
                BinaryPrimitives.WriteInt64LittleEndian(acknowledgement, command.ReportId);
                int generation = BinaryPrimitives.ReadInt32LittleEndian(command.Payload.Buffer.AsSpan(
                    DualSenseBluetoothAudioPacer.NativeCommandIdentityOffset + sizeof(long)));
                BinaryPrimitives.WriteInt32LittleEndian(acknowledgement.AsSpan(sizeof(long)), generation);
                acknowledgement[^1] = (byte)DualSenseBluetoothAudioPacer.AcknowledgementDisposition.Presented;
                Invoke(Pacer, "ReleaseOutboundCommandLocked", command);
                return acknowledgement;
            }
        }

        internal void Acknowledge(byte[] acknowledgement) =>
            Invoke(Pacer, "ProcessNativeStateAcknowledgement", acknowledgement);

        internal void StartPhysicalOwner()
        {
            Set(Device, "physicalOutputStopRequested", 0);
            long generation = Get<long>(Device, "physicalOutputGeneration");
            physicalOwner = new Thread(() =>
            {
                try { Invoke(Device, "PhysicalOutputLoop", generation); }
                catch (Exception ex) { physicalOwnerFailure = ex; }
            }) { IsBackground = true, Name = "Native credit wake test" };
            Set(Device, "physicalOutputThread", physicalOwner);
            physicalOwner.Start();
        }

        internal void StartSenderWithSyntheticPipeReader()
        {
            NamedPipeServerStream server = Get<NamedPipeServerStream>(Pacer, "commandPipe");
            var connected = server.WaitForConnectionAsync();
            syntheticReader = new NamedPipeClientStream(".", commandPipeName,
                PipeDirection.In, PipeOptions.Asynchronous);
            syntheticReader.Connect(2000);
            Assert.IsTrue(connected.Wait(2000));
            pipeReader = new Thread(() =>
            {
                try
                {
                    byte[] header = new byte[5];
                    byte[] payload = new byte[4096];
                    while (true)
                    {
                        int kind = syntheticReader.ReadByte();
                        if (kind < 0) return;
                        header[0] = (byte)kind;
                        syntheticReader.ReadExactly(header, 1, 4);
                        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                        if (length < 0 || length > payload.Length)
                            throw new InvalidDataException("Invalid synthetic command length.");
                        syntheticReader.ReadExactly(payload, 0, length);
                        if (kind == (byte)DualSenseBluetoothAudioPacer.MessageKind.UpdateGameStateAndTemplate)
                            Interlocked.Increment(ref NativeFramesRead);
                    }
                }
                catch (Exception ex) { pipeReaderFailure = ex; }
            }) { IsBackground = true, Name = "Synthetic native command pipe reader" };
            pipeReader.Start();
            Get<Thread>(Pacer, "senderThread").Start();
        }

        internal void StopPhysicalOwner()
        {
            if (physicalOwner == null) return;
            Set(Device, "physicalOutputStopRequested", 1);
            OutputSignal.Set();
            if ((physicalOwner.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                Assert.IsTrue(physicalOwner.Join(2000), "Synthetic physical owner did not drain.");
            Assert.IsNull(physicalOwnerFailure);
            physicalOwner = null;
        }

        internal void AssertPending(byte light, byte heavy)
        {
            Assert.IsTrue(Get<long>(Device, "pendingBluetoothNativeGameRevision") > 0);
            byte[] exact = Get<byte[]>(Device, "pendingBluetoothNativeGameExactState");
            Assert.AreEqual(light, exact[15]);
            Assert.AreEqual(heavy, exact[16]);
        }

        internal int NativeCommandCount
        {
            get
            {
                object ring = Get<object>(Pacer, "outboundCommands");
                return Get<DualSenseBluetoothAudioPacer.OutboundCommand[]>(ring, "entries")
                    .Count(command => command.Kind ==
                        DualSenseBluetoothAudioPacer.MessageKind.UpdateGameStateAndTemplate);
            }
        }

        internal void AssertNoRecovery() => Assert.AreEqual(0,
            Get<int>(Device, "bluetoothAudioRecoveryWorkerScheduled"),
            "Healthy native capacity pressure scheduled transport recovery.");

        public void Dispose()
        {
            StopPhysicalOwner();
            releaseRecovery.Set();
            bool idle = Get<ManualResetEvent>(Device,
                "bluetoothAudioRecoveryWorkerIdle").WaitOne(2000);
            InstallPacer(null);
            Pacer.Dispose();
            if (pipeReader != null &&
                (pipeReader.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                Assert.IsTrue(pipeReader.Join(2000), "Synthetic pipe reader did not drain.");
            syntheticReader?.Dispose();
            // A failed join must not dispose a signal still used by a worker.
            if (idle)
            {
                RecoveryEntered.Dispose();
                releaseRecovery.Dispose();
            }
            Assert.IsTrue(idle, "Synthetic recovery owner did not drain.");
            Assert.IsNull(pipeReaderFailure);
        }
    }
}
