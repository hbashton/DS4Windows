using DS4Windows;
using DS4WinWPF.DS4Control;
using SBC;
using System;
using System.Collections.Generic;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualShock4BluetoothAudioProtocolTests
    {
        [TestMethod]
        public void SpeakerStreamCoalescesPhysicalEffectReportsAtThirtyHertz()
        {
            Assert.IsTrue(DS4Device.ShouldDeferBluetoothEffectDuringSpeaker(
                usingBluetooth: true, speakerEnabled: true, force: false,
                reportPending: true,
                elapsedMilliseconds:
                    DS4Device.BLUETOOTH_EFFECT_INTERVAL_DURING_SPEAKER_MS - 1));
            Assert.IsFalse(DS4Device.ShouldDeferBluetoothEffectDuringSpeaker(
                usingBluetooth: true, speakerEnabled: true, force: false,
                reportPending: true,
                elapsedMilliseconds:
                    DS4Device.BLUETOOTH_EFFECT_INTERVAL_DURING_SPEAKER_MS));
            Assert.IsFalse(DS4Device.ShouldDeferBluetoothEffectDuringSpeaker(
                usingBluetooth: true, speakerEnabled: true, force: true,
                reportPending: true, elapsedMilliseconds: 0));
            Assert.IsFalse(DS4Device.ShouldDeferBluetoothEffectDuringSpeaker(
                usingBluetooth: true, speakerEnabled: true, force: false,
                reportPending: true, elapsedMilliseconds: 0,
                audioControlRefreshPending: true),
                "An audio control packet may have zeroed the lightbar and must be followed by an immediate effect refresh.");
            Assert.IsFalse(DS4Device.ShouldDeferBluetoothEffectDuringSpeaker(
                usingBluetooth: true, speakerEnabled: false, force: false,
                reportPending: true, elapsedMilliseconds: 0));
        }

        [TestMethod]
        public void SpeakerEncoderProducesControllerSizedFrames()
        {
            var encoder = new SbcEncoder();
            var configuration = new SbcFrame
            {
                Frequency = SbcFrequency.Freq32K,
                Mode = SbcMode.JointStereo,
                AllocationMethod = SbcBitAllocationMethod.SNR,
                Blocks = 16,
                Subbands = 8,
                Bitpool = 48,
            };
            short[] left = BuildTone(128, 32000, 440.0);
            short[] right = BuildTone(128, 32000, 660.0);

            byte[] encoded = encoder.Encode(left, right, configuration);

            Assert.IsNotNull(encoded);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength,
                encoded.Length);
            Assert.AreEqual((byte)0x9C, encoded[0]);
        }

        [TestMethod]
        public void DualShock4EncoderMatchesReferenceStrategyVector()
        {
            const int frameCount = 512;
            var encoder = new DualShock4SbcEncoder();
            var encodedStream = new MemoryStream(frameCount *
                DualShock4SbcEncoder.FrameLength);
            var left = new short[DualShock4SbcEncoder.SamplesPerChannel];
            var right = new short[DualShock4SbcEncoder.SamplesPerChannel];
            var encoded = new byte[DualShock4SbcEncoder.FrameLength];
            var random = new Random(0x0D54A0D1);
            double phase1 = 0.0;
            double phase2 = 0.0;
            double phase3 = 0.0;

            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int sample = 0; sample < left.Length; sample++)
                {
                    phase1 += 2 * Math.PI * (110 + (frame % 700) * 0.7) /
                        32000.0;
                    phase2 += 2 * Math.PI * 997 / 32000.0;
                    phase3 += 2 * Math.PI * 7133 / 32000.0;
                    double transient = ((frame * 128 + sample) % 31621) < 3 ?
                        0.92 : 0.0;
                    double noiseLeft = (random.NextDouble() * 2 - 1) * 0.055;
                    double noiseRight = (random.NextDouble() * 2 - 1) * 0.055;
                    double gain = frame % 977 < 81 ? 0.96 : 0.62;
                    double sampleLeft = gain * (0.50 * Math.Sin(phase1) +
                        0.22 * Math.Sin(phase2) + 0.12 * Math.Sin(phase3) +
                        noiseLeft) + transient;
                    double sampleRight = gain * (0.46 * Math.Sin(phase1 + 0.17) +
                        0.24 * Math.Sin(phase2) - 0.10 * Math.Sin(phase3) +
                        noiseRight) - transient;
                    left[sample] = (short)Math.Clamp((int)Math.Round(
                        sampleLeft * short.MaxValue), short.MinValue,
                        short.MaxValue);
                    right[sample] = (short)Math.Clamp((int)Math.Round(
                        sampleRight * short.MaxValue), short.MinValue,
                        short.MaxValue);
                }

                Assert.IsTrue(encoder.Encode(left, right, encoded));
                encodedStream.Write(encoded, 0, encoded.Length);
            }

            string digest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    encodedStream.ToArray()));
            Assert.AreEqual(
                "18E247031ADC51A95E39D9D313431D32B6BDE963E9CBF94D423E9D1391BFBDD5",
                digest);
        }

        [TestMethod]
        public void SpeakerEncoderRoundTripPreservesChannelAmplitude()
        {
            const int frameSamples = 128;
            const int frameCount = 100;
            const int leftAmplitude = 12000;
            const int rightAmplitude = 8000;
            var encoder = new SbcEncoder();
            var decoder = new SbcDecoder();
            var configuration = new SbcFrame
            {
                Frequency = SbcFrequency.Freq32K,
                Mode = SbcMode.JointStereo,
                AllocationMethod = SbcBitAllocationMethod.SNR,
                Blocks = 16,
                Subbands = 8,
                Bitpool = 48,
            };
            var decodedLeft = new List<short>();
            var decodedRight = new List<short>();

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                short[] left = new short[frameSamples];
                short[] right = new short[frameSamples];
                for (int sample = 0; sample < frameSamples; sample++)
                {
                    int absolute = frameIndex * frameSamples + sample;
                    left[sample] = (short)(Math.Sin(2.0 * Math.PI * 1000.0 *
                        absolute / 32000.0) * leftAmplitude);
                    right[sample] = (short)(Math.Sin(2.0 * Math.PI * 500.0 *
                        absolute / 32000.0) * rightAmplitude);
                }

                byte[] encoded = encoder.Encode(left, right, configuration);
                Assert.IsTrue(decoder.Decode(encoded, out short[] outputLeft,
                    out short[] outputRight, out _));
                if (frameIndex >= 10)
                {
                    decodedLeft.AddRange(outputLeft);
                    decodedRight.AddRange(outputRight);
                }
            }

            int leftPeak = decodedLeft.Max(value => Math.Abs((int)value));
            int rightPeak = decodedRight.Max(value => Math.Abs((int)value));
            double leftRms = Math.Sqrt(decodedLeft.Average(value =>
                (double)value * value));
            double rightRms = Math.Sqrt(decodedRight.Average(value =>
                (double)value * value));

            Assert.IsTrue(leftPeak >= 11000 && leftPeak <= 13000,
                $"Left peak {leftPeak} should remain near {leftAmplitude}.");
            Assert.IsTrue(rightPeak >= 7400 && rightPeak <= 8600,
                $"Right peak {rightPeak} should remain near {rightAmplitude}.");
            Assert.IsTrue(leftRms >= 8000 && leftRms <= 9000,
                $"Unexpected left RMS {leftRms:F2}.");
            Assert.IsTrue(rightRms >= 5300 && rightRms <= 6000,
                $"Unexpected right RMS {rightRms:F2}.");
        }

        [TestMethod]
        public void ContinuousSpeakerEncoderNeverCollapses()
        {
            const int frameSamples = 128;
            const int frameCount = 2500; // Ten seconds at 32 kHz.
            const int amplitude = 12000;
            var encoder = new SbcEncoder();
            var decoder = new SbcDecoder();
            var configuration = new SbcFrame
            {
                Frequency = SbcFrequency.Freq32K,
                Mode = SbcMode.JointStereo,
                AllocationMethod = SbcBitAllocationMethod.SNR,
                Blocks = 16,
                Subbands = 8,
                Bitpool = 48,
            };
            double minimumRms = double.MaxValue;

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                short[] left = new short[frameSamples];
                short[] right = new short[frameSamples];
                for (int sample = 0; sample < frameSamples; sample++)
                {
                    int absolute = frameIndex * frameSamples + sample;
                    left[sample] = (short)(Math.Sin(2.0 * Math.PI * 997.0 *
                        absolute / 32000.0) * amplitude);
                    right[sample] = (short)(Math.Sin(2.0 * Math.PI * 1499.0 *
                        absolute / 32000.0) * amplitude);
                }

                byte[] encoded = encoder.Encode(left, right, configuration);
                Assert.IsTrue(decoder.Decode(encoded, out short[] decodedLeft,
                    out short[] decodedRight, out _),
                    $"Frame {frameIndex} did not decode.");
                if (frameIndex < 10)
                {
                    continue;
                }

                double rms = Math.Sqrt(decodedLeft.Concat(decodedRight)
                    .Average(value => (double)value * value));
                minimumRms = Math.Min(minimumRms, rms);
                Assert.IsTrue(rms > 6500.0,
                    $"Frame {frameIndex} collapsed to RMS {rms:F2}.");
            }

            Assert.IsTrue(minimumRms > 6500.0,
                $"Continuous SBC minimum RMS was {minimumRms:F2}.");
        }

        [TestMethod]
        public void RealtimeSpeakerEncoderDoesNotAllocatePerFrame()
        {
            var encoder = new SbcEncoder();
            var configuration = new SbcFrame
            {
                Frequency = SbcFrequency.Freq32K,
                Mode = SbcMode.JointStereo,
                AllocationMethod = SbcBitAllocationMethod.SNR,
                Blocks = 16,
                Subbands = 8,
                Bitpool = 48,
            };
            short[] left = new short[128];
            short[] right = new short[128];
            byte[] output = new byte[109];
            for (int index = 0; index < left.Length; index++)
            {
                left[index] = (short)(index * 73 - 4000);
                right[index] = (short)(4000 - index * 51);
            }

            for (int index = 0; index < 10; index++)
            {
                Assert.IsTrue(encoder.Encode(left, right, configuration,
                    output));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1000; index++)
            {
                Assert.IsTrue(encoder.Encode(left, right, configuration,
                    output));
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(allocated < 1024,
                $"Realtime SBC encoding allocated {allocated} bytes.");
        }

        [TestMethod]
        public void SpeakerReportHasExpectedLayoutAndCrc()
        {
            byte[] first = Enumerable.Repeat((byte)0x11,
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength).ToArray();
            byte[] second = Enumerable.Repeat((byte)0x22,
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength).ToArray();

            byte[] report =
                DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                    0x1234, first, second);

            Assert.AreEqual(270, report.Length);
            CollectionAssert.AreEqual(new byte[]
            {
                0x14, 0x40, 0xA0, 0x34, 0x12, 0x02,
            }, report.Take(6).ToArray());
            CollectionAssert.AreEqual(first,
                report.Skip(6).Take(first.Length).ToArray());
            CollectionAssert.AreEqual(second,
                report.Skip(6 + first.Length).Take(second.Length).ToArray());
            int crcOffset = report.Length - sizeof(uint);
            byte[] prefixedReport = new byte[crcOffset + 1];
            prefixedReport[0] = 0xA2;
            Buffer.BlockCopy(report, 0, prefixedReport, 1, crcOffset);
            Assert.AreEqual(
                Crc32Algorithm.Compute(prefixedReport),
                ReadUInt32(report, crcOffset));
        }

        [TestMethod]
        public void OneFrameSpeakerReportUsesRealtimeTransportPacket()
        {
            byte[] frame = Enumerable.Repeat((byte)0x5A,
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength)
                .ToArray();

            byte[] report =
                DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                    0xBEEF, new[] { frame });

            Assert.AreEqual(142, report.Length);
            CollectionAssert.AreEqual(new byte[]
            {
                0x12, 0x40, 0xA0, 0xEF, 0xBE, 0x02,
            }, report.Take(6).ToArray());
            CollectionAssert.AreEqual(frame,
                report.Skip(6).Take(frame.Length).ToArray());
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));
        }

        [TestMethod]
        public void FourFrameSpeakerReportUsesLargeTransportPacket()
        {
            byte[][] frames = Enumerable.Range(1, 4)
                .Select(value => Enumerable.Repeat((byte)value,
                    DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength)
                    .ToArray())
                .ToArray();

            byte[] report =
                DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                    0xFFFE, frames);

            Assert.AreEqual(462, report.Length);
            CollectionAssert.AreEqual(new byte[]
            {
                0x17, 0x40, 0xA0, 0xFE, 0xFF, 0x02,
            }, report.Take(6).ToArray());
            for (int index = 0; index < frames.Length; index++)
            {
                CollectionAssert.AreEqual(frames[index], report.Skip(
                    6 + index * frames[index].Length)
                    .Take(frames[index].Length).ToArray());
            }

            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));
        }

        [TestMethod]
        public void FourFrameSpeakerReportRepresentsExactlySixteenMilliseconds()
        {
            Assert.AreEqual(32000,
                DualShock4BluetoothAudioProtocol.SpeakerSampleRate);
            Assert.AreEqual(128,
                DualShock4BluetoothAudioProtocol.SpeakerSamplesPerFrame);
            Assert.AreEqual(16,
                DualShock4BluetoothAudioProtocol.SpeakerLargeReportDurationMilliseconds);
            Assert.AreEqual(4,
                DualShock4BluetoothAudioProtocol.
                    SpeakerRealtimeReportDurationMilliseconds);
            Assert.AreEqual(4,
                DualShock4BluetoothAudioProtocol.
                    SpeakerDirectFramesPerReport);
            Assert.AreEqual(16,
                DualShock4BluetoothAudioProtocol.
                    SpeakerDirectReportDurationMilliseconds);
            Assert.AreEqual(5,
                DualShock4BluetoothAudioProtocol.
                    SpeakerDirectPrimeReports);
            Assert.AreEqual(20,
                DualShock4BluetoothAudioProtocol.SpeakerRealtimePrimeFrames);
            Assert.AreEqual(16,
                DualShock4BluetoothAudioProtocol.
                    SpeakerRealtimeSourceCushionFrames);
            Assert.AreEqual(36,
                DualShock4BluetoothAudioProtocol.SpeakerMinimumBufferedFrames);
        }

        [TestMethod]
        public void ProductionDuplexA1IsDefaultAndOtherTransportsAreExplicit()
        {
            Assert.AreEqual(DualShock4AudioTransportMode.ProductionDuplexA1,
                DualShock4AudioTransportSettings.Parse(null));
            Assert.AreEqual(DualShock4AudioTransportMode.Reference,
                DualShock4AudioTransportSettings.Parse("reference"));
            Assert.AreEqual(DualShock4AudioTransportMode.ProductionDuplexA1,
                DualShock4AudioTransportSettings.Parse("unknown"));
            Assert.AreEqual(DualShock4AudioTransportMode.Scheduled,
                DualShock4AudioTransportSettings.Parse(" scheduled "));
            Assert.AreEqual(DualShock4AudioTransportMode.Scheduled,
                DualShock4AudioTransportSettings.Parse("CLOCKED"));
            Assert.AreEqual(DualShock4AudioTransportMode.PadForgeAsync,
                DualShock4AudioTransportSettings.Parse("padforge"));
            Assert.AreEqual(DualShock4AudioTransportMode.PadForgeAsync,
                DualShock4AudioTransportSettings.Parse(" PADFORGE-ASYNC "));
            Assert.AreEqual("padforge-async",
                DualShock4AudioTransportSettings.Format(
                    DualShock4AudioTransportMode.PadForgeAsync));
            Assert.AreEqual(DualShock4AudioTransportMode.ProductionReplay,
                DualShock4AudioTransportSettings.Parse(
                    " production-replay "));
            Assert.AreEqual(DualShock4AudioTransportMode.ProductionReplay,
                DualShock4AudioTransportSettings.Parse(
                    "HISTORICAL-REPLAY"));
            Assert.AreEqual("production-replay",
                DualShock4AudioTransportSettings.Format(
                    DualShock4AudioTransportMode.ProductionReplay));
            Assert.AreEqual(DualShock4AudioTransportMode.ProductionA0,
                DualShock4AudioTransportSettings.Parse(
                    " PRODUCTION-A0 "));
            Assert.AreEqual("production-a0",
                DualShock4AudioTransportSettings.Format(
                    DualShock4AudioTransportMode.ProductionA0));
            Assert.AreEqual(
                DualShock4AudioTransportMode.ProductionDuplexA1,
                DualShock4AudioTransportSettings.Parse(
                    " PRODUCTION-DUPLEX-A1 "));
            Assert.AreEqual(
                DualShock4AudioTransportMode.ProductionDuplexA1,
                DualShock4AudioTransportSettings.Parse("duplex-a1"));
            Assert.AreEqual("production-duplex-a1",
                DualShock4AudioTransportSettings.Format(
                    DualShock4AudioTransportMode.ProductionDuplexA1));
            Assert.AreEqual(DualShock4AudioTransportMode.FifoBuffered,
                DualShock4AudioTransportSettings.Parse(
                    " fifo-buffered "));
            Assert.AreEqual(DualShock4AudioTransportMode.FifoBuffered,
                DualShock4AudioTransportSettings.Parse("FIFO-PRIME"));
            Assert.AreEqual("fifo-buffered",
                DualShock4AudioTransportSettings.Format(
                    DualShock4AudioTransportMode.FifoBuffered));
            Assert.AreEqual(DualShock4AudioTransportMode.CreditBuffered,
                DualShock4AudioTransportSettings.Parse(
                    " credit-buffered "));
            Assert.AreEqual(DualShock4AudioTransportMode.CreditBuffered,
                DualShock4AudioTransportSettings.Parse("CREDIT-WINDOW"));
            Assert.AreEqual("credit-buffered",
                DualShock4AudioTransportSettings.Format(
                    DualShock4AudioTransportMode.CreditBuffered));
        }

        [TestMethod]
        public void ReferenceTransportWaitsForFourFramesBeforeEachWake()
        {
            for (int buffered = 0; buffered <
                DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport;
                buffered++)
            {
                Assert.IsFalse(DualShock4AudioTransportSettings.
                    ShouldWakeReferenceSender(buffered));
            }

            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldWakeReferenceSender(
                    DualShock4BluetoothAudioProtocol.
                        SpeakerLargeFramesPerReport));
        }

        [TestMethod]
        public void ReferenceTransportDrainsLargeReportsThenOneSmallTail()
        {
            int buffered = 11;
            var selected = new List<int>();
            while (true)
            {
                int count = DualShock4AudioTransportSettings.
                    SelectReferenceReportFrameCount(buffered);
                if (count == 0)
                {
                    break;
                }
                selected.Add(count);
                buffered -= count;
            }

            CollectionAssert.AreEqual(new[] { 4, 4, 2 }, selected);
            Assert.AreEqual(1, buffered);
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                SelectReferenceReportFrameCount(4));
            Assert.AreEqual(2, DualShock4AudioTransportSettings.
                SelectReferenceReportFrameCount(3));
            Assert.AreEqual(0, DualShock4AudioTransportSettings.
                SelectReferenceReportFrameCount(1));
        }

        [TestMethod]
        public void PadForgeAsyncTransportUsesTheExactAvailabilityDrainPolicy()
        {
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldWakePadForgeAsyncSender(3));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldWakePadForgeAsyncSender(4));

            var cases = new[]
            {
                (Buffered: 4, Reports: new[] { 4 }, Remaining: 0),
                (Buffered: 5, Reports: new[] { 4 }, Remaining: 1),
                (Buffered: 6, Reports: new[] { 4, 2 }, Remaining: 0),
                (Buffered: 7, Reports: new[] { 4, 2 }, Remaining: 1),
                (Buffered: 8, Reports: new[] { 4, 4 }, Remaining: 0),
                (Buffered: 9, Reports: new[] { 4, 4 }, Remaining: 1),
            };
            foreach (var item in cases)
            {
                int buffered = item.Buffered;
                var reports = new List<int>();
                while (true)
                {
                    int count = DualShock4AudioTransportSettings.
                        SelectPadForgeAsyncReportFrameCount(buffered);
                    if (count == 0)
                    {
                        break;
                    }
                    reports.Add(count);
                    buffered -= count;
                }

                CollectionAssert.AreEqual(item.Reports, reports,
                    $"Unexpected drain for {item.Buffered} frames.");
                Assert.AreEqual(item.Remaining, buffered);
                Assert.IsFalse(reports.Contains(
                    DualShock4BluetoothAudioProtocol.
                        SpeakerRealtimeFramesPerReport),
                    "Availability transport must never select report 0x12.");
            }
        }

        [TestMethod]
        public void PadForgeAsyncCapacityStopsBeforeAQueuedFrameCanBeDequeued()
        {
            Assert.AreEqual(8, DualShock4AudioTransportSettings.
                PadForgeAsyncSlotCount);
            for (int pending = 0; pending <
                DualShock4AudioTransportSettings.PadForgeAsyncSlotCount;
                pending++)
            {
                Assert.IsTrue(DualShock4AudioTransportSettings.
                    CanSubmitPadForgeAsync(pending));
            }
            Assert.IsFalse(DualShock4AudioTransportSettings.
                CanSubmitPadForgeAsync(8));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                CanSubmitPadForgeAsync(32));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                DualShock4AudioTransportSettings.CanSubmitPadForgeAsync(-1));
        }

        [TestMethod]
        public void ProductionReplayReconstructsHistoricalPrimeAndReserve()
        {
            Assert.AreEqual(1, DualShock4AudioTransportSettings.
                ProductionReplayFramesPerReport);
            Assert.AreEqual(20, DualShock4AudioTransportSettings.
                ProductionReplayPrimeReports);
            Assert.AreEqual(20, DualShock4AudioTransportSettings.
                ProductionReplayPrimeFrames);
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                ProductionReplayRetainedSourceFrames);
            Assert.AreEqual(36, DualShock4AudioTransportSettings.
                ProductionReplayStartupBufferedFrames);
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                ProductionReplayCadenceMilliseconds);
            Assert.AreEqual(32, DualShock4AudioTransportSettings.
                ProductionReplaySlotCount);

            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldStartProductionReplay(35));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldStartProductionReplay(36));
            int buffered =
                DualShock4AudioTransportSettings.
                    ProductionReplayStartupBufferedFrames;
            for (int report = 0; report <
                DualShock4AudioTransportSettings.
                    ProductionReplayPrimeReports; report++)
            {
                int count = DualShock4AudioTransportSettings.
                    SelectProductionReplayReportFrameCount(buffered);
                Assert.AreEqual(1, count);
                buffered -= count;
            }
            Assert.AreEqual(DualShock4AudioTransportSettings.
                ProductionReplayRetainedSourceFrames, buffered);

            byte[] frame = new byte[
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength];
            byte[] packet = DualShock4BluetoothAudioProtocol.
                BuildSpeakerReport(0x1234, new[] { frame });
            Assert.AreEqual(0x12, packet[0]);
            Assert.AreEqual(0x34, packet[3]);
            Assert.AreEqual(0x12, packet[4]);
        }

        [TestMethod]
        public void ProductionReplayUsesFixedFourMillisecondClockAndIdleReprime()
        {
            Assert.AreEqual(40000,
                DualShock4AudioTransportSettings.
                    GetProductionReplayCadenceTicks(10_000_000));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                CanSubmitProductionReplay(31));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                CanSubmitProductionReplay(32));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldBeginProductionReplayReprime(0, 199));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldBeginProductionReplayReprime(0, 200));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldBeginProductionReplayReprime(1, 200));
            Assert.AreEqual(0, DualShock4AudioTransportSettings.
                SelectProductionReplayReportFrameCount(0));
            Assert.AreEqual(1, DualShock4AudioTransportSettings.
                SelectProductionReplayReportFrameCount(4));
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                ProductionReplayQueueServoTargetFrames);
            Assert.AreEqual(1.0, DualShock4AudioTransportSettings.
                GetProductionReplayQueueServoRatio(0, enabled: false),
                1.0e-12);
            Assert.AreEqual(1.0, DualShock4AudioTransportSettings.
                GetProductionReplayQueueServoRatio(16, enabled: true),
                1.0e-12);
            Assert.AreEqual(1.002, DualShock4AudioTransportSettings.
                GetProductionReplayQueueServoRatio(0, enabled: true),
                1.0e-12);
            Assert.AreEqual(0.998, DualShock4AudioTransportSettings.
                GetProductionReplayQueueServoRatio(64, enabled: true),
                1.0e-12);
        }

        [TestMethod]
        public void ProductionA0SelectsTheExactProductionReplayPolicy()
        {
            Assert.IsTrue(DualShock4AudioTransportSettings.
                UsesProductionReplayPolicy(
                    DualShock4AudioTransportMode.ProductionReplay));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                UsesProductionReplayPolicy(
                    DualShock4AudioTransportMode.ProductionA0));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                UsesProductionReplayPolicy(
                    DualShock4AudioTransportMode.ProductionDuplexA1));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                UsesProductionReplayPolicy(
                    DualShock4AudioTransportMode.FifoBuffered));

            Assert.AreEqual(20, DualShock4AudioTransportSettings.
                ProductionReplayPrimeReports);
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                ProductionReplayRetainedSourceFrames);
            Assert.AreEqual(36, DualShock4AudioTransportSettings.
                ProductionReplayStartupBufferedFrames);
            Assert.AreEqual(32, DualShock4AudioTransportSettings.
                ProductionReplaySlotCount);
            Assert.AreEqual(40000,
                DualShock4AudioTransportSettings.
                    GetProductionReplayCadenceTicks(10_000_000));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldStartProductionReplay(35));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldStartProductionReplay(36));
            Assert.AreEqual(1.0, DualShock4AudioTransportSettings.
                GetProductionReplayQueueServoRatio(16, enabled: true),
                1.0e-12);
        }

        [TestMethod]
        public void ProductionA0ForcesSpeakerOnlyModeOnControlAndData()
        {
            byte[] frame = new byte[
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength];
            byte[] speakerReport = DualShock4BluetoothAudioProtocol.
                BuildSpeakerReport(7, new[] { frame });
            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionReplayAudioMode(speakerReport,
                    microphoneEnabled: true);
            Assert.AreEqual(0xA1, speakerReport[2]);

            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionA0AudioMode(speakerReport);
            Assert.AreEqual(0x12, speakerReport[0]);
            Assert.AreEqual(0xA0, speakerReport[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, speakerReport,
                    speakerReport.Length - sizeof(uint)),
                ReadUInt32(speakerReport,
                    speakerReport.Length - sizeof(uint)));

            byte[] control = DualShock4BluetoothAudioProtocol.
                BuildAudioControlReport(speakerEnabled: true,
                    microphoneEnabled: true, speakerVolume: 0x4F,
                    headphoneVolume: 0x4F, microphoneVolume: 0x4F);
            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionReplayAudioMode(control,
                    microphoneEnabled: true);
            Assert.AreEqual(0xA1, control[2]);

            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionA0AudioMode(control);
            Assert.AreEqual(0x11, control[0]);
            Assert.AreEqual(0xA0, control[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, control, control.Length - sizeof(uint)),
                ReadUInt32(control, control.Length - sizeof(uint)));
        }

        [TestMethod]
        public void ProductionDuplexUsesA0WithoutCaptureAndA1WithCapture()
        {
            byte[] frame = new byte[
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength];
            byte[] report = DualShock4BluetoothAudioProtocol.
                BuildSpeakerReport(11, new[] { frame });

            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionDuplexAudioMode(report,
                    microphoneEnabled: false);
            Assert.AreEqual(0xA0, report[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));

            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionDuplexAudioMode(report,
                    microphoneEnabled: true);
            Assert.AreEqual(0xA1, report[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));

            byte[] control = DualShock4BluetoothAudioProtocol.
                BuildAudioControlReport(speakerEnabled: true,
                    microphoneEnabled: true, speakerVolume: 0x4F,
                    headphoneVolume: 0x4F, microphoneVolume: 0x4F);
            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionDuplexAudioMode(control,
                    microphoneEnabled: true);
            Assert.AreEqual(0xA1, control[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, control, control.Length - sizeof(uint)),
                ReadUInt32(control, control.Length - sizeof(uint)));

            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionDuplexAudioMode(control,
                    microphoneEnabled: false);
            Assert.AreEqual(0xA0, control[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, control, control.Length - sizeof(uint)),
                ReadUInt32(control, control.Length - sizeof(uint)));
        }

        [TestMethod]
        public void ProductionReplayUsesHistoricalA2AndFullDuplexA1Modes()
        {
            byte[] frame = new byte[
                DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength];
            byte[] speakerReport = DualShock4BluetoothAudioProtocol.
                BuildSpeakerReport(7, new[] { frame });
            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionReplayAudioMode(speakerReport,
                    microphoneEnabled: false);
            Assert.AreEqual(0x12, speakerReport[0]);
            Assert.AreEqual(0xA2, speakerReport[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, speakerReport,
                    speakerReport.Length - sizeof(uint)),
                ReadUInt32(speakerReport,
                    speakerReport.Length - sizeof(uint)));

            byte[] control = DualShock4BluetoothAudioProtocol.
                BuildAudioControlReport(speakerEnabled: true,
                    microphoneEnabled: false, speakerVolume: 0x4F,
                    headphoneVolume: 0x4F, microphoneVolume: 0x4F);
            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionReplayAudioMode(control,
                    microphoneEnabled: false);
            Assert.AreEqual(0x11, control[0]);
            Assert.AreEqual(0xA2, control[2]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, control, control.Length - sizeof(uint)),
                ReadUInt32(control, control.Length - sizeof(uint)));

            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionReplayAudioMode(speakerReport,
                    microphoneEnabled: true);
            DualShock4BluetoothSpeakerPassthrough.
                ApplyProductionReplayAudioMode(control,
                    microphoneEnabled: true);
            Assert.AreEqual(0xA1, speakerReport[2]);
            Assert.AreEqual(0xA1, control[2]);
        }

        [TestMethod]
        public void FifoBufferedPrimeConsumesSixteenFramesAndRetainsSixteen()
        {
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                FifoBufferedPrimeSlotCount);
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                FifoBufferedPrimeReports);
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                FifoBufferedPrimeFramesPerReport);
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                FifoBufferedPrimeFrames);
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                FifoBufferedRetainedSourceFrames);
            Assert.AreEqual(32, DualShock4AudioTransportSettings.
                FifoBufferedStartupBufferedFrames);
            Assert.AreEqual(1, DualShock4AudioTransportSettings.
                FifoBufferedSteadyFramesPerReport);
            Assert.IsTrue(DualShock4BluetoothAudioProtocol.
                SpeakerEncodedFrameQueueLimit >=
                DualShock4AudioTransportSettings.
                    FifoBufferedStartupBufferedFrames);

            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldStartFifoBuffered(31));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldStartFifoBuffered(32));
            int buffered = DualShock4AudioTransportSettings.
                FifoBufferedStartupBufferedFrames;
            for (int report = 0; report <
                DualShock4AudioTransportSettings.FifoBufferedPrimeReports;
                report++)
            {
                int count = DualShock4AudioTransportSettings.
                    SelectFifoBufferedPrimeFrameCount(buffered);
                Assert.AreEqual(4, count);
                buffered -= count;
            }
            Assert.AreEqual(DualShock4AudioTransportSettings.
                FifoBufferedRetainedSourceFrames, buffered);
        }

        [TestMethod]
        public void FifoBufferedSteadyTransportUsesProductionClockServoAndReprime()
        {
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                FifoBufferedCadenceMilliseconds);
            Assert.AreEqual(40000, DualShock4AudioTransportSettings.
                GetFifoBufferedCadenceTicks(10_000_000));
            Assert.AreEqual(0, DualShock4AudioTransportSettings.
                SelectFifoBufferedSteadyFrameCount(0));
            Assert.AreEqual(1, DualShock4AudioTransportSettings.
                SelectFifoBufferedSteadyFrameCount(1));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                CanSubmitFifoBufferedPrime(3));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                CanSubmitFifoBufferedPrime(4));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                DualShock4AudioTransportSettings.
                    CanSubmitFifoBufferedPrime(-1));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldBeginFifoBufferedReprime(0, 199));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldBeginFifoBufferedReprime(0, 200));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldBeginFifoBufferedReprime(1, 200));
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                FifoBufferedQueueServoTargetFrames);
            Assert.AreEqual(1.0, DualShock4AudioTransportSettings.
                GetFifoBufferedQueueServoRatio(16, enabled: true),
                1.0e-12);
            Assert.AreEqual(1.002, DualShock4AudioTransportSettings.
                GetFifoBufferedQueueServoRatio(0, enabled: true),
                1.0e-12);
            Assert.AreEqual(0.998, DualShock4AudioTransportSettings.
                GetFifoBufferedQueueServoRatio(64, enabled: true),
                1.0e-12);
        }

        [TestMethod]
        public void FifoBufferedCounterIsContinuousAcrossPrimeAndSteadyReports()
        {
            byte[][] primeFrames = Enumerable.Range(0, 4)
                .Select(index => Enumerable.Repeat((byte)(index + 1),
                    DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength)
                    .ToArray())
                .ToArray();
            ushort counter = 0xFFF0;
            ushort[] expectedPrimeCounters =
                { 0xFFF0, 0xFFF4, 0xFFF8, 0xFFFC };
            foreach (ushort expectedCounter in expectedPrimeCounters)
            {
                Assert.AreEqual(expectedCounter, counter);
                byte[] prime = DualShock4BluetoothAudioProtocol.
                    BuildSpeakerReport(counter, primeFrames,
                        microphoneEnabled: true);
                DualShock4BluetoothSpeakerPassthrough.
                    ApplyFifoBufferedAudioMode(prime);
                Assert.AreEqual(0x17, prime[0]);
                Assert.AreEqual(0xA2, prime[2]);
                Assert.AreEqual((byte)counter, prime[3]);
                Assert.AreEqual((byte)(counter >> 8), prime[4]);
                Assert.AreEqual(
                    DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                        0xA2, prime, prime.Length - sizeof(uint)),
                    ReadUInt32(prime, prime.Length - sizeof(uint)));
                counter = DualShock4AudioTransportSettings.
                    AdvanceFifoBufferedPrimeFrameNumber(counter);
            }

            Assert.AreEqual((ushort)0, counter,
                "Four packed reports must advance by sixteen frames exactly.");
            byte[] steady = DualShock4BluetoothAudioProtocol.
                BuildSpeakerReport(counter, new[] { primeFrames[0] },
                    microphoneEnabled: true);
            DualShock4BluetoothSpeakerPassthrough.
                ApplyFifoBufferedAudioMode(steady);
            Assert.AreEqual(0x12, steady[0]);
            Assert.AreEqual(0xA2, steady[2]);
            Assert.AreEqual(0, steady[3]);
            Assert.AreEqual(0, steady[4]);
            counter = DualShock4AudioTransportSettings.
                AdvanceFifoBufferedSteadyFrameNumber(counter);
            Assert.AreEqual((ushort)1, counter);
        }

        [TestMethod]
        public void CreditBufferedTransportFillsFourteenPhysicalCreditsAndKeepsSourceCushion()
        {
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                CreditBufferedFramesPerReport);
            Assert.AreEqual(14, DualShock4AudioTransportSettings.
                CreditBufferedPrimeReports);
            Assert.AreEqual(14, DualShock4AudioTransportSettings.
                CreditBufferedSlotCount);
            Assert.AreEqual(56, DualShock4AudioTransportSettings.
                CreditBufferedPrimeFrames);
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                CreditBufferedRetainedSourceFrames);
            Assert.AreEqual(72, DualShock4AudioTransportSettings.
                CreditBufferedStartupBufferedFrames);
            Assert.AreEqual(16, DualShock4AudioTransportSettings.
                CreditBufferedCadenceMilliseconds);
            Assert.AreEqual(224,
                DualShock4AudioTransportSettings.CreditBufferedPrimeReports *
                DualShock4AudioTransportSettings.
                    CreditBufferedCadenceMilliseconds);
            Assert.IsTrue(DualShock4BluetoothAudioProtocol.
                SpeakerEncodedFrameQueueLimit >=
                DualShock4AudioTransportSettings.
                    CreditBufferedStartupBufferedFrames);

            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldStartCreditBuffered(71));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldStartCreditBuffered(72));
            int buffered = DualShock4AudioTransportSettings.
                CreditBufferedStartupBufferedFrames;
            for (int report = 0; report <
                DualShock4AudioTransportSettings.
                    CreditBufferedPrimeReports; report++)
            {
                int count = DualShock4AudioTransportSettings.
                    SelectCreditBufferedReportFrameCount(buffered);
                Assert.AreEqual(4, count);
                buffered -= count;
            }
            Assert.AreEqual(DualShock4AudioTransportSettings.
                CreditBufferedRetainedSourceFrames, buffered);
        }

        [TestMethod]
        public void CreditBufferedTransportUsesFixedClockBoundedPoolAndIdleReprime()
        {
            Assert.AreEqual(160000, DualShock4AudioTransportSettings.
                GetCreditBufferedCadenceTicks(10_000_000));
            Assert.AreEqual((ushort)4, DualShock4AudioTransportSettings.
                AdvanceCreditBufferedFrameNumber(0));
            Assert.AreEqual((ushort)0, DualShock4AudioTransportSettings.
                AdvanceCreditBufferedFrameNumber(0xFFFC));
            Assert.AreEqual(0, DualShock4AudioTransportSettings.
                SelectCreditBufferedReportFrameCount(3));
            Assert.AreEqual(4, DualShock4AudioTransportSettings.
                SelectCreditBufferedReportFrameCount(4));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                CanSubmitCreditBuffered(13));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                CanSubmitCreditBuffered(14));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                CanSubmitCreditBuffered(32));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                DualShock4AudioTransportSettings.
                    CanSubmitCreditBuffered(-1));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldBeginCreditBufferedReprime(0, 199));
            Assert.IsTrue(DualShock4AudioTransportSettings.
                ShouldBeginCreditBufferedReprime(0, 200));
            Assert.IsFalse(DualShock4AudioTransportSettings.
                ShouldBeginCreditBufferedReprime(1, 200));
        }

        [TestMethod]
        public void CreditBufferedReportIsSpeakerOnlyA2WithFourSequentialFrames()
        {
            byte[][] frames = Enumerable.Range(0, 4)
                .Select(index => Enumerable.Repeat((byte)(index + 1),
                    DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength)
                    .ToArray())
                .ToArray();
            byte[] report = DualShock4BluetoothAudioProtocol.
                BuildSpeakerReport(0xFFFC, frames,
                    microphoneEnabled: true);

            DualShock4BluetoothSpeakerPassthrough.
                ApplyCreditBufferedAudioMode(report);

            Assert.AreEqual(0x17, report[0]);
            Assert.AreEqual(0xA2, report[2]);
            Assert.AreEqual(0xFC, report[3]);
            Assert.AreEqual(0xFF, report[4]);
            for (int index = 0; index < frames.Length; index++)
            {
                CollectionAssert.AreEqual(frames[index], report
                    .Skip(6 + index * frames[index].Length)
                    .Take(frames[index].Length).ToArray());
            }
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));
        }

        [TestMethod]
        public void DirectSpeakerStartupRequiresPrimeAndLiveSourceCushions()
        {
            int prime = DualShock4BluetoothAudioProtocol.
                SpeakerRealtimePrimeFrames;
            int liveSource = DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeSourceCushionFrames;
            int startup = DualShock4BluetoothAudioProtocol.
                SpeakerMinimumBufferedFrames;

            Assert.AreEqual(prime + liveSource, startup);
            Assert.IsTrue(startup > prime,
                "The physical speaker lane must not arm with only its hardware prime.");
            Assert.AreEqual(prime,
                DualShock4BluetoothAudioProtocol.SpeakerDirectPrimeReports *
                DualShock4BluetoothAudioProtocol.SpeakerDirectFramesPerReport);
            Assert.AreEqual(80,
                DualShock4BluetoothAudioProtocol.SpeakerDirectPrimeReports *
                DualShock4BluetoothAudioProtocol.
                    SpeakerDirectReportDurationMilliseconds);
            Assert.AreEqual(64, liveSource *
                DualShock4BluetoothAudioProtocol.
                    SpeakerRealtimeReportDurationMilliseconds);
        }

        [TestMethod]
        public void DirectSpeakerUnderrunPreservesTailBeforeSilencePadding()
        {
            int reportFrames = DualShock4BluetoothAudioProtocol.
                SpeakerDirectFramesPerReport;

            for (int available = 0; available < reportFrames; available++)
            {
                Assert.AreEqual(available,
                    DualShock4BluetoothSpeakerPassthrough.
                        GetRealFrameCountForSubmission(reportFrames, available,
                            allowSilence: true, forceSilence: false));
            }

            Assert.AreEqual(reportFrames,
                DualShock4BluetoothSpeakerPassthrough.
                    GetRealFrameCountForSubmission(reportFrames,
                        reportFrames + 3, allowSilence: true,
                        forceSilence: false));
            Assert.AreEqual(-1,
                DualShock4BluetoothSpeakerPassthrough.
                    GetRealFrameCountForSubmission(reportFrames,
                        reportFrames - 1, allowSilence: false,
                        forceSilence: false));
            Assert.AreEqual(0,
                DualShock4BluetoothSpeakerPassthrough.
                    GetRealFrameCountForSubmission(reportFrames,
                        reportFrames, allowSilence: true,
                        forceSilence: true));
        }

        [TestMethod]
        public void DirectSpeakerBatchUsesOneLargeReportForSixteenMilliseconds()
        {
            int frameCount = DualShock4BluetoothAudioProtocol.
                SpeakerDirectFramesPerReport;
            byte[][] frames = new byte[frameCount][];
            for (int index = 0; index < frames.Length; index++)
            {
                frames[index] = new byte[
                    DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength];
            }

            byte[] report =
                DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                    0x3456, frames);

            Assert.AreEqual(0x17, report[0]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.SpeakerLargeReportLength,
                report.Length);
            Assert.AreEqual(0x56, report[3]);
            Assert.AreEqual(0x34, report[4]);
        }

        [TestMethod]
        public void DirectStreamBatchedCadenceNeedsNoPaddingAtNominalRate()
        {
            // VIIPER supplies a long-term average of 320 PCM frames every
            // 10 ms. After the startup cushion, four encoded frames every
            // 16 ms consume that source at exactly the same 32 kHz rate.
            int[] packetFrameCounts =
            {
                320, 320, 288, 352, 320, 256, 384,
            };
            long producedFrames = 0;
            long submittedRealFrames = 0;
            int pendingSamples = 0;
            int queuedFrames = 0;
            int paddedFrames = 0;
            int packetIndex = 0;
            int nextReportMillisecond = int.MaxValue;
            bool started = false;

            // Ten minutes, using integral milliseconds so both the 10 ms
            // producer and 16 ms presenter retain their exact phase.
            for (int millisecond = 0; millisecond <= 10 * 60 * 1000;
                millisecond++)
            {
                if (millisecond % 10 == 0)
                {
                    pendingSamples += packetFrameCounts[
                        packetIndex++ % packetFrameCounts.Length];
                    int encoded = pendingSamples /
                        DualShock4BluetoothAudioProtocol.SpeakerSamplesPerFrame;
                    pendingSamples %=
                        DualShock4BluetoothAudioProtocol.SpeakerSamplesPerFrame;
                    producedFrames += encoded;
                    queuedFrames += encoded;
                }

                if (!started && queuedFrames >=
                        DualShock4BluetoothAudioProtocol.
                            SpeakerMinimumBufferedFrames)
                {
                    int primeFrames =
                        DualShock4BluetoothAudioProtocol.
                            SpeakerDirectPrimeReports *
                        DualShock4BluetoothAudioProtocol.
                            SpeakerDirectFramesPerReport;
                    queuedFrames -= primeFrames;
                    submittedRealFrames += primeFrames;
                    nextReportMillisecond = millisecond +
                        DualShock4BluetoothAudioProtocol.
                            SpeakerDirectReportDurationMilliseconds;
                    started = true;
                }

                if (started && millisecond == nextReportMillisecond)
                {
                    int realFrames =
                        DualShock4BluetoothSpeakerPassthrough.
                            GetRealFrameCountForSubmission(
                                DualShock4BluetoothAudioProtocol.
                                    SpeakerDirectFramesPerReport,
                                queuedFrames, allowSilence: true,
                                forceSilence: false);
                    queuedFrames -= realFrames;
                    submittedRealFrames += realFrames;
                    paddedFrames += DualShock4BluetoothAudioProtocol.
                        SpeakerDirectFramesPerReport - realFrames;
                    nextReportMillisecond +=
                        DualShock4BluetoothAudioProtocol.
                            SpeakerDirectReportDurationMilliseconds;
                }
            }

            Assert.IsTrue(started);
            Assert.AreEqual(0, paddedFrames);
            Assert.AreEqual(producedFrames,
                submittedRealFrames + queuedFrames);
        }

        [TestMethod]
        public void SpeakerControlReportMatchesSonyBluetoothAudioLayout()
        {
            byte[] report =
                DualShock4BluetoothAudioProtocol.BuildAudioControlReport(
                    speakerEnabled: true, microphoneEnabled: false,
                    speakerVolume: 0x4F, headphoneVolume: 0x4F,
                    microphoneVolume: 0x4F);

            Assert.AreEqual(78, report.Length);
            Assert.AreEqual(0x11, report[0]);
            Assert.AreEqual(0xC0, report[1]);
            Assert.AreEqual(0xA0, report[2]);
            Assert.AreEqual(0xF3, report[3]);
            Assert.AreEqual(0x4F, report[21]);
            Assert.AreEqual(0x4F, report[22]);
            Assert.AreEqual(0x00, report[23]);
            Assert.AreEqual(0x4F, report[24]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));
        }

        [TestMethod]
        public void MicrophoneModeKeepsSpeakerPayloadOnMicrophoneInputLane()
        {
            byte[] control =
                DualShock4BluetoothAudioProtocol.BuildAudioControlReport(
                    speakerEnabled: true, microphoneEnabled: true,
                    speakerVolume: 0x4F, headphoneVolume: 0x4F,
                    microphoneVolume: 0x40);
            byte[][] frames = Enumerable.Range(0, 4)
                .Select(_ => new byte[
                    DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength])
                .ToArray();
            byte[] speaker =
                DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                    0, frames, microphoneEnabled: true);

            Assert.AreEqual(0xA1, control[2]);
            Assert.AreEqual(0xF3, control[3]);
            Assert.AreEqual(0xA1, speaker[2]);
        }

        [DataTestMethod]
        [DataRow(1, 0x12)]
        [DataRow(2, 0x14)]
        [DataRow(4, 0x17)]
        public void FullDuplexSpeakerReportsAlwaysRetainMicrophoneMode(
            int frameCount, int expectedReportId)
        {
            byte[][] frames = Enumerable.Range(0, frameCount)
                .Select(_ => new byte[
                    DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength])
                .ToArray();

            byte[] report =
                DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                    0x1234, frames, microphoneEnabled: true);

            Assert.AreEqual((byte)expectedReportId, report[0]);
            Assert.AreEqual(0xA1, report[2],
                "Any A0 speaker payload would switch genuine DS4 hardware out of microphone mode.");
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));
        }

        [TestMethod]
        public void MicrophoneOnlyControlReportUsesMicrophoneTransaction()
        {
            byte[] report =
                DualShock4BluetoothAudioProtocol.BuildAudioControlReport(
                    speakerEnabled: false, microphoneEnabled: true,
                    speakerVolume: 0x4F, headphoneVolume: 0x4F,
                    microphoneVolume: 0x40);

            Assert.AreEqual(0xA1, report[2]);
            Assert.AreEqual(0xF3, report[3]);
            Assert.AreEqual(0x00, report[21]);
            Assert.AreEqual(0x00, report[22]);
            Assert.AreEqual(0x40, report[23]);
            Assert.AreEqual(0x00, report[24]);
            Assert.AreEqual(
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA2, report, report.Length - sizeof(uint)),
                ReadUInt32(report, report.Length - sizeof(uint)));
        }

        [TestMethod]
        public void ExtractsAudioOnlyMicrophoneFrames()
        {
            byte[] frame = BuildMsbcFrame();
            byte[] report = BuildMicrophoneInputReport(0x12, hasHid: false,
                frame, frame);
            var extracted = new List<byte[]>();

            int count =
                DualShock4BluetoothAudioProtocol.ExtractMicrophoneSbcFrames(
                    report, report.Length, extracted.Add);

            Assert.AreEqual(2, count);
            Assert.AreEqual(2, extracted.Count);
            CollectionAssert.AreEqual(frame, extracted[0]);
            CollectionAssert.AreEqual(frame, extracted[1]);
            Assert.IsTrue(
                DualShock4BluetoothAudioProtocol.ValidateInputReportCrc(
                    report, report.Length));
        }

        [TestMethod]
        public void ExtractedMicrophoneFramesCarryIncrementingAudioCounter()
        {
            byte[] frame = BuildMsbcFrame();
            byte[] report = BuildMicrophoneInputReport(0x12, hasHid: false,
                frame, frame);
            var sequences = new List<ushort>();
            var extracted = new List<byte[]>();

            int count =
                DualShock4BluetoothAudioProtocol.ExtractMicrophoneSbcFrames(
                    report, report.Length, (sequence, extractedFrame) =>
                    {
                        sequences.Add(sequence);
                        extracted.Add(extractedFrame);
                    });

            Assert.AreEqual(2, count);
            CollectionAssert.AreEqual(new ushort[] { 0x1234, 0x1235 },
                sequences);
            CollectionAssert.AreEqual(frame, extracted[0]);
            CollectionAssert.AreEqual(frame, extracted[1]);
        }

        [TestMethod]
        public void ExtractsStatePrefixedMicrophoneFrame()
        {
            byte[] frame = BuildMsbcFrame();
            byte[] report = BuildMicrophoneInputReport(0x13, hasHid: true,
                frame);
            var extracted = new List<byte[]>();

            int count =
                DualShock4BluetoothAudioProtocol.ExtractMicrophoneSbcFrames(
                    report, report.Length, extracted.Add);

            Assert.AreEqual(1, count);
            CollectionAssert.AreEqual(frame, extracted.Single());
            Assert.IsTrue(DualShock4BluetoothAudioProtocol.HasHidState(report));
        }

        [TestMethod]
        public void ExtractsGenuineHardwareMicrophoneTarget()
        {
            byte[] frame = BuildStandardMicrophoneFrame();
            byte[] report = BuildMicrophoneInputReport(0x13, hasHid: true,
                audioTarget: 0x01, frame);
            var extracted = new List<byte[]>();

            int count =
                DualShock4BluetoothAudioProtocol.ExtractMicrophoneSbcFrames(
                    report, report.Length, extracted.Add);

            Assert.AreEqual(1, count);
            CollectionAssert.AreEqual(frame, extracted.Single());
        }

        [TestMethod]
        public void GenuineCuhZct2A1ReportUsesItsLogicalLengthAndStandardSbc()
        {
            const string logicalReportHex =
                "13C080817E807F0800040000931D0CF9FF0200F7FFD4FFBC1F4A070000000000030000011183AF911880000000008000000080000000008000000080000000008000000080000000040D000000008AF0019C311D94E4454554D7C25376BA5ADC75CDB6E3AC68BA8D64A948EA936DA7521331BA82EB70D3BACB869936D334AD371BA429A4CD1B89B668928DA74112CE39F0D27400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000B3C9A6BA";
            byte[] logicalReport = Convert.FromHexString(logicalReportHex);
            byte[] hidReadBuffer = new byte[547];
            Buffer.BlockCopy(logicalReport, 0, hidReadBuffer, 0,
                logicalReport.Length);
            // Model the stale bytes Windows can leave beyond the real 0x13
            // packet. They must never participate in CRC or SBC scanning.
            Array.Fill(hidReadBuffer, (byte)0xCC,
                logicalReport.Length,
                hidReadBuffer.Length - logicalReport.Length);
            var sequences = new List<ushort>();
            var frames = new List<byte[]>();

            int logicalLength = DualShock4BluetoothAudioProtocol.
                GetInputReportLength(hidReadBuffer[0]);
            bool validCrc = DualShock4BluetoothAudioProtocol.
                ValidateInputReportCrc(hidReadBuffer, logicalLength);
            int extracted = DualShock4BluetoothAudioProtocol.
                ExtractMicrophoneSbcFrames(hidReadBuffer, logicalLength,
                    (sequence, frame) =>
                    {
                        sequences.Add(sequence);
                        frames.Add(frame);
                    });
            bool decoded = new SbcDecoder().Decode(frames.Single(),
                out short[] samples, out short[] right,
                out SbcFrame configuration);

            Assert.AreEqual(206, logicalLength);
            Assert.IsTrue(validCrc);
            Assert.AreEqual(1, extracted);
            CollectionAssert.AreEqual(new ushort[] { 0xF08A }, sequences);
            Assert.IsTrue(decoded);
            Assert.AreEqual(SbcFrequency.Freq16K, configuration.Frequency);
            Assert.AreEqual(SbcMode.Mono, configuration.Mode);
            Assert.AreEqual(29, configuration.Bitpool);
            Assert.AreEqual(66, frames.Single().Length);
            Assert.AreEqual(128, samples.Length);
            Assert.IsNull(right);
        }

        [TestMethod]
        public void MsbcRoundTripProducesOneHundredTwentyMonoSamples()
        {
            byte[] encoded = BuildMsbcFrame();
            var decoder = new SbcDecoder();

            bool decoded = decoder.Decode(encoded, out short[] left,
                out short[] right, out SbcFrame configuration);

            Assert.IsTrue(decoded);
            Assert.IsTrue(configuration.IsMsbc);
            Assert.AreEqual(120, left.Length);
            Assert.IsNull(right);
            Assert.IsTrue(left.Any(sample => sample != 0));
        }

        [TestMethod]
        public void StandardSbcMicrophoneFrameIsExtractedAndDecoded()
        {
            byte[] frame = BuildStandardMicrophoneFrame();
            byte[] report = BuildMicrophoneInputReport(0x12, hasHid: false,
                frame);
            var extracted = new List<byte[]>();

            int count =
                DualShock4BluetoothAudioProtocol.ExtractMicrophoneSbcFrames(
                    report, report.Length, extracted.Add);
            bool decoded = new SbcDecoder().Decode(extracted.Single(),
                out short[] samples, out short[] right,
                out SbcFrame decodedConfiguration);

            Assert.AreEqual(1, count);
            CollectionAssert.AreEqual(frame, extracted.Single());
            Assert.IsTrue(decoded);
            Assert.AreEqual(SbcFrequency.Freq16K,
                decodedConfiguration.Frequency);
            Assert.AreEqual(SbcMode.Mono, decodedConfiguration.Mode);
            Assert.AreEqual(128, samples.Length);
            Assert.IsNull(right);
        }

        [TestMethod]
        public void CorruptInputCrcIsRejected()
        {
            byte[] report = BuildMicrophoneInputReport(0x12, hasHid: false,
                BuildMsbcFrame());
            report[10] ^= 0x80;

            Assert.IsFalse(
                DualShock4BluetoothAudioProtocol.ValidateInputReportCrc(
                    report, report.Length));
        }

        [TestMethod]
        public void InputCrcMatchesExistingDs4SeedImplementation()
        {
            byte[] report = BuildMicrophoneInputReport(0x12, hasHid: false,
                BuildMsbcFrame());
            int crcOffset = report.Length - sizeof(uint);
            byte[] prefixedReport = new byte[crcOffset + 1];
            prefixedReport[0] = 0xA1;
            Buffer.BlockCopy(report, 0, prefixedReport, 1, crcOffset);
            uint expected = Crc32Algorithm.Compute(prefixedReport);

            Assert.AreEqual(expected,
                DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                    0xA1, report, crcOffset));
        }

        [TestMethod]
        public async Task BluetoothAudioStateSerializesLaneControlWrites()
        {
            var state = new DualShock4BluetoothAudioState();
            using var speakerWriterEntered = new ManualResetEventSlim(false);
            using var releaseSpeakerWriter = new ManualResetEventSlim(false);
            using var microphoneUpdateStarted = new ManualResetEventSlim(false);
            using var microphoneWriterEntered = new ManualResetEventSlim(false);
            DualShock4BluetoothAudioState.Snapshot speakerPublished = null;
            DualShock4BluetoothAudioState.Snapshot microphonePublished = null;

            Task speaker = Task.Run(() => state.Update(true, null,
                null, null, null, snapshot =>
                {
                    speakerPublished = snapshot;
                    speakerWriterEntered.Set();
                    releaseSpeakerWriter.Wait();
                    return true;
                }));
            Assert.IsTrue(speakerWriterEntered.Wait(TimeSpan.FromSeconds(2)));

            Task microphone = Task.Run(() =>
            {
                microphoneUpdateStarted.Set();
                return state.Update(null, true, null, null, null, snapshot =>
                {
                    microphonePublished = snapshot;
                    microphoneWriterEntered.Set();
                    return true;
                });
            });

            try
            {
                Assert.IsTrue(microphoneUpdateStarted.Wait(
                    TimeSpan.FromSeconds(2)));
                Assert.IsFalse(microphoneWriterEntered.Wait(100),
                    "A later 0x11 publisher entered before the earlier writer completed.");
            }
            finally
            {
                releaseSpeakerWriter.Set();
            }

            await Task.WhenAll(speaker, microphone);
            Assert.IsTrue(speakerPublished.SpeakerEnabled);
            Assert.IsFalse(speakerPublished.MicrophoneEnabled);
            Assert.IsTrue(microphonePublished.SpeakerEnabled);
            Assert.IsTrue(microphonePublished.MicrophoneEnabled);
            Assert.IsTrue(state.Current.SpeakerEnabled);
            Assert.IsTrue(state.Current.MicrophoneEnabled);
        }

        [TestMethod]
        public async Task SynchronizedSpeakerReadWaitsForMicrophoneModePublish()
        {
            var state = new DualShock4BluetoothAudioState();
            state.Update(true, false, 0x4F, 0x4F, 0x40, _ => true);
            using var publishEntered = new ManualResetEventSlim(false);
            using var releasePublish = new ManualResetEventSlim(false);
            using var readStarted = new ManualResetEventSlim(false);
            using var speakerSubmitted = new ManualResetEventSlim(false);
            DualShock4BluetoothAudioState.Snapshot observed = null;
            byte[] submittedReport = null;
            bool updateResult = false;

            Task microphoneUpdate = Task.Run(() =>
            {
                updateResult = state.Update(null, true, null, null, null,
                    snapshot =>
                    {
                        Assert.IsTrue(snapshot.SpeakerEnabled);
                        Assert.IsTrue(snapshot.MicrophoneEnabled);
                        publishEntered.Set();
                        releasePublish.Wait();
                        return true;
                    });
            });
            Assert.IsTrue(publishEntered.Wait(TimeSpan.FromSeconds(2)));

            byte[][] frames =
            {
                new byte[DualShock4BluetoothAudioProtocol.
                    SpeakerSbcFrameLength],
            };
            Task speakerRead = Task.Run(() =>
            {
                readStarted.Set();
                state.ReadSynchronized(snapshot =>
                {
                    observed = snapshot;
                    submittedReport =
                        DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                            0, frames, microphoneEnabled:
                                snapshot.MicrophoneEnabled);
                    speakerSubmitted.Set();
                });
            });

            try
            {
                Assert.IsTrue(readStarted.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(speakerSubmitted.Wait(100),
                    "A speaker payload escaped while the A1 control publish still owned the audio-state gate.");
            }
            finally
            {
                releasePublish.Set();
            }

            await Task.WhenAll(microphoneUpdate, speakerRead);
            Assert.IsTrue(updateResult);
            Assert.IsNotNull(observed);
            Assert.IsTrue(observed.SpeakerEnabled);
            Assert.IsTrue(observed.MicrophoneEnabled);
            Assert.IsNotNull(submittedReport);
            Assert.AreEqual(0xA1, submittedReport[2],
                "The first speaker payload after the committed microphone update must retain A1 mode.");
            Assert.IsTrue(state.Current.MicrophoneEnabled);
        }

        [TestMethod]
        public void ProductionDuplexPublishesTheSameModeUsedOnTheWire()
        {
            var state = new DualShock4BluetoothAudioState();
            var controlModes = new List<byte>();
            var dataModes = new List<byte>();

            bool Publish(bool? speakerEnabled, bool? microphoneEnabled)
            {
                return state.Update(speakerEnabled, microphoneEnabled,
                    0x4F, 0x4F, 0x40, snapshot =>
                    {
                        byte[] control = DualShock4BluetoothAudioProtocol.
                            BuildAudioControlReport(snapshot.SpeakerEnabled,
                                snapshot.MicrophoneEnabled,
                                snapshot.SpeakerVolume,
                                snapshot.HeadphoneVolume,
                                snapshot.MicrophoneVolume);
                        DualShock4BluetoothSpeakerPassthrough.
                            ApplyProductionDuplexAudioMode(control,
                                snapshot.MicrophoneEnabled);
                        controlModes.Add(control[2]);
                        return true;
                    });
            }

            void SubmitData()
            {
                state.ReadSynchronized(snapshot =>
                {
                    byte[] data = DualShock4BluetoothAudioProtocol.
                        BuildSpeakerReport(0,
                            new[] { new byte[DualShock4BluetoothAudioProtocol.
                                SpeakerSbcFrameLength] },
                            microphoneEnabled:
                                snapshot.MicrophoneEnabled);
                    DualShock4BluetoothSpeakerPassthrough.
                        ApplyProductionDuplexAudioMode(data,
                            snapshot.MicrophoneEnabled);
                    dataModes.Add(data[2]);
                });
            }

            Assert.IsTrue(Publish(true, false));
            SubmitData();
            Assert.IsTrue(Publish(null, true));
            SubmitData();
            Assert.IsTrue(Publish(null, false));
            SubmitData();

            CollectionAssert.AreEqual(new byte[] { 0xA0, 0xA1, 0xA0 },
                controlModes);
            CollectionAssert.AreEqual(new byte[] { 0xA0, 0xA1, 0xA0 },
                dataModes);
            Assert.IsTrue(state.Current.SpeakerEnabled);
            Assert.IsFalse(state.Current.MicrophoneEnabled);
        }

        [TestMethod]
        public void BluetoothAudioStateCanPreserveAnOwnedSpeakerLane()
        {
            var state = new DualShock4BluetoothAudioState();
            state.Update(true, false, 0x30, 0x31, 0x32,
                _ => true);

            state.Update(null, true, 0x40, 0x41, 0x42,
                _ => true);

            Assert.IsTrue(state.Current.SpeakerEnabled);
            Assert.IsTrue(state.Current.MicrophoneEnabled);
            Assert.AreEqual(0x40, state.Current.SpeakerVolume);
            Assert.AreEqual(0x41, state.Current.HeadphoneVolume);
            Assert.AreEqual(0x42, state.Current.MicrophoneVolume);
        }

        [TestMethod]
        public void BluetoothAudioStateDoesNotCommitFailedControlWrite()
        {
            var state = new DualShock4BluetoothAudioState();

            bool written = state.Update(true, null, 0x30, null, null,
                _ => false);

            Assert.IsFalse(written);
            Assert.IsFalse(state.Current.SpeakerEnabled);
            Assert.AreEqual(0x4F, state.Current.SpeakerVolume);
        }

        [TestMethod]
        public void DualSenseDirectSpeakerResamplerProducesExactRate()
        {
            var resampler = new StereoPcm48To32LinearResampler();
            var source = new float[480 * 2];
            var destination = new byte[480 * 2 * sizeof(short)];
            int totalOutputFrames = 0;

            for (int packet = 0; packet < 100; packet++)
            {
                for (int frame = 0; frame < 480; frame++)
                {
                    int absoluteFrame = packet * 480 + frame;
                    float sample = (float)(Math.Sin(2.0 * Math.PI * 1000.0 *
                        absoluteFrame / 48000.0) * 0.5);
                    source[frame * 2] = sample;
                    source[frame * 2 + 1] = -sample;
                }

                int bytes = resampler.Convert(source, 480, destination);
                Assert.AreEqual(320 * 2 * sizeof(short), bytes);
                totalOutputFrames += bytes / (2 * sizeof(short));
            }

            Assert.AreEqual(32000, totalOutputFrames,
                "One second at 48 kHz must remain exactly one second at 32 kHz.");
        }

        [TestMethod]
        public void DualSenseDirectSpeakerResamplerPreservesUsbBoundaryPhase()
        {
            const int frameCount = 1440;
            var source = new float[frameCount * 2];
            for (int frame = 0; frame < frameCount; frame++)
            {
                source[frame * 2] = (float)Math.Sin(
                    2.0 * Math.PI * 997.0 * frame / 48000.0);
                source[frame * 2 + 1] = (float)Math.Sin(
                    2.0 * Math.PI * 1499.0 * frame / 48000.0);
            }

            var contiguousResampler =
                new StereoPcm48To32LinearResampler();
            var contiguous = new byte[frameCount * 2 * sizeof(short)];
            int contiguousBytes = contiguousResampler.Convert(source,
                frameCount, contiguous);
            Array.Resize(ref contiguous, contiguousBytes);

            var chunkedResampler = new StereoPcm48To32LinearResampler();
            var chunked = new List<byte>(contiguousBytes);
            int[] chunkSizes = { 1, 2, 7, 31, 480, 5, 193, 721 };
            int sourceOffset = 0;
            foreach (int requestedFrames in chunkSizes)
            {
                int frames = Math.Min(requestedFrames,
                    frameCount - sourceOffset);
                if (frames <= 0)
                {
                    break;
                }

                var packet = new float[frames * 2];
                Array.Copy(source, sourceOffset * 2, packet, 0,
                    packet.Length);
                var packetOutput = new byte[(frames + 2) * 2 *
                    sizeof(short)];
                int packetBytes = chunkedResampler.Convert(packet, frames,
                    packetOutput);
                for (int index = 0; index < packetBytes; index++)
                {
                    chunked.Add(packetOutput[index]);
                }
                sourceOffset += frames;
            }

            if (sourceOffset < frameCount)
            {
                int frames = frameCount - sourceOffset;
                var packet = new float[frames * 2];
                Array.Copy(source, sourceOffset * 2, packet, 0,
                    packet.Length);
                var packetOutput = new byte[(frames + 2) * 2 *
                    sizeof(short)];
                int packetBytes = chunkedResampler.Convert(packet, frames,
                    packetOutput);
                for (int index = 0; index < packetBytes; index++)
                {
                    chunked.Add(packetOutput[index]);
                }
            }

            CollectionAssert.AreEqual(contiguous, chunked.ToArray(),
                "Changing USB transfer boundaries must not reset the 48-to-32 kHz phase.");
        }

        [TestMethod]
        public void DualShock4DriftModeParsesAndBoundsQueueSteering()
        {
            Assert.AreEqual(DualShock4AudioDriftMode.Fractional,
                DualShock4AudioDriftSettings.Parse(null));
            Assert.AreEqual(DualShock4AudioDriftMode.Fractional,
                DualShock4AudioDriftSettings.Parse(" fractional "));
            Assert.AreEqual(DualShock4AudioDriftMode.Fractional,
                DualShock4AudioDriftSettings.Parse("unknown"));
            Assert.AreEqual(DualShock4AudioDriftMode.Off,
                DualShock4AudioDriftSettings.Parse("OFF"));
            Assert.AreEqual(DualShock4AudioDriftMode.Slip,
                DualShock4AudioDriftSettings.Parse("Slip"));

            Assert.AreEqual(1.002,
                DualShock4AudioDriftSettings.CalculateTargetOutputRatio(
                    queueDepth: 0, targetQueueDepth: 16), 1.0e-12);
            Assert.AreEqual(1.0,
                DualShock4AudioDriftSettings.CalculateTargetOutputRatio(
                    queueDepth: 16, targetQueueDepth: 16), 1.0e-12);
            Assert.AreEqual(0.998,
                DualShock4AudioDriftSettings.CalculateTargetOutputRatio(
                    queueDepth: 64, targetQueueDepth: 16), 1.0e-12);
            Assert.AreEqual(1.0001,
                DualShock4AudioDriftSettings.SlewOutputRatio(1.0, 1.002),
                1.0e-12);
            Assert.AreEqual(0.9999,
                DualShock4AudioDriftSettings.SlewOutputRatio(1.0, 0.998),
                1.0e-12);
            Assert.AreEqual(1.00205,
                DualShock4AudioDriftSettings.SlewOutputRatio(1.00195,
                    2.0), 1.0e-12);
        }

        [TestMethod]
        public void DualShock4FractionalDriftUnityIsExactAcrossCallbacks()
        {
            const int frameCount = 1024;
            var sourceSamples = new short[frameCount * 2];
            for (int frame = 0; frame < frameCount; frame++)
            {
                sourceSamples[frame * 2] = (short)(frame * 17 - 8000);
                sourceSamples[frame * 2 + 1] = (short)(12000 - frame * 19);
            }
            var source = new byte[sourceSamples.Length * sizeof(short)];
            Buffer.BlockCopy(sourceSamples, 0, source, 0, source.Length);

            var contiguousResampler =
                new StereoPcm16FractionalResampler();
            var contiguous = new short[(frameCount + 4) * 2];
            int contiguousFrames = contiguousResampler.Convert(source,
                source.Length, contiguous, 1.0);
            Assert.AreEqual(frameCount, contiguousFrames);
            for (int sample = 0; sample < sourceSamples.Length; sample++)
            {
                Assert.AreEqual(sourceSamples[sample], contiguous[sample],
                    $"Unity conversion changed sample {sample}.");
            }

            var chunkedResampler = new StereoPcm16FractionalResampler();
            var chunked = new List<short>(sourceSamples.Length);
            int[] packetFrames = { 1, 7, 320, 2, 193, 501 };
            int sourceFrame = 0;
            foreach (int frames in packetFrames)
            {
                var packet = new byte[frames * 2 * sizeof(short)];
                Buffer.BlockCopy(source, sourceFrame * 2 * sizeof(short),
                    packet, 0, packet.Length);
                var output = new short[(frames + 4) * 2];
                int outputFrames = chunkedResampler.Convert(packet,
                    packet.Length, output, 1.0);
                for (int sample = 0; sample < outputFrames * 2; sample++)
                {
                    chunked.Add(output[sample]);
                }
                sourceFrame += frames;
            }

            CollectionAssert.AreEqual(sourceSamples, chunked.ToArray(),
                "Unity ASRC must be bit-exact regardless of callback boundaries.");
        }

        [DataTestMethod]
        [DataRow(0.998)]
        [DataRow(1.002)]
        public void DualShock4FractionalDriftHasBoundedCountAndContinuity(
            double ratio)
        {
            const int packetFrames = 320;
            const int packetCount = 100;
            var resampler = new StereoPcm16FractionalResampler();
            int totalOutputFrames = 0;
            int? previousLeft = null;

            for (int packetIndex = 0; packetIndex < packetCount; packetIndex++)
            {
                var inputSamples = new short[packetFrames * 2];
                for (int frame = 0; frame < packetFrames; frame++)
                {
                    int absoluteFrame = packetIndex * packetFrames + frame;
                    inputSamples[frame * 2] = (short)(absoluteFrame - 16000);
                    inputSamples[frame * 2 + 1] =
                        (short)(16000 - absoluteFrame);
                }
                var input = new byte[inputSamples.Length * sizeof(short)];
                Buffer.BlockCopy(inputSamples, 0, input, 0, input.Length);
                var output = new short[(packetFrames + 4) * 2];
                int outputFrames = resampler.Convert(input, input.Length,
                    output, ratio);
                for (int frame = 0; frame < outputFrames; frame++)
                {
                    int left = output[frame * 2];
                    if (previousLeft.HasValue)
                    {
                        int delta = left - previousLeft.Value;
                        Assert.IsTrue(delta >= 0 && delta <= 2,
                            $"Fractional interpolation jumped by {delta} at " +
                            $"output frame {totalOutputFrames + frame}.");
                    }
                    previousLeft = left;
                }
                totalOutputFrames += outputFrames;
            }

            double expectedFrames = packetFrames * packetCount * ratio;
            Assert.IsTrue(Math.Abs(totalOutputFrames - expectedFrames) <= 2.0,
                $"Ratio {ratio:F3} produced {totalOutputFrames} frames; " +
                $"expected approximately {expectedFrames:F2}.");
        }

        [TestMethod]
        public void DirectAudioSchedulerPreservesAbsolutePhaseForSubMillisecondJitter()
        {
            const long scheduled = 10_000;
            const long cadence = 16_000;
            const long rebaseThreshold = 1_000;

            Assert.AreEqual(10_000,
                DualShock4AudioReportScheduler.
                    GetDirectRebaseLatenessTicks(10_000_000),
                "The production rebase threshold must remain exactly 1 ms.");

            long next = DualShock4AudioReportScheduler.AdvanceDeadline(
                scheduled, scheduled + rebaseThreshold - 1, cadence,
                rebaseThreshold, out bool rebased);

            Assert.IsFalse(rebased);
            Assert.AreEqual(scheduled + cadence, next);
        }

        [TestMethod]
        public void DirectAudioSchedulerRebasesAfterMeaningfulStall()
        {
            const long scheduled = 10_000;
            const long cadence = 16_000;
            const long rebaseThreshold = 1_000;
            const long actualWake = scheduled + rebaseThreshold;

            long next = DualShock4AudioReportScheduler.AdvanceDeadline(
                scheduled, actualWake, cadence, rebaseThreshold,
                out bool rebased);

            Assert.IsTrue(rebased);
            Assert.AreEqual(actualWake + cadence, next);
            Assert.AreEqual(cadence, next - actualWake,
                "A late report must not be followed by a catch-up interval.");
        }

        [TestMethod]
        public void DirectAudioSchedulerMaintainsCadenceAfterRebase()
        {
            const long cadence = 16_000;
            const long rebaseThreshold = 1_000;
            const long firstScheduled = 10_000;
            const long stalledWake = 18_000;

            long rebasedDeadline =
                DualShock4AudioReportScheduler.AdvanceDeadline(
                    firstScheduled, stalledWake, cadence, rebaseThreshold,
                    out bool firstRebased);
            long followingDeadline =
                DualShock4AudioReportScheduler.AdvanceDeadline(
                    rebasedDeadline, rebasedDeadline, cadence,
                    rebaseThreshold, out bool secondRebased);

            Assert.IsTrue(firstRebased);
            Assert.IsFalse(secondRebased);
            Assert.AreEqual(cadence,
                followingDeadline - rebasedDeadline);
        }

        [TestMethod]
        public void DirectAudioSchedulerDoesNotCatchUpAfterLateSubmission()
        {
            const long currentScheduled = 26_000;
            const long previousReport = 35_000;
            const long cadence = 16_000;
            const long rebaseThreshold = 1_000;

            long currentDeadline =
                DualShock4AudioReportScheduler.SelectCurrentDeadline(
                    currentScheduled, previousReport, cadence,
                    rebaseThreshold, out bool rebased);

            Assert.IsTrue(rebased);
            Assert.AreEqual(previousReport + cadence, currentDeadline);
            Assert.AreEqual(cadence,
                currentDeadline - previousReport,
                "Work which delayed the previous submission must not cause " +
                "an immediate catch-up report on the next loop.");
        }

        [TestMethod]
        public void DirectAudioSchedulerKeepsPhaseAfterSubMillisecondSubmissionJitter()
        {
            const long currentScheduled = 26_000;
            const long previousScheduled = 10_000;
            const long previousReport = previousScheduled + 999;
            const long cadence = 16_000;
            const long rebaseThreshold = 1_000;

            long currentDeadline =
                DualShock4AudioReportScheduler.SelectCurrentDeadline(
                    currentScheduled, previousReport, cadence,
                    rebaseThreshold, out bool rebased);

            Assert.IsFalse(rebased);
            Assert.AreEqual(currentScheduled, currentDeadline);
        }

        [TestMethod]
        public void DualShock4ControllerClockTracksAcrossTimestampWrapAndDeliveryTails()
        {
            const long frequency = 10_000_000;
            const double controllerClockRatio = 1.0015;
            var discipline = new DualShock4ControllerClockDiscipline();
            ushort controllerTimestamp = 65_000;
            long idealHostTimestamp = 1_000_000;
            long deliveredHostTimestamp = idealHostTimestamp;
            discipline.Observe(controllerTimestamp, deliveredHostTimestamp,
                frequency);

            for (int packet = 1; packet <= 10_000; packet++)
            {
                controllerTimestamp = unchecked((ushort)(
                    controllerTimestamp + 750));
                idealHostTimestamp += (long)Math.Round(40_000.0 /
                    controllerClockRatio);
                // Periodically model a 30 ms positive Bluetooth delivery tail
                // followed by queued reports draining in-order. Every one-
                // second bucket still contains low-delay observations.
                int tailPacket = packet % 250;
                long delay = tailPacket < 8 ?
                    Math.Max(0, 300_000 - tailPacket * 39_000L) : 0;
                deliveredHostTimestamp = Math.Max(
                    deliveredHostTimestamp + 1,
                    idealHostTimestamp + delay);
                discipline.Observe(controllerTimestamp,
                    deliveredHostTimestamp, frequency);
            }

            Assert.IsTrue(discipline.HasEstimate);
            Assert.IsTrue(discipline.AcceptedFits > 20);
            Assert.AreEqual(controllerClockRatio, discipline.RawRatio,
                0.00015);
            Assert.AreEqual(controllerClockRatio, discipline.Ratio,
                0.00020);
            Assert.IsTrue(discipline.CumulativeControllerTicks >
                ushort.MaxValue,
                "The fit must survive repeated 16-bit timestamp wraps.");
        }

        [TestMethod]
        public void DualShock4ControllerClockRejectsInvalidIntervalsAndOutlierFits()
        {
            const long frequency = 10_000_000;
            var discipline = new DualShock4ControllerClockDiscipline();
            ushort controllerTimestamp = 100;
            long hostTimestamp = 100_000;
            discipline.Observe(controllerTimestamp, hostTimestamp,
                frequency);

            Assert.IsFalse(discipline.Observe(controllerTimestamp,
                hostTimestamp + 40_000, frequency),
                "Duplicate controller timestamps must not enter the fit.");
            controllerTimestamp = unchecked((ushort)(controllerTimestamp +
                DualShock4ControllerClockDiscipline.
                    MaximumControllerIntervalTicks + 1));
            Assert.IsFalse(discipline.Observe(controllerTimestamp,
                hostTimestamp + 80_000, frequency),
                "Ambiguous long controller intervals must restart the fit.");
            Assert.AreEqual(1.0, discipline.Ratio, 1.0e-12);

            // A syntactically valid but physically impossible 10% clock skew
            // is measured, exposed as raw telemetry, and rejected before it
            // can affect the published production ratio.
            discipline.Reset();
            controllerTimestamp = 1_000;
            hostTimestamp = 2_000_000;
            discipline.Observe(controllerTimestamp, hostTimestamp,
                frequency);
            for (int packet = 0; packet < 2_500; packet++)
            {
                controllerTimestamp = unchecked((ushort)(
                    controllerTimestamp + 750));
                hostTimestamp += (long)Math.Round(40_000.0 / 1.10);
                discipline.Observe(controllerTimestamp, hostTimestamp,
                    frequency);
            }

            Assert.IsTrue(discipline.RejectedFits > 0);
            Assert.IsFalse(discipline.HasEstimate);
            Assert.AreEqual(1.10, discipline.RawRatio, 0.002);
            Assert.AreEqual(1.0, discipline.Ratio, 1.0e-12);
        }

        [TestMethod]
        public void DualShock4ControllerClockConvergesAndClampsPublishedRatio()
        {
            const long frequency = 10_000_000;
            var discipline = new DualShock4ControllerClockDiscipline();
            ushort controllerTimestamp = 0;
            long hostTimestamp = 1;
            discipline.Observe(controllerTimestamp, hostTimestamp,
                frequency);

            for (int packet = 0; packet < 15_000; packet++)
            {
                controllerTimestamp = unchecked((ushort)(
                    controllerTimestamp + 750));
                hostTimestamp += (long)Math.Round(40_000.0 / 1.019);
                discipline.Observe(controllerTimestamp, hostTimestamp,
                    frequency);
            }

            Assert.IsTrue(discipline.HasEstimate);
            Assert.AreEqual(
                DualShock4ControllerClockDiscipline.MaximumPublishedRatio,
                discipline.Ratio, 1.0e-12);

            discipline.Reset();
            Assert.IsFalse(discipline.HasEstimate);
            Assert.AreEqual(1.0, discipline.RawRatio, 1.0e-12);
            Assert.AreEqual(1.0, discipline.Ratio, 1.0e-12,
                "Reconnect reset must discard the prior controller clock.");
        }

        [TestMethod]
        public void ControllerClockCadenceAndAsrcRemainCoupledAndBounded()
        {
            const long frequency = 10_000_000;
            const long nominalCadence = 160_000;
            const double controllerClockRatio = 1.002;

            long targetCadence = DualShock4AudioReportScheduler.
                MapControllerClockToCadenceTicks(nominalCadence,
                    controllerClockRatio);
            double asrcRatio = DualShock4AudioDriftSettings.
                CalculateAsrcOutputRatio(controllerClockRatio,
                    queueDepth: 16, targetQueueDepth: 16);
            double sourceFramesPerReport = 32_000.0 * targetCadence /
                frequency;

            Assert.AreEqual(nominalCadence / controllerClockRatio,
                targetCadence, 1.0);
            Assert.AreEqual(controllerClockRatio, asrcRatio, 1.0e-12);
            Assert.AreEqual(512.0, sourceFramesPerReport * asrcRatio,
                0.01,
                "Clock-paced consumption and ASRC production must balance.");

            double upper = DualShock4AudioDriftSettings.
                CalculateAsrcOutputRatio(1.005, queueDepth: 0,
                    targetQueueDepth: 16);
            double lower = DualShock4AudioDriftSettings.
                CalculateAsrcOutputRatio(0.995, queueDepth: 64,
                    targetQueueDepth: 16);
            Assert.IsTrue(upper <= 1.008 && upper > 1.0);
            Assert.IsTrue(lower >= 0.992 && lower < 1.0);

            long firstStep = DualShock4AudioReportScheduler.
                SteerCadenceTicks(nominalCadence, nominalCadence,
                    controllerClockRatio);
            Assert.IsTrue(firstStep < nominalCadence);
            Assert.IsTrue(nominalCadence - firstStep <= 2,
                "Hardware cadence changes must be slew-limited.");

            var resampler = new StereoPcm16FractionalResampler();
            var maximumPacket = new byte[1024 * 2 * sizeof(short)];
            var maximumOutput = new short[(1024 + 16) * 2];
            int maximumOutputFrames = resampler.Convert(maximumPacket,
                maximumPacket.Length, maximumOutput, 1.008);
            Assert.IsTrue(maximumOutputFrames > 1024,
                "The preallocated ASRC path must cover the combined clock " +
                "and queue-correction upper bound.");
        }

        private static byte[] BuildMicrophoneInputReport(byte reportId,
            bool hasHid, params byte[][] frames)
        {
            return BuildMicrophoneInputReport(reportId, hasHid, 0x03, frames);
        }

        private static byte[] BuildMicrophoneInputReport(byte reportId,
            bool hasHid, byte audioTarget, params byte[][] frames)
        {
            int reportLength =
                DualShock4BluetoothAudioProtocol.GetInputReportLength(reportId);
            byte[] report = new byte[reportLength];
            report[0] = reportId;
            report[1] = hasHid ? (byte)0xC0 : (byte)0x40;
            report[2] = 0x80;
            int audioOffset = hasHid ? 78 : 3;
            report[audioOffset] = 0x34;
            report[audioOffset + 1] = 0x12;
            report[audioOffset + 2] = audioTarget;
            int offset = audioOffset + 3;
            foreach (byte[] frame in frames)
            {
                Buffer.BlockCopy(frame, 0, report, offset, frame.Length);
                offset += frame.Length;
            }

            int crcOffset = report.Length - sizeof(uint);
            uint crc = DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                0xA1, report, crcOffset);
            report[crcOffset] = (byte)crc;
            report[crcOffset + 1] = (byte)(crc >> 8);
            report[crcOffset + 2] = (byte)(crc >> 16);
            report[crcOffset + 3] = (byte)(crc >> 24);
            return report;
        }

        private static byte[] BuildStandardMicrophoneFrame()
        {
            var encoder = new SbcEncoder();
            var configuration = new SbcFrame
            {
                Frequency = SbcFrequency.Freq16K,
                Mode = SbcMode.Mono,
                AllocationMethod = SbcBitAllocationMethod.Loudness,
                Blocks = 16,
                Subbands = 8,
                Bitpool = 24,
            };
            return encoder.Encode(BuildTone(128, 16000, 700.0), null,
                configuration);
        }

        private static byte[] BuildMsbcFrame()
        {
            var encoder = new SbcEncoder();
            byte[] encoded = encoder.Encode(
                BuildTone(120, 16000, 1000.0), null, SbcFrame.CreateMsbc());
            Assert.IsNotNull(encoded);
            Assert.AreEqual(57, encoded.Length);
            return encoded;
        }

        private static short[] BuildTone(int samples, int sampleRate,
            double frequency)
        {
            var result = new short[samples];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = (short)(Math.Sin(
                    2.0 * Math.PI * frequency * index / sampleRate) *
                    short.MaxValue * 0.4);
            }
            return result;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return data[offset] |
                (uint)(data[offset + 1] << 8) |
                (uint)(data[offset + 2] << 16) |
                (uint)(data[offset + 3] << 24);
        }
    }
}
