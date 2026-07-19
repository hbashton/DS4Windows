using System;
using SBC;

namespace DS4Windows
{
    /// <summary>
    /// Packet framing shared by the original DualShock 4 Bluetooth speaker
    /// and headset microphone transports. Audio travels inside HID reports;
    /// it is not an A2DP/HFP Bluetooth profile exposed by Windows.
    /// </summary>
    public static class DualShock4BluetoothAudioProtocol
    {
        public const int SpeakerSmallReportLength = 270;
        public const int SpeakerLargeReportLength = 462;
        public const int SpeakerSmallFramesPerReport = 2;
        public const int SpeakerLargeFramesPerReport = 4;
        public const int SpeakerMinimumBufferedFrames = SpeakerLargeFramesPerReport;
        public const int SpeakerSbcFrameLength = 109;
        public const int MicrophoneMsbcFrameLength = 57;
        public const int MicrophoneSamplesPerFrame = 120;

        private const int HidStateLength = 75;
        private const int BluetoothHeaderLength = 3;
        // Genuine CUH-ZCT2 hardware uses target 0x01 for the microphone lane.
        // Older protocol notes and synthetic fixtures use 0x03, so accept both.
        private const byte MicrophoneAudioTarget = 0x01;
        private const byte LegacyMicrophoneAudioTarget = 0x03;

        public static int GetInputReportLength(byte reportId)
        {
            return reportId switch
            {
                0x11 => 78,
                0x12 => 142,
                0x13 => 206,
                0x14 => 270,
                0x15 => 334,
                0x16 => 398,
                0x17 => 462,
                0x18 => 526,
                0x19 => 547,
                _ => 0,
            };
        }

        public static bool HasHidState(byte[] report)
        {
            return report != null && report.Length > 1 && (report[1] & 0x80) != 0;
        }

        public static bool HasAudio(byte[] report)
        {
            return report != null && report.Length > 2 && (report[2] & 0x80) != 0;
        }

        public static bool ValidateInputReportCrc(byte[] report, int reportLength)
        {
            if (report == null || reportLength < 8 || reportLength > report.Length)
            {
                return false;
            }

            int crcOffset = reportLength - sizeof(uint);
            uint expected = ReadUInt32LittleEndian(report, crcOffset);
            return expected == ComputeBluetoothCrc(0xA1, report, crcOffset);
        }

        public static byte[] BuildSpeakerReport(ushort frameNumber, byte[] firstFrame,
            byte[] secondFrame, byte audioTarget = 0x02)
        {
            return BuildSpeakerReport(frameNumber,
                new[] { firstFrame, secondFrame }, audioTarget);
        }

        /// <summary>
        /// Builds the DS4 Bluetooth speaker packet used by Sony hardware. Four
        /// SBC frames use report 0x17; the two-frame drain fallback uses 0x14.
        /// The frame counter advances by the number of encoded SBC frames, not
        /// by the number of HID reports.
        /// </summary>
        public static byte[] BuildSpeakerReport(ushort frameNumber,
            byte[][] frames, byte audioTarget = 0x02,
            bool microphoneEnabled = false)
        {
            if (frames == null ||
                (frames.Length != SpeakerSmallFramesPerReport &&
                    frames.Length != SpeakerLargeFramesPerReport))
            {
                throw new ArgumentException(
                    "A DS4 speaker report must contain exactly two or four SBC frames.",
                    nameof(frames));
            }

            for (int index = 0; index < frames.Length; index++)
            {
                if (frames[index] == null ||
                    frames[index].Length != SpeakerSbcFrameLength)
                {
                    throw new ArgumentException(
                        $"A DS4 speaker SBC frame must be {SpeakerSbcFrameLength} bytes.",
                        nameof(frames));
                }
            }

            bool large = frames.Length == SpeakerLargeFramesPerReport;
            int reportLength = large ? SpeakerLargeReportLength :
                SpeakerSmallReportLength;
            byte[] report = new byte[reportLength];
            report[0] = large ? (byte)0x17 : (byte)0x14;
            report[1] = 0x40;
            // The low three bits select the DS4 microphone input mode. Mode 2
            // (the A2 value copied by the original proof-of-concepts) stops
            // ordinary HID input on genuine hardware. A0 is speaker-only;
            // A1 requests the combined HID + microphone report 0x13.
            report[2] = microphoneEnabled ? (byte)0xA1 : (byte)0xA0;
            report[3] = (byte)frameNumber;
            report[4] = (byte)(frameNumber >> 8);
            report[5] = audioTarget;
            for (int index = 0; index < frames.Length; index++)
            {
                Buffer.BlockCopy(frames[index], 0, report,
                    6 + index * SpeakerSbcFrameLength, SpeakerSbcFrameLength);
            }

            WriteBluetoothCrc(report, reportLength);
            return report;
        }

        /// <summary>
        /// Builds the one-shot report 0x11 which arms or disarms the DS4 audio
        /// plane. Only volume validity bits are asserted, matching the Sony
        /// protocol reference; rumble, lightbar, and flash state therefore
        /// remain owned by the regular output dispatcher.
        /// </summary>
        public static byte[] BuildAudioControlReport(bool speakerEnabled,
            bool microphoneEnabled, byte speakerVolume,
            byte headphoneVolume, byte microphoneVolume)
        {
            const int reportLength = 78;
            byte[] report = new byte[reportLength];
            bool audioEnabled = speakerEnabled || microphoneEnabled;
            // Audio control is specifically a 78-byte report 0x11. A DS4 may
            // also accept the 334-byte report 0x15 for ordinary effects, but
            // putting 0x15 on this short packet makes the firmware stop
            // reporting input. Keep this invariant inside the packet builder.
            report[0] = 0x11;
            report[1] = 0xC0;
            report[2] = microphoneEnabled ? (byte)0xA1 :
                speakerEnabled ? (byte)0xA0 : (byte)0x00;

            byte validity = 0;
            if (speakerEnabled)
            {
                // PadForge keeps only the speaker/headphone volume fields valid
                // on its dedicated audio session. Effects remain on the primary
                // DS4Windows control-transfer session.
                validity |= 0xB0;
            }

            if (microphoneEnabled)
            {
                validity |= 0x40;
            }

            if (!audioEnabled)
            {
                // Explicitly mute all audio lanes when the transport is torn down.
                validity = 0xF0;
            }

            report[3] = validity;
            report[21] = speakerEnabled ? headphoneVolume : (byte)0;
            report[22] = speakerEnabled ? headphoneVolume : (byte)0;
            report[23] = microphoneEnabled ? microphoneVolume : (byte)0;
            report[24] = speakerEnabled ? speakerVolume : (byte)0;
            WriteBluetoothCrc(report, reportLength);
            return report;
        }

        public static int ExtractMicrophoneSbcFrames(byte[] report, int reportLength,
            Action<byte[]> frameHandler)
        {
            if (report == null || frameHandler == null ||
                reportLength <= BluetoothHeaderLength + sizeof(uint) ||
                reportLength > report.Length || !HasAudio(report))
            {
                return 0;
            }

            int crcOffset = reportLength - sizeof(uint);
            int audioOffset = FindMicrophoneAudioOffset(report, crcOffset);
            if (audioOffset < 0)
            {
                return 0;
            }

            int count = 0;
            int scan = audioOffset + 3;
            while (scan + SbcFrame.HeaderSize <= crcOffset)
            {
                int frameLength = GetMicrophoneSbcFrameLength(report, scan,
                    crcOffset);
                if (frameLength <= 0)
                {
                    scan++;
                    continue;
                }

                if (scan + frameLength > crcOffset)
                {
                    break;
                }

                byte[] frame = new byte[frameLength];
                Buffer.BlockCopy(report, scan, frame, 0, frame.Length);
                frameHandler(frame);
                count++;
                scan += frameLength;
            }

            return count;
        }

        private static int GetMicrophoneSbcFrameLength(byte[] report, int offset,
            int limit)
        {
            if (offset < 0 || offset + SbcFrame.HeaderSize > limit)
            {
                return 0;
            }

            if (report[offset] == 0xAD)
            {
                return MicrophoneMsbcFrameLength;
            }

            if (report[offset] != 0x9C)
            {
                return 0;
            }

            byte header = report[offset + 1];
            var frame = new SbcFrame
            {
                Frequency = (SbcFrequency)((header >> 6) & 0x03),
                Blocks = ((((header >> 4) & 0x03) + 1) * 4),
                Mode = (SbcMode)((header >> 2) & 0x03),
                AllocationMethod =
                    (SbcBitAllocationMethod)((header >> 1) & 0x01),
                Subbands = (header & 0x01) == 0 ? 4 : 8,
                Bitpool = report[offset + 2],
            };
            return frame.GetFrameSize();
        }

        public static uint ComputeBluetoothCrc(byte prefix, byte[] data, int length)
        {
            if (data == null || length < 0 || length > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            uint crc = 0xFFFFFFFFu;
            crc = UpdateCrc(crc, prefix);
            for (int index = 0; index < length; index++)
            {
                crc = UpdateCrc(crc, data[index]);
            }

            return ~crc;
        }

        private static int FindMicrophoneAudioOffset(byte[] report, int crcOffset)
        {
            int audioOnlyOffset = BluetoothHeaderLength;
            int stateAndAudioOffset = BluetoothHeaderLength + HidStateLength;

            if (HasHidState(report) &&
                stateAndAudioOffset + 3 <= crcOffset &&
                IsMicrophoneAudioTarget(report[stateAndAudioOffset + 2]))
            {
                return stateAndAudioOffset;
            }

            if (audioOnlyOffset + 3 <= crcOffset &&
                IsMicrophoneAudioTarget(report[audioOnlyOffset + 2]))
            {
                return audioOnlyOffset;
            }

            // Some firmware revisions leave the HID flag stale. Check the
            // state-prefixed location as a compatibility fallback.
            if (stateAndAudioOffset + 3 <= crcOffset &&
                IsMicrophoneAudioTarget(report[stateAndAudioOffset + 2]))
            {
                return stateAndAudioOffset;
            }

            return -1;
        }

        private static bool IsMicrophoneAudioTarget(byte target)
        {
            return target == MicrophoneAudioTarget ||
                target == LegacyMicrophoneAudioTarget;
        }

        private static void WriteBluetoothCrc(byte[] report, int reportLength)
        {
            int crcOffset = reportLength - sizeof(uint);
            uint crc = ComputeBluetoothCrc(0xA2, report, crcOffset);
            report[crcOffset] = (byte)crc;
            report[crcOffset + 1] = (byte)(crc >> 8);
            report[crcOffset + 2] = (byte)(crc >> 16);
            report[crcOffset + 3] = (byte)(crc >> 24);
        }

        private static uint ReadUInt32LittleEndian(byte[] data, int offset)
        {
            return data[offset] |
                (uint)(data[offset + 1] << 8) |
                (uint)(data[offset + 2] << 16) |
                (uint)(data[offset + 3] << 24);
        }

        private static uint UpdateCrc(uint crc, byte value)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }

            return crc;
        }
    }
}
