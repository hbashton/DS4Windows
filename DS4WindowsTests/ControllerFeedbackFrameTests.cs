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
        public void ClaimAdvancesOnlyAfterSuccessfulCompletionAndRetriesFailure()
        {
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackClaimCursor cursor = new();
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 1,
                timestampMicroseconds: 1_000,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_049, cursor,
                    out ControllerFeedbackFrame first,
                    out ulong firstToken));
            Assert.AreEqual(1UL, first.Sequence);
            Assert.AreNotEqual(0UL, firstToken);
            Assert.AreEqual(0UL, cursor.AppliedRevision);
            Assert.AreEqual(0UL, cursor.ReleasedRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(1_049, cursor, out _, out ulong blocked));
            Assert.AreEqual(0UL, blocked);
            ControllerFeedbackMailbox wrongMailbox = new();
            Assert.IsTrue(wrongMailbox.TryPublish(CreateFrame()));
            Assert.IsFalse(wrongMailbox.Complete(cursor, firstToken,
                delivered: true), "Another mailbox completed the claim.");
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                wrongMailbox.Claim(1_000, cursor, out _, out _),
                "A cursor was silently reused by another mailbox.");
            ControllerFeedbackClaimCursor wrongMailboxCursor = new();
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                wrongMailbox.Claim(1_000, wrongMailboxCursor, out _,
                    out ulong wrongMailboxToken));
            Assert.IsTrue(wrongMailbox.Complete(wrongMailboxCursor,
                wrongMailboxToken, delivered: true));
            Assert.IsFalse(mailbox.Complete(cursor, firstToken + 1,
                delivered: true), "A stale token disturbed the claim.");
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(1_049, cursor, out _, out _));
            Assert.IsTrue(mailbox.Complete(cursor, firstToken,
                delivered: false));
            Assert.AreEqual(0UL, cursor.AppliedRevision);

            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_049, cursor,
                    out ControllerFeedbackFrame retried,
                    out ulong retryToken));
            Assert.AreEqual(first, retried);
            Assert.AreNotEqual(firstToken, retryToken);
            Assert.IsFalse(mailbox.Complete(cursor, firstToken,
                delivered: true),
                "The first token completed an active retry claim.");
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(1_049, cursor, out _, out _),
                "A stale token disturbed the active retry claim.");
            Assert.IsTrue(mailbox.Complete(cursor, retryToken,
                delivered: true));
            Assert.AreEqual(1UL, cursor.AppliedRevision);
            Assert.AreEqual(0UL, cursor.ReleasedRevision);
            Assert.IsFalse(mailbox.Complete(cursor, retryToken,
                delivered: true), "A duplicate completion was accepted.");
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(1_049, cursor, out _, out _));

            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                mailbox.Claim(1_050, cursor,
                    out ControllerFeedbackFrame released,
                    out ulong releaseToken));
            Assert.AreEqual(default(ControllerFeedbackFrame), released);
            Assert.IsTrue(mailbox.Complete(cursor, releaseToken,
                delivered: false));
            Assert.AreEqual(0UL, cursor.ReleasedRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                mailbox.Claim(1_050, cursor, out _,
                    out ulong releaseRetryToken));
            Assert.IsTrue(mailbox.Complete(cursor, releaseRetryToken,
                delivered: true));
            Assert.AreEqual(1UL, cursor.ReleasedRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(2_000, cursor, out _, out _));

            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 2,
                timestampMicroseconds: 1_050,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                mailbox.Claim(1_100, cursor,
                    out ControllerFeedbackFrame expired,
                    out ulong expiredToken));
            Assert.AreEqual(default(ControllerFeedbackFrame), expired);
            Assert.IsTrue(mailbox.Complete(cursor, expiredToken,
                delivered: true));
            Assert.AreEqual(2UL, cursor.AppliedRevision);
            Assert.AreEqual(2UL, cursor.ReleasedRevision);
            Assert.IsFalse(mailbox.TryReadFresh(1_100, out _, out ulong staleRevision));
            Assert.AreEqual(2UL, staleRevision);

            Assert.IsTrue(mailbox.TryPublish(CreateFrame(sequence: 3,
                timestampMicroseconds: 1_100,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_100, cursor,
                    out ControllerFeedbackFrame recovered,
                    out ulong recoveredToken));
            Assert.AreEqual(3UL, recovered.Sequence);
            Assert.IsTrue(mailbox.Complete(cursor, recoveredToken,
                delivered: true));
            Assert.AreEqual(3UL, cursor.AppliedRevision);
        }

        [TestMethod]
        public void EmptyMailboxBindsCursorPermanently()
        {
            ControllerFeedbackMailbox empty = new();
            ControllerFeedbackMailbox other = new();
            ControllerFeedbackClaimCursor cursor = new();

            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                empty.Claim(1_000, cursor, out _, out ulong emptyToken));
            Assert.AreEqual(0UL, emptyToken);
            Assert.AreSame(empty, cursor.OwnerMailbox);

            Assert.IsTrue(other.TryPublish(CreateFrame()));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                other.Claim(1_000, cursor, out _, out _));

            ControllerFeedbackClaimCursor otherCursor = new();
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                other.Claim(1_000, otherCursor, out _,
                    out ulong otherToken));
            Assert.IsTrue(other.Complete(otherCursor, otherToken,
                delivered: true));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                empty.Claim(1_000, otherCursor, out _, out _),
                "A completed cursor changed mailbox ownership.");
        }

        [TestMethod]
        public void NewerPublicationSupersedesAnInFlightRevision()
        {
            ControllerFeedbackMailbox successful = new();
            ControllerFeedbackClaimCursor successCursor = new();
            ControllerFeedbackFrame first = CreateFrame(sequence: 1);
            ControllerFeedbackFrame second = CreateFrame(sequence: 2);
            Assert.IsTrue(successful.TryPublish(first));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                successful.Claim(1_000, successCursor, out _,
                    out ulong firstToken));
            Assert.IsTrue(successful.TryPublish(second));
            Assert.IsTrue(successful.Complete(successCursor, firstToken,
                delivered: true));
            Assert.AreEqual(1UL, successCursor.AppliedRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                successful.Claim(1_000, successCursor,
                    out ControllerFeedbackFrame newest,
                    out ulong newestToken));
            Assert.AreEqual(second, newest);
            Assert.IsTrue(successful.Complete(successCursor, newestToken,
                delivered: true));
            Assert.AreEqual(2UL, successCursor.AppliedRevision);

            ControllerFeedbackMailbox failed = new();
            ControllerFeedbackClaimCursor failureCursor = new();
            Assert.IsTrue(failed.TryPublish(first));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                failed.Claim(1_000, failureCursor, out _,
                    out ulong failedToken));
            Assert.IsTrue(failed.TryPublish(second));
            Assert.IsTrue(failed.Complete(failureCursor, failedToken,
                delivered: false));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                failed.Claim(1_000, failureCursor,
                    out ControllerFeedbackFrame afterFailure,
                    out ulong afterFailureToken));
            Assert.AreEqual(second, afterFailure,
                "A replaced revision was replayed after failure.");
            Assert.IsTrue(failed.Complete(failureCursor,
                afterFailureToken, delivered: true));
        }

        [TestMethod]
        public void PreAdmissionRevalidationPreventsStaleActuation()
        {
            ControllerFeedbackFrame apply = CreateFrame(sequence: 1,
                timestampMicroseconds: 1_000,
                timeToLiveMicroseconds: 50);
            ControllerFeedbackFrame stop = CreateFrame(sequence: 2,
                command: ControllerFeedbackCommand.Stop,
                bodyLow: 0, bodyHigh: 0, leftTrigger: 0,
                rightTrigger: 0, timestampMicroseconds: 1_001,
                timeToLiveMicroseconds: 50);

            ControllerFeedbackMailbox alreadyAdmitted = new();
            ControllerFeedbackClaimCursor admittedCursor = new();
            Assert.IsTrue(alreadyAdmitted.TryPublish(apply));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                alreadyAdmitted.Claim(1_000, admittedCursor, out _,
                    out ulong admittedToken));
            Assert.IsTrue(alreadyAdmitted.CanDeliver(admittedCursor,
                admittedToken, 1_000));
            Assert.IsTrue(alreadyAdmitted.TryPublish(stop));
            Assert.IsTrue(alreadyAdmitted.Complete(admittedCursor,
                admittedToken, delivered: true),
                "An already admitted Apply lost its terminal completion.");
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                alreadyAdmitted.Claim(1_001, admittedCursor,
                    out ControllerFeedbackFrame admittedStop,
                    out ulong admittedStopToken));
            Assert.IsTrue(admittedStop.IsStop);
            Assert.IsTrue(alreadyAdmitted.CanDeliver(admittedCursor,
                admittedStopToken, 1_001));
            Assert.IsTrue(alreadyAdmitted.Complete(admittedCursor,
                admittedStopToken, delivered: true));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                alreadyAdmitted.Claim(1_051, admittedCursor, out _, out _),
                "A completed Stop produced a duplicate expiry release.");

            ControllerFeedbackMailbox notAdmitted = new();
            ControllerFeedbackClaimCursor stoppedCursor = new();
            Assert.IsTrue(notAdmitted.TryPublish(apply));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                notAdmitted.Claim(1_000, stoppedCursor, out _,
                    out ulong staleApplyToken));
            Assert.IsTrue(notAdmitted.TryPublish(stop));
            Assert.IsFalse(notAdmitted.CanDeliver(stoppedCursor,
                staleApplyToken, 1_001),
                "A superseded Apply remained eligible for admission.");
            Assert.IsTrue(notAdmitted.Complete(stoppedCursor,
                staleApplyToken, delivered: false));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                notAdmitted.Claim(1_001, stoppedCursor,
                    out ControllerFeedbackFrame directStop,
                    out ulong directStopToken));
            Assert.IsTrue(directStop.IsStop);
            Assert.IsTrue(notAdmitted.CanDeliver(stoppedCursor,
                directStopToken, 1_001));
            Assert.IsTrue(notAdmitted.Complete(stoppedCursor,
                directStopToken, delivered: true));

            ControllerFeedbackMailbox expired = new();
            ControllerFeedbackClaimCursor expiredCursor = new();
            Assert.IsTrue(expired.TryPublish(apply));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                expired.Claim(1_049, expiredCursor, out _,
                    out ulong expiringToken));
            Assert.IsFalse(expired.CanDeliver(expiredCursor,
                expiringToken, 1_050),
                "An expired Apply remained eligible for admission.");
            Assert.IsTrue(expired.Complete(expiredCursor, expiringToken,
                delivered: false));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                expired.Claim(1_050, expiredCursor, out _,
                    out ulong releaseToken));
            Assert.IsTrue(expired.CanDeliver(expiredCursor, releaseToken,
                1_050));
            Assert.IsTrue(expired.Complete(expiredCursor, releaseToken,
                delivered: true));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                expired.Claim(2_000, expiredCursor, out _, out _));
        }

        [TestMethod]
        public void PreAdmissionRevalidationRejectsInvalidClaimIdentity()
        {
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackMailbox other = new();
            ControllerFeedbackClaimCursor cursor = new();
            ControllerFeedbackClaimCursor wrongCursor = new();
            Assert.IsTrue(mailbox.TryPublish(CreateFrame()));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_000, cursor, out _, out ulong token));

            Assert.IsFalse(mailbox.CanDeliver(cursor, 0, 1_000));
            Assert.IsFalse(mailbox.CanDeliver(cursor, token + 1, 1_000));
            Assert.IsFalse(other.CanDeliver(cursor, token, 1_000));
            Assert.IsFalse(mailbox.CanDeliver(wrongCursor, token, 1_000));
            Assert.IsTrue(mailbox.CanDeliver(cursor, token, 1_000),
                "Invalid checks disturbed the original claim.");
            Assert.IsTrue(mailbox.Complete(cursor, token,
                delivered: false));

            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_000, cursor, out _, out ulong retryToken));
            Assert.AreNotEqual(token, retryToken);
            Assert.IsFalse(mailbox.CanDeliver(cursor, token, 1_000),
                "A stale token admitted the retry claim.");
            Assert.IsTrue(mailbox.CanDeliver(cursor, retryToken, 1_000));
            Assert.IsTrue(mailbox.Complete(cursor, retryToken,
                delivered: true));
            Assert.IsFalse(mailbox.CanDeliver(cursor, retryToken, 1_000),
                "A completed token remained admissible.");
        }

        [TestMethod]
        public void TokenWrapSkipsZeroAndReferenceAliasCannotDuplicateCompletion()
        {
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackClaimCursor cursor = new()
            {
                NextToken = ulong.MaxValue,
            };
            ControllerFeedbackClaimCursor alias = cursor;
            Assert.IsTrue(mailbox.TryPublish(CreateFrame()));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_000, cursor, out _, out ulong token));
            Assert.AreEqual(1UL, token);
            Assert.IsTrue(mailbox.Complete(alias, token, delivered: true));
            Assert.IsFalse(mailbox.Complete(cursor, token,
                delivered: true),
                "A reference alias duplicated a completed token.");
        }

        [TestMethod]
        public void FailedDeliveryFinallyMakesTheClaimRetryable()
        {
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackClaimCursor cursor = new();
            Assert.IsTrue(mailbox.TryPublish(CreateFrame()));
            ulong token = 0;
            bool delivered = false;

            try
            {
                Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                    mailbox.Claim(1_000, cursor, out _, out token));
                throw new InvalidOperationException("simulated write failure");
            }
            catch (InvalidOperationException)
            {
                // A transport owner records/logs the write failure here.
            }
            finally
            {
                Assert.IsTrue(mailbox.Complete(cursor, token, delivered));
            }

            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                mailbox.Claim(1_000, cursor, out _, out ulong retryToken));
            Assert.AreNotEqual(token, retryToken);
            Assert.IsTrue(mailbox.Complete(cursor, retryToken,
                delivered: true));
        }

        [TestMethod]
        public void CompletedStopSuppressesExpiryReleaseButNeutralDoesNot()
        {
            ControllerFeedbackMailbox stopMailbox = new();
            ControllerFeedbackClaimCursor stopCursor = new();
            Assert.IsTrue(stopMailbox.TryPublish(CreateFrame(
                command: ControllerFeedbackCommand.Stop,
                bodyLow: 0, bodyHigh: 0, leftTrigger: 0, rightTrigger: 0,
                timestampMicroseconds: 1_000,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                stopMailbox.Claim(1_000, stopCursor,
                    out ControllerFeedbackFrame stop,
                    out ulong stopToken));
            Assert.IsTrue(stop.IsStop);
            Assert.IsTrue(stopMailbox.Complete(stopCursor, stopToken,
                delivered: true));
            Assert.AreEqual(1UL, stopCursor.AppliedRevision);
            Assert.AreEqual(1UL, stopCursor.ReleasedRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                stopMailbox.Claim(1_050, stopCursor, out _, out _));

            ControllerFeedbackMailbox neutralMailbox = new();
            ControllerFeedbackClaimCursor neutralCursor = new();
            Assert.IsTrue(neutralMailbox.TryPublish(CreateFrame(
                command: ControllerFeedbackCommand.Neutral,
                bodyLow: 0, bodyHigh: 0, leftTrigger: 0, rightTrigger: 0,
                timestampMicroseconds: 1_000,
                timeToLiveMicroseconds: 50)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Frame,
                neutralMailbox.Claim(1_000, neutralCursor, out _,
                    out ulong neutralToken));
            Assert.IsTrue(neutralMailbox.Complete(neutralCursor,
                neutralToken, delivered: true));
            Assert.AreEqual(1UL, neutralCursor.AppliedRevision);
            Assert.AreEqual(0UL, neutralCursor.ReleasedRevision);
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                neutralMailbox.Claim(1_050, neutralCursor, out _,
                    out ulong neutralReleaseToken));
            Assert.IsTrue(neutralMailbox.Complete(neutralCursor,
                neutralReleaseToken, delivered: true));
        }

        [TestMethod]
        public void FarFutureFrameProducesOneRelease()
        {
            ControllerFeedbackMailbox mailbox = new();
            ControllerFeedbackClaimCursor cursor = new();
            Assert.IsTrue(mailbox.TryPublish(CreateFrame(
                timestampMicroseconds: 10_001)));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.Release,
                mailbox.Claim(5_000, cursor,
                    out ControllerFeedbackFrame released,
                    out ulong releaseToken));
            Assert.AreEqual(default(ControllerFeedbackFrame), released);
            Assert.IsTrue(mailbox.Complete(cursor, releaseToken,
                delivered: true));
            Assert.AreEqual(ControllerFeedbackClaimDisposition.None,
                mailbox.Claim(5_000, cursor, out _, out _));
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
            ControllerFeedbackClaimCursor cursor = new();
            Span<byte> packet = stackalloc byte[
                ControllerFeedbackFrame.SerializedLength];

            for (ulong sequence = 1; sequence <= 1_000; sequence++)
            {
                ControllerFeedbackFrame frame = CreatePatternFrame(sequence);
                mailbox.TryPublish(frame);
                mailbox.TryReadLatest(out _, out _);
                mailbox.Claim(frame.TimestampMicroseconds, cursor,
                    out _, out ulong claimToken);
                mailbox.Complete(cursor, claimToken, delivered: true);
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
                if (mailbox.Claim(frame.TimestampMicroseconds, cursor,
                    out _, out ulong claimToken) !=
                        ControllerFeedbackClaimDisposition.Frame ||
                    !mailbox.Complete(cursor, claimToken,
                        delivered: true))
                {
                    throw new InvalidOperationException(
                        "claim completion failed");
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
