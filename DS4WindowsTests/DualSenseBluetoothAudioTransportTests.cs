using DS4Windows;
using DS4Windows.InputDevices;
using System.Collections;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseBluetoothAudioTransportTests
    {
        [TestMethod]
        public void SaturatedWriterRetainsLogicalReportUnlessTransportFailed()
        {
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.ShouldRetainSaturatedWrite(
                    accepted: false, transportFault: false));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.ShouldRetainSaturatedWrite(
                    accepted: true, transportFault: false));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.ShouldRetainSaturatedWrite(
                    accepted: false, transportFault: true));
        }

        [TestMethod]
        public void PairedTransportUsesMeasuredTransportWindowsWritePoolAndHostClock()
        {
            Assert.AreEqual(8,
                DualSenseBluetoothAudioPacer.PairedAudioTransportSlotCount);
            Assert.AreEqual(8,
                DualSenseBluetoothAudioPacer.PairedAudioInFlightLimit);
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldWaitForPhysicalWriteCredit(
                        measuredTransportAudioTransport: false,
                        pairedAudioReports: true));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldWaitForPhysicalWriteCredit(
                        measuredTransportAudioTransport: true,
                        pairedAudioReports: false));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.
                    ShouldWaitForPhysicalWriteCredit(
                        measuredTransportAudioTransport: false,
                        pairedAudioReports: false));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldApplyInputPhaseCorrection(
                        compactCombinedTransport: false,
                        pairedAudioReports: true));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldApplyInputPhaseCorrection(
                        compactCombinedTransport: true,
                        pairedAudioReports: false));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldApplyInputPhaseCorrection(
                        compactCombinedTransport: false,
                        pairedAudioReports: false));
        }

        [TestMethod]
        public void PairedTransportDropsWholeSaturatedAudioGeneration()
        {
            Assert.IsTrue(DualSenseBluetoothAudioPacer.
                ShouldDropSaturatedAudio(
                    measuredTransportAudioTransport: false,
                    pairedAudioReport: true,
                    controlOnly: false,
                    accepted: false,
                    transportFault: false));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                ShouldDropSaturatedAudio(
                    measuredTransportAudioTransport: false,
                    pairedAudioReport: true,
                    controlOnly: false,
                    accepted: false,
                    transportFault: true));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                ShouldDropSaturatedAudio(
                    measuredTransportAudioTransport: false,
                    pairedAudioReport: false,
                    controlOnly: false,
                    accepted: false,
                    transportFault: false));
        }

        [TestMethod]
        public void MicrophoneAndStateBarrierLeavesOddFrameForNewMode()
        {
            Assert.AreEqual(0,
                DualSenseBluetoothAudioPacer.
                    CompletePairedReportBoundary(0));
            Assert.AreEqual(0,
                DualSenseBluetoothAudioPacer.
                    CompletePairedReportBoundary(1));
            Assert.AreEqual(2,
                DualSenseBluetoothAudioPacer.
                    CompletePairedReportBoundary(3));
            Assert.AreEqual(6,
                DualSenseBluetoothAudioPacer.
                    CompletePairedReportBoundary(6));
        }

        [TestMethod]
        public void SaturatedReportsReturnToExactFifoHead()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<int>(4);
            Assert.IsTrue(ring.TryEnqueue(30));
            Assert.IsTrue(ring.TryEnqueue(40));
            Assert.IsTrue(ring.TryEnqueueFront(20));
            Assert.IsTrue(ring.TryEnqueueFront(10));

            foreach (int expected in new[] { 10, 20, 30, 40 })
            {
                Assert.IsTrue(ring.TryDequeue(out int actual));
                Assert.AreEqual(expected, actual);
            }
        }

        [TestMethod]
        public void SaturatedPairedReportReturnsAtomicallyToFifoHead()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<int>(5);
            Assert.IsTrue(ring.TryEnqueue(1));
            Assert.IsTrue(ring.TryEnqueue(2));
            Assert.IsTrue(ring.TryEnqueue(30));
            Assert.IsTrue(ring.TryDequeue(out _));
            Assert.IsTrue(ring.TryDequeue(out _));
            Assert.IsTrue(ring.TryEnqueue(40));
            Assert.IsTrue(ring.TryEnqueue(50));
            Assert.IsTrue(ring.TryEnqueuePairFront(10, 20));

            foreach (int expected in new[] { 10, 20, 30, 40, 50 })
            {
                Assert.IsTrue(ring.TryDequeue(out int actual));
                Assert.AreEqual(expected, actual);
            }
        }

        [TestMethod]
        public void OrderedGroupReplacementKeepsRetainedFifoOrder()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<int>(6);
            foreach (int value in new[] { 1, 2, 3, 4 })
            {
                Assert.IsTrue(ring.TryEnqueue(value));
            }

            Assert.IsTrue(ring.TryReplaceWhereWithGroup(
                value => (value & 1) == 0, new[] { 7, 8, 9 }));

            foreach (int expected in new[] { 1, 3, 7, 8, 9 })
            {
                Assert.IsTrue(ring.TryDequeue(out int actual));
                Assert.AreEqual(expected, actual);
            }
        }

        [TestMethod]
        public void FailedOrderedGroupReplacementLeavesFifoUntouched()
        {
            var ring = new DualSenseBluetoothAudioPacerRing<int>(5);
            foreach (int value in new[] { 1, 2, 3, 4, 5 })
            {
                Assert.IsTrue(ring.TryEnqueue(value));
            }

            Assert.IsFalse(ring.TryReplaceWhereWithGroup(
                value => value == 2, new[] { 7, 8 }));

            foreach (int expected in new[] { 1, 2, 3, 4, 5 })
            {
                Assert.IsTrue(ring.TryDequeue(out int actual));
                Assert.AreEqual(expected, actual);
            }
        }

        [TestMethod]
        public void PairedReportPreservesBothSequentialAudioFrames()
        {
            byte[] first = CreateSpeakerReport(0x11, 0x21, 0x31);
            byte[] second = CreateSpeakerReport(0x12, 0x22, 0x32);
            byte[] paired = new byte[
                DualSenseBluetoothPairedAudioReportBuilder.ReportLength];

            DualSenseBluetoothPairedAudioReportBuilder.Build(first, second,
                reportSequence: 7, packetSequence: 0x42, paired);

            Assert.AreEqual(0x39, paired[0]);
            Assert.AreEqual(0x70, paired[1]);
            Assert.AreEqual(0x91, paired[2]);
            Assert.AreEqual(6, paired[3]);
            Assert.AreEqual(0x7E, paired[4]);
            Assert.AreEqual(0x42, paired[9]);
            Assert.AreEqual(0xD2, paired[10]);
            Assert.AreEqual(64, paired[11]);
            Assert.AreEqual(0x11, paired[12]);
            Assert.AreEqual(0x12, paired[76]);
            Assert.AreEqual(0xD3, paired[140]);
            Assert.AreEqual(200, paired[141]);
            Assert.AreEqual(0x21, paired[142]);
            Assert.AreEqual(0x22, paired[342]);
            uint expectedCrc =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(paired,
                    paired.Length - sizeof(uint));
            Assert.AreEqual(expectedCrc,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    paired.AsSpan(paired.Length - sizeof(uint))));
        }

        [TestMethod]
        public void PhysicalSequenceMatchesCombinedReportReferenceControlThenAudio()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] control = CreateSpeakerReport(0x00, 0x00, 0x00);

            sequence.PrepareControl(control);

            Assert.AreEqual((byte)0x00, control[1]);
            Assert.AreEqual((byte)0x00, control[10]);
            sequence.Commit(audio: false);
            Assert.AreEqual((byte)1, sequence.NextReportSequence);
            Assert.AreEqual((byte)0, sequence.MediaPacketSequence,
                "A control-only report consumed the media packet counter.");

            byte[] first = CreateSpeakerReport(0x11, 0x21, 0xA0, 1);
            byte[] second = CreateSpeakerReport(0x12, 0x22, 0xB0, 2);
            byte[] paired = new byte[
                DualSenseBluetoothPairedAudioReportBuilder.ReportLength];
            sequence.PreparePairedAudio(first, second, paired);

            Assert.AreEqual((byte)0x10, paired[1],
                "The first 0x39 did not follow the accepted control report.");
            Assert.AreEqual((byte)2, paired[9],
                "The first 0x39 did not publish the combined-report reference's first two-frame media counter.");
            sequence.Commit(audio: true);
            Assert.AreEqual((byte)2, sequence.NextReportSequence);
            Assert.AreEqual((byte)2, sequence.MediaPacketSequence);
        }

        [TestMethod]
        public void PhysicalSequenceMatchesV5NativeMicrophoneTransition()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] initialization = CreateSpeakerReport(
                0x00, 0x00, 0x50, 0x22);
            byte[] microphoneStatus = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    MicrophoneStatusReportLength];

            sequence.PrepareMicrophoneStatus(enabled: true, initialization,
                microphoneStatus);

            Assert.AreEqual((byte)0x32, microphoneStatus[0]);
            Assert.AreEqual((byte)0x50, microphoneStatus[1]);
            Assert.AreEqual((byte)0x91, microphoneStatus[2]);
            Assert.AreEqual((byte)0x07, microphoneStatus[3]);
            Assert.AreEqual((byte)0xFF, microphoneStatus[4]);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)0x80, microphoneStatus[index]);
            }
            Assert.AreEqual((byte)0x23, microphoneStatus[10],
                "The native 0x32 did not consume one media interval.");
            uint expectedCrc =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                    microphoneStatus,
                    microphoneStatus.Length - sizeof(uint));
            Assert.AreEqual(expectedCrc,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    microphoneStatus.AsSpan(
                        microphoneStatus.Length - sizeof(uint))));

            sequence.CommitMicrophoneStatus();
            byte[] duplex = CreateSpeakerReport(
                0x11, 0x21, 0xA0, 0x23);
            duplex[4] = 0xFF;
            sequence.PrepareFullDuplexAudio(duplex);
            Assert.AreEqual((byte)0x50, duplex[1],
                "The dedicated 0x32 sequence disturbed the audio sequence.");
            Assert.AreEqual((byte)0x24, duplex[10],
                "The first audio frame did not follow the 0x32 media interval.");
            Assert.AreEqual((byte)6,
                sequence.NextMicrophoneStatusSequence);
        }

        [TestMethod]
        public void NativeMicrophoneTransitionsMatchV5WireOrdering()
        {
            Assert.AreEqual(0,
                DualSenseBluetoothAudioPacer.
                    GetNativeMicrophoneTransitionReportsAhead(
                        committedMicrophoneEnabled: false,
                        requestedMicrophoneEnabled: true),
                "Enable must send 0x32 before its first duplex 0x36.");
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    GetNativeMicrophonePresentationMode(
                        committedMicrophoneEnabled: false,
                        requestedMicrophoneEnabled: true));

            Assert.AreEqual(2,
                DualSenseBluetoothAudioPacer.
                    GetNativeMicrophoneTransitionReportsAhead(
                        committedMicrophoneEnabled: true,
                        requestedMicrophoneEnabled: false),
                "Disable must send two speaker-only 0x36 reports before 0x32.");
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    GetNativeMicrophonePresentationMode(
                        committedMicrophoneEnabled: true,
                        requestedMicrophoneEnabled: false));
        }

        [TestMethod]
        public void NativeMicrophoneDisableConsumesSharedMediaCounterInOrder()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] lastDuplex = CreateSpeakerReport(
                0x00, 0x00, 0x50, 0x5B);
            lastDuplex[4] = 0xFF;
            byte[] firstSpeakerOnly = CreateSpeakerReport(
                0x00, 0x00, 0x50, 0x5C);
            byte[] secondSpeakerOnly = CreateSpeakerReport(
                0x00, 0x00, 0x50, 0x5D);
            byte[] microphoneStatus = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    MicrophoneStatusReportLength];

            sequence.PrepareFullDuplexAudio(lastDuplex);
            Assert.AreEqual((byte)0x5B, lastDuplex[10]);
            sequence.Commit(audio: true);
            sequence.PrepareNativeAudio(firstSpeakerOnly);
            Assert.AreEqual((byte)0x5C, firstSpeakerOnly[10]);
            sequence.Commit(audio: true);
            sequence.PrepareNativeAudio(secondSpeakerOnly);
            Assert.AreEqual((byte)0x5D, secondSpeakerOnly[10]);
            sequence.Commit(audio: true);
            byte nextReportSequence = sequence.NextReportSequence;
            sequence.PrepareMicrophoneStatus(enabled: false, secondSpeakerOnly,
                microphoneStatus);

            Assert.AreEqual((byte)0x5E, microphoneStatus[10],
                "0x32 overtook one of the two speaker-only media intervals.");
            Assert.AreEqual((byte)0xFE, microphoneStatus[4]);
            sequence.CommitMicrophoneStatus();
            Assert.AreEqual(nextReportSequence, sequence.NextReportSequence,
                "The independent 0x32 sequence disturbed 0x36 ordering.");

            byte[] nextSpeakerOnly = CreateSpeakerReport(
                0x00, 0x00, 0x50, 0x00);
            sequence.PrepareNativeAudio(nextSpeakerOnly);
            Assert.AreEqual((byte)0x5F, nextSpeakerOnly[10]);
        }

        [TestMethod]
        public void RejectedNativeMicrophoneTransitionIsExactlyRetriable()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] initialization = CreateSpeakerReport(
                0x00, 0x00, 0x50, 0x22);
            byte[] firstAttempt = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    MicrophoneStatusReportLength];
            byte[] retry = new byte[firstAttempt.Length];

            sequence.PrepareMicrophoneStatus(enabled: true, initialization,
                firstAttempt);
            sequence.PrepareMicrophoneStatus(enabled: true, initialization,
                retry);

            CollectionAssert.AreEqual(firstAttempt, retry,
                "A rejected 0x32 changed before the HID writer accepted it.");
            Assert.AreEqual((byte)5,
                sequence.NextMicrophoneStatusSequence);
            Assert.AreEqual((byte)0x22, sequence.MediaPacketSequence,
                "Preparing an unaccepted 0x32 consumed the media counter.");
            Assert.AreEqual((byte)5, sequence.NextReportSequence,
                "Preparing an unaccepted 0x32 disturbed the audio sequence.");

            sequence.CommitMicrophoneStatus();

            Assert.AreEqual((byte)6,
                sequence.NextMicrophoneStatusSequence);
            Assert.AreEqual((byte)0x23, sequence.MediaPacketSequence);
            Assert.AreEqual((byte)5, sequence.NextReportSequence,
                "Committing 0x32 disturbed the independent audio sequence.");
        }

        [TestMethod]
        public void CombinedReportReferenceControllerStateUsesGlobalOutputSequence()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] initialization = CreateSpeakerReport(
                0, 0, 0x50, 0x22);
            byte[] state = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength];
            for (int index = 0; index < state.Length; index++)
            {
                state[index] = (byte)(index + 1);
            }
            byte[] report = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStateReportLength];

            sequence.PrepareControllerState(state, initialization, report);

            Assert.AreEqual((byte)0x31, report[0]);
            Assert.AreEqual((byte)0x50, report[1]);
            Assert.AreEqual((byte)0x10, report[2]);
            CollectionAssert.AreEqual(state,
                report.AsSpan(3, state.Length).ToArray());
            for (int index = 50; index < 74; index++)
            {
                Assert.AreEqual((byte)0, report[index]);
            }
            uint expectedCrc =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(report,
                    report.Length - sizeof(uint));
            Assert.AreEqual(expectedCrc,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    report.AsSpan(report.Length - sizeof(uint))));

            sequence.CommitControllerState();
            Assert.AreEqual((byte)6,
                sequence.NextControllerStateSequence);
            Assert.AreEqual((byte)6, sequence.NextReportSequence,
                "The 0x31 latch did not consume the shared output sequence.");
            Assert.AreEqual((byte)0x22, sequence.MediaPacketSequence,
                "A 0x31 latch update consumed the media counter.");

            byte[] first = CreateSpeakerReport(1, 2, 0, 0x23);
            byte[] second = CreateSpeakerReport(3, 4, 0, 0x24);
            byte[] paired = new byte[
                DualSenseBluetoothPairedAudioReportBuilder.ReportLength];
            sequence.PreparePairedAudio(first, second, paired);
            Assert.AreEqual((byte)0x60, paired[1],
                "The 0x39 report did not follow the ordered 0x31 state latch.");
        }

        [TestMethod]
        public void PairedDuplexCarriesMicrophoneHapticsAndSpeakerTogether()
        {
            byte[] first = CreateSpeakerReport(0x31, 0x41, 0, 1);
            byte[] second = CreateSpeakerReport(0x32, 0x42, 0, 2);
            first[4] = second[4] = 0xFF;
            byte[] paired = new byte[
                DualSenseBluetoothPairedAudioReportBuilder.ReportLength];

            DualSenseBluetoothPairedAudioReportBuilder.Build(first, second,
                reportSequence: 4, packetSequence: 2, paired);

            Assert.AreEqual((byte)0x39, paired[0]);
            Assert.AreEqual((byte)0x7F, paired[4],
                "The paired carrier did not arm the microphone lane.");
            Assert.AreEqual((byte)0x31, paired[12]);
            Assert.AreEqual((byte)0x32, paired[76]);
            Assert.AreEqual((byte)0x41, paired[142]);
            Assert.AreEqual((byte)0x42, paired[342]);
        }

        [TestMethod]
        public void MicrophoneEnabledAudioUsesV5128ByteDepths()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] duplex = CreateSpeakerReport(
                0x11, 0x21, 0x20, 0x33);
            duplex[4] = 0xFF;
            for (int index = 5; index <= 9; index++)
            {
                duplex[index] = 80;
            }
            duplex[11] = 0x90;
            duplex[12] = 63;

            Assert.IsTrue(DualSenseBluetoothAudioPacer.
                RequiresFullDuplexAudioReport(duplex));
            sequence.PrepareFullDuplexAudio(duplex);

            Assert.AreEqual((byte)0x36, duplex[0]);
            Assert.AreEqual((byte)0xFF, duplex[4]);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)0x80, duplex[index]);
            }
            Assert.AreEqual((byte)0x90, duplex[11]);
            Assert.AreEqual((byte)63, duplex[12]);
            Assert.AreEqual((byte)0xD2, duplex[76]);
            Assert.AreEqual((byte)0x93, duplex[142]);

            uint expectedCrc =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(duplex,
                    duplex.Length - sizeof(uint));
            Assert.AreEqual(expectedCrc,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    duplex.AsSpan(duplex.Length - sizeof(uint))));

            byte[] playback = CreateSpeakerReport(0, 0, 0);
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                RequiresFullDuplexAudioReport(playback));
        }

        [TestMethod]
        public void NativeAudioPreparationNormalizesEveryMediaLaneDepth()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] duplex = CreateSpeakerReport(0x31, 0x41, 0x90, 1);
            duplex[4] = 0xFF;
            for (int index = 5; index <= 9; index++)
            {
                duplex[index] = 80;
            }

            sequence.PrepareNativeAudio(duplex);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)0x80, duplex[index]);
            }

            byte[] speakerOnly = CreateSpeakerReport(0x32, 0x42, 0xA0, 2);
            speakerOnly[4] = 0xFE;
            for (int index = 5; index <= 9; index++)
            {
                speakerOnly[index] = 80;
            }

            sequence.PrepareNativeAudio(speakerOnly);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)0x80, speakerOnly[index]);
            }
        }

        [TestMethod]
        public void PhysicalSequenceAdvancesOnlyAfterAcceptedPairedReport()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] first = CreateSpeakerReport(0x11, 0x21, 0x00, 1);
            byte[] second = CreateSpeakerReport(0x12, 0x22, 0x10, 2);
            byte[] initial = new byte[
                DualSenseBluetoothPairedAudioReportBuilder.ReportLength];
            byte[] retried = new byte[initial.Length];
            byte[] following = new byte[initial.Length];

            sequence.PreparePairedAudio(first, second, initial);
            sequence.PreparePairedAudio(first, second, retried);
            CollectionAssert.AreEqual(initial, retried,
                "A rejected/uncommitted physical write spent sequence numbers.");

            sequence.Commit(audio: true);
            first[10] = 3;
            second[10] = 4;
            sequence.PreparePairedAudio(first, second, following);
            Assert.AreEqual((byte)0x10, following[1]);
            Assert.AreEqual((byte)4, following[9]);
        }

        [TestMethod]
        public void PhysicalSequencePresentsSingleAudioAtNativeCadenceShape()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] first = CreateSpeakerReport(0x11, 0x21, 0xA0, 1);
            byte[] second = CreateSpeakerReport(0x12, 0x22, 0xB0, 2);

            sequence.PrepareSingleAudio(first);
            Assert.AreEqual((byte)0xA0, first[1]);
            Assert.AreEqual((byte)1, first[10]);
            Assert.AreEqual((byte)0x36, first[0]);
            sequence.Commit(audio: true);

            sequence.PrepareSingleAudio(second);
            Assert.AreEqual((byte)0xB0, second[1]);
            Assert.AreEqual((byte)2, second[10]);
            Assert.AreEqual((byte)0x36, second[0]);
            sequence.Commit(audio: true);

            Assert.AreEqual((byte)12, sequence.NextReportSequence);
            Assert.AreEqual((byte)2, sequence.MediaPacketSequence);
            Assert.AreEqual(0,
                DualSenseBluetoothAudioPacer.ControllerReserveTransferIntervals,
                "The 0x36 route must not replay the paired 5 ms startup burst.");
            Assert.AreEqual(32,
                DualSenseBluetoothAudioPacer.SingleAudioTransportSlotCount);
            Assert.AreEqual(32,
                DualSenseBluetoothAudioPacer.SingleAudioInFlightLimit,
                "V5 advances a 32-slot OVERLAPPED ownership ring.");
        }

        [TestMethod]
        public void MeasuredTransportReportBuildsExactCompactSpeakerPacket()
        {
            byte[] source = CreateSpeakerReport(0x5A, 0x21, 0x70, 0x42);
            byte[] original = (byte[])source.Clone();
            for (int index = 0; index < 200; index++)
            {
                source[144 + index] = (byte)(index ^ 0xA5);
                original[144 + index] = source[144 + index];
            }
            byte[] report = new byte[
                DualSenseBluetoothMeasuredTransportAudioReportBuilder.ReportLength];

            DualSenseBluetoothMeasuredTransportAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, report);

            Assert.AreEqual(334, report.Length);
            Assert.AreEqual((byte)0x35, report[0]);
            Assert.AreEqual((byte)0x90, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)7, report[3]);
            Assert.AreEqual((byte)0xFE, report[4]);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)96, report[index]);
            }
            Assert.AreEqual((byte)0x42, report[10]);
            Assert.AreEqual((byte)0x93, report[11]);
            Assert.AreEqual((byte)200, report[12]);
            CollectionAssert.AreEqual(source.Skip(144).Take(200).ToArray(),
                report.Skip(13).Take(200).ToArray());
            AssertRangeIsZero(report, 213, report.Length - sizeof(uint) - 213,
                "The compact report tail contained unexpected data.");
            uint expectedCrc =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(report,
                    report.Length - sizeof(uint));
            Assert.AreEqual(expectedCrc,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    report.AsSpan(report.Length - sizeof(uint))));
            CollectionAssert.AreEqual(original, source,
                "The pure compact-report builder mutated its logical source.");
        }

        [TestMethod]
        public void MeasuredTransportReportPreservesAuxDestination()
        {
            byte[] source = CreateSpeakerReport(0, 0, 0, 0x42);
            source[142] = 0x96;
            byte[] report = new byte[
                DualSenseBluetoothMeasuredTransportAudioReportBuilder.ReportLength];

            DualSenseBluetoothMeasuredTransportAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, report);

            Assert.AreEqual((byte)0x96, report[11]);
            Assert.AreEqual((byte)200, report[12]);
            CollectionAssert.AreEqual(source.Skip(144).Take(200).ToArray(),
                report.Skip(13).Take(200).ToArray());
        }

        [TestMethod]
        public void MeasuredTransportPhysicalSequenceAdvancesOnlyAfterAcceptedWrites()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] first = CreateSpeakerReport(0x11, 0x21, 0xA0, 7);
            byte[] initial = new byte[
                DualSenseBluetoothMeasuredTransportAudioReportBuilder.ReportLength];
            byte[] retry = new byte[initial.Length];

            sequence.PrepareMeasuredTransportAudio(first, initial);
            sequence.PrepareMeasuredTransportAudio(first, retry);
            CollectionAssert.AreEqual(initial, retry,
                "An uncommitted compact write spent a physical sequence.");
            Assert.AreEqual((byte)0xA0, initial[1]);
            Assert.AreEqual((byte)7, initial[10]);

            sequence.Commit(audio: true);
            Assert.AreEqual((byte)11, sequence.NextReportSequence);
            Assert.AreEqual((byte)7, sequence.MediaPacketSequence);

            byte[] control = CreateSpeakerReport(0, 0, 0);
            sequence.PrepareControl(control);
            Assert.AreEqual((byte)0xB0, control[1]);
            Assert.AreEqual((byte)7, control[10]);
            sequence.Commit(audio: false);

            byte[] second = CreateSpeakerReport(0x12, 0x22, 0, 8);
            sequence.PrepareMeasuredTransportAudio(second, retry);
            Assert.AreEqual((byte)0xC0, retry[1]);
            Assert.AreEqual((byte)8, retry[10]);
        }

        [TestMethod]
        public void CompactCombinedReportCarriesHapticsAndSpeakerAtomically()
        {
            byte[] source = CreateSpeakerReport(0, 0, 0x50, 0x42);
            for (int index = 0; index < 64; index++)
            {
                source[78 + index] = (byte)(index ^ 0x5A);
            }
            for (int index = 0; index < 200; index++)
            {
                source[144 + index] = (byte)(index ^ 0xA5);
            }
            byte[] report = new byte[
                DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.
                    ReportLength];

            DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, report);

            Assert.AreEqual(334, report.Length);
            Assert.AreEqual((byte)0x35, report[0]);
            Assert.AreEqual((byte)0x90, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)7, report[3]);
            Assert.AreEqual((byte)0xFE, report[4]);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)96, report[index]);
            }
            Assert.AreEqual((byte)0x42, report[10]);
            Assert.AreEqual((byte)0x92, report[11]);
            Assert.AreEqual((byte)64, report[12]);
            CollectionAssert.AreEqual(source.Skip(78).Take(64).ToArray(),
                report.Skip(13).Take(64).ToArray());
            Assert.AreEqual((byte)0x93, report[77]);
            Assert.AreEqual((byte)200, report[78]);
            CollectionAssert.AreEqual(source.Skip(144).Take(200).ToArray(),
                report.Skip(79).Take(200).ToArray());
            AssertRangeIsZero(report, 279,
                report.Length - sizeof(uint) - 279,
                "The combined report tail contained unexpected data.");
            uint expectedCrc =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(report,
                    report.Length - sizeof(uint));
            Assert.AreEqual(expectedCrc,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    report.AsSpan(report.Length - sizeof(uint))));
        }

        [TestMethod]
        public void CompactCombinedReportPreservesAuxDestination()
        {
            byte[] source = CreateSpeakerReport(0, 0, 0, 0x42);
            source[142] = 0x96;
            byte[] report = new byte[
                DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.
                    ReportLength];

            DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, report);

            Assert.AreEqual((byte)0x92, report[11]);
            Assert.AreEqual((byte)64, report[12]);
            Assert.AreEqual((byte)0x96, report[77]);
            Assert.AreEqual((byte)200, report[78]);
            CollectionAssert.AreEqual(source.Skip(144).Take(200).ToArray(),
                report.Skip(79).Take(200).ToArray());
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CompactAudioReportsUseReferenceHeaderForEachMicrophoneMode(
            bool microphoneEnabled)
        {
            byte[] source = CreateSpeakerReport(0, 0, 0, 0x42);
            source[4] = (byte)(0xFE | (microphoneEnabled ? 1 : 0));
            byte[] speaker = new byte[
                DualSenseBluetoothMeasuredTransportAudioReportBuilder.ReportLength];
            byte[] combined = new byte[
                DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.
                    ReportLength];

            DualSenseBluetoothMeasuredTransportAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, speaker);
            DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, combined);

            if (microphoneEnabled)
            {
                Assert.AreEqual((byte)7, speaker[3]);
                Assert.AreEqual((byte)7, combined[3]);
                Assert.AreEqual((byte)0xFF, speaker[4]);
                Assert.AreEqual((byte)0xFF, combined[4]);
                for (int index = 5; index <= 9; index++)
                {
                    Assert.AreEqual((byte)96, speaker[index]);
                    Assert.AreEqual((byte)96, combined[index]);
                }
                Assert.AreEqual((byte)0x42, speaker[10]);
                Assert.AreEqual((byte)0x42, combined[10]);
                Assert.AreEqual((byte)0x93, speaker[11]);
                Assert.AreEqual((byte)200, speaker[12]);
                Assert.AreEqual((byte)0x92, combined[11]);
                Assert.AreEqual((byte)64, combined[12]);
                Assert.AreEqual((byte)0x93, combined[77]);
                Assert.AreEqual((byte)200, combined[78]);
            }
            else
            {
                Assert.AreEqual((byte)7, speaker[3]);
                Assert.AreEqual((byte)7, combined[3]);
                Assert.AreEqual((byte)0xFE, speaker[4]);
                Assert.AreEqual((byte)0xFE, combined[4]);
                for (int index = 5; index <= 9; index++)
                {
                    Assert.AreEqual((byte)96, speaker[index]);
                    Assert.AreEqual((byte)96, combined[index]);
                }
                Assert.AreEqual((byte)0x42, speaker[10]);
                Assert.AreEqual((byte)0x42, combined[10]);
                Assert.AreEqual((byte)0x93, speaker[11]);
                Assert.AreEqual((byte)200, speaker[12]);
                Assert.AreEqual((byte)0x92, combined[11]);
                Assert.AreEqual((byte)64, combined[12]);
                Assert.AreEqual((byte)0x93, combined[77]);
                Assert.AreEqual((byte)200, combined[78]);
            }
        }

        [TestMethod]
        public void PendingMicrophoneModeCannotOvertakePhysicalCommitBoundary()
        {
            byte[] source = CreateSpeakerReport(0x34, 0x56, 0x70, 0x42);
            source[4] = 0xFF;
            byte[] expectedHaptics = source.Skip(78).Take(64).ToArray();
            byte[] expectedSpeaker = source.Skip(144).Take(200).ToArray();
            byte[] physical = new byte[
                DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.
                    ReportLength];

            DualSenseBluetoothAudioPacer.ApplyCommittedMicrophoneMode(source,
                microphoneEnabled: false);
            DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, physical);
            Assert.AreEqual((byte)0xFE, physical[4]);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)96, physical[index]);
            }
            CollectionAssert.AreEqual(expectedHaptics,
                physical.Skip(13).Take(64).ToArray());
            CollectionAssert.AreEqual(expectedSpeaker,
                physical.Skip(79).Take(200).ToArray());

            DualSenseBluetoothAudioPacer.ApplyCommittedMicrophoneMode(source,
                microphoneEnabled: true);
            DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.Build(source,
                reportSequence: 10, packetSequence: 0x43, physical);
            Assert.AreEqual((byte)7, physical[3]);
            Assert.AreEqual((byte)0xFF, physical[4]);
            for (int index = 5; index <= 9; index++)
            {
                Assert.AreEqual((byte)96, physical[index]);
            }
            Assert.AreEqual((byte)0x43, physical[10]);
            CollectionAssert.AreEqual(expectedHaptics,
                physical.Skip(13).Take(64).ToArray());
            CollectionAssert.AreEqual(expectedSpeaker,
                physical.Skip(79).Take(200).ToArray());
        }

        [TestMethod]
        public void LegacyCompactTransportSelectorsAreIgnored()
        {
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseMeasuredTransportAudioTransport("35"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseMeasuredTransportAudioTransport("35combined"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseCompactCombinedHapticsTransport("35combined"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseCompactCombinedHapticsTransport("35"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseMeasuredTransportAudioTransport(null));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseMeasuredTransportAudioTransport("36"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseMeasuredTransportAudioTransport("0x35"));
        }

        [TestMethod]
        public void NativeAudioCarriesControllerStateAtomically()
        {
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                RequiresSeparateControllerStateTransport(null));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                RequiresSeparateControllerStateTransport("36"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                RequiresSeparateControllerStateTransport("35"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                RequiresSeparateControllerStateTransport("35combined"));
        }

        private static byte[] CreateSpeakerReport(byte haptics,
            byte audio, byte sequence, byte packetSequence = 0)
        {
            byte[] report = new byte[DualSenseBluetoothAudioPacer.ReportLength];
            report[0] = 0x36;
            report[1] = sequence;
            report[4] = 0xFE;
            report[10] = packetSequence;
            report[5] = report[6] = report[7] = report[8] = 64;
            report[76] = 0xD2;
            report[77] = 64;
            Array.Fill(report, haptics, 78, 64);
            report[142] = 0x93;
            report[143] = 200;
            Array.Fill(report, audio, 144, 200);
            return report;
        }

        private static readonly FieldInfo ConnectionTypeField =
            typeof(DS4Device).GetField("conType",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo EventQueueField =
            typeof(DS4Device).GetField("eventQueue",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo HasInputEventsField =
            typeof(DS4Device).GetField("hasInputEvts",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OutputTransportStoppingField =
            typeof(DualSenseDevice).GetField(
                "bluetoothOutputTransportStopping",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MicrophoneStreamingRequestedField =
            typeof(DualSenseDevice).GetField(
                "bluetoothMicrophoneStreamingRequested",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MicrophoneControlPendingField =
            typeof(DualSenseDevice).GetField(
                "bluetoothMicrophoneControlUpdatePending",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerClockActiveClaimField =
            typeof(DualSenseDevice).GetField(
                "bluetoothSpeakerClockActiveClaim",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerClockLeaseExpiryField =
            typeof(DualSenseDevice).GetField(
                "bluetoothSpeakerClockLeaseExpiryTimestamp",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerReportSequenceField =
            typeof(DualSenseDevice).GetField(
                "bluetoothCombinedSpeakerReportSequence",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerPacketSequenceField =
            typeof(DualSenseDevice).GetField(
                "bluetoothCombinedSpeakerPacketSequence",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CachedCombinedReportField =
            typeof(DualSenseDevice).GetField(
                "latestBluetoothCombinedSpeakerReport",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NativeStateTimestampField =
            typeof(DualSenseDevice).GetField(
                "latestBluetoothCombinedNativeStateTimestamp",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo BuildCombinedControlReportMethod =
            typeof(DualSenseDevice).GetMethod(
                "BuildBluetoothCombinedControlReport",
                BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo UpdateCachedCombinedStateMethod =
            typeof(DualSenseDevice).GetMethod(
                "UpdateCachedBluetoothCombinedState",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo
            UpdateCachedCombinedStateFromBluetoothOutputMethod =
                typeof(DualSenseDevice).GetMethod(
                    "UpdateCachedBluetoothCombinedStateFromBluetoothOutput",
                    BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ReleaseNativeGameOutputOwnershipMethod =
            typeof(DualSenseDevice).GetMethod(
                "ReleaseNativeGameOutputOwnership",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo DrainQueuedInputEventsMethod =
            typeof(DualSenseDevice).GetMethod(
                "DrainQueuedInputEvents",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RecordBluetoothMicrophoneFrameMethod =
            typeof(DualSenseDevice).GetMethod(
                "RecordBluetoothMicrophoneFrame",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo
            ApplyBluetoothMicrophoneStreamingRequestMethod =
                typeof(DualSenseDevice).GetMethod(
                    "ApplyBluetoothMicrophoneStreamingRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, types: new[] { typeof(byte[]) },
                    modifiers: null);
        private static readonly MethodInfo
            RequiresCompletionAwareBluetoothControlWriteMethod =
                typeof(DualSenseDevice).GetMethod(
                    "RequiresCompletionAwareBluetoothControlWrite",
                    BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo ClaimBluetoothSpeakerClockMethod =
            typeof(DualSenseDevice).GetMethod(
                "ClaimBluetoothSpeakerClock",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo
            PacerReferenceRetainsBluetoothTransportOwnershipMethod =
                typeof(DualSenseDevice).GetMethod(
                    "PacerReferenceRetainsBluetoothTransportOwnership",
                    BindingFlags.Static | BindingFlags.NonPublic);

        [TestMethod]
        public void DiagnosticPcmTraceHasRecoverableStreamingHeaderImmediately()
        {
            string path = Path.Combine(Path.GetTempPath(),
                $"ds4windows-dualsense-trace-{Guid.NewGuid():N}.wav");
            try
            {
                using Pcm16WaveTraceWriter writer =
                    Pcm16WaveTraceWriter.TryCreate(path, 48000, 2);
                Assert.IsNotNull(writer);

                byte[] header = new byte[44];
                using (var stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                {
                    Assert.AreEqual(header.Length, stream.Read(header, 0,
                        header.Length));
                }

                CollectionAssert.AreEqual("RIFF"u8.ToArray(),
                    header.AsSpan(0, 4).ToArray());
                CollectionAssert.AreEqual("WAVE"u8.ToArray(),
                    header.AsSpan(8, 4).ToArray());
                CollectionAssert.AreEqual("data"u8.ToArray(),
                    header.AsSpan(36, 4).ToArray());
                Assert.AreEqual(uint.MaxValue,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        header.AsSpan(40, 4)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void DiagnosticPcmTraceDrainsAndFinalizesExactWaveLengths()
        {
            string path = Path.Combine(Path.GetTempPath(),
                $"ds4windows-dualsense-trace-{Guid.NewGuid():N}.wav");
            byte[] pcm = Enumerable.Range(0, 4096)
                .Select(index => (byte)(index * 31)).ToArray();
            try
            {
                using (Pcm16WaveTraceWriter writer =
                    Pcm16WaveTraceWriter.TryCreate(path, 32000, 2))
                {
                    Assert.IsNotNull(writer);
                    writer.Write(pcm, 0, pcm.Length);
                }

                byte[] wave = File.ReadAllBytes(path);
                Assert.AreEqual(44 + pcm.Length, wave.Length);
                Assert.AreEqual((uint)(36 + pcm.Length),
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        wave.AsSpan(4, 4)));
                Assert.AreEqual((uint)pcm.Length,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        wave.AsSpan(40, 4)));
                CollectionAssert.AreEqual(pcm,
                    wave.AsSpan(44).ToArray());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void MicrophoneControlWriteBypassesInputEventQueue()
        {
            DualSenseDevice device = CreateBluetoothDevice();

            try
            {
                // A hardware-less device may reject a direct HID write. The
                // regression contract is that microphone control must never be
                // serialized behind input reports, so only queue state matters.
                device.SetBluetoothMicrophoneStreaming(true);
            }
            catch
            {
            }

            Assert.AreEqual(0, GetEventQueue(device).Count,
                "Mic control entered the input event queue and can starve when mic input stalls.");
            Assert.IsFalse(GetFieldValue<bool>(HasInputEventsField, device),
                "Mic control marked an input-thread event pending.");
        }

        [TestMethod]
        public void MicrophoneOnlyInputCanDrainOrdinaryControllerEvents()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            int invoked = 0;
            device.queueEvent(() => invoked++);

            Assert.IsNotNull(DrainQueuedInputEventsMethod);
            DrainQueuedInputEventsMethod.Invoke(device, null);

            Assert.AreEqual(1, invoked);
            Assert.AreEqual(0, GetEventQueue(device).Count);
            Assert.IsFalse(GetFieldValue<bool>(HasInputEventsField, device));
        }

        [TestMethod]
        public void ShutdownOwnershipGateRejectsLateSpeakerFrameBeforeHandoff()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(OutputTransportStoppingField, device, 1);

            bool accepted = device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200);

            Assert.IsFalse(accepted,
                "A late producer callback restarted Bluetooth output during shutdown.");
            Assert.IsFalse(device.BluetoothCombinedOutputTransportEnabled,
                "The rejected callback initialized a new combined transport.");
            Assert.AreEqual(1L, device.BluetoothSpeakerFramesDropped);
        }

        [TestMethod]
        public void FaultedPacerReferenceStillRetainsTransportOwnership()
        {
            Assert.IsNotNull(
                PacerReferenceRetainsBluetoothTransportOwnershipMethod);

            Assert.IsTrue((bool)
                PacerReferenceRetainsBluetoothTransportOwnershipMethod.Invoke(
                    null, new object[] { true }),
                "A retained faulted/stopping helper reference allowed a competing direct HID writer.");
            Assert.IsFalse((bool)
                PacerReferenceRetainsBluetoothTransportOwnershipMethod.Invoke(
                    null, new object[] { false }));
        }

        [TestMethod]
        public void FailedFirstSpeakerSubmissionDoesNotClaimActiveClock()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;

            bool accepted = device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200);

            Assert.IsFalse(accepted,
                "The hardware-less speaker submission unexpectedly succeeded.");
            Assert.AreEqual(0L, GetFieldValue<long>(
                SpeakerClockActiveClaimField, device),
                "A failed first frame left a false active speaker clock.");
            Assert.AreEqual(0L, GetFieldValue<long>(
                SpeakerClockLeaseExpiryField, device));
        }

        [TestMethod]
        public void RejectedSpeakerSubmissionDoesNotConsumeSequence()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;
            SetFieldValue(SpeakerReportSequenceField, device, (byte)9);
            SetFieldValue(SpeakerPacketSequenceField, device, (byte)41);

            Assert.IsFalse(device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200));
            Assert.AreEqual((byte)9, GetFieldValue<byte>(
                SpeakerReportSequenceField, device));
            Assert.AreEqual((byte)41, GetFieldValue<byte>(
                SpeakerPacketSequenceField, device));
        }

        [TestMethod]
        public void FailedLaterSpeakerSubmissionPreservesPriorAcceptedLease()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;
            Assert.IsNotNull(ClaimBluetoothSpeakerClockMethod);
            long existingClaim = (long)ClaimBluetoothSpeakerClockMethod.Invoke(
                device, new object[] { 3000 });
            long existingExpiry = GetFieldValue<long>(
                SpeakerClockLeaseExpiryField, device);

            bool accepted = device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200);

            Assert.IsFalse(accepted,
                "The hardware-less speaker submission unexpectedly succeeded.");
            Assert.AreEqual(existingClaim, GetFieldValue<long>(
                SpeakerClockActiveClaimField, device),
                "A failed later frame cleared the lease earned by queued audio.");
            Assert.AreEqual(existingExpiry, GetFieldValue<long>(
                SpeakerClockLeaseExpiryField, device),
                "A failed later frame falsely extended the active clock lease.");
        }

        [TestMethod]
        public void AudioOnlyCombinedCarrierPreservesProfileLightbarAndHaptics()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.LightBarColor = new DS4Color(12, 34, 56);
            byte[] report = BuildCombinedControlReport(0, 0, false);
            report[78] = 0x44;
            report[79] = 0x55;

            device.WriteBluetoothCombinedHapticsAudioOutputReport(report, 0,
                report.Length, hasNativeGameState: false);

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            AssertV5AudioContract(cached);
            Assert.AreEqual((byte)12, cached[13 + 44]);
            Assert.AreEqual((byte)34, cached[13 + 45]);
            Assert.AreEqual((byte)56, cached[13 + 46]);
            Assert.AreEqual((byte)0x44, cached[78]);
            Assert.AreEqual((byte)0x55, cached[79]);
            AssertRangeIsZero(cached, 80, 62,
                "Audio-only carrier haptics were not copied atomically.");
            Assert.AreEqual(0L, GetFieldValue<long>(
                NativeStateTimestampField, device));
        }

        [TestMethod]
        public void NativeGameCarrierPreservesDynamicStateWithoutReplacingV5AudioContract()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.LightBarColor = new DS4Color(12, 34, 56);
            byte[] report = BuildCombinedControlReport(0, 0, false);
            report[13] = 0x02;
            report[14] = 0x08;
            report[15] = 0x41;
            report[16] = 0x52;
            report[17] = 0x13;
            report[18] = 0x24;
            report[19] = 0x35;
            report[20] = 0x46;
            report[22] = 0xA0;
            for (int index = 23; index <= 49; index++)
            {
                report[index] = (byte)(0x60 + index - 23);
            }
            report[50] = 0x57;
            report[56] = 0x1B;
            report[13 + 44] = 90;
            report[13 + 45] = 91;
            report[13 + 46] = 92;

            device.WriteBluetoothCombinedHapticsAudioOutputReport(report, 0,
                report.Length, hasNativeGameState: true);

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            AssertV5AudioContract(cached, expectedFlag0: 0xF2,
                expectedFlag1: 0x8B);
            Assert.AreEqual((byte)0x41, cached[15]);
            Assert.AreEqual((byte)0x52, cached[16]);
            for (int index = 23; index <= 49; index++)
            {
                Assert.AreEqual(report[index], cached[index],
                    $"Native trigger/effect state changed at byte {index}.");
            }
            Assert.AreEqual((byte)0x1B, cached[56]);
            Assert.AreEqual((byte)90, cached[13 + 44]);
            Assert.AreEqual((byte)91, cached[13 + 45]);
            Assert.AreEqual((byte)92, cached[13 + 46]);
            Assert.IsTrue(GetFieldValue<long>(NativeStateTimestampField,
                device) > 0);
        }

        [TestMethod]
        public void IdleNativeCarrierPreservesGameSelectedVibrationMode()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            byte[] report = BuildCombinedControlReport(0, 0, false);
            report[13] = 0x02;
            report[15] = 0x00;
            report[16] = 0x00;

            device.WriteBluetoothCombinedHapticsAudioOutputReport(report, 0,
                report.Length, hasNativeGameState: true);

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            AssertV5AudioContract(cached, expectedFlag0: 0xF2,
                expectedFlag1: 0xF7);
        }

        [TestMethod]
        public void UsbStateMergePreservesV5AudioContractAndDynamicControls()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            byte[] initial = BuildCombinedControlReport(0, 0, false);
            initial[78] = 0x21;
            initial[79] = 0x43;
            device.WriteBluetoothCombinedHapticsAudioOutputReport(initial, 0,
                initial.Length, hasNativeGameState: false);

            byte[] usb = new byte[48];
            usb[0] = 0x02;
            usb[1] = 0x00;
            usb[2] = 0x08;
            usb[3] = 0x00;
            usb[4] = 0x00;
            usb[5] = 0x13;
            usb[6] = 0x24;
            usb[7] = 0x35;
            usb[8] = 0x46;
            usb[9] = 0x01;
            usb[10] = 0xA0;
            for (int relativeIndex = 10; relativeIndex <= 36;
                relativeIndex++)
            {
                usb[1 + relativeIndex] =
                    (byte)(0x70 + relativeIndex - 10);
            }
            usb[38] = 0x57;
            usb[44] = 0x1C;
            usb[45] = 0x81;
            usb[46] = 0x82;
            usb[47] = 0x83;

            Assert.IsNotNull(UpdateCachedCombinedStateMethod);
            Assert.IsTrue((bool)UpdateCachedCombinedStateMethod.Invoke(device,
                new object[] { usb, 0 }));

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            AssertV5AudioContract(cached, expectedFlag0: 0xF0,
                expectedFlag1: 0x8B);
            Assert.AreEqual((byte)0x00, cached[15]);
            Assert.AreEqual((byte)0x00, cached[16]);
            Assert.AreEqual((byte)0x00, cached[21]);
            for (int relativeIndex = 10; relativeIndex <= 36;
                relativeIndex++)
            {
                int combinedIndex = 13 + relativeIndex;
                Assert.AreEqual(usb[1 + relativeIndex], cached[combinedIndex],
                    $"USB trigger/effect state changed at relative byte {relativeIndex}.");
            }
            Assert.AreEqual((byte)0x1C, cached[56]);
            Assert.AreEqual((byte)0x81, cached[57]);
            Assert.AreEqual((byte)0x82, cached[58]);
            Assert.AreEqual((byte)0x83, cached[59]);
            Assert.AreEqual((byte)0x21, cached[78],
                "A profile/lightbar state merge cleared active haptics.");
            Assert.AreEqual((byte)0x43, cached[79],
                "A profile/lightbar state merge cleared active haptics.");
        }

        [TestMethod]
        public void NativeGameStateRemainsAuthoritativeUntilVirtualPadDetaches()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            byte[] native = BuildCombinedControlReport(0, 0, false);
            native[13] = 0x0C;
            native[14] = 0x14;
            native[23] = 0x21;
            native[24] = 0xFC;
            native[34] = 0x22;
            native[35] = 0xFD;
            device.WriteBluetoothCombinedHapticsAudioOutputReport(native, 0,
                native.Length, hasNativeGameState: true);

            // Reproduce the former failure: a later DS4Windows profile output
            // attempted to replace a game's latched trigger state after 100 ms.
            SetFieldValue(NativeStateTimestampField, device, 1L);
            byte[] profile = new byte[78];
            profile[0] = 0x31;
            profile[2] = 0xF0;
            profile[3] = 0xC3;
            profile[12] = 0;
            profile[23] = 0;

            Assert.IsNotNull(
                UpdateCachedCombinedStateFromBluetoothOutputMethod);
            Assert.IsTrue((bool)
                UpdateCachedCombinedStateFromBluetoothOutputMethod.Invoke(
                    device, new object[] { profile }));

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            Assert.AreEqual((byte)0x21, cached[23]);
            Assert.AreEqual((byte)0xFC, cached[24]);
            Assert.AreEqual((byte)0x22, cached[34]);
            Assert.AreEqual((byte)0xFD, cached[35]);
            Assert.AreEqual((byte)0xFC, cached[13],
                "The profile writer replaced the game's native validity bits.");
            Assert.AreEqual((byte)0x97, cached[14]);
        }

        [TestMethod]
        public void VirtualPadDetachReturnsStateOwnershipToActiveProfile()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            byte[] native = BuildCombinedControlReport(0, 0, false);
            native[13] = 0x0C;
            native[23] = 0x21;
            native[34] = 0x22;
            device.WriteBluetoothCombinedHapticsAudioOutputReport(native, 0,
                native.Length, hasNativeGameState: true);

            Assert.IsNotNull(ReleaseNativeGameOutputOwnershipMethod);
            ReleaseNativeGameOutputOwnershipMethod.Invoke(device, null);
            Assert.AreEqual(0L, GetFieldValue<long>(
                NativeStateTimestampField, device));

            byte[] profile = new byte[78];
            profile[0] = 0x31;
            profile[2] = 0xF0;
            profile[3] = 0xC3;
            profile[12] = 0;
            profile[23] = 0;
            Assert.IsTrue((bool)
                UpdateCachedCombinedStateFromBluetoothOutputMethod.Invoke(
                    device, new object[] { profile }));

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            Assert.AreEqual((byte)0, cached[23]);
            Assert.AreEqual((byte)0, cached[34]);
            Assert.AreEqual((byte)0xF0, cached[13]);
            Assert.AreEqual((byte)0xC3, cached[14]);
        }

        [TestMethod]
        public void ShutdownOwnershipGateRejectsMicrophoneRearmWithoutMutation()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(OutputTransportStoppingField, device, 1);

            bool accepted = device.SetBluetoothMicrophoneStreaming(true);

            Assert.IsFalse(accepted,
                "A late VIIPER microphone re-arm was accepted during shutdown.");
            Assert.AreEqual(0, GetFieldValue<int>(
                MicrophoneStreamingRequestedField, device),
                "The rejected re-arm changed requested microphone state.");
            Assert.AreEqual(0, GetFieldValue<int>(
                MicrophoneControlPendingField, device),
                "The rejected re-arm created a pending control transition.");
            Assert.IsFalse(device.BluetoothCombinedOutputTransportEnabled,
                "The rejected re-arm initialized a new combined transport.");
        }

        [DataTestMethod]
        [DataRow(true, true, false, true)]
        [DataRow(true, false, false, true)]
        [DataRow(false, false, true, false)]
        [DataRow(false, false, false, false)]
        [DataRow(false, true, true, false)]
        [DataRow(false, true, false, false)]
        public void OnlyExplicitControlBarrierWaitsForPhysicalCompletion(
            bool completionRequested, bool speakerClockActive,
            bool pacerOwnsTransport, bool expected)
        {
            Assert.IsNotNull(
                RequiresCompletionAwareBluetoothControlWriteMethod);
            bool actual = (bool)
                RequiresCompletionAwareBluetoothControlWriteMethod.Invoke(null,
                    new object[] { completionRequested, speakerClockActive,
                        pacerOwnsTransport });

            Assert.AreEqual(expected, actual,
                "Ordinary idle helper control must queue physically without blocking the caller for completion.");
        }

        [TestMethod]
        public void PhysicalMicrophoneFrameCommitsPendingEnable()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(MicrophoneStreamingRequestedField, device, 1);
            SetFieldValue(MicrophoneControlPendingField, device, 1);
            byte[] microphoneReport = new byte[78];
            microphoneReport[0] = 0x31;
            microphoneReport[1] = 0x02;

            Assert.IsNotNull(RecordBluetoothMicrophoneFrameMethod);
            RecordBluetoothMicrophoneFrameMethod.Invoke(device,
                new object[] { microphoneReport });

            Assert.AreEqual(0, GetFieldValue<int>(
                MicrophoneControlPendingField, device),
                "Physical input did not commit the pending microphone enable.");
            Assert.AreEqual(1L, device.BluetoothMicrophoneFramesReceived);
        }

        [TestMethod]
        public void LateMicrophoneFrameCannotCommitPendingDisable()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(MicrophoneStreamingRequestedField, device, 0);
            SetFieldValue(MicrophoneControlPendingField, device, 1);
            byte[] microphoneReport = new byte[78];
            microphoneReport[0] = 0x31;
            microphoneReport[1] = 0x02;

            Assert.IsNotNull(RecordBluetoothMicrophoneFrameMethod);
            RecordBluetoothMicrophoneFrameMethod.Invoke(device,
                new object[] { microphoneReport });

            Assert.AreEqual(1, GetFieldValue<int>(
                MicrophoneControlPendingField, device),
                "An in-flight microphone packet falsely committed disable.");
            Assert.AreEqual(0L, device.BluetoothMicrophoneFramesReceived);
        }

        [TestMethod]
        public void PendingMicrophoneEnableMapsProfileMaximumToPhysicalAdcCeiling()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.MicrophoneVolume = byte.MaxValue;
            SetFieldValue(MicrophoneStreamingRequestedField, device, 1);
            SetFieldValue(MicrophoneControlPendingField, device, 1);
            byte[] report = BuildCombinedControlReport(
                sequence: 0, packetSequence: 0, microphoneEnabled: false);
            report[13] &= unchecked((byte)~0x40);
            report[19] = 0;

            Assert.IsNotNull(
                ApplyBluetoothMicrophoneStreamingRequestMethod);
            ApplyBluetoothMicrophoneStreamingRequestMethod.Invoke(device,
                new object[] { report });

            Assert.AreNotEqual(0, report[4] & 0x01,
                "The physical microphone stream-enable bit was not set.");
            Assert.AreNotEqual(0, report[13] & 0x40,
                "The controller was not told that microphone volume is valid.");
            Assert.AreEqual((byte)0xFF, report[19],
                "The combined transport must retain the native transport's full-scale microphone gain.");
        }

        [TestMethod]
        public void CommittedMicrophoneEnableDoesNotReplayAdcControl()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.MicrophoneVolume = byte.MaxValue;
            SetFieldValue(MicrophoneStreamingRequestedField, device, 1);
            SetFieldValue(MicrophoneControlPendingField, device, 0);
            byte[] report = BuildCombinedControlReport(
                sequence: 0, packetSequence: 0, microphoneEnabled: false);
            report[13] &= unchecked((byte)~0x40);
            report[19] = 0;

            Assert.IsNotNull(
                ApplyBluetoothMicrophoneStreamingRequestMethod);
            ApplyBluetoothMicrophoneStreamingRequestMethod.Invoke(device,
                new object[] { report });

            Assert.AreNotEqual(0, report[4] & 0x01);
            Assert.AreEqual(0, report[13] & 0x40,
                "Steady-state audio frames must not replay the one-shot ADC control bit.");
            Assert.AreEqual((byte)0, report[19]);
        }

        [DataTestMethod]
        [DataRow(false, (byte)0xFE)]
        [DataRow(true, (byte)0xFF)]
        public void CombinedControlReportMatchesKnownGoodVdsLayout(
            bool microphoneEnabled, byte expectedAudioControl)
        {
            byte[] report = BuildCombinedControlReport(
                sequence: 0x0A,
                packetSequence: 0x37,
                microphoneEnabled);

            Assert.AreEqual(398, report.Length);
            Assert.AreEqual((byte)0x36, report[0]);
            Assert.AreEqual((byte)0xA0, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)0x07, report[3]);
            Assert.AreEqual(expectedAudioControl, report[4]);
            for (int index = 5; index <= 9; index++)
            {
                // VIIPER's canonical control/state baseline requests the
                // minimum documented queue. A report carrying a real speaker
                // frame separately raises these fields to 0x80.
                Assert.AreEqual((byte)0x10, report[index],
                    $"Unexpected packet 0x11 buffer depth at byte {index}.");
            }
            Assert.AreEqual((byte)0x37, report[10]);

            Assert.AreEqual((byte)0x90, report[11]);
            Assert.AreEqual((byte)63, report[12]);
            CollectionAssert.AreEqual(BuildExpectedDefaultState(),
                CopyRange(report, 13, 63),
                "Packet 0x10 did not contain the known-good vDS default state.");

            Assert.AreEqual((byte)0x92, report[76]);
            Assert.AreEqual((byte)64, report[77]);
            AssertRangeIsZero(report, 78, 64,
                "The control report's haptics lane was not silent.");

            AssertRangeIsZero(report, 142, report.Length - 4 - 142,
                "The control report unexpectedly included a speaker TLV or Opus data.");
            AssertCrcIsValid(report);
        }

        private static byte[] BuildCombinedControlReport(byte sequence,
            byte packetSequence, bool microphoneEnabled)
        {
            Assert.IsNotNull(BuildCombinedControlReportMethod);
            return (byte[])BuildCombinedControlReportMethod.Invoke(null,
                new object[] { sequence, packetSequence, microphoneEnabled });
        }

        private static byte[] BuildExpectedDefaultState()
        {
            byte[] state = new byte[63];
            byte[] knownState =
            {
                0xFD, 0xF7, 0x00, 0x00, 0x64, 0x64, 0xFF, 0x09,
                0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x07, 0x00,
                0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00,
            };
            Array.Copy(knownState, state, knownState.Length);
            return state;
        }

        private static void AssertV5AudioContract(byte[] report,
            byte expectedFlag0 = 0xFD, byte expectedFlag1 = 0xF7)
        {
            Assert.AreEqual(expectedFlag0, report[13]);
            Assert.AreEqual(expectedFlag1, report[14]);
            Assert.AreEqual((byte)0x64, report[17]);
            Assert.AreEqual((byte)0x64, report[18]);
            Assert.AreEqual((byte)0xFF, report[19]);
            Assert.AreEqual((byte)0x09, report[20]);
            Assert.AreEqual((byte)0x0F, report[22]);
            Assert.AreEqual((byte)0x0A, report[50]);
        }

        private static byte[] CopyRange(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        private static void AssertRangeIsZero(byte[] report, int offset,
            int length, string message)
        {
            for (int index = offset; index < offset + length; index++)
            {
                Assert.AreEqual((byte)0, report[index],
                    $"{message} Unexpected byte at offset {index}.");
            }
        }

        private static DualSenseDevice CreateBluetoothDevice()
        {
            var hidDevice = (HidDevice)RuntimeHelpers.GetUninitializedObject(
                typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice, "Bluetooth transport test");
            SetFieldValue(ConnectionTypeField, device, ConnectionType.BT);
            return device;
        }

        private static ICollection GetEventQueue(DualSenseDevice device)
        {
            return GetFieldValue<ICollection>(EventQueueField, device);
        }

        private static T GetFieldValue<T>(FieldInfo field, object instance)
        {
            Assert.IsNotNull(field);
            return (T)field.GetValue(instance);
        }

        private static void SetFieldValue(FieldInfo field, object instance,
            object value)
        {
            Assert.IsNotNull(field);
            field.SetValue(instance, value);
        }

        private static void AssertCrcIsValid(byte[] report)
        {
            uint expected = ComputeCrc(report, report.Length - sizeof(uint));
            uint actual = (uint)(report[^4] |
                (report[^3] << 8) |
                (report[^2] << 16) |
                (report[^1] << 24));
            Assert.AreEqual(expected, actual);
        }

        private static uint ComputeCrc(byte[] data, int length)
        {
            uint crc = ~0xEADA2D49u;
            for (int index = 0; index < length; index++)
            {
                crc ^= data[index];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^
                        ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
            }

            return ~crc;
        }
    }
}
