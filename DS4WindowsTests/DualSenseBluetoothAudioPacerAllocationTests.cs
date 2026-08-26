using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseBluetoothAudioPacerAllocationTests
    {
        private const int ReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;
        private const int QueuePayloadLength = sizeof(long) + sizeof(int) +
            sizeof(long) + ReportLength;
        private static readonly Predicate<
            DualSenseBluetoothAudioPacer.OutboundCommand>
                IsSecondReport = command => command.ReportId == 2;

        [TestMethod]
        public void ActiveSpeakerAndRealtimeHapticsStorageAllocatesZeroAfterWarmup()
        {
            using DualSenseRealtimeHapticsSharedRing producer =
                CreateSharedRing(8,
                    out DualSenseRealtimeHapticsSharedRing consumer);
            using (consumer)
            {
                var payloads = new DualSenseBluetoothAudioPacerPayloadPool(
                    capacity: 8,
                    DualSenseBluetoothAudioPacer.
                        GameStateAndTemplatePayloadLength);
                var commands = new DualSenseBluetoothAudioPacerRing<
                    DualSenseBluetoothAudioPacer.OutboundCommand>(8);
                byte[] report = new byte[ReportLength];
                byte[] presented = new byte[ReportLength];

                bool succeeded = true;
                for (int index = 0; index < 128; index++)
                {
                    succeeded &= RunLoadedCycle(producer, consumer, payloads,
                        commands, report, presented, index);
                }
                Assert.IsTrue(succeeded, "Warmup must exercise every lane.");

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 10_000; index++)
                {
                    succeeded &= RunLoadedCycle(producer, consumer, payloads,
                        commands, report, presented, index + 1_000);
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() -
                    before;

                Assert.IsTrue(succeeded,
                    "The loaded speaker/haptics cycle must retain ownership.");
                Assert.AreEqual(0L, allocated,
                    $"Loaded pacer storage allocated {allocated} bytes.");
                Assert.AreEqual(payloads.Capacity,
                    payloads.AvailableCount);
                Assert.AreEqual(0, commands.Count);
                Assert.AreEqual(0, producer.Count);
            }
        }

        [TestMethod]
        public void InFlightPayloadIsNotReusedUntilWriteCompletes()
        {
            var payloads = new DualSenseBluetoothAudioPacerPayloadPool(4,
                QueuePayloadLength);
            var commands = new DualSenseBluetoothAudioPacerRing<
                DualSenseBluetoothAudioPacer.OutboundCommand>(3);
            byte[] report = new byte[ReportLength];
            EnqueueSpeaker(payloads, commands, report, reportId: 1);
            Assert.IsTrue(commands.TryDequeue(
                out DualSenseBluetoothAudioPacer.OutboundCommand inFlight));

            using var writeStarted = new ManualResetEventSlim(false);
            using var allowWriteCompletion = new ManualResetEventSlim(false);
            Task write = Task.Run(() =>
            {
                writeStarted.Set();
                allowWriteCompletion.Wait();
                payloads.Return(inFlight.Payload);
            });
            Assert.IsTrue(writeStarted.Wait(1000));

            Assert.AreEqual(3, payloads.AvailableCount,
                "A blocked write must retain its exact payload lease.");
            Assert.IsTrue(payloads.TryRent(QueuePayloadLength,
                out DualSenseBluetoothAudioPacerPayloadLease producerLease),
                "A producer must remain independent of the blocked writer.");
            Assert.AreNotSame(inFlight.Payload.Buffer, producerLease.Buffer);
            payloads.Return(producerLease);

            allowWriteCompletion.Set();
            Assert.IsTrue(write.Wait(1000));
            Assert.AreEqual(payloads.Capacity, payloads.AvailableCount);
        }

        [TestMethod]
        public void BlockingPhysicalWriteDoesNotBlockLifecycleAdmission()
        {
            object stateLock = new object();
            var boundary =
                new DualSenseBluetoothAudioPacerPhysicalWriteBoundary(
                    stateLock);
            using var writer = new BlockingPhysicalWriter();
            byte[] report = new byte[ReportLength];

            Task<bool> blockedWrite = Task.Run(() => boundary.TryWrite(writer,
                report, out _));
            Assert.IsTrue(writer.Started.Wait(1000));
            Assert.IsFalse(blockedWrite.IsCompleted);

            int admittedResetGeneration = 0;
            Task lifecycleAdmission = Task.Run(() =>
            {
                lock (stateLock)
                {
                    admittedResetGeneration = 2;
                }
            });
            Assert.IsTrue(lifecycleAdmission.Wait(1000),
                "Pipe/lifecycle admission waited behind physical HID I/O.");
            Assert.AreEqual(2, admittedResetGeneration);
            Assert.IsFalse(blockedWrite.IsCompleted,
                "The fixture must still represent a blocked HID writer.");

            lock (stateLock)
            {
                Assert.ThrowsException<InvalidOperationException>(() =>
                    boundary.TryWrite(writer, report, out _),
                    "The runtime boundary must reject lock-held HID I/O.");
            }

            writer.Release.Set();
            Assert.IsTrue(blockedWrite.Wait(1000));
            Assert.IsTrue(blockedWrite.Result);
        }

        [TestMethod]
        public void FailureReplacementAndResetReturnEveryPayloadExactlyOnce()
        {
            var payloads = new DualSenseBluetoothAudioPacerPayloadPool(7,
                QueuePayloadLength);
            var commands = new DualSenseBluetoothAudioPacerRing<
                DualSenseBluetoothAudioPacer.OutboundCommand>(4);
            var removed = new DualSenseBluetoothAudioPacer.OutboundCommand[4];
            byte[] report = new byte[ReportLength];
            for (long reportId = 1; reportId <= 4; reportId++)
            {
                EnqueueSpeaker(payloads, commands, report, reportId);
            }

            Assert.IsTrue(payloads.TryRent(QueuePayloadLength,
                out DualSenseBluetoothAudioPacerPayloadLease rejectedLease));
            var rejected = new DualSenseBluetoothAudioPacer.OutboundCommand(
                DualSenseBluetoothAudioPacer.MessageKind.QueueReport,
                rejectedLease, QueuePayloadLength, reportId: 5);
            Assert.IsFalse(commands.TryEnqueue(rejected));
            payloads.Return(rejected.Payload);

            Assert.IsTrue(payloads.TryRent(QueuePayloadLength,
                out DualSenseBluetoothAudioPacerPayloadLease resetLease));
            var reset = new DualSenseBluetoothAudioPacer.OutboundCommand(
                DualSenseBluetoothAudioPacer.MessageKind.
                    ResetControllerStateTransitions,
                resetLease, sizeof(int));
            Assert.IsTrue(commands.TryReplaceWhereWithOne(
                command => command.ReportId == 2, reset, removed,
                out int replacedCount));
            Assert.AreEqual(1, replacedCount);
            payloads.Return(removed[0].Payload);
            removed[0] = default;

            int drainedCount = commands.ClearInto(removed);
            Assert.AreEqual(4, drainedCount);
            for (int index = 0; index < drainedCount; index++)
            {
                payloads.Return(removed[index].Payload);
                removed[index] = default;
            }

            Assert.AreEqual(payloads.Capacity, payloads.AvailableCount,
                "Failed admission and lifecycle reset must leak no slots.");
            Assert.AreEqual(0, commands.Count);
        }

        [TestMethod]
        public void SaturatedFailureAndResetStorageAllocatesZeroAfterWarmup()
        {
            var payloads = new DualSenseBluetoothAudioPacerPayloadPool(7,
                QueuePayloadLength);
            var commands = new DualSenseBluetoothAudioPacerRing<
                DualSenseBluetoothAudioPacer.OutboundCommand>(4);
            var removed = new DualSenseBluetoothAudioPacer.OutboundCommand[4];
            byte[] report = new byte[ReportLength];

            bool succeeded = true;
            for (int index = 0; index < 128; index++)
            {
                succeeded &= RunFailureResetCycle(payloads, commands, removed,
                    report);
            }
            Assert.IsTrue(succeeded, "Warmup must exercise saturation/reset.");

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                succeeded &= RunFailureResetCycle(payloads, commands, removed,
                    report);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(succeeded,
                "Every failure/reset cycle must return exact slot ownership.");
            Assert.AreEqual(0L, allocated,
                $"Saturated failure/reset storage allocated {allocated} bytes.");
            Assert.AreEqual(payloads.Capacity, payloads.AvailableCount);
            Assert.AreEqual(0, commands.Count);
        }

        private static bool RunLoadedCycle(
            DualSenseRealtimeHapticsSharedRing producer,
            DualSenseRealtimeHapticsSharedRing consumer,
            DualSenseBluetoothAudioPacerPayloadPool payloads,
            DualSenseBluetoothAudioPacerRing<
                DualSenseBluetoothAudioPacer.OutboundCommand> commands,
            byte[] report, byte[] presented, int value)
        {
            for (int index = 0;
                index < DualSenseBluetoothAudioPacer.
                    RealtimeHapticsDataLength;
                index++)
            {
                report[DualSenseBluetoothAudioPacer.
                    RealtimeHapticsDataOffset + index] =
                    (byte)(value + index);
            }

            if (!producer.Publish(report,
                    DualSenseBluetoothAudioPacer.RealtimeHapticsDataOffset,
                    generation: 1, long.MaxValue, value + 1L) ||
                !consumer.PrepareForPresentation(presented, value + 2L))
            {
                return false;
            }
            consumer.CommitPrepared();

            if (!payloads.TryRent(QueuePayloadLength,
                out DualSenseBluetoothAudioPacerPayloadLease payload))
            {
                return false;
            }

            long reportId = value + 1L;
            DualSenseBluetoothAudioPacer.BuildQueuePayloadInto(reportId,
                epoch: 1, hapticsExpiryQpc: long.MaxValue, report,
                payload.Buffer);
            var command = new DualSenseBluetoothAudioPacer.OutboundCommand(
                DualSenseBluetoothAudioPacer.MessageKind.QueueReport,
                payload, QueuePayloadLength, reportId);
            if (!commands.TryEnqueue(command) ||
                !commands.TryDequeue(out command))
            {
                payloads.Return(payload);
                return false;
            }

            bool valid = command.ReportId == reportId &&
                command.PayloadLength == QueuePayloadLength &&
                ReferenceEquals(command.Payload.Buffer, payload.Buffer);
            payloads.Return(command.Payload);
            return valid;
        }

        private static void EnqueueSpeaker(
            DualSenseBluetoothAudioPacerPayloadPool payloads,
            DualSenseBluetoothAudioPacerRing<
                DualSenseBluetoothAudioPacer.OutboundCommand> commands,
            byte[] report, long reportId)
        {
            Assert.IsTrue(payloads.TryRent(QueuePayloadLength,
                out DualSenseBluetoothAudioPacerPayloadLease payload));
            DualSenseBluetoothAudioPacer.BuildQueuePayloadInto(reportId,
                epoch: 1, hapticsExpiryQpc: long.MaxValue, report,
                payload.Buffer);
            Assert.IsTrue(commands.TryEnqueue(
                new DualSenseBluetoothAudioPacer.OutboundCommand(
                    DualSenseBluetoothAudioPacer.MessageKind.QueueReport,
                    payload, QueuePayloadLength, reportId)));
        }

        private static bool RunFailureResetCycle(
            DualSenseBluetoothAudioPacerPayloadPool payloads,
            DualSenseBluetoothAudioPacerRing<
                DualSenseBluetoothAudioPacer.OutboundCommand> commands,
            DualSenseBluetoothAudioPacer.OutboundCommand[] removed,
            byte[] report)
        {
            for (long reportId = 1; reportId <= 4; reportId++)
            {
                if (!payloads.TryRent(QueuePayloadLength,
                        out DualSenseBluetoothAudioPacerPayloadLease payload))
                {
                    return false;
                }
                DualSenseBluetoothAudioPacer.BuildQueuePayloadInto(reportId,
                    epoch: 1, hapticsExpiryQpc: long.MaxValue, report,
                    payload.Buffer);
                if (!commands.TryEnqueue(
                        new DualSenseBluetoothAudioPacer.OutboundCommand(
                            DualSenseBluetoothAudioPacer.MessageKind.QueueReport,
                            payload, QueuePayloadLength, reportId)))
                {
                    payloads.Return(payload);
                    return false;
                }
            }

            if (!payloads.TryRent(QueuePayloadLength,
                    out DualSenseBluetoothAudioPacerPayloadLease rejectedLease))
            {
                return false;
            }
            var rejected = new DualSenseBluetoothAudioPacer.OutboundCommand(
                DualSenseBluetoothAudioPacer.MessageKind.QueueReport,
                rejectedLease, QueuePayloadLength, reportId: 5);
            bool rejectedAsExpected = !commands.TryEnqueue(rejected);
            payloads.Return(rejected.Payload);
            if (!rejectedAsExpected ||
                !payloads.TryRent(sizeof(int),
                    out DualSenseBluetoothAudioPacerPayloadLease resetLease))
            {
                return false;
            }

            var reset = new DualSenseBluetoothAudioPacer.OutboundCommand(
                DualSenseBluetoothAudioPacer.MessageKind.
                    ResetControllerStateTransitions,
                resetLease, sizeof(int));
            if (!commands.TryReplaceWhereWithOne(IsSecondReport, reset,
                    removed, out int replacedCount) || replacedCount != 1)
            {
                payloads.Return(reset.Payload);
                return false;
            }
            payloads.Return(removed[0].Payload);
            removed[0] = default;

            int drainedCount = commands.ClearInto(removed);
            if (drainedCount != 4)
            {
                return false;
            }
            for (int index = 0; index < drainedCount; index++)
            {
                payloads.Return(removed[index].Payload);
                removed[index] = default;
            }

            return payloads.AvailableCount == payloads.Capacity &&
                commands.Count == 0;
        }

        private static DualSenseRealtimeHapticsSharedRing CreateSharedRing(
            int capacity, out DualSenseRealtimeHapticsSharedRing consumer)
        {
            string prefix = "DS4Windows.Tests.PacerAllocations." +
                Guid.NewGuid().ToString("N");
            DualSenseRealtimeHapticsSharedRing producer =
                DualSenseRealtimeHapticsSharedRing.CreateOwner(prefix,
                    capacity);
            consumer = DualSenseRealtimeHapticsSharedRing.OpenConsumer(
                producer.MapName, producer.SpaceAvailableName,
                producer.StopRequestedName, producer.Capacity);
            return producer;
        }

        private sealed class BlockingPhysicalWriter :
            IDualSenseBluetoothAudioPacerPhysicalWriter, IDisposable
        {
            internal readonly ManualResetEventSlim Started =
                new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim Release =
                new ManualResetEventSlim(false);

            public bool TryWrite(byte[] report, out bool transportFault)
            {
                transportFault = false;
                Started.Set();
                Release.Wait();
                return true;
            }

            public void Dispose()
            {
                Release.Set();
                Started.Dispose();
                Release.Dispose();
            }
        }
    }
}
