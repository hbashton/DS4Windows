using System;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ProUsbHdRumblePhysicalWriterTests
{
    private const ulong DeviceGeneration = 7;
    private const ulong TransportGeneration = 11;

    [TestMethod]
    public void ConstructorRequiresExactProModelAndLifetime()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new Switch2ProUsbHdRumblePhysicalWriter(null,
                DeviceGeneration, TransportGeneration));

        RecordingLease lease = new(DeviceGeneration, TransportGeneration);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbHdRumblePhysicalWriter(lease, 0,
                TransportGeneration));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbHdRumblePhysicalWriter(lease,
                DeviceGeneration, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbHdRumblePhysicalWriter(lease,
                DeviceGeneration, TransportGeneration, initialCounter: 16));

        lease.Authenticated = false;
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2ProUsbHdRumblePhysicalWriter(lease,
                DeviceGeneration, TransportGeneration));
        lease.Authenticated = true;
        lease.ThrowAuthentication = true;
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2ProUsbHdRumblePhysicalWriter(lease,
                DeviceGeneration, TransportGeneration));

        RecordingLease joyConLease = new(DeviceGeneration,
            TransportGeneration)
        {
            ExpectedAuthenticationModel =
                Switch2ControllerModel.JoyCon2Left,
        };
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2ProUsbHdRumblePhysicalWriter(joyConLease,
                DeviceGeneration, TransportGeneration));
    }

    [TestMethod]
    public void EncodesExactSideSeparatedSixtyFourByteProReport()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 10);
        Switch2HdRumblePhysicalSubmission submission = CreateSubmission(
            sequence: 1, seed: 17);

        Switch2HdRumblePhysicalWriteResult result = writer.TryWrite(
            submission);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, lease.Calls);
        Assert.AreEqual(Switch2UsbHdRumbleCodec.ReportLength,
            lease.LastLength);
        Assert.AreEqual(Switch2UsbHdRumbleCodec.ProControllerReportId,
            lease.LastReport[0]);
        Assert.AreEqual((byte)0x5A, lease.LastReport[1]);
        Assert.AreEqual((byte)0x5A, lease.LastReport[17]);
        for (int index = 33; index < lease.LastReport.Length; index++)
        {
            Assert.AreEqual((byte)0, lease.LastReport[index],
                $"Reserved byte {index} must remain zero.");
        }

        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            lease.LastReport, out byte counter,
            out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right,
            out Switch2UsbHdRumbleDecodeFailure failure));
        Assert.AreEqual(Switch2UsbHdRumbleDecodeFailure.None, failure);
        Assert.AreEqual((byte)10, counter);
        Assert.AreEqual(submission.Left, left);
        Assert.AreEqual(submission.Right, right);
        Assert.AreNotEqual(left, right);
        Assert.AreEqual(Switch2ControllerModel.ProController2,
            lease.LastExpectedModel);
        Assert.AreEqual(DeviceGeneration,
            lease.LastExpectedDeviceGeneration);
        Assert.AreEqual(TransportGeneration,
            lease.LastExpectedTransportGeneration);
    }

    [TestMethod]
    public void OwnsCounterAndWrapsModuloSixteen()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 14);

        for (int index = 0; index < 20; index++)
        {
            Assert.IsTrue(writer.TryWrite(CreateSubmission(
                sequence: (ulong)(index + 1), seed: index + 1)).Succeeded);
        }

        for (int index = 0; index < 20; index++)
        {
            Assert.AreEqual((byte)((14 + index) & 0x0F),
                lease.Counters[index]);
        }
    }

    [TestMethod]
    public void StopEncodesOneSideSeparatedNeutralReport()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 4);
        Switch2HdRumblePhysicalSubmission stop =
            Switch2HdRumblePhysicalSubmission.CreateStop(DeviceGeneration,
                TransportGeneration, deliveryEpoch: 31);

        Assert.IsTrue(writer.TryWrite(stop).Succeeded);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            lease.LastReport, out byte counter,
            out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual((byte)4, counter);
        Assert.AreEqual(default(Switch2HdRumbleGroup), left);
        Assert.AreEqual(default(Switch2HdRumbleGroup), right);
    }

    [TestMethod]
    public void ProvenRejectionRetriesExactBytesWithoutAdvancingCounter()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration)
        {
            Result = Rejected(
                Switch2ProUsbHdRumbleTransportWriteFailure.
                    TransportRejected),
        };
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 6);
        Switch2HdRumblePhysicalSubmission submission = CreateSubmission(1,
            22);

        Switch2HdRumblePhysicalWriteResult rejected = writer.TryWrite(
            submission);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.ProvenRejected,
            rejected.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.
            TransportRejected, rejected.Failure);

        lease.Result = Completed();
        Assert.IsTrue(writer.TryWrite(submission).Succeeded);
        Assert.AreEqual(2, lease.Calls);
        CollectionAssert.AreEqual(lease.FirstReport, lease.LastReport);
        Assert.AreEqual((byte)6, lease.Counters[0]);
        Assert.AreEqual((byte)6, lease.Counters[1]);
    }

    [TestMethod]
    public void UncertainWriteRetriesExactBytesWithoutAdvancingCounter()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration)
        {
            Result = Uncertain(
                Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded,
                bytesTransferred: 37),
        };
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 9);
        Switch2HdRumblePhysicalSubmission submission = CreateSubmission(1,
            23);

        Switch2HdRumblePhysicalWriteResult uncertain = writer.TryWrite(
            submission);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.
            OutcomeUncertain, uncertain.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.TransportEnded,
            uncertain.Failure);

        lease.Result = Completed();
        Assert.IsTrue(writer.TryWrite(submission).Succeeded);
        CollectionAssert.AreEqual(lease.FirstReport, lease.LastReport);
        Assert.AreEqual((byte)9, lease.Counters[0]);
        Assert.AreEqual((byte)9, lease.Counters[1]);
    }

    [TestMethod]
    public void NewerResolvingSubmissionUsesNextCounterAndNewBytes()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration)
        {
            Result = Uncertain(
                Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded),
        };
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 3);

        Assert.IsTrue(writer.TryWrite(CreateSubmission(1, 31)).IsUncertain);
        lease.Result = Completed();
        Assert.IsTrue(writer.TryWrite(CreateSubmission(2, 77)).Succeeded);

        Assert.AreEqual((byte)3, lease.Counters[0]);
        Assert.AreEqual((byte)4, lease.Counters[1]);
        CollectionAssert.AreNotEqual(lease.FirstReport, lease.LastReport);
    }

    [TestMethod]
    public void StopAfterUncertainApplyUsesNextCounterAndExactNeutral()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration)
        {
            Result = Uncertain(
                Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded),
        };
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 8);

        Assert.IsTrue(writer.TryWrite(CreateSubmission(1, 35)).IsUncertain);
        lease.Result = Completed();
        Switch2HdRumblePhysicalSubmission stop =
            Switch2HdRumblePhysicalSubmission.CreateStop(DeviceGeneration,
                TransportGeneration, deliveryEpoch: 31);
        Assert.IsTrue(writer.TryWrite(stop).Succeeded);

        Assert.AreEqual((byte)8, lease.Counters[0]);
        Assert.AreEqual((byte)9, lease.Counters[1]);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            lease.LastReport, out byte counter,
            out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual((byte)9, counter);
        Assert.AreEqual(default(Switch2HdRumbleGroup), left);
        Assert.AreEqual(default(Switch2HdRumbleGroup), right);
        for (int index = 33; index < lease.LastReport.Length; index++)
        {
            Assert.AreEqual((byte)0, lease.LastReport[index]);
        }
        CollectionAssert.AreNotEqual(lease.FirstReport, lease.LastReport);
    }

    [TestMethod]
    public void MapsEveryTypedTransportFailureForBothOutcomeClasses()
    {
        var mappings = new[]
        {
            (Switch2ProUsbHdRumbleTransportWriteFailure.InvalidReport,
                Switch2HdRumblePhysicalWriteFailure.InvalidSubmission),
            (Switch2ProUsbHdRumbleTransportWriteFailure.StaleLifetime,
                Switch2HdRumblePhysicalWriteFailure.StaleLifetime),
            (Switch2ProUsbHdRumbleTransportWriteFailure.Busy,
                Switch2HdRumblePhysicalWriteFailure.Busy),
            (Switch2ProUsbHdRumbleTransportWriteFailure.TransportRejected,
                Switch2HdRumblePhysicalWriteFailure.TransportRejected),
            (Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded,
                Switch2HdRumblePhysicalWriteFailure.TransportEnded),
            (Switch2ProUsbHdRumbleTransportWriteFailure.DependencyThrew,
                Switch2HdRumblePhysicalWriteFailure.DependencyThrew),
        };

        foreach (var mapping in mappings)
        {
            RecordingLease rejectedLease = new(DeviceGeneration,
                TransportGeneration)
            {
                Result = Rejected(mapping.Item1),
            };
            Switch2ProUsbHdRumblePhysicalWriter rejectedWriter = new(
                rejectedLease, DeviceGeneration, TransportGeneration);
            Switch2HdRumblePhysicalWriteResult rejected =
                rejectedWriter.TryWrite(CreateSubmission(1, 41));
            Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.
                ProvenRejected, rejected.Outcome);
            Assert.AreEqual(mapping.Item2, rejected.Failure);

            RecordingLease uncertainLease = new(DeviceGeneration,
                TransportGeneration)
            {
                Result = Uncertain(mapping.Item1),
            };
            Switch2ProUsbHdRumblePhysicalWriter uncertainWriter = new(
                uncertainLease, DeviceGeneration, TransportGeneration);
            Switch2HdRumblePhysicalWriteResult uncertain =
                uncertainWriter.TryWrite(CreateSubmission(1, 42));
            Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.
                OutcomeUncertain, uncertain.Outcome);
            Assert.AreEqual(mapping.Item2, uncertain.Failure);
        }
    }

    [TestMethod]
    public void MalformedOrForeignCompletionNeverBecomesSuccessEvidence()
    {
        AssertMalformedResult(default,
            Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
        AssertMalformedResult(
            Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, bytesTransferred: 63),
            Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
        AssertMalformedResult(
            Switch2ProUsbHdRumbleTransportWriteResult.Reject(
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration,
                Switch2ProUsbHdRumbleTransportWriteFailure.None),
            Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
        AssertMalformedResult(
            Switch2ProUsbHdRumbleTransportWriteResult.Reject(
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration,
                Switch2ProUsbHdRumbleTransportWriteFailure.
                    TransportRejected,
                bytesTransferred: 1),
            Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
        AssertMalformedResult(
            Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
                TransportGeneration),
            Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
        AssertMalformedResult(
            Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                Switch2ControllerModel.ProController2,
                DeviceGeneration + 1, TransportGeneration),
            Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
        AssertMalformedResult(
            Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration + 1),
            Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
    }

    [TestMethod]
    public void WriteExceptionIsUncertainAndRetainsExactReport()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration)
        {
            ThrowWrite = true,
        };
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 12);
        Switch2HdRumblePhysicalSubmission submission = CreateSubmission(1,
            51);

        Switch2HdRumblePhysicalWriteResult first = writer.TryWrite(
            submission);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.
            OutcomeUncertain, first.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
            first.Failure);

        lease.ThrowWrite = false;
        Assert.IsTrue(writer.TryWrite(submission).Succeeded);
        CollectionAssert.AreEqual(lease.FirstReport, lease.LastReport);
        Assert.AreEqual((byte)12, lease.Counters[0]);
        Assert.AreEqual((byte)12, lease.Counters[1]);
    }

    [TestMethod]
    public void AuthenticationFailureBeforeWriteIsProvenAndDoesNoIo()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration);
        lease.Authenticated = false;

        Switch2HdRumblePhysicalWriteResult stale = writer.TryWrite(
            CreateSubmission(1, 61));
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.ProvenRejected,
            stale.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.StaleLifetime,
            stale.Failure);
        Assert.AreEqual(0, lease.Calls);

        lease.Authenticated = true;
        lease.ThrowAuthentication = true;
        Switch2HdRumblePhysicalWriteResult threw = writer.TryWrite(
            CreateSubmission(2, 62));
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.ProvenRejected,
            threw.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
            threw.Failure);
        Assert.AreEqual(0, lease.Calls);
    }

    [TestMethod]
    public void InvalidStaleAndExpiredSubmissionsNeverReachTransport()
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration);

        Switch2HdRumblePhysicalWriteResult invalid = writer.TryWrite(default);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.
            InvalidSubmission, invalid.Failure);
        Switch2HdRumblePhysicalWriteResult stale = writer.TryWrite(
            CreateSubmission(1, 71, deviceGeneration:
                DeviceGeneration + 1));
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.StaleLifetime,
            stale.Failure);
        Switch2HdRumblePhysicalWriteResult expired = writer.TryWrite(
            CreateSubmission(2, 72, timestampMicroseconds: 1));
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.
            InvalidSubmission, expired.Failure);
        Assert.AreEqual(0, lease.Calls);
    }

    [TestMethod]
    public void OneTransportCallMayBeInFlight()
    {
        BlockingLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration);
        Switch2HdRumblePhysicalSubmission first = CreateSubmission(1, 81);
        Switch2HdRumblePhysicalSubmission second = CreateSubmission(2, 82);

        Task<Switch2HdRumblePhysicalWriteResult> task = Task.Run(() =>
            writer.TryWrite(first));
        Assert.IsTrue(lease.Entered.Wait(TimeSpan.FromSeconds(5)));

        Switch2HdRumblePhysicalWriteResult busy = writer.TryWrite(second);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.ProvenRejected,
            busy.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.Busy,
            busy.Failure);
        Assert.AreEqual(1, lease.Calls);
        Assert.AreEqual(1, lease.MaximumConcurrent);

        lease.Release.Set();
        Assert.IsTrue(task.GetAwaiter().GetResult().Succeeded);
    }

    [TestMethod]
    public void ReentrantTransportCannotInterleaveAnotherReport()
    {
        ReentrantLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration);
        lease.Writer = writer;
        lease.ReentrantSubmission = CreateSubmission(2, 92);

        Assert.IsTrue(writer.TryWrite(CreateSubmission(1, 91)).Succeeded);
        Assert.AreEqual(1, lease.Calls);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.ProvenRejected,
            lease.ReentrantResult.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.Busy,
            lease.ReentrantResult.Failure);
    }

    [TestMethod]
    public void TypedTransportResultRejectsImpossibleShapes()
    {
        Assert.IsTrue(Completed().HasValidInvariants());
        Assert.IsTrue(Uncertain(
            Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded,
            bytesTransferred: 64).HasValidInvariants());
        Assert.IsFalse(default(
            Switch2ProUsbHdRumbleTransportWriteResult).HasValidInvariants());
        Assert.IsFalse(Switch2ProUsbHdRumbleTransportWriteResult.Complete(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, 0).HasValidInvariants());
        Assert.IsFalse(Switch2ProUsbHdRumbleTransportWriteResult.Reject(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration,
            Switch2ProUsbHdRumbleTransportWriteFailure.TransportRejected,
            1).HasValidInvariants());
        Assert.IsFalse(Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration,
            Switch2ProUsbHdRumbleTransportWriteFailure.None).
                HasValidInvariants());
    }

    [TestMethod]
    public void DirectWriteHotPathAllocatesNothingAfterWarmup()
    {
        AllocationLease lease = new(DeviceGeneration, TransportGeneration);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration);
        ulong sequence = 1;

        for (int index = 0; index < 128; index++)
        {
            Assert.IsTrue(writer.TryWrite(CreateSubmission(sequence++,
                index + 1)).Succeeded);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 10_000; index++)
        {
            succeeded &= writer.TryWrite(CreateSubmission(sequence++,
                index + 129)).Succeeded;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated,
            $"Switch 2 USB Pro direct writes allocated {allocated} bytes.");
        Assert.AreEqual(10_128, lease.Calls);
    }

    [TestMethod]
    public void ExactUncertainRetryAllocatesNothingAfterWarmup()
    {
        AllocationLease lease = new(DeviceGeneration, TransportGeneration)
        {
            Result = Uncertain(
                Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded),
        };
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration, initialCounter: 13);
        Switch2HdRumblePhysicalSubmission stop =
            Switch2HdRumblePhysicalSubmission.CreateStop(DeviceGeneration,
                TransportGeneration, deliveryEpoch: 31);

        for (int index = 0; index < 128; index++)
        {
            Assert.IsTrue(writer.TryWrite(stop).IsUncertain);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool uncertain = true;
        for (int index = 0; index < 10_000; index++)
        {
            uncertain &= writer.TryWrite(stop).IsUncertain;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(uncertain);
        Assert.AreEqual(0L, allocated,
            $"Switch 2 USB Pro exact retries allocated {allocated} bytes.");
        Assert.AreEqual(10_128, lease.Calls);
    }

    private static void AssertMalformedResult(
        Switch2ProUsbHdRumbleTransportWriteResult transportResult,
        Switch2HdRumblePhysicalWriteFailure expectedFailure)
    {
        RecordingLease lease = new(DeviceGeneration, TransportGeneration)
        {
            Result = transportResult,
        };
        Switch2ProUsbHdRumblePhysicalWriter writer = new(lease,
            DeviceGeneration, TransportGeneration);

        Switch2HdRumblePhysicalWriteResult result = writer.TryWrite(
            CreateSubmission(1, 101));

        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.
            OutcomeUncertain, result.Outcome);
        Assert.AreEqual(expectedFailure, result.Failure);
        Assert.AreEqual(1, lease.Calls);
    }

    private static Switch2ProUsbHdRumbleTransportWriteResult Completed() =>
        Switch2ProUsbHdRumbleTransportWriteResult.Complete(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration);

    private static Switch2ProUsbHdRumbleTransportWriteResult Rejected(
        Switch2ProUsbHdRumbleTransportWriteFailure failure) =>
        Switch2ProUsbHdRumbleTransportWriteResult.Reject(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, failure);

    private static Switch2ProUsbHdRumbleTransportWriteResult Uncertain(
        Switch2ProUsbHdRumbleTransportWriteFailure failure,
        int bytesTransferred = 0) =>
        Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, failure, bytesTransferred);

    private static Switch2HdRumblePhysicalSubmission CreateSubmission(
        ulong sequence, int seed, ulong deviceGeneration = DeviceGeneration,
        ulong transportGeneration = TransportGeneration,
        ulong timestampMicroseconds = 0)
    {
        if (timestampMicroseconds == 0 &&
            !ControllerFeedbackClock.TryGetTimestampMicroseconds(
                out timestampMicroseconds))
        {
            throw new InvalidOperationException(
                "The canonical feedback clock is unavailable.");
        }

        ushort a = (ushort)((seed * 3 + 1) & 0x03FF);
        ushort b = (ushort)((seed * 5 + 2) & 0x03FF);
        ushort c = (ushort)((seed * 7 + 3) & 0x03FF);
        ushort d = (ushort)((seed * 11 + 4) & 0x03FF);
        Switch2HdRumbleGroup left = new(
            new Switch2HdRumbleSubframe(0x112, a, 0x187, b),
            new Switch2HdRumbleSubframe(0x113, b, 0x188, c),
            new Switch2HdRumbleSubframe(0x114, c, 0x189, d));
        Switch2HdRumbleGroup right = new(
            new Switch2HdRumbleSubframe(0x187, d, 0x112, c),
            new Switch2HdRumbleSubframe(0x188, c, 0x113, b),
            new Switch2HdRumbleSubframe(0x189, b, 0x114, a));
        Switch2HdRumbleFeedbackSynthesis synthesis = new(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            ControllerFeedbackCommand.Apply,
            Switch2HdRumbleFeedbackFidelity.
                SideLocalImpulseApproximation,
            left, right, sequence, deviceGeneration, transportGeneration,
            ownershipEpoch: 19, timestampMicroseconds,
            timeToLiveMicroseconds:
                ControllerFeedbackFrame.MaxTimeToLiveMicroseconds);
        if (!Switch2HdRumblePhysicalSubmission.TryCreateFrame(synthesis,
                deliveryEpoch: 31, out var submission))
        {
            throw new InvalidOperationException(
                "The test submission is invalid.");
        }
        return submission;
    }

    private sealed class RecordingLease :
        ISwitch2ProUsbHdRumbleTransportLease
    {
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;
        private int concurrent;

        internal RecordingLease(ulong deviceGeneration,
            ulong transportGeneration)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
        }

        internal bool Authenticated = true;
        internal bool ThrowAuthentication;
        internal bool ThrowWrite;
        internal Switch2ControllerModel ExpectedAuthenticationModel =
            Switch2ControllerModel.ProController2;
        internal Switch2ProUsbHdRumbleTransportWriteResult Result =
            Completed();
        internal readonly byte[] FirstReport =
            new byte[Switch2UsbHdRumbleCodec.ReportLength];
        internal readonly byte[] LastReport =
            new byte[Switch2UsbHdRumbleCodec.ReportLength];
        internal readonly byte[] Counters = new byte[64];
        internal int Calls;
        internal int LastLength;
        internal int MaximumConcurrent;
        internal Switch2ControllerModel LastExpectedModel;
        internal ulong LastExpectedDeviceGeneration;
        internal ulong LastExpectedTransportGeneration;

        public bool Authenticates(Switch2ControllerModel model,
            ulong candidateDeviceGeneration,
            ulong candidateTransportGeneration)
        {
            if (ThrowAuthentication)
            {
                throw new InvalidOperationException(
                    "Injected authentication exception.");
            }
            return Authenticated && model == ExpectedAuthenticationModel &&
                candidateDeviceGeneration == deviceGeneration &&
                candidateTransportGeneration == transportGeneration;
        }

        public Switch2ProUsbHdRumbleTransportWriteResult TryWriteReport(
            ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            int active = Interlocked.Increment(ref concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            LastLength = report.Length;
            LastExpectedModel = expectedModel;
            LastExpectedDeviceGeneration = expectedDeviceGeneration;
            LastExpectedTransportGeneration = expectedTransportGeneration;
            report.CopyTo(LastReport);
            if (Calls == 0)
            {
                report.CopyTo(FirstReport);
            }
            Counters[Calls] = (byte)(report[1] & 0x0F);
            Calls++;
            Interlocked.Decrement(ref concurrent);
            if (ThrowWrite)
            {
                throw new InvalidOperationException(
                    "Injected synchronous write exception.");
            }
            return Result;
        }
    }

    private sealed class BlockingLease :
        ISwitch2ProUsbHdRumbleTransportLease
    {
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;
        private int concurrent;

        internal BlockingLease(ulong deviceGeneration,
            ulong transportGeneration)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
        }

        internal readonly ManualResetEventSlim Entered = new(false);
        internal readonly ManualResetEventSlim Release = new(false);
        internal int Calls;
        internal int MaximumConcurrent;

        public bool Authenticates(Switch2ControllerModel model,
            ulong candidateDeviceGeneration,
            ulong candidateTransportGeneration) =>
            model == Switch2ControllerModel.ProController2 &&
            candidateDeviceGeneration == deviceGeneration &&
            candidateTransportGeneration == transportGeneration;

        public Switch2ProUsbHdRumbleTransportWriteResult TryWriteReport(
            ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            int active = Interlocked.Increment(ref concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            Interlocked.Increment(ref Calls);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref concurrent);
            return Completed();
        }
    }

    private sealed class ReentrantLease :
        ISwitch2ProUsbHdRumbleTransportLease
    {
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;

        internal ReentrantLease(ulong deviceGeneration,
            ulong transportGeneration)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
        }

        internal Switch2ProUsbHdRumblePhysicalWriter Writer;
        internal Switch2HdRumblePhysicalSubmission ReentrantSubmission;
        internal Switch2HdRumblePhysicalWriteResult ReentrantResult;
        internal int Calls;

        public bool Authenticates(Switch2ControllerModel model,
            ulong candidateDeviceGeneration,
            ulong candidateTransportGeneration) =>
            model == Switch2ControllerModel.ProController2 &&
            candidateDeviceGeneration == deviceGeneration &&
            candidateTransportGeneration == transportGeneration;

        public Switch2ProUsbHdRumbleTransportWriteResult TryWriteReport(
            ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            Calls++;
            ReentrantResult = Writer.TryWrite(ReentrantSubmission);
            return Completed();
        }
    }

    private sealed class AllocationLease :
        ISwitch2ProUsbHdRumbleTransportLease
    {
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;
        internal AllocationLease(ulong deviceGeneration,
            ulong transportGeneration)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
            Result = Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                Switch2ControllerModel.ProController2, deviceGeneration,
                transportGeneration);
        }

        internal int Calls;
        internal byte Checksum;
        internal Switch2ProUsbHdRumbleTransportWriteResult Result;

        public bool Authenticates(Switch2ControllerModel model,
            ulong candidateDeviceGeneration,
            ulong candidateTransportGeneration) =>
            model == Switch2ControllerModel.ProController2 &&
            candidateDeviceGeneration == deviceGeneration &&
            candidateTransportGeneration == transportGeneration;

        public Switch2ProUsbHdRumbleTransportWriteResult TryWriteReport(
            ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            Calls++;
            Checksum ^= report[Calls & 63];
            return Result;
        }
    }
}
