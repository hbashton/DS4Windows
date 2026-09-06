using System;
using System.Collections.Generic;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothHdRumblePhysicalWriterTests
{
    private const ulong DeviceGeneration = 17;
    private const ulong TransportGeneration = 23;

    [TestMethod]
    public void UsesPinnedModelSpecificDonorCharacteristicUuids()
    {
        Assert.AreEqual(new Guid("fa19b0fb-cd1f-46a7-84a1-bbb09e00c149"),
            Switch2BluetoothHdRumblePhysicalWriter.CharacteristicUuidFor(
                Switch2ControllerModel.JoyCon2Right));
        Assert.AreEqual(new Guid("289326cb-a471-485d-a8f4-240c14f18241"),
            Switch2BluetoothHdRumblePhysicalWriter.CharacteristicUuidFor(
                Switch2ControllerModel.JoyCon2Left));
        Assert.AreEqual(new Guid("cc483f51-9258-427d-a939-630c31f72b05"),
            Switch2BluetoothHdRumblePhysicalWriter.CharacteristicUuidFor(
                Switch2ControllerModel.ProController2));
        Assert.AreEqual(Guid.Empty,
            Switch2BluetoothHdRumblePhysicalWriter.CharacteristicUuidFor(
                Switch2ControllerModel.Unknown));
    }

    [TestMethod]
    public void ProWritesZeroEnvelopeAndIndependentLeftThenRightGroups()
    {
        RecordingLease lease = new(Switch2ControllerModel.ProController2);
        Switch2BluetoothHdRumblePhysicalWriter writer = new(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, initialCounter: 10);
        Switch2HdRumblePhysicalSubmission submission = CreateSubmission(1, 7);

        Assert.IsTrue(writer.TryWrite(submission).Succeeded);
        Assert.AreEqual(Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength,
            lease.LastPayload.Length);
        Assert.AreEqual((byte)0, lease.LastPayload[0]);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out byte counter,
            out Switch2HdRumbleGroup left, out Switch2HdRumbleGroup right,
            out Switch2BluetoothHdRumbleDecodeFailure failure));
        Assert.AreEqual(Switch2BluetoothHdRumbleDecodeFailure.None, failure);
        Assert.AreEqual((byte)10, counter);
        Assert.AreEqual(submission.Left, left);
        Assert.AreEqual(submission.Right, right);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, true)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false)]
    public void JoyConWritesOnlyItsPhysicalSide(
        Switch2ControllerModel model, bool expectedLeft)
    {
        RecordingLease lease = new(model);
        Switch2BluetoothHdRumblePhysicalWriter writer = new(lease, model,
            DeviceGeneration, TransportGeneration, initialCounter: 3);
        Switch2HdRumblePhysicalSubmission submission = CreateSubmission(1, 11);

        Assert.IsTrue(writer.TryWrite(submission).Succeeded);
        Assert.AreEqual(Switch2BluetoothHdRumbleCodec.JoyConPayloadLength,
            lease.LastPayload.Length);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
            lease.LastPayload, out byte counter, out Switch2HdRumbleGroup group,
            out _));
        Assert.AreEqual((byte)3, counter);
        Assert.AreEqual(expectedLeft ? submission.Left : submission.Right,
            group);
    }

    [TestMethod]
    public void RejectionAndUncertaintyRetryExactPayloadAndCounter()
    {
        RecordingLease lease = new(Switch2ControllerModel.ProController2)
        {
            ResultKind = Switch2BluetoothHdRumbleTransportWriteOutcome.
                ProvenRejected,
        };
        Switch2BluetoothHdRumblePhysicalWriter writer = new(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, initialCounter: 15);
        Switch2HdRumblePhysicalSubmission first = CreateSubmission(1, 13);

        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.ProvenRejected,
            writer.TryWrite(first).Outcome);
        byte[] rejected = (byte[])lease.LastPayload.Clone();

        lease.ResultKind = Switch2BluetoothHdRumbleTransportWriteOutcome.
            OutcomeUncertain;
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.OutcomeUncertain,
            writer.TryWrite(first).Outcome);
        CollectionAssert.AreEqual(rejected, lease.LastPayload);

        lease.ResultKind = Switch2BluetoothHdRumbleTransportWriteOutcome.
            Completed;
        Assert.IsTrue(writer.TryWrite(first).Succeeded);
        CollectionAssert.AreEqual(rejected, lease.LastPayload);

        Assert.IsTrue(writer.TryWrite(CreateSubmission(2, 14)).Succeeded);
        Assert.AreEqual((byte)0x50, lease.LastPayload[1]);
    }

    [TestMethod]
    public void StopWritesExplicitNeutralEnvelope()
    {
        RecordingLease lease = new(Switch2ControllerModel.ProController2);
        Switch2BluetoothHdRumblePhysicalWriter writer = new(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, initialCounter: 4);
        Switch2HdRumblePhysicalSubmission stop =
            Switch2HdRumblePhysicalSubmission.CreateStop(DeviceGeneration,
                TransportGeneration, deliveryEpoch: 41);

        Assert.IsTrue(writer.TryWrite(stop).Succeeded);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out byte counter,
            out Switch2HdRumbleGroup left, out Switch2HdRumbleGroup right,
            out _));
        Assert.AreEqual((byte)4, counter);
        Assert.AreEqual(default(Switch2HdRumbleGroup), left);
        Assert.AreEqual(default(Switch2HdRumbleGroup), right);
    }

    [TestMethod]
    public void ConstructorAndSubmissionFailClosedAcrossLifetimes()
    {
        RecordingLease lease = new(Switch2ControllerModel.ProController2);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2BluetoothHdRumblePhysicalWriter(lease,
                Switch2ControllerModel.Unknown, DeviceGeneration,
                TransportGeneration));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2BluetoothHdRumblePhysicalWriter(lease,
                Switch2ControllerModel.ProController2, 0,
                TransportGeneration));

        Switch2BluetoothHdRumblePhysicalWriter writer = new(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration);
        Switch2HdRumblePhysicalWriteResult result = writer.TryWrite(
            CreateSubmission(1, 17, deviceGeneration: DeviceGeneration + 1));
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.StaleLifetime,
            result.Failure);
        Assert.AreEqual(0, lease.Calls);
    }

    private static Switch2HdRumblePhysicalSubmission CreateSubmission(
        ulong sequence, int seed,
        ulong deviceGeneration = DeviceGeneration,
        ulong transportGeneration = TransportGeneration)
    {
        if (!ControllerFeedbackClock.TryGetTimestampMicroseconds(
                out ulong timestampMicroseconds))
        {
            throw new InvalidOperationException();
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
            Switch2HdRumbleFeedbackFidelity.SideLocalImpulseApproximation,
            left, right, sequence, deviceGeneration, transportGeneration,
            ownershipEpoch: 29, timestampMicroseconds,
            ControllerFeedbackFrame.MaxTimeToLiveMicroseconds);
        Assert.IsTrue(Switch2HdRumblePhysicalSubmission.TryCreateFrame(
            synthesis, deliveryEpoch: 31, out var submission));
        return submission;
    }

    private sealed class RecordingLease :
        ISwitch2BluetoothHdRumbleTransportLease
    {
        private readonly Switch2ControllerModel model;

        internal RecordingLease(Switch2ControllerModel model)
        {
            this.model = model;
        }

        internal Switch2BluetoothHdRumbleTransportWriteOutcome ResultKind =
            Switch2BluetoothHdRumbleTransportWriteOutcome.Completed;
        internal byte[] LastPayload = Array.Empty<byte>();
        internal int Calls;

        public bool Authenticates(Switch2ControllerModel candidateModel,
            ulong deviceGeneration, ulong transportGeneration) =>
            candidateModel == model && deviceGeneration == DeviceGeneration &&
            transportGeneration == TransportGeneration;

        public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(
            ReadOnlySpan<byte> payload,
            Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            Calls++;
            LastPayload = payload.ToArray();
            return ResultKind switch
            {
                Switch2BluetoothHdRumbleTransportWriteOutcome.Completed =>
                    Switch2BluetoothHdRumbleTransportWriteResult.Complete(
                        model, DeviceGeneration, TransportGeneration,
                        payload.Length),
                Switch2BluetoothHdRumbleTransportWriteOutcome.ProvenRejected =>
                    Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                        model, DeviceGeneration, TransportGeneration,
                        Switch2BluetoothHdRumbleTransportWriteFailure.
                            TransportRejected),
                _ => Switch2BluetoothHdRumbleTransportWriteResult.Uncertain(
                    model, DeviceGeneration, TransportGeneration,
                    Switch2BluetoothHdRumbleTransportWriteFailure.TimedOut),
            };
        }
    }
}
