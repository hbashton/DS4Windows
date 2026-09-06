using System.Buffers.Binary;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class XboxOneBrokerFeedbackIngressTests
    {
        [TestMethod]
        public void DelayedAdmissionObservesCanonicalWatermarkWithoutAdvancingIt()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(ControllerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 1, 1,
                out var ingress));
            Assert.IsTrue(ingress.TryPublish(Wire(Frame(1))));
            Assert.IsFalse(ingress.HasPublishedTerminalStop);
            Assert.IsFalse(ingress.AuthenticatesDelayedFrame(Frame(1)));
            Assert.IsTrue(ingress.AuthenticatesDelayedFrame(Frame(2)));
            Assert.IsTrue(ingress.AuthenticatesDelayedFrame(Frame(2)),
                "Checking queue admission must not consume a broker sequence.");
            Assert.IsFalse(ingress.AuthenticatesDelayedFrame(Frame(2,
                deviceGeneration: 2)));
            Assert.IsFalse(ingress.AuthenticatesDelayedFrame(Frame(2,
                transportGeneration: 2)));
            Assert.IsFalse(ingress.AuthenticatesDelayedFrame(Frame(2,
                ownershipEpoch: 2)));
            Assert.IsFalse(ingress.AuthenticatesDelayedFrame(Frame(2,
                source: ControllerFeedbackSource.XboxSeriesVirtualDevice)));

            Assert.IsTrue(runtime.TryAcquireWriter(1, 1, out var writer));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, writer, out var applied, out var applyToken));
            Assert.IsTrue(runtime.TryAdmit(writer, applyToken, 1_000));
            Assert.IsTrue(runtime.Complete(writer, applyToken, true, 1_000));

            var stop = Frame(2, ControllerFeedbackCommand.Stop, timestamp: 1_001);
            Assert.IsTrue(ingress.TryPublish(Wire(stop)));
            Assert.IsTrue(ingress.HasPublishedTerminalStop);
            Assert.IsFalse(ingress.AuthenticatesDelayedFrame(Frame(3)));
            Assert.IsFalse(ingress.TryPublish(Wire(Frame(3))));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                runtime.Claim(1_001, writer, out var delivery, out var stopToken));
            Assert.AreEqual(applied.DeliveryEpoch, delivery.DeliveryEpoch,
                "The existing runtime must stop the same admitted physical effect.");
            Assert.IsTrue(runtime.TryAdmit(writer, stopToken, 1_001));
            Assert.IsTrue(runtime.Complete(writer, stopToken, true, 1_001));
            Assert.IsTrue(ingress.TryRetire());
            Assert.IsFalse(ingress.AuthenticatesDelayedFrame(Frame(4)));
        }

        [TestMethod]
        public void ViiperGoldenFrameEntersExistingCanonicalRuntime()
        {
            const ulong deviceGeneration = 0x1112131415161718UL;
            const ulong transportGeneration = 0x2122232425262728UL;
            const ulong ownershipEpoch = 0x3132333435363738UL;
            const ulong timestamp = 0x4142434445464748UL;
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxSeriesVirtualDevice,
                deviceGeneration, transportGeneration, ownershipEpoch,
                out XboxOneBrokerFeedbackIngress ingress));

            byte[] wire =
            {
                0x43, 0x46, 0x42, 0x4B, 0x01, 0x00, 0x48, 0x00,
                0x02, 0x01, 0x0F, 0x00, 0x00, 0x40, 0x00, 0x80,
                0xFF, 0xBF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
                0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
                0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
                0x28, 0x27, 0x26, 0x25, 0x24, 0x23, 0x22, 0x21,
                0x38, 0x37, 0x36, 0x35, 0x34, 0x33, 0x32, 0x31,
                0x48, 0x47, 0x46, 0x45, 0x44, 0x43, 0x42, 0x41,
                0x90, 0xD0, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00,
            };

            Assert.IsTrue(ingress.TryPublish(wire));
            Assert.IsTrue(runtime.TryAcquireWriter(deviceGeneration,
                transportGeneration,
                out ControllerFeedbackWriterLease writer));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(timestamp, writer,
                    out ControllerFeedbackDelivery delivery,
                    out ulong claimToken));
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.NativeGame,
                delivery.Origin);
            Assert.AreEqual(ControllerFeedbackSource.XboxSeriesVirtualDevice,
                delivery.Frame.Source);
            Assert.AreEqual(0x0102030405060708UL,
                delivery.Frame.Sequence);
            Assert.AreEqual((ushort)0x4000, delivery.Frame.BodyLow);
            Assert.AreEqual((ushort)0x8000, delivery.Frame.BodyHigh);
            Assert.AreEqual((ushort)0xBFFF,
                delivery.Frame.LeftTrigger);
            Assert.AreEqual(ushort.MaxValue,
                delivery.Frame.RightTrigger);
            Assert.AreEqual(250_000UL,
                delivery.Frame.TimeToLiveMicroseconds);
            Assert.IsTrue(runtime.TryAdmit(writer, claimToken, timestamp));
            Assert.IsTrue(runtime.Complete(writer, claimToken,
                delivered: true, timestamp));
        }

        [TestMethod]
        public void ExactBindingAndCanonicalOrderingFailClosed()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 7, 11, 13,
                out XboxOneBrokerFeedbackIngress ingress));
            byte[] first = Wire(Frame(sequence: 1, deviceGeneration: 7,
                transportGeneration: 11, ownershipEpoch: 13));
            Assert.IsTrue(ingress.TryPublish(first));
            Assert.IsFalse(ingress.TryPublish(first),
                "The canonical runtime must reject a duplicate sequence.");

            Assert.IsFalse(ingress.TryPublish(Wire(Frame(sequence: 2,
                source: ControllerFeedbackSource.XboxSeriesVirtualDevice,
                deviceGeneration: 7, transportGeneration: 11,
                ownershipEpoch: 13))));
            Assert.IsFalse(ingress.TryPublish(Wire(Frame(sequence: 2,
                deviceGeneration: 8, transportGeneration: 11,
                ownershipEpoch: 13))));
            Assert.IsFalse(ingress.TryPublish(Wire(Frame(sequence: 2,
                deviceGeneration: 7, transportGeneration: 12,
                ownershipEpoch: 13))));
            Assert.IsFalse(ingress.TryPublish(Wire(Frame(sequence: 2,
                deviceGeneration: 7, transportGeneration: 11,
                ownershipEpoch: 14))));

            byte[] malformed = (byte[])first.Clone();
            malformed[20] = 1;
            Assert.IsFalse(ingress.TryPublish(malformed));
            Assert.IsFalse(ingress.TryPublish(first.AsSpan(0,
                first.Length - 1)));

            byte[] zeroSequence = (byte[])first.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(
                zeroSequence.AsSpan(24, sizeof(ulong)), 0);
            Assert.IsFalse(ingress.TryPublish(zeroSequence));
            byte[] zeroTtl = (byte[])first.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(
                zeroTtl.AsSpan(64, sizeof(ulong)), 0);
            Assert.IsFalse(ingress.TryPublish(zeroTtl));
            byte[] excessiveTtl = (byte[])first.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(
                excessiveTtl.AsSpan(64, sizeof(ulong)), 250_001);
            Assert.IsFalse(ingress.TryPublish(excessiveTtl));

            Assert.IsTrue(runtime.TryAcquireWriter(7, 11,
                out ControllerFeedbackWriterLease writer));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, writer,
                    out ControllerFeedbackDelivery delivery, out _));
            Assert.AreEqual(1UL, delivery.Frame.Sequence,
                "Rejected input changed the NativeGame slot.");
        }

        [TestMethod]
        public void ExpiredSuccessorAdvancesWatermarkAndForcesCanonicalStop()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 2, 3,
                out XboxOneBrokerFeedbackIngress ingress));
            Assert.IsTrue(ingress.TryPublish(Wire(Frame(sequence: 1,
                deviceGeneration: 1, transportGeneration: 2,
                ownershipEpoch: 3, timestamp: 1_000, ttl: 100))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 2,
                out ControllerFeedbackWriterLease writer));

            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, writer,
                    out ControllerFeedbackDelivery applied,
                    out ulong applyToken));
            Assert.IsTrue(runtime.TryAdmit(writer, applyToken, 1_000));
            Assert.IsTrue(runtime.Complete(writer, applyToken,
                delivered: true, 1_000));

            Assert.IsTrue(ingress.TryPublish(Wire(Frame(sequence: 2,
                deviceGeneration: 1, transportGeneration: 2,
                ownershipEpoch: 3, timestamp: 1_000, ttl: 100))));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                runtime.Claim(1_100, writer,
                    out ControllerFeedbackDelivery stop,
                    out ulong stopToken));
            Assert.AreEqual(applied.DeliveryEpoch, stop.DeliveryEpoch);
            Assert.IsTrue(runtime.TryAdmit(writer, stopToken, 1_100));
            Assert.IsTrue(runtime.Complete(writer, stopToken,
                delivered: true, 1_100));
        }

        [TestMethod]
        public void ExplicitStopIsTerminalAndRetirementFencesIngress()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 2, 3,
                out XboxOneBrokerFeedbackIngress ingress));
            Assert.IsTrue(ingress.TryPublish(Wire(Frame(sequence: 1,
                deviceGeneration: 1, transportGeneration: 2,
                ownershipEpoch: 3))));
            Assert.IsTrue(ingress.TryPublish(Wire(Frame(sequence: 2,
                command: ControllerFeedbackCommand.Stop,
                deviceGeneration: 1, transportGeneration: 2,
                ownershipEpoch: 3))));
            Assert.IsFalse(ingress.TryPublish(Wire(Frame(sequence: 3,
                deviceGeneration: 1, transportGeneration: 2,
                ownershipEpoch: 3))),
                "The ingress must not bypass canonical Stop terminality.");

            Assert.IsTrue(ingress.TryRetire());
            Assert.IsFalse(ingress.TryRetire());
            Assert.IsFalse(ingress.TryPublish(Wire(Frame(sequence: 4,
                command: ControllerFeedbackCommand.Stop,
                deviceGeneration: 1, transportGeneration: 2,
                ownershipEpoch: 3))));

            Assert.IsTrue(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 2, 4,
                out XboxOneBrokerFeedbackIngress successor));
            Assert.IsTrue(successor.TryPublish(Wire(Frame(sequence: 1,
                deviceGeneration: 1, transportGeneration: 2,
                ownershipEpoch: 4))));
        }

        [TestMethod]
        public void InvalidSessionBindingIsRejected()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsFalse(XboxOneBrokerFeedbackIngress.TryCreate(null,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 1, 1,
                out _));
            Assert.IsFalse(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.Xbox360VirtualDevice, 1, 1, 1,
                out _));
            Assert.IsFalse(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 0, 1, 1,
                out _));
            Assert.IsFalse(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 0, 1,
                out _));
            Assert.IsFalse(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 1, 0,
                out _));
        }

        [TestMethod]
        public void ValidatedSteadyStatePublicationAllocatesNothing()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(XboxOneBrokerFeedbackIngress.TryCreate(runtime,
                ControllerFeedbackSource.XboxOneVirtualDevice, 1, 2, 3,
                out XboxOneBrokerFeedbackIngress ingress));
            byte[] wire = Wire(Frame(sequence: 1, deviceGeneration: 1,
                transportGeneration: 2, ownershipEpoch: 3));
            Assert.IsTrue(ingress.TryPublish(wire));

            bool accepted = true;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (ulong sequence = 2; sequence <= 10_001; sequence++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    wire.AsSpan(24, sizeof(ulong)), sequence);
                accepted &= ingress.TryPublish(wire);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(accepted);
            Assert.AreEqual(0L, allocated);
        }

        private static ControllerFeedbackFrame Frame(ulong sequence,
            ControllerFeedbackCommand command =
                ControllerFeedbackCommand.Apply,
            ControllerFeedbackSource source =
                ControllerFeedbackSource.XboxOneVirtualDevice,
            ulong deviceGeneration = 1, ulong transportGeneration = 1,
            ulong ownershipEpoch = 1, ulong timestamp = 1_000,
            ulong ttl = 250_000)
        {
            ushort bodyLow = command == ControllerFeedbackCommand.Apply ?
                (ushort)1 : (ushort)0;
            Assert.IsTrue(ControllerFeedbackFrame.TryCreate(source, command,
                ControllerFeedbackActuators.All, bodyLow, 0, 0, 0,
                sequence, deviceGeneration, transportGeneration,
                ownershipEpoch, timestamp, ttl,
                out ControllerFeedbackFrame frame));
            return frame;
        }

        private static byte[] Wire(in ControllerFeedbackFrame frame)
        {
            byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
            Assert.IsTrue(frame.TryWriteTo(wire));
            return wire;
        }
    }
}
