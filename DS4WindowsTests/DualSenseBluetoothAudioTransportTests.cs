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
        public void PairedTransportUsesPadForgeWindowsWritePoolAndHostClock()
        {
            Assert.AreEqual(8,
                DualSenseBluetoothAudioPacer.PairedAudioTransportSlotCount);
            Assert.AreEqual(8,
                DualSenseBluetoothAudioPacer.PairedAudioInFlightLimit);
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldWaitForPhysicalWriteCredit(
                        padForgeAudioTransport: false,
                        pairedAudioReports: true));
            Assert.IsFalse(
                DualSenseBluetoothAudioPacer.
                    ShouldWaitForPhysicalWriteCredit(
                        padForgeAudioTransport: true,
                        pairedAudioReports: false));
            Assert.IsTrue(
                DualSenseBluetoothAudioPacer.
                    ShouldWaitForPhysicalWriteCredit(
                        padForgeAudioTransport: false,
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
            Assert.IsTrue(
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
                    padForgeAudioTransport: false,
                    pairedAudioReport: true,
                    controlOnly: false,
                    accepted: false,
                    transportFault: false));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                ShouldDropSaturatedAudio(
                    padForgeAudioTransport: false,
                    pairedAudioReport: true,
                    controlOnly: false,
                    accepted: false,
                    transportFault: true));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                ShouldDropSaturatedAudio(
                    padForgeAudioTransport: false,
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
        public void PhysicalSequenceMatchesDs5DongleControlThenAudio()
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
                "The first 0x39 did not publish DS5Dongle's first two-frame media counter.");
            sequence.Commit(audio: true);
            Assert.AreEqual((byte)2, sequence.NextReportSequence);
            Assert.AreEqual((byte)2, sequence.MediaPacketSequence);
        }

        [TestMethod]
        public void PhysicalSequenceMatchesNativeMicrophoneTransition()
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
            Assert.AreEqual((byte)0x01, microphoneStatus[3]);
            Assert.AreEqual((byte)0x03, microphoneStatus[4]);
            uint expectedCrc =
                DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                    microphoneStatus,
                    microphoneStatus.Length - sizeof(uint));
            Assert.AreEqual(expectedCrc,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    microphoneStatus.AsSpan(
                        microphoneStatus.Length - sizeof(uint))));

            sequence.Commit(audio: false);
            byte[] duplex = CreateSpeakerReport(
                0x11, 0x21, 0xA0, 0x23);
            duplex[4] = 0xFF;
            sequence.PrepareFullDuplexAudio(duplex);
            Assert.AreEqual((byte)0x60, duplex[1],
                "The duplex report did not follow the accepted 0x32 transition.");
            Assert.AreEqual((byte)0x23, duplex[10],
                "The 0x32 transition consumed the media packet counter.");
        }

        [TestMethod]
        public void Ds5DongleControllerStateUsesGlobalOutputSequence()
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
        public void MicrophoneEnabledAudioUsesFullStateCarrierAnd64ByteDepths()
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
                Assert.AreEqual((byte)64, duplex[index]);
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
            Assert.AreEqual(10,
                DualSenseBluetoothAudioPacer.SingleAudioTransportSlotCount);
        }

        [TestMethod]
        public void PadForgeReportBuildsExactCompactSpeakerPacket()
        {
            byte[] source = CreateSpeakerReport(0x5A, 0x21, 0x70, 0x42);
            byte[] original = (byte[])source.Clone();
            for (int index = 0; index < 200; index++)
            {
                source[144 + index] = (byte)(index ^ 0xA5);
                original[144 + index] = source[144 + index];
            }
            byte[] report = new byte[
                DualSenseBluetoothPadForgeAudioReportBuilder.ReportLength];

            DualSenseBluetoothPadForgeAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, report);

            Assert.AreEqual(334, report.Length);
            Assert.AreEqual((byte)0x35, report[0]);
            Assert.AreEqual((byte)0x90, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)7, report[3]);
            Assert.AreEqual((byte)0xFE, report[4]);
            AssertRangeIsZero(report, 5, 4,
                "The compact session header contained unexpected bytes.");
            Assert.AreEqual((byte)0xFF, report[9]);
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
        public void PadForgeReportPreservesAuxDestination()
        {
            byte[] source = CreateSpeakerReport(0, 0, 0, 0x42);
            source[142] = 0x96;
            byte[] report = new byte[
                DualSenseBluetoothPadForgeAudioReportBuilder.ReportLength];

            DualSenseBluetoothPadForgeAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, report);

            Assert.AreEqual((byte)0x96, report[11]);
            Assert.AreEqual((byte)200, report[12]);
            CollectionAssert.AreEqual(source.Skip(144).Take(200).ToArray(),
                report.Skip(13).Take(200).ToArray());
        }

        [TestMethod]
        public void PadForgePhysicalSequenceAdvancesOnlyAfterAcceptedWrites()
        {
            var sequence = new DualSenseBluetoothPhysicalOutputSequence();
            byte[] first = CreateSpeakerReport(0x11, 0x21, 0xA0, 7);
            byte[] initial = new byte[
                DualSenseBluetoothPadForgeAudioReportBuilder.ReportLength];
            byte[] retry = new byte[initial.Length];

            sequence.PreparePadForgeAudio(first, initial);
            sequence.PreparePadForgeAudio(first, retry);
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
            sequence.PreparePadForgeAudio(second, retry);
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
                DualSenseBluetoothPadForgeCombinedAudioReportBuilder.
                    ReportLength];

            DualSenseBluetoothPadForgeCombinedAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, report);

            Assert.AreEqual(334, report.Length);
            Assert.AreEqual((byte)0x35, report[0]);
            Assert.AreEqual((byte)0x90, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)7, report[3]);
            Assert.AreEqual((byte)0xFE, report[4]);
            AssertRangeIsZero(report, 5, 4,
                "The combined session header contained unexpected bytes.");
            Assert.AreEqual((byte)0xFF, report[9]);
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
                DualSenseBluetoothPadForgeCombinedAudioReportBuilder.
                    ReportLength];

            DualSenseBluetoothPadForgeCombinedAudioReportBuilder.Build(source,
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
        public void CompactAudioReportsDoNotRewriteMicrophoneSection(
            bool microphoneEnabled)
        {
            byte[] source = CreateSpeakerReport(0, 0, 0, 0x42);
            source[4] = (byte)(0xFE | (microphoneEnabled ? 1 : 0));
            byte[] speaker = new byte[
                DualSenseBluetoothPadForgeAudioReportBuilder.ReportLength];
            byte[] combined = new byte[
                DualSenseBluetoothPadForgeCombinedAudioReportBuilder.
                    ReportLength];

            DualSenseBluetoothPadForgeAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, speaker);
            DualSenseBluetoothPadForgeCombinedAudioReportBuilder.Build(source,
                reportSequence: 9, packetSequence: 0x42, combined);

            Assert.AreEqual((byte)0xFE, speaker[4],
                "The compact speaker carrier tried to rewrite the microphone lane.");
            Assert.AreEqual((byte)0xFE, combined[4],
                "The compact speaker/haptics carrier tried to rewrite the microphone lane.");
            for (int index = 5; index <= 8; index++)
            {
                Assert.AreEqual((byte)0, speaker[index]);
                Assert.AreEqual((byte)0, combined[index]);
            }
            Assert.AreEqual((byte)0xFF, speaker[9]);
            Assert.AreEqual((byte)0xFF, combined[9]);
            Assert.AreEqual((byte)0x92, combined[11]);
            Assert.AreEqual((byte)64, combined[12]);
            Assert.AreEqual((byte)0x93, combined[77]);
            Assert.AreEqual((byte)200, combined[78]);
        }

        [TestMethod]
        public void CompactTransportToggleRequiresExact35Value()
        {
            Assert.IsTrue(DualSenseBluetoothAudioPacer.
                UsePadForgeAudioTransport("35"));
            Assert.IsTrue(DualSenseBluetoothAudioPacer.
                UsePadForgeAudioTransport("35combined"));
            Assert.IsTrue(DualSenseBluetoothAudioPacer.
                UseCompactCombinedHapticsTransport("35combined"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UseCompactCombinedHapticsTransport("35"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UsePadForgeAudioTransport(null));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UsePadForgeAudioTransport("36"));
            Assert.IsFalse(DualSenseBluetoothAudioPacer.
                UsePadForgeAudioTransport("0x35"));
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
        public void LegacySpeakerCompatibilityPropagatesSubmissionFailure()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;
            byte[] report = new byte[334];
            report[0] = 0x35;
            report[11] = 0x93;
            report[12] = 200;

            Assert.IsFalse(device.WriteBluetoothSpeakerAudioOutputReport(
                report, 0, report.Length),
                "The compatibility API hid the physical speaker submission failure.");
        }

        [TestMethod]
        public void AudioOnlyCombinedCarrierPreservesProfileLightbar()
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
            Assert.AreEqual((byte)12, cached[13 + 44]);
            Assert.AreEqual((byte)34, cached[13 + 45]);
            Assert.AreEqual((byte)56, cached[13 + 46]);
            AssertRangeIsZero(cached, 78, 64,
                "Audio-only carriers must not refresh stale native haptics.");
            Assert.AreEqual(0L, GetFieldValue<long>(
                NativeStateTimestampField, device));
        }

        [TestMethod]
        public void NativeGameCombinedCarrierRemainsAuthoritative()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.LightBarColor = new DS4Color(12, 34, 56);
            byte[] report = BuildCombinedControlReport(0, 0, false);
            report[13 + 44] = 90;
            report[13 + 45] = 91;
            report[13 + 46] = 92;

            device.WriteBluetoothCombinedHapticsAudioOutputReport(report, 0,
                report.Length, hasNativeGameState: true);

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            Assert.AreEqual((byte)90, cached[13 + 44]);
            Assert.AreEqual((byte)91, cached[13 + 45]);
            Assert.AreEqual((byte)92, cached[13 + 46]);
            Assert.IsTrue(GetFieldValue<long>(NativeStateTimestampField,
                device) > 0);
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
            Assert.AreEqual((byte)0x40, report[19],
                "The combined transport must not overdrive the physical DualSense ADC.");
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
                // frame separately raises these fields to 0x40.
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
                0xFD, 0xF7, 0x00, 0x00, 0x7F, 0x64, 0xFF, 0x09,
                0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x07, 0x00,
                0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00,
            };
            Array.Copy(knownState, state, knownState.Length);
            return state;
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
