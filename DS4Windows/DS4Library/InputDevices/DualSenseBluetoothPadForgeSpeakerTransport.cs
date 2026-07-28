using System;
using System.Buffers.Binary;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// DualSense Bluetooth speaker transport using Sony report 0x35. One
    /// fixed-size Opus packet is submitted per radio tick through eight
    /// independently pending OVERLAPPED writes. The native HID queue absorbs
    /// short HidBth completion stalls; this class never waits or sends a
    /// catch-up burst on the real-time producer thread.
    /// </summary>
    internal sealed class DualSenseBluetoothPadForgeSpeakerTransport : IDisposable
    {
        internal const int ReportLength = 334;
        internal const int OpusFrameLength = 200;
        internal const int OpusFrameOffset = 13;
        internal const int NativeWriteSlots = 8;

        private const byte SessionPacketType = 0x91;
        private const byte SpeakerPacketType = 0x93;
        private const byte HeadsetPacketType = 0x96;
        private const byte SessionPayloadLength = 7;
        private const byte NormalInputMask = 0xFE;
        private const byte MicrophoneInputMask = 0xFF;

        private readonly SafeFileHandle audioHandle;
        private readonly DualSenseBluetoothRealtimeWriter writer;
        private readonly bool headsetOnly;
        private readonly byte[] report = new byte[ReportLength];
        private int reportSequence;
        private byte packetCounter;
        private int disposeStarted;
        private long acceptedReports;
        private long saturatedReports;
        private long transportFaults;

        private DualSenseBluetoothPadForgeSpeakerTransport(
            SafeFileHandle audioHandle,
            DualSenseBluetoothRealtimeWriter writer,
            bool headsetOnly)
        {
            this.audioHandle = audioHandle;
            this.writer = writer;
            this.headsetOnly = headsetOnly;
        }

        internal long AcceptedReports =>
            Interlocked.Read(ref acceptedReports);

        internal long SaturatedReports =>
            Interlocked.Read(ref saturatedReports);

        internal long TransportFaults =>
            Interlocked.Read(ref transportFaults);

        internal double MaximumCompletionMilliseconds =>
            writer?.MaximumCompletionMilliseconds ?? 0.0;

        internal double MaximumSubmissionGapMilliseconds =>
            writer?.MaximumSubmissionGapMilliseconds ?? 0.0;

        internal long MaximumNativeWritesAhead =>
            writer?.MaximumAudioPendingBeforeSubmission ?? 0;

        internal static bool TryCreate(DualSenseDevice device,
            bool headsetOnly,
            out DualSenseBluetoothPadForgeSpeakerTransport transport,
            out string error)
        {
            transport = null;
            error = string.Empty;
            if (device?.HidDevice == null ||
                !device.HidDevice.TryOpenDedicatedAudioHandle(
                    out SafeFileHandle audioHandle))
            {
                error = "Could not open a shared overlapped DualSense audio handle.";
                return false;
            }

            try
            {
                if (!DualSenseBluetoothRealtimeWriter.TryCreate(audioHandle,
                        ReportLength, out DualSenseBluetoothRealtimeWriter writer,
                        out int writerError, slotCount: NativeWriteSlots,
                        audioInFlightLimit: NativeWriteSlots))
                {
                    error = "Could not initialize the eight-slot DualSense " +
                        $"audio writer. Win32Error={writerError}.";
                    audioHandle.Dispose();
                    return false;
                }

                transport = new DualSenseBluetoothPadForgeSpeakerTransport(
                    audioHandle, writer, headsetOnly);
                return true;
            }
            catch (Exception ex)
            {
                audioHandle.Dispose();
                error = $"Could not initialize the DualSense 0x35 transport: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Submits exactly one report. A false result without a hard fault
        /// means the oldest of the eight native slots is still pending; the
        /// caller must skip this frame and preserve the absolute cadence.
        /// </summary>
        internal bool TrySend(byte[] opusFrame, int opusLength,
            bool microphoneEnabled, out bool hardFault)
        {
            hardFault = false;
            if (Volatile.Read(ref disposeStarted) != 0 ||
                opusFrame == null || opusLength != OpusFrameLength ||
                opusFrame.Length < opusLength)
            {
                hardFault = true;
                Interlocked.Increment(ref transportFaults);
                return false;
            }

            BuildReport(report, opusFrame, opusLength, headsetOnly,
                microphoneEnabled, ref reportSequence, ref packetCounter);
            bool accepted = writer.TryWrite(report, out hardFault);
            if (accepted)
            {
                Interlocked.Increment(ref acceptedReports);
            }
            else if (hardFault)
            {
                Interlocked.Increment(ref transportFaults);
            }
            else
            {
                Interlocked.Increment(ref saturatedReports);
            }

            return accepted;
        }

        internal static void BuildReport(byte[] destination,
            byte[] opusFrame, int opusLength, bool headsetOnly,
            bool microphoneEnabled, ref int reportSequence,
            ref byte packetCounter)
        {
            if (destination == null || destination.Length != ReportLength)
            {
                throw new ArgumentException(
                    $"The DualSense audio report must be {ReportLength} bytes.",
                    nameof(destination));
            }
            if (opusFrame == null || opusLength != OpusFrameLength ||
                opusFrame.Length < opusLength)
            {
                throw new ArgumentException(
                    $"The DualSense Opus frame must be {OpusFrameLength} bytes.",
                    nameof(opusFrame));
            }

            Array.Clear(destination, 0, destination.Length);
            destination[0] = 0x35;
            destination[1] = (byte)((reportSequence & 0x0F) << 4);
            reportSequence = (reportSequence + 1) & 0x0F;
            destination[2] = SessionPacketType;
            destination[3] = SessionPayloadLength;
            destination[4] = microphoneEnabled ?
                MicrophoneInputMask : NormalInputMask;
            destination[9] = 0xFF;
            destination[10] = packetCounter++;
            destination[11] = headsetOnly ?
                HeadsetPacketType : SpeakerPacketType;
            destination[12] = OpusFrameLength;
            Buffer.BlockCopy(opusFrame, 0, destination,
                OpusFrameOffset, OpusFrameLength);

            uint crc = DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                destination, ReportLength - sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.AsSpan(ReportLength - sizeof(uint), sizeof(uint)),
                crc);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            {
                return;
            }

            writer?.Dispose();
            writer?.WaitForDisposal(1000);
            audioHandle?.Dispose();
        }
    }
}
