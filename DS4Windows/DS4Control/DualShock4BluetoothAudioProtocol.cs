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
        public const int SpeakerRealtimeReportLength = 142;
        public const int SpeakerSmallReportLength = 270;
        public const int SpeakerLargeReportLength = 462;
        public const int SpeakerRealtimeFramesPerReport = 1;
        public const int SpeakerSmallFramesPerReport = 2;
        public const int SpeakerLargeFramesPerReport = 4;
        // The physical pad accepts report 0x12 with one 4 ms SBC frame, so keep
        // the format available for protocol tests and diagnostics. The direct
        // production lane uses four-frame 0x17 reports: Windows HIDCLASS pads
        // these variable-length writes to the controller's 547-byte maximum,
        // so one-frame reports only quadruple the HID transaction rate.
        public const int SpeakerRealtimeReportDurationMilliseconds =
            SpeakerSamplesPerFrame * SpeakerRealtimeFramesPerReport * 1000 /
            SpeakerSampleRate;
        public const int SpeakerRealtimePrimeFrames = 20;
        public const int SpeakerRealtimeSourceCushionFrames = 16;
        public const int SpeakerMinimumBufferedFrames =
            SpeakerRealtimePrimeFrames + SpeakerRealtimeSourceCushionFrames;
        public const int SpeakerEncodedFrameQueueLimit =
            SpeakerLargeFramesPerReport * 32;
        public const int SpeakerSampleRate = 32000;
        public const int SpeakerSamplesPerFrame = 128;
        public const int SpeakerLargeReportDurationMilliseconds =
            SpeakerSamplesPerFrame * SpeakerLargeFramesPerReport * 1000 /
            SpeakerSampleRate;
        public const int SpeakerDirectFramesPerReport =
            SpeakerLargeFramesPerReport;
        public const int SpeakerDirectReportDurationMilliseconds =
            SpeakerLargeReportDurationMilliseconds;
        public const int SpeakerDirectPrimeReports =
            SpeakerRealtimePrimeFrames / SpeakerDirectFramesPerReport;
        public const int SpeakerSbcFrameLength = 109;
        public const int MicrophoneMsbcFrameLength = 57;
        public const int MicrophoneSamplesPerFrame = 120;

        /// <summary>
        /// Selects the legacy batched Sony speaker report from frames which
        /// already exist. The direct VIIPER sender uses the four-frame result
        /// for its steady 16 ms cadence; the two-frame result remains useful
        /// when draining a legacy source tail.
        /// </summary>
        public static int GetSpeakerReportFrameCount(int bufferedFrames)
        {
            if (bufferedFrames >= SpeakerLargeFramesPerReport)
            {
                return SpeakerLargeFramesPerReport;
            }

            return bufferedFrames >= SpeakerSmallFramesPerReport ?
                SpeakerSmallFramesPerReport : 0;
        }

        private const int HidStateLength = 75;
        private const int BluetoothHeaderLength = 3;
        private const byte SpeakerAudioMode = 0xA0;
        private const byte MicrophoneAudioMode = 0xA1;
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
        /// Builds a DS4 Bluetooth speaker packet used by Sony hardware. One,
        /// two, and four SBC frames use reports 0x12, 0x14, and 0x17. The frame
        /// counter advances by the number of encoded SBC frames, not by the
        /// number of HID reports.
        /// </summary>
        public static byte[] BuildSpeakerReport(ushort frameNumber,
            byte[][] frames, byte audioTarget = 0x02,
            bool microphoneEnabled = false, byte bluetoothPollRate = 0)
        {
            if (frames == null ||
                (frames.Length != SpeakerRealtimeFramesPerReport &&
                    frames.Length != SpeakerSmallFramesPerReport &&
                    frames.Length != SpeakerLargeFramesPerReport))
            {
                throw new ArgumentException(
                    "A DS4 speaker report must contain exactly one, two, or four SBC frames.",
                    nameof(frames));
            }

            int reportLength = GetSpeakerReportLength(frames.Length);
            byte[] report = new byte[reportLength];
            WriteSpeakerReport(report, frameNumber, frames, frames.Length,
                audioTarget, microphoneEnabled, bluetoothPollRate);
            return report;
        }

        /// <summary>
        /// Writes a Sony speaker report into caller-owned storage. This keeps
        /// the realtime Bluetooth lane allocation-free while preserving the
        /// allocating builder used by tests and non-realtime callers.
        /// </summary>
        public static void WriteSpeakerReport(byte[] report,
            ushort frameNumber, byte[][] frames, int frameCount,
            byte audioTarget = 0x02, bool microphoneEnabled = false,
            byte bluetoothPollRate = 0)
        {
            if (frames == null ||
                (frameCount != SpeakerRealtimeFramesPerReport &&
                    frameCount != SpeakerSmallFramesPerReport &&
                    frameCount != SpeakerLargeFramesPerReport) ||
                frames.Length < frameCount)
            {
                throw new ArgumentException(
                    "A DS4 speaker report must contain exactly one, two, or four SBC frames.",
                    nameof(frames));
            }

            for (int index = 0; index < frameCount; index++)
            {
                if (frames[index] == null ||
                    frames[index].Length != SpeakerSbcFrameLength)
                {
                    throw new ArgumentException(
                        $"A DS4 speaker SBC frame must be {SpeakerSbcFrameLength} bytes.",
                        nameof(frames));
                }
            }

            int reportLength = GetSpeakerReportLength(frameCount);
            if (report == null || report.Length < reportLength)
            {
                throw new ArgumentException(
                    $"The DS4 speaker report buffer must hold {reportLength} bytes.",
                    nameof(report));
            }

            Array.Clear(report, 0, reportLength);
            report[0] = frameCount switch
            {
                SpeakerRealtimeFramesPerReport => (byte)0x12,
                SpeakerSmallFramesPerReport => (byte)0x14,
                _ => (byte)0x17,
            };
            // These low bits are the DS4 Bluetooth input interval, not spare
            // audio-report bits. Ordinary effects preserve the profile value
            // with 0xC0 | rate; audio data reports use the Sony 0x40 prefix but
            // must preserve the same rate. Writing bare 0x40 on every 0x17
            // silently reset the controller to rate zero throughout playback,
            // defeating the profile setting and increasing shared-link traffic.
            report[1] = (byte)(0x40 |
                Math.Min(bluetoothPollRate, (byte)16));
            // Byte 2 selects the controller's inbound report mode even on a
            // report which carries outbound speaker SBC. Hardware sweeps on a
            // genuine CUH-ZCT2 show that A2 stops ordinary controller input
            // after a few reports. A0 preserves normal input while the report
            // ID, target byte, and SBC payload still select speaker output;
            // A1 preserves microphone input during full duplex.
            report[2] = microphoneEnabled ? MicrophoneAudioMode :
                SpeakerAudioMode;
            report[3] = (byte)frameNumber;
            report[4] = (byte)(frameNumber >> 8);
            report[5] = audioTarget;
            for (int index = 0; index < frameCount; index++)
            {
                Buffer.BlockCopy(frames[index], 0, report,
                    6 + index * SpeakerSbcFrameLength, SpeakerSbcFrameLength);
            }

            WriteBluetoothCrc(report, reportLength);
        }

        private static int GetSpeakerReportLength(int frameCount)
        {
            return frameCount switch
            {
                SpeakerRealtimeFramesPerReport => SpeakerRealtimeReportLength,
                SpeakerSmallFramesPerReport => SpeakerSmallReportLength,
                SpeakerLargeFramesPerReport => SpeakerLargeReportLength,
                _ => throw new ArgumentOutOfRangeException(nameof(frameCount)),
            };
        }

        /// <summary>
        /// Builds the one-shot report 0x11 which arms or disarms the DS4 audio
        /// plane. This initialization report also carries the current rumble,
        /// lightbar, and flash state. Keeping the initial effects in this same
        /// ordered packet avoids a second competing Bluetooth HID write
        /// immediately after the audio lane is armed.
        /// </summary>
        public static byte[] BuildAudioControlReport(bool speakerEnabled,
            bool microphoneEnabled, byte speakerVolume,
            byte headphoneVolume, byte microphoneVolume,
            byte rightFastRumble = 0, byte leftSlowRumble = 0,
            byte lightbarRed = 0, byte lightbarGreen = 0,
            byte lightbarBlue = 0, byte flashOn = 0, byte flashOff = 0,
            byte bluetoothPollRate = 0)
        {
            const int reportLength = 78;
            byte[] report = new byte[reportLength];
            bool audioEnabled = speakerEnabled || microphoneEnabled;
            // Audio control is specifically a 78-byte report 0x11. A DS4 may
            // also accept the 334-byte report 0x15 for ordinary effects, but
            // putting 0x15 on this short packet makes the firmware stop
            // reporting input. Keep this invariant inside the packet builder.
            report[0] = 0x11;
            // The lower six bits also own the controller's Bluetooth input
            // interval. Resetting them to zero while arming audio increases
            // inbound radio traffic and can starve speaker ACL completions.
            // Preserve the active profile value in the same atomic report.
            report[1] = (byte)(0xC0 | Math.Min(bluetoothPollRate, (byte)16));
            // A0 keeps ordinary controller input alive for speaker-only
            // streaming. A1 selects microphone input even when speaker output
            // is also active; full-duplex speaker payloads retain A1 as well.
            report[2] = microphoneEnabled ? MicrophoneAudioMode :
                (speakerEnabled ? SpeakerAudioMode : (byte)0x00);

            // This is the Sony/DS4AudioStreamer audio-plane validity mask used
            // by the zero-dropout physical-controller trace. The normal effects
            // dispatcher resumes ownership immediately after this one-shot arm.
            byte validity = audioEnabled ? (byte)0xF3 : (byte)0xF0;

            report[3] = validity;
            report[6] = rightFastRumble;
            report[7] = leftSlowRumble;
            report[8] = lightbarRed;
            report[9] = lightbarGreen;
            report[10] = lightbarBlue;
            report[11] = flashOn;
            report[12] = flashOff;
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
            if (frameHandler == null)
            {
                return 0;
            }

            return ExtractMicrophoneSbcFrames(report, reportLength,
                (_, frame) => frameHandler(frame));
        }

        public static int ExtractMicrophoneSbcFrames(byte[] report, int reportLength,
            Action<ushort, byte[]> frameHandler)
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

            ushort frameNumber = (ushort)(report[audioOffset] |
                (report[audioOffset + 1] << 8));
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
                frameHandler(unchecked((ushort)(frameNumber + count)), frame);
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
