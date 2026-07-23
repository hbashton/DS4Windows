using System;

namespace DS4Windows
{
    internal enum DualShock4AudioTransportMode
    {
        Reference,
        PadForgeAsync,
        ProductionReplay,
        ProductionA0,
        ProductionDuplexA1,
        FifoBuffered,
        CreditBuffered,
        Scheduled,
    }

    /// <summary>
    /// Runtime selection and pure queue policy for the physical DualShock 4
    /// speaker transport. The reference lane follows DS4AudioStreamer: source
    /// availability wakes the sender, and each HID write completes before the
    /// next report is presented.
    /// </summary>
    internal static class DualShock4AudioTransportSettings
    {
        internal const string EnvironmentVariableName =
            "DS4WINDOWS_DS4_AUDIO_TRANSPORT_MODE";
        internal const int PadForgeAsyncSlotCount = 8;
        internal const int ProductionReplaySlotCount = 32;
        internal const int ProductionReplayRetainedSourceFrames = 16;
        internal const int ProductionReplayQueueServoTargetFrames =
            ProductionReplayRetainedSourceFrames;
        internal const int ProductionReplayFramesPerReport =
            DualShock4BluetoothAudioProtocol.SpeakerRealtimeFramesPerReport;
        internal const int ProductionReplayPrimeFrames =
            ProductionReplayPrimeReports * ProductionReplayFramesPerReport;
        internal const int ProductionReplayPrimeReports =
            20;
        internal const int ProductionReplayStartupBufferedFrames =
            ProductionReplayPrimeFrames +
                ProductionReplayRetainedSourceFrames;
        internal const int ProductionReplayCadenceMilliseconds = 4;
        internal const int ProductionReplayIdleReprimeMilliseconds = 200;
        internal const byte ProductionReplaySpeakerAudioMode = 0xA2;
        internal const byte ProductionReplayMicrophoneAudioMode = 0xA1;
        internal const byte ProductionA0SpeakerAudioMode = 0xA0;
        internal const byte ProductionDuplexSpeakerAudioMode = 0xA0;
        internal const byte ProductionDuplexMicrophoneAudioMode = 0xA1;
        // The clocked transport presents only four-frame 0x17 reports. Eight
        // reports contain 128 ms of audio; presenting the startup reports four
        // milliseconds apart builds about 100 ms of controller-side coverage
        // without the zero-interval 0x17/0x14 bursts measured in the reference
        // transports. Steady state then consumes exactly one 16 ms report per
        // 16 ms period. A late submission always starts a new period.
        internal const int ScheduledPrimeReports = 8;
        internal const int ScheduledPrimeFramesPerReport =
            DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport;
        internal const int ScheduledPrimeFrames = ScheduledPrimeReports *
            ScheduledPrimeFramesPerReport;
        internal const int ScheduledRetainedSourceFrames =
            DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeSourceCushionFrames;
        internal const int ScheduledStartupBufferedFrames =
            ScheduledPrimeFrames + ScheduledRetainedSourceFrames;
        internal const int ScheduledPrimeCadenceMilliseconds = 4;
        internal const int ScheduledSteadyCadenceMilliseconds =
            DualShock4BluetoothAudioProtocol.
                SpeakerLargeReportDurationMilliseconds;
        // A deliberately shallow controller-FIFO probe: four 0x17 reports
        // carry sixteen unique SBC frames (64 ms), then steady state returns
        // to the proven one-frame 0x12 production cadence. The independent
        // sixteen-frame source cushion remains queued after every prime.
        internal const int FifoBufferedPrimeSlotCount = 4;
        internal const int FifoBufferedPrimeReports = 4;
        internal const int FifoBufferedPrimeFramesPerReport =
            DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport;
        internal const int FifoBufferedPrimeFrames =
            FifoBufferedPrimeReports * FifoBufferedPrimeFramesPerReport;
        internal const int FifoBufferedRetainedSourceFrames =
            ProductionReplayRetainedSourceFrames;
        internal const int FifoBufferedStartupBufferedFrames =
            FifoBufferedPrimeFrames + FifoBufferedRetainedSourceFrames;
        internal const int FifoBufferedSteadyFramesPerReport =
            DualShock4BluetoothAudioProtocol.SpeakerRealtimeFramesPerReport;
        internal const int FifoBufferedCadenceMilliseconds =
            DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeReportDurationMilliseconds;
        internal const int FifoBufferedIdleReprimeMilliseconds =
            ProductionReplayIdleReprimeMilliseconds;
        internal const int FifoBufferedQueueServoTargetFrames =
            ProductionReplayQueueServoTargetFrames;
        // Keep ordinary HID/gyro input active while the controller-side audio
        // cushion is primed. A1 adds microphone audio without changing the
        // speaker packet cadence.
        internal const byte FifoBufferedSpeakerAudioMode = 0xA0;
        internal const byte FifoBufferedMicrophoneAudioMode = 0xA1;
        // Retained as the original physical-credit comparison lane. It caps
        // fourteen four-frame reports at the ACL-credit ceiling observed in
        // ETW; that credit window is transport capacity, not proof that the
        // controller retains 224 ms of playable audio.
        internal const int CreditBufferedSlotCount = 14;
        internal const int CreditBufferedPrimeReports = 14;
        internal const int CreditBufferedFramesPerReport =
            DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport;
        internal const int CreditBufferedPrimeFrames =
            CreditBufferedPrimeReports * CreditBufferedFramesPerReport;
        internal const int CreditBufferedRetainedSourceFrames =
            DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeSourceCushionFrames;
        internal const int CreditBufferedStartupBufferedFrames =
            CreditBufferedPrimeFrames + CreditBufferedRetainedSourceFrames;
        internal const int CreditBufferedCadenceMilliseconds =
            DualShock4BluetoothAudioProtocol.
                SpeakerLargeReportDurationMilliseconds;
        internal const int CreditBufferedIdleReprimeMilliseconds = 200;
        internal const byte CreditBufferedSpeakerAudioMode = 0xA2;

        internal static DualShock4AudioTransportMode Parse(string value)
        {
            if (string.Equals(value?.Trim(), "reference",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.Reference;
            }
            if (string.Equals(value?.Trim(), "scheduled",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "clocked",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.Scheduled;
            }
            if (string.Equals(value?.Trim(), "padforge",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "padforge-async",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "async",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.PadForgeAsync;
            }
            if (string.Equals(value?.Trim(), "production-replay",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "historical-replay",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.ProductionReplay;
            }
            if (string.Equals(value?.Trim(), "production-a0",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.ProductionA0;
            }
            if (string.Equals(value?.Trim(), "production-duplex-a1",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "production-duplex",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "duplex-a1",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.ProductionDuplexA1;
            }
            if (string.Equals(value?.Trim(), "credit-buffered",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "credit-window",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.CreditBuffered;
            }
            if (string.Equals(value?.Trim(), "fifo-buffered",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value?.Trim(), "fifo-prime",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioTransportMode.FifoBuffered;
            }

            // The validated production lane remains in A0 for speaker-only
            // playback and transitions to A1 only while a virtual capture
            // client is active. Unknown values fail closed to this duplex-
            // capable lane; the explicit production-a0 value remains as a
            // microphone-disabled diagnostic rollback.
            return DualShock4AudioTransportMode.ProductionDuplexA1;
        }

        internal static string Format(DualShock4AudioTransportMode mode)
        {
            return mode switch
            {
                DualShock4AudioTransportMode.PadForgeAsync =>
                    "padforge-async",
                DualShock4AudioTransportMode.ProductionReplay =>
                    "production-replay",
                DualShock4AudioTransportMode.ProductionA0 =>
                    "production-a0",
                DualShock4AudioTransportMode.ProductionDuplexA1 =>
                    "production-duplex-a1",
                DualShock4AudioTransportMode.FifoBuffered =>
                    "fifo-buffered",
                DualShock4AudioTransportMode.CreditBuffered =>
                    "credit-buffered",
                DualShock4AudioTransportMode.Scheduled => "scheduled",
                _ => "reference",
            };
        }

        internal static bool ShouldWakeReferenceSender(int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= DualShock4BluetoothAudioProtocol.
                SpeakerLargeFramesPerReport;
        }

        internal static int SelectReferenceReportFrameCount(
            int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return DualShock4BluetoothAudioProtocol.
                GetSpeakerReportFrameCount(bufferedFrames);
        }

        internal static bool ShouldWakePadForgeAsyncSender(int bufferedFrames)
        {
            return ShouldWakeReferenceSender(bufferedFrames);
        }

        internal static int SelectPadForgeAsyncReportFrameCount(
            int bufferedFrames)
        {
            return SelectReferenceReportFrameCount(bufferedFrames);
        }

        internal static bool CanSubmitPadForgeAsync(int pendingWrites)
        {
            if (pendingWrites < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pendingWrites));
            }

            return pendingWrites < PadForgeAsyncSlotCount;
        }

        internal static bool ShouldStartProductionReplay(int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >=
                ProductionReplayStartupBufferedFrames;
        }

        internal static int SelectProductionReplayReportFrameCount(
            int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= ProductionReplayFramesPerReport ?
                ProductionReplayFramesPerReport : 0;
        }

        internal static bool CanSubmitProductionReplay(int pendingWrites)
        {
            if (pendingWrites < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pendingWrites));
            }

            return pendingWrites < ProductionReplaySlotCount;
        }

        internal static bool ShouldBeginProductionReplayReprime(
            int bufferedFrames, long sourceIdleMilliseconds)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }
            if (sourceIdleMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceIdleMilliseconds));
            }

            return bufferedFrames == 0 && sourceIdleMilliseconds >=
                ProductionReplayIdleReprimeMilliseconds;
        }

        internal static long GetProductionReplayCadenceTicks(long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            return checked((long)Math.Round(frequency *
                (ProductionReplayCadenceMilliseconds / 1000.0)));
        }

        internal static double GetProductionReplayQueueServoRatio(
            int bufferedFrames, bool enabled)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return enabled ?
                DualShock4AudioDriftSettings.CalculateTargetOutputRatio(
                    bufferedFrames,
                    ProductionReplayQueueServoTargetFrames) : 1.0;
        }

        internal static byte GetProductionReplayAudioMode(
            bool microphoneEnabled)
        {
            return microphoneEnabled ? ProductionReplayMicrophoneAudioMode :
                ProductionReplaySpeakerAudioMode;
        }

        internal static byte GetProductionDuplexAudioMode(
            bool microphoneEnabled)
        {
            return microphoneEnabled ? ProductionDuplexMicrophoneAudioMode :
                ProductionDuplexSpeakerAudioMode;
        }

        internal static byte GetFifoBufferedAudioMode(
            bool microphoneEnabled)
        {
            return microphoneEnabled ? FifoBufferedMicrophoneAudioMode :
                FifoBufferedSpeakerAudioMode;
        }

        internal static bool UsesProductionReplayPolicy(
            DualShock4AudioTransportMode mode)
        {
            return mode == DualShock4AudioTransportMode.ProductionReplay ||
                mode == DualShock4AudioTransportMode.ProductionA0 ||
                mode == DualShock4AudioTransportMode.ProductionDuplexA1;
        }

        internal static bool ShouldStartFifoBuffered(int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= FifoBufferedStartupBufferedFrames;
        }

        internal static int SelectFifoBufferedPrimeFrameCount(
            int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= FifoBufferedPrimeFramesPerReport ?
                FifoBufferedPrimeFramesPerReport : 0;
        }

        internal static int SelectFifoBufferedSteadyFrameCount(
            int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= FifoBufferedSteadyFramesPerReport ?
                FifoBufferedSteadyFramesPerReport : 0;
        }

        internal static bool CanSubmitFifoBufferedPrime(int pendingWrites)
        {
            if (pendingWrites < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pendingWrites));
            }

            return pendingWrites < FifoBufferedPrimeSlotCount;
        }

        internal static bool ShouldBeginFifoBufferedReprime(
            int bufferedFrames, long sourceIdleMilliseconds)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }
            if (sourceIdleMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceIdleMilliseconds));
            }

            return bufferedFrames == 0 && sourceIdleMilliseconds >=
                FifoBufferedIdleReprimeMilliseconds;
        }

        internal static long GetFifoBufferedCadenceTicks(long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            return checked((long)Math.Round(frequency *
                (FifoBufferedCadenceMilliseconds / 1000.0)));
        }

        internal static double GetFifoBufferedQueueServoRatio(
            int bufferedFrames, bool enabled)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return enabled ?
                DualShock4AudioDriftSettings.CalculateTargetOutputRatio(
                    bufferedFrames,
                    FifoBufferedQueueServoTargetFrames) : 1.0;
        }

        internal static ushort AdvanceFifoBufferedPrimeFrameNumber(
            ushort frameNumber)
        {
            return unchecked((ushort)(frameNumber +
                FifoBufferedPrimeFramesPerReport));
        }

        internal static ushort AdvanceFifoBufferedSteadyFrameNumber(
            ushort frameNumber)
        {
            return unchecked((ushort)(frameNumber +
                FifoBufferedSteadyFramesPerReport));
        }

        internal static bool ShouldStartCreditBuffered(int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= CreditBufferedStartupBufferedFrames;
        }

        internal static int SelectCreditBufferedReportFrameCount(
            int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= CreditBufferedFramesPerReport ?
                CreditBufferedFramesPerReport : 0;
        }

        internal static bool CanSubmitCreditBuffered(int pendingWrites)
        {
            if (pendingWrites < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pendingWrites));
            }

            return pendingWrites < CreditBufferedSlotCount;
        }

        internal static bool ShouldBeginCreditBufferedReprime(
            int bufferedFrames, long sourceIdleMilliseconds)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }
            if (sourceIdleMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceIdleMilliseconds));
            }

            return bufferedFrames == 0 && sourceIdleMilliseconds >=
                CreditBufferedIdleReprimeMilliseconds;
        }

        internal static long GetCreditBufferedCadenceTicks(long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            return checked((long)Math.Round(frequency *
                (CreditBufferedCadenceMilliseconds / 1000.0)));
        }

        internal static ushort AdvanceCreditBufferedFrameNumber(
            ushort frameNumber)
        {
            return unchecked((ushort)(frameNumber +
                CreditBufferedFramesPerReport));
        }

        internal static bool ShouldStartScheduled(int bufferedFrames)
        {
            if (bufferedFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedFrames));
            }

            return bufferedFrames >= ScheduledStartupBufferedFrames;
        }

        internal static long GetScheduledPrimeCadenceTicks(long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            return checked((long)Math.Round(frequency *
                (ScheduledPrimeCadenceMilliseconds / 1000.0)));
        }

        internal static long GetScheduledSteadyCadenceTicks(long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            return checked((long)Math.Round(frequency *
                (ScheduledSteadyCadenceMilliseconds / 1000.0)));
        }
    }
}
