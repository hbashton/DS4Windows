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
                0x14, 0x40, 0xA2, 0x34, 0x12, 0x02,
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
            byte[] frame = encoder.Encode(BuildTone(128, 16000, 700.0), null,
                configuration);
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
            int reportLength =
                DualShock4BluetoothAudioProtocol.GetInputReportLength(reportId);
            byte[] report = new byte[reportLength];
            report[0] = reportId;
            report[1] = hasHid ? (byte)0xC0 : (byte)0x40;
            report[2] = 0x80;
            int audioOffset = hasHid ? 78 : 3;
            report[audioOffset] = 0x34;
            report[audioOffset + 1] = 0x12;
            report[audioOffset + 2] = 0x03;
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
