using System;
using System.Buffers.Binary;
using System.Threading;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerFeedbackFrameTests
    {
        [TestMethod]
        public void WireEnumValuesRemainStable()
        {
            Assert.AreEqual((byte)1,
                (byte)ControllerFeedbackSource.XboxOneVirtualDevice);
            Assert.AreEqual((byte)2,
                (byte)ControllerFeedbackSource.XboxSeriesVirtualDevice);
            Assert.AreEqual((byte)3,
                (byte)ControllerFeedbackSource.Xbox360VirtualDevice);
            Assert.AreEqual((byte)4,
                (byte)ControllerFeedbackSource.DualSenseVirtualDevice);
            Assert.AreEqual((byte)5,
                (byte)ControllerFeedbackSource.DualSenseEdgeVirtualDevice);
            Assert.AreEqual((byte)6,
                (byte)ControllerFeedbackSource.DualShock4VirtualDevice);
            Assert.AreEqual((byte)1,
                (byte)ControllerFeedbackCommand.Apply);
            Assert.AreEqual((byte)2,
                (byte)ControllerFeedbackCommand.Neutral);
            Assert.AreEqual((byte)3,
                (byte)ControllerFeedbackCommand.Stop);
            Assert.AreEqual((byte)0x01,
                (byte)ControllerFeedbackActuators.BodyLow);
            Assert.AreEqual((byte)0x02,
                (byte)ControllerFeedbackActuators.BodyHigh);
            Assert.AreEqual((byte)0x04,
                (byte)ControllerFeedbackActuators.LeftTrigger);
            Assert.AreEqual((byte)0x08,
                (byte)ControllerFeedbackActuators.RightTrigger);
        }

        [TestMethod]
        public void VersionOneWireRoundTripPreservesCompleteFeedbackSnapshot()
        {
            ControllerFeedbackFrame expected = CreateFrame(
                source: ControllerFeedbackSource.XboxSeriesVirtualDevice,
                bodyLow: 0x1122, bodyHigh: 0x3344,
                leftTrigger: 0x5566, rightTrigger: 0x7788,
                sequence: 0x0102030405060708,
                deviceGeneration: 0x1112131415161718,
                transportGeneration: 0x2122232425262728,
                ownershipEpoch: 0x3132333435363738,
                timestampMicroseconds: 0x4142434445464748,
                timeToLiveMicroseconds: 250_000);
            Span<byte> packet = stackalloc byte[
                ControllerFeedbackFrame.SerializedLength];

            Assert.IsTrue(expected.TryWriteTo(packet));
            Assert.AreEqual((byte)'C', packet[0]);
            Assert.AreEqual((byte)'F', packet[1]);
            Assert.AreEqual((byte)'B', packet[2]);
            Assert.AreEqual((byte)'K', packet[3]);
            Assert.AreEqual(ControllerFeedbackFrame.CurrentVersion,
                BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]));
            Assert.AreEqual((ushort)ControllerFeedbackFrame.SerializedLength,
                BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]));
            Assert.AreEqual((ushort)0x1122,
                BinaryPrimitives.ReadUInt16LittleEndian(packet[12..]));
            Assert.AreEqual((ushort)0x7788,
                BinaryPrimitives.ReadUInt16LittleEndian(packet[18..]));
            Assert.AreEqual(0x0102030405060708UL,
                BinaryPrimitives.ReadUInt64LittleEndian(packet[24..]));
            Assert.IsTrue(ControllerFeedbackFrame.TryReadFrom(packet,
                out ControllerFeedbackFrame actual));
            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void WireReaderRejectsWrongVersionSizeReservedByteAndTruncation()
        {
            ControllerFeedbackFrame frame = CreateFrame();
            Span<byte> packet = stackalloc byte[
                ControllerFeedbackFrame.SerializedLength];
            Assert.IsTrue(frame.TryWriteTo(packet));

            packet[4]++;
            Assert.IsFalse(ControllerFeedbackFrame.TryReadFrom(packet, out _));
            Assert.IsTrue(frame.TryWriteTo(packet));
            packet[6]--;
            Assert.IsFalse(ControllerFeedbackFrame.TryReadFrom(packet, out _));
            Assert.IsTrue(frame.TryWriteTo(packet));
            packet[11] = 1;
            Assert.IsFalse(ControllerFeedbackFrame.TryReadFrom(packet, out _));
            Assert.IsTrue(frame.TryWriteTo(packet));
            packet[20] = 1;
            Assert.IsFalse(ControllerFeedbackFrame.TryReadFrom(packet, out _));
            Assert.IsTrue(frame.TryWriteTo(packet));
            Assert.IsFalse(ControllerFeedbackFrame.TryReadFrom(packet[..^1],
                out _));
        }

        [TestMethod]
        public void InvariantsRejectAmbiguousOrOutOfCapabilityActuatorState()
        {
            Assert.IsFalse(TryCreate(out _,
                source: ControllerFeedbackSource.Invalid));
            Assert.IsFalse(TryCreate(out _,
                actuators: ControllerFeedbackActuators.BodyLow,
                bodyLow: 1, bodyHigh: 2));
            Assert.IsFalse(TryCreate(out _,
                actuators: (ControllerFeedbackActuators)0x80,
                bodyLow: 1));
            Assert.IsFalse(TryCreate(out _,
                actuators: ControllerFeedbackActuators.BodyLow,
                bodyLow: 1, bodyHigh: 0, leftTrigger: 0,
                rightTrigger: 0));
            Assert.IsFalse(TryCreate(out _,
                command: ControllerFeedbackCommand.Apply,
                bodyLow: 0, bodyHigh: 0, leftTrigger: 0,
                rightTrigger: 0));
            Assert.IsFalse(TryCreate(out _,
                command: ControllerFeedbackCommand.Neutral,
                bodyLow: 1));
            Assert.IsFalse(TryCreate(out _, timeToLiveMicroseconds: 0));
            Assert.IsTrue(TryCreate(out _, timeToLiveMicroseconds:
                ControllerFeedbackFrame.MaxTimeToLiveMicroseconds));
            Assert.IsFalse(TryCreate(out _, timeToLiveMicroseconds:
                ControllerFeedbackFrame.MaxTimeToLiveMicroseconds + 1));
            Assert.IsFalse(default(ControllerFeedbackFrame).
                HasValidInvariants());
        }

        [TestMethod]
        public void NeutralAndStopHaveDistinctExplicitLifecycleMeaning()
        {
            ControllerFeedbackFrame neutral = CreateFrame(
                command: ControllerFeedbackCommand.Neutral,
                bodyLow: 0, bodyHigh: 0, leftTrigger: 0,
                rightTrigger: 0);
            ControllerFeedbackFrame stop = CreateFrame(
                command: ControllerFeedbackCommand.Stop,
                bodyLow: 0, bodyHigh: 0, leftTrigger: 0,
                rightTrigger: 0);

            Assert.IsTrue(neutral.IsNeutral);
            Assert.IsFalse(neutral.IsStop);
            Assert.IsTrue(stop.IsStop);
            Assert.IsFalse(stop.IsNeutral);
            Assert.AreNotEqual(neutral, stop);
        }

        [TestMethod]
        public void ExpiryUsesInclusiveBoundaryAndCannotWrap()
        {
            ControllerFeedbackFrame ordinary = CreateFrame(
                timestampMicroseconds: 1_000,
                timeToLiveMicroseconds: 250);
            Assert.IsFalse(ordinary.IsExpiredAt(999));
            Assert.IsFalse(ordinary.IsExpiredAt(1_249));
            Assert.IsTrue(ordinary.IsExpiredAt(1_250));

            ControllerFeedbackFrame futureBoundary = CreateFrame(
                timestampMicroseconds: 10_000,
                timeToLiveMicroseconds: 250);
            Assert.IsFalse(futureBoundary.IsExpiredAt(5_000));
            Assert.IsTrue(futureBoundary.IsExpiredAt(4_999));

            ControllerFeedbackFrame nearLimit = CreateFrame(
                timestampMicroseconds: ulong.MaxValue - 20,
                timeToLiveMicroseconds: 10);
            Assert.IsTrue(nearLimit.IsExpiredAt(5),
                "An implausibly future timestamp remained live.");
            Assert.IsFalse(nearLimit.IsExpiredAt(ulong.MaxValue - 21));
            Assert.IsFalse(nearLimit.IsExpiredAt(ulong.MaxValue - 11));
            Assert.IsTrue(nearLimit.IsExpiredAt(ulong.MaxValue - 10));
        }

        [TestMethod]
        public void QpcConversionVectorsMatchViiperWithoutOverflow()
        {
            Assert.AreEqual("windows-qpc-host-v1",
                ControllerFeedbackClock.Domain);
            Assert.IsTrue(ControllerFeedbackClock.TryConvertQpcTicks(
                0, 10_000_000, out ulong zero));
            Assert.AreEqual(0UL, zero);
            Assert.IsTrue(ControllerFeedbackClock.TryConvertQpcTicks(
                12_345_678, 10_000_000, out ulong fractional));
            Assert.AreEqual(1_234_567UL, fractional);
            Assert.IsTrue(ControllerFeedbackClock.TryConvertQpcTicks(
                ulong.MaxValue, ulong.MaxValue, out ulong maximum));
            Assert.AreEqual(1_000_000UL, maximum);
            Assert.IsFalse(ControllerFeedbackClock.TryConvertQpcTicks(
                1, 0, out _));
            Assert.IsFalse(ControllerFeedbackClock.TryConvertQpcTicks(
                ulong.MaxValue, 1, out _));
            Assert.IsTrue(ControllerFeedbackClock.
                TryGetTimestampMicroseconds(out ulong sampled));
            Assert.IsTrue(sampled > 0);
        }

        [TestMethod]
        public void MailboxRejectsStaleFencesAndRequiresNewEpochForSourceChange()
        {
            ControllerFeedbackMailbox mailbox = new();
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 10)));
            Assert.IsFalse(mailbox.TryPublish(CreateFrame(sequence: 9)));
            Assert.IsFalse(mailbox.TryPublish(CreateFrame(sequence: 11,
                source: ControllerFeedbackSource.DualSenseVirtualDevice)));
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 1,
                ownershipEpoch: 2,
                source: ControllerFeedbackSource.DualSenseVirtualDevice)));
            Assert.IsFalse(mailbox.TryPublish(CreateFrame(sequence: 99,
                ownershipEpoch: 1)));
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 1,
                deviceGeneration: 2, transportGeneration: 1,
                ownershipEpoch: 1)));

            Assert.IsTrue(mailbox.TryReadLatest(
                out ControllerFeedbackFrame latest, out ulong revision));
            Assert.AreEqual(2UL, latest.DeviceGeneration);
            Assert.AreEqual(3UL, revision);
        }

        [TestMethod]
        public void StopIsTerminalUntilAReplacementOwnershipEpochArrives()
        {
            ControllerFeedbackMailbox mailbox = new();
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 1)));
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 2,
                command: ControllerFeedbackCommand.Stop,
                bodyLow: 0, bodyHigh: 0, leftTrigger: 0,
                rightTrigger: 0)));
            Assert.IsFalse(mailbox.TryPublish(CreateFrame(sequence: 3)));
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 1,
                ownershipEpoch: 2)));
        }

        [TestMethod]
        public void ClaimEmitsOneReleaseWhenAppliedRevisionExpires()
        {
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackClaimCursor cursor = default;
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 1,
                timestampMicroseconds: 1_000,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_049, ref cursor,
                    out ControllerFeedbackFrame first));
            Assert.AreEqual(1UL, first.Sequence);
            Assert.AreEqual(1UL, cursor.Revision);
            Assert.AreEqual(0UL, cursor.ReleaseRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(1_049, ref cursor, out _));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                mailbox.Claim(1_050, ref cursor,
                    out ControllerFeedbackFrame released));
            Assert.AreEqual(default(ControllerFeedbackFrame), released);
            Assert.AreEqual(1UL, cursor.ReleaseRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(2_000, ref cursor, out _));

            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 2,
                timestampMicroseconds: 1_050,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                mailbox.Claim(1_100, ref cursor,
                    out ControllerFeedbackFrame expired));
            Assert.AreEqual(default(ControllerFeedbackFrame), expired);
            Assert.AreEqual(2UL, cursor.Revision);
            Assert.AreEqual(2UL, cursor.ReleaseRevision);
            Assert.IsFalse(mailbox.TryReadFresh(1_100, out _, out ulong staleRevision));
            Assert.AreEqual(2UL, staleRevision);

            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 3,
                timestampMicroseconds: 1_100,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_100, ref cursor,
                    out ControllerFeedbackFrame recovered));
            Assert.AreEqual(3UL, recovered.Sequence);
            Assert.AreEqual(3UL, cursor.Revision);
        }

        [TestMethod]
        public void FarFutureFrameProducesOneRelease()
        {
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackClaimCursor cursor = default;
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(
                timestampMicroseconds: 10_001)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                mailbox.Claim(5_000, ref cursor,
                    out ControllerFeedbackFrame released));
            Assert.AreEqual(default(ControllerFeedbackFrame), released);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(5_000, ref cursor, out _));
        }

        [TestMethod]
        public void ConcurrentPublicationNeverExposesHybridSnapshot()
        {
            const ulong finalSequence = 30_000;
            ControllerFeedbackMailbox mailbox = new();
            Assert.IsTrue(mailbox.TryPublish(CreatePatternFrame(1)));
            int writerDone = 0;
            int publishFailure = 0;
            using ManualResetEvent start = new(false);
            Thread writer = new(() =>
            {
                start.WaitOne();
                for (ulong sequence = 2; sequence <= finalSequence;
                    sequence++)
                {
                    ControllerFeedbackFrame next =
                        CreatePatternFrame(sequence);
                    if (!mailbox.TryPublish(next))
                    {
                        Interlocked.Exchange(ref publishFailure, 1);
                        break;
                    }
                }
                Volatile.Write(ref writerDone, 1);
            });
            writer.Start();
            start.Set();

            do
            {
                Assert.IsTrue(mailbox.TryReadLatest(
                    out ControllerFeedbackFrame observed, out _));
                AssertPattern(observed);
            }
            while (Volatile.Read(ref writerDone) == 0);

            Assert.IsTrue(writer.Join(1_000));
            Assert.AreEqual(0, publishFailure);
            Assert.IsTrue(mailbox.TryReadLatest(
                out ControllerFeedbackFrame final, out _));
            Assert.AreEqual(finalSequence, final.Sequence);
            AssertPattern(final);
        }

        [TestMethod]
        public void PublishReadAndSpanSerializationAllocateZeroAfterWarmup()
        {
            const ulong iterations = 20_000;
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackClaimCursor cursor = default;
            Span<byte> packet = stackalloc byte[
                ControllerFeedbackFrame.SerializedLength];

            for (ulong sequence = 1; sequence <= 1_000; sequence++)
            {
                ControllerFeedbackFrame frame = CreatePatternFrame(sequence);
                mailbox.TryPublish(frame);
                mailbox.TryReadLatest(out _, out _);
                mailbox.Claim(frame.TimestampMicroseconds, ref cursor,
                    out _);
                frame.TryWriteTo(packet);
                ControllerFeedbackFrame.TryReadFrom(packet, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (ulong sequence = 1_001;
                sequence <= iterations + 1_000; sequence++)
            {
                ControllerFeedbackFrame frame = CreatePatternFrame(sequence);
                mailbox.TryPublish(frame);
                mailbox.TryReadLatest(out _, out _);
                if (mailbox.Claim(frame.TimestampMicroseconds, ref cursor,
                    out _) != ControllerFeedbackClaimDisposition.Frame)
                {
                    throw new InvalidOperationException("claim failed");
                }
                frame.TryWriteTo(packet);
                ControllerFeedbackFrame.TryReadFrom(packet, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                $"Canonical feedback publication allocated {allocated} bytes after warmup.");
        }

        private static ControllerFeedbackFrame CreatePatternFrame(
            ulong sequence)
        {
            return CreateFrame(bodyLow: Pattern(sequence, 1),
                bodyHigh: Pattern(sequence, 2),
                leftTrigger: Pattern(sequence, 3),
                rightTrigger: Pattern(sequence, 4), sequence: sequence,
                timestampMicroseconds: 100_000 + sequence);
        }

        private static void AssertPattern(in ControllerFeedbackFrame frame)
        {
            Assert.AreEqual(Pattern(frame.Sequence, 1), frame.BodyLow);
            Assert.AreEqual(Pattern(frame.Sequence, 2), frame.BodyHigh);
            Assert.AreEqual(Pattern(frame.Sequence, 3), frame.LeftTrigger);
            Assert.AreEqual(Pattern(frame.Sequence, 4), frame.RightTrigger);
            Assert.AreEqual(100_000UL + frame.Sequence,
                frame.TimestampMicroseconds);
            Assert.AreEqual(ControllerFeedbackActuators.All,
                frame.Actuators);
        }

        private static ushort Pattern(ulong sequence, byte salt) =>
            (ushort)(((sequence * (ulong)(salt * 257) + salt) %
                ushort.MaxValue) + 1);

        private static ControllerFeedbackFrame CreateFrame(
            ControllerFeedbackSource source =
                ControllerFeedbackSource.XboxOneVirtualDevice,
            ControllerFeedbackCommand command =
                ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators actuators =
                ControllerFeedbackActuators.All,
            ushort bodyLow = 1, ushort bodyHigh = 2,
            ushort leftTrigger = 3, ushort rightTrigger = 4,
            ulong sequence = 1,
            ulong deviceGeneration = 1, ulong transportGeneration = 1,
            ulong ownershipEpoch = 1, ulong timestampMicroseconds = 1_000,
            ulong timeToLiveMicroseconds = 50_000)
        {
            Assert.IsTrue(TryCreate(out ControllerFeedbackFrame frame,
                source, command, actuators, bodyLow,
                bodyHigh, leftTrigger, rightTrigger, sequence,
                deviceGeneration, transportGeneration, ownershipEpoch,
                timestampMicroseconds, timeToLiveMicroseconds));
            return frame;
        }

        private static bool TryCreate(
            out ControllerFeedbackFrame frame,
            ControllerFeedbackSource source =
                ControllerFeedbackSource.XboxOneVirtualDevice,
            ControllerFeedbackCommand command =
                ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators actuators =
                ControllerFeedbackActuators.All,
            ushort bodyLow = 1, ushort bodyHigh = 2,
            ushort leftTrigger = 3, ushort rightTrigger = 4,
            ulong sequence = 1,
            ulong deviceGeneration = 1, ulong transportGeneration = 1,
            ulong ownershipEpoch = 1, ulong timestampMicroseconds = 1_000,
            ulong timeToLiveMicroseconds = 50_000)
        {
            return ControllerFeedbackFrame.TryCreate(source, command,
                actuators, bodyLow, bodyHigh, leftTrigger, rightTrigger,
                sequence, deviceGeneration, transportGeneration,
                ownershipEpoch, timestampMicroseconds,
                timeToLiveMicroseconds, out frame);
        }
    }
}
