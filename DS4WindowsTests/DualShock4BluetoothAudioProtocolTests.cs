using DS4Windows;
using SBC;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualShock4BluetoothAudioProtocolTests
    {
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
            Assert.AreEqual(0xB0, report[3]);
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
        public void MicrophoneModeUsesCombinedHidInputValue()
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
            Assert.AreEqual(0xF0, control[3]);
            Assert.AreEqual(0xA1, speaker[2]);
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
