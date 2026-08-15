/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Concentus;
using DS4Windows.InputDevices;
using SBC;

namespace DS4Windows
{
    internal delegate void ViiperAtomicAudioHapticsHandler(
        ViiperOutDevice source, byte[] payload, int feedbackOffset,
        int feedbackLength, int speakerPcmOffset, int speakerPcmLength,
        int targetDeviceIndex);

    internal static class ViiperStateWriteRateSettings
    {
        internal const string EnvironmentVariableName =
            "DS4WINDOWS_VIIPER_STATE_RATE_HZ";
        internal const int DefaultDualShock4RateHz = 200;
        internal const int DefaultDualSenseRateHz = 250;
        private const int MinimumRateHz = 30;
        private const int MaximumRateHz = 1000;

        internal static int Parse(string value, int defaultRateHz = 0)
        {
            int fallback = defaultRateHz >= MinimumRateHz &&
                defaultRateHz <= MaximumRateHz ? defaultRateHz : 0;
            string normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return fallback;
            }
            if (string.Equals(normalized, "off",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "immediate",
                    StringComparison.OrdinalIgnoreCase) || normalized == "0")
            {
                return 0;
            }
            if (!int.TryParse(normalized, out int rateHz) ||
                rateHz < MinimumRateHz || rateHz > MaximumRateHz)
            {
                return fallback;
            }

            return rateHz;
        }

        internal static int GetDefaultRateHz(ViiperVirtualDeviceType deviceType)
        {
            switch (deviceType)
            {
                case ViiperVirtualDeviceType.DualShock4:
                    // Full-speed HID bInterval=5 (one input opportunity every
                    // five milliseconds).
                    return DefaultDualShock4RateHz;
                case ViiperVirtualDeviceType.DualSense:
                case ViiperVirtualDeviceType.DualSenseEdge:
                    // High-speed HID bInterval=6 (one input opportunity every
                    // 32 microframes, or four milliseconds).
                    return DefaultDualSenseRateHz;
                default:
                    return 0;
            }
        }

        internal static long GetMinimumIntervalTicks(int rateHz)
        {
            return rateHz <= 0 ? 0 : Math.Max(1,
                (Stopwatch.Frequency + rateHz - 1) / rateHz);
        }

        internal static long GetRemainingTicks(long now, long previousWriteStart,
            long minimumIntervalTicks)
        {
            if (previousWriteStart <= 0 || minimumIntervalTicks <= 0)
            {
                return 0;
            }

            long remaining = previousWriteStart + minimumIntervalTicks - now;
            return Math.Max(0, remaining);
        }
    }

    public enum ViiperVirtualDeviceType
    {
        Xbox360,
        DualShock4,
        DualSense,
        DualSenseEdge,
        Switch2Pro,
    }

    public sealed class ViiperOutDevice : OutputDevice
    {
        private const string DefaultHost = "127.0.0.1";
        private const int DefaultPort = 3242;
        private const int DualSenseBaseFeedbackLength = 6;
        private const int DualSenseTriggerFeedbackOffset = 6;
        // VIIPER sends compact feedback, not a full native HID output report:
        // base rumble/LED bytes plus two native-spaced trigger effect blocks.
        private const int DualSenseTriggerEffectLength = 11;
        private const int DualSenseCompatExtendedFeedbackLength = DualSenseBaseFeedbackLength + (DualSenseTriggerEffectLength * 2);
        private const int DualSenseNativeOutputReportLength = 48;
        private const int DualSenseNativeOutputReportOffset = DualSenseCompatExtendedFeedbackLength;
        private const int DualSenseBluetoothHapticsReportLength = 141;
        private const int DualSenseBluetoothHapticsReportOffset = DualSenseNativeOutputReportOffset + DualSenseNativeOutputReportLength;
        private const int DualSenseExtendedFeedbackLength = DualSenseBluetoothHapticsReportOffset + DualSenseBluetoothHapticsReportLength;
        private const int DualSenseCombinedBluetoothReportLength = 398;
        private const int DualSenseCombinedBluetoothReportOffset = DualSenseNativeOutputReportOffset + DualSenseNativeOutputReportLength;
        private const int DualSenseCombinedExtendedFeedbackLength = DualSenseCombinedBluetoothReportOffset + DualSenseCombinedBluetoothReportLength;
        private const int DualSenseV5SpeakerPcmLength =
            480 * sizeof(short) * 2;
        internal const int DualSenseAtomicFeedbackLength =
            DualSenseCombinedExtendedFeedbackLength;
        private const int DualSenseMicrophoneOpusFrameLength = 71;
        private const int DualSenseMicrophoneFramesPerPacket = 480;
        private const int DualSenseMicrophonePcmFrameLength = DualSenseMicrophoneFramesPerPacket * 2 * sizeof(short);
        private const int DualShock4VirtualMicrophoneFramesPerPacket = 160;
        private const int DualShock4VirtualMicrophonePcmFrameLength =
            DualShock4VirtualMicrophoneFramesPerPacket * sizeof(short);
        private const int DualShock4MicrophoneSourceSampleRate = 16000;
        private const int DualShock4MicrophoneSourceSamplesPerPacket =
            DualShock4MicrophoneSourceSampleRate / 100;
        private const int DualShock4MicrophoneDecodedFifoCapacity =
            DualShock4MicrophoneSourceSamplesPerPacket * 8;
        private const int DualShock4MicrophoneMaximumConcealedFrames = 4;
        private const int DualShock4MicrophoneCrossfadeSamples = 16;
        // A single DS4 HID report can carry several SBC frames. Keep enough
        // room for a complete burst plus a scheduler hiccup without making
        // stale microphone latency unbounded.
        private const int MaxPendingMicrophoneFrames = 16;
        private const byte ViiperStreamFrameInputState = 0x01;
        private const byte ViiperStreamFrameMicrophonePcm = 0x02;
        private const byte ViiperStreamFrameOutputState = 0x81;
        private const byte ViiperStreamFrameSpeakerPcm = 0x82;
        private const byte ViiperStreamFrameAtomicAudioHaptics = 0x83;
        private const byte ViiperStreamFrameRealtimeHaptics = 0x84;
        private const byte ViiperStreamFrameVersionV2 = 0x02;
        private const byte ViiperStreamFrameVersionV3 = 0x03;
        private const byte ViiperStreamFrameVersionV4 = 0x04;
        private const byte ViiperStreamFrameVersionV5 = 0x05;
        private const byte FeedbackSpeakerKindPcm = 0;
        private const byte FeedbackSpeakerKindAtomicAudioHaptics = 1;
        private const int AtomicAudioHapticsFeedbackLengthPrefix = 2;
        private const int MaxStreamRecoveryAttempts = 8;
        private const int InitialStreamRecoveryBackoffMilliseconds = 50;
        private const int MaximumStreamRecoveryBackoffMilliseconds = 1000;
        private const int MicrophoneDisableRetryMilliseconds = 250;
        // Virtual speaker formats have different proven buffering contracts.
        // DualSense's atomic 10 ms carriers feed a physical transport with its
        // own reservoir and must stay inside an 80 ms live window. DualShock 4
        // packets feed the historical SBC production lane, whose validated
        // 160 ms handoff reserve must not inherit the DualSense expiry policy.
        // A 4 KiB slot covers either virtual format without allocations.
        private const int FeedbackSpeakerSlotLength = 4096;
        internal const int DualSenseFeedbackSpeakerQueueCapacity = 8;
        internal const int DualSenseFeedbackSpeakerMaximumAgeMilliseconds = 80;
        internal const int DualShock4FeedbackSpeakerQueueCapacity = 16;
        internal const int DualShock4FeedbackSpeakerMaximumAgeMilliseconds = 0;
        // Native DualSense feedback arrives at roughly 150 Hz and is valid for
        // only 30 ms in the physical combined transport. Four ordered reports
        // preserve waveform continuity while preventing a stalled callback
        // from turning old game effects into almost a second of input lag.
        internal const int FeedbackOrderedControlQueueCapacity = 4;
        internal const int FeedbackOrderedControlMaximumAgeMilliseconds = 20;

        private readonly OutContType outputType;
        private readonly ViiperVirtualDeviceType viiperType;
        private readonly bool audioOnlySidecar;
        private readonly ViiperClient client;
        private readonly ViiperLiveValidationLease liveValidationLease;
        private readonly object liveValidationObservationLock = new object();
        private readonly object pendingPacketLock = new object();
        private readonly object microphoneQueueLock = new object();
        private readonly object microphoneProcessingLock = new object();
        private readonly object writerThreadLock = new object();
        private readonly object microphoneWriterThreadLock = new object();
        private readonly object streamRecoveryLock = new object();
        private readonly object feedbackThreadLock = new object();
        private readonly object feedbackDispatchThreadLock = new object();
        private readonly object virtualSpeakerSubscriberLock = new object();
        private readonly ReaderWriterLockSlim feedbackDispatchGenerationBarrier =
            new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly object physicalDualSenseIdentityLock = new object();
        private readonly object microphoneSourceLock = new object();
        private readonly object microphoneControlTransitionLock = new object();
        private readonly object legacyDualSenseRumbleLock = new object();
        private readonly AutoResetEvent writerSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent writerRateWaitStopSignal =
            new ManualResetEvent(false);
        private readonly AutoResetEvent microphoneWriterSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent feedbackSpeakerSignal =
            new AutoResetEvent(false);
        private readonly AutoResetEvent feedbackControlSignal =
            new AutoResetEvent(false);
        private readonly ManualResetEvent microphoneInterfaceStopSignal = new ManualResetEvent(false);
        private readonly Queue<PendingMicrophoneFrame> pendingMicrophoneFrames =
            new Queue<PendingMicrophoneFrame>(MaxPendingMicrophoneFrames);
        private readonly short[] microphoneMonoPcm = new short[DualSenseMicrophoneFramesPerPacket];
        private readonly byte[] microphoneStereoPcm = new byte[DualSenseMicrophonePcmFrameLength];
        private readonly byte[] dualShock4MicrophonePcm =
            new byte[DualShock4VirtualMicrophonePcmFrameLength];
        private readonly short[] dualShock4DecodedPcmFifo =
            new short[DualShock4MicrophoneDecodedFifoCapacity];
        private readonly short[] dualShock4SourcePcmPacket =
            new short[DualShock4MicrophoneSourceSamplesPerPacket];
        private readonly short[] dualShock4DecodedSbcPcm =
            new short[SbcFrame.MaxSamples];
        private readonly SbcFrame dualShock4DecodedSbcFrame =
            new SbcFrame();
        private readonly short[] dualShock4LastDecodedPcm =
            new short[SbcFrame.MaxSamples];
        private readonly short[] dualShock4ConcealmentPcm =
            new short[SbcFrame.MaxSamples];
        private readonly DualSenseMicrophoneProcessor microphoneProcessor = new DualSenseMicrophoneProcessor();
        private readonly ViiperMicrophoneTelemetry microphoneTelemetry =
            new ViiperMicrophoneTelemetry();
        private readonly MicrophoneDisableRetryTracker<DS4Device>
            microphoneDisableRetries =
                new MicrophoneDisableRetryTracker<DS4Device>();
        private readonly ViiperFeedbackDispatchBuffer feedbackDispatchBuffer;
        private ViiperDeviceStream deviceStream;
        private Thread feedbackThread;
        private Thread feedbackSpeakerDispatchThread;
        private Thread feedbackControlDispatchThread;
        private Thread stateWriterThread;
        private Thread microphoneWriterThread;
        private Thread microphoneInterfaceThread;
        private byte[] pendingStatePacket;
        private long pendingStatePacketQueuedTimestamp;
        private IOpusDecoder microphoneDecoder;
        private SbcDecoder microphoneSbcDecoder;
        private DS4Device microphoneSourceDevice;
        private DS4Device legacyDualSenseRumbleDevice;
        private byte legacyDualSenseLightFast;
        private byte legacyDualSenseHeavySlow;
        private bool legacyDualSenseRumbleKnown;
        private byte lastTriggerLabLeftRumble;
        private byte lastTriggerLabRightRumble;
        private int lastTriggerLabRumbleSignature;
        private bool triggerLabRumbleStateKnown;
        private bool lastTriggerLabLeftRumbleEnabled;
        private bool lastTriggerLabRightRumbleEnabled;
        private int dualShock4DecodedPcmFifoCount;
        private int dualShock4LastDecodedPcmCount;
        private ushort dualShock4LastMicrophoneSequence;
        private bool dualShock4MicrophoneSequenceKnown;
        private short dualShock4ResamplePreviousSample;
        private bool dualShock4ResamplePreviousSampleKnown;
        private volatile bool writerStopRequested;
        private volatile bool feedbackDispatchStopRequested = true;
        private bool activeStreamUsesFramedProtocol;
        private bool activeStreamSupportsMicrophone;
        private bool activeStreamSupportsDirectSpeaker;
        private bool activeStreamSupportsAtomicAudioHaptics;
        private bool activeStreamUsesAudioOnlyDescriptor;
        private byte activeStreamFrameVersion;
        private int microphoneVolume = 128;
        private int microphoneNoiseSuppression = (int)DualSenseMicrophoneNoiseSuppression.Balanced;
        private long lastMicrophoneCompressedRxTimestamp;
        private long lastMicrophoneProcessedTimestamp;
        private long lastMicrophoneSubmittedTimestamp;
        private long lastMicrophoneArmTimestamp;
        private long streamGeneration;
        private long feedbackDispatchGeneration;
        private long feedbackDispatchThreadGeneration;
        private long microphoneWorkerGeneration;
        private long stateWriterGeneration;
        private long stateWriterThreadGeneration;
        private int streamRecoveryAttempts;
        private long replacedPendingPacketCount;
        private long submittedPacketCount;
        private long writtenPacketCount;
        private long microphoneArmAttempts;
        private long microphoneArmFailures;
        private long microphoneCompressedFramesReceived;
        private long microphoneOpusFramesReceived;
        private long microphoneSbcFramesReceived;
        private long microphoneFramesDecoded;
        private long microphoneFramesProcessed;
        private long microphoneFramesSubmitted;
        private long microphoneFramesDropped;
        private long microphoneDecodeFailures;
        private long microphoneSequenceGaps;
        private long microphoneConcealedFrames;
        private long microphoneDuplicateFrames;
        private long microphoneOutOfOrderFrames;
        private long microphoneDiscontinuities;
        private long microphonePhysicalReceiveRecoveries;
        private long microphoneDecodeProcessRecoveries;
        private long microphoneVirtualSubmissionRecoveries;
        private long lastStateQueuedTimestamp;
        private long lastStateWrittenTimestamp;
        private long maximumStateQueueGapTicks;
        private long maximumStatePacketAgeTicks;
        private long maximumStateWriteDurationTicks;
        private long maximumStateWriteGapTicks;
        private long minimumStateWriteStartGapTicks = long.MaxValue;
        private long lastRateLimitedStateWriteStartedTimestamp;
        private int stateWriteRateHz;
        private long stateWriteMinimumIntervalTicks;
        private DateTime lastWriterHealthLogUtc = DateTime.MinValue;
        private DateTime lastMicrophoneHealthLogUtc = DateTime.MinValue;
        private int lastInputDeviceIndex = -1;
        private int submitFailureLogged;
        private int microphoneUnavailableLogged;
        private int microphoneNoiseSuppressionUnavailableLogged;
        private int microphoneProcessingFailureLogged;
        private int microphoneMuted;
        private int virtualMicrophoneInterfaceActive;
        private int virtualMicrophoneInterfaceStateKnown;
        private ViiperMicrophoneBufferSnapshot virtualMicrophoneBufferSnapshot =
            ViiperMicrophoneBufferSnapshot.Empty;
        private int lastMicrophoneRecoveryStage;
        private int edgePhysicalMismatchLogged;
        private int feedbackSpeakerCallbackFailureLogged;
        private int feedbackControlCallbackFailureLogged;
        private long lastFeedbackSpeakerDispatchTimestamp;
        private long maximumFeedbackSpeakerDispatchGapTicks;
        private long maximumFeedbackSpeakerCallbackTicks;
        private long feedbackSpeakerDelivered;
        private long feedbackSpeakerStale;
        private long feedbackSpeakerNoSubscriberDeferrals;
        private long feedbackSpeakerCallbackFailures;
        private long feedbackControlDelivered;
        private long feedbackControlStale;
        private long feedbackControlCallbackFailures;
        private int activeFeedbackLength;
        private string physicalDualSenseIdentityPath;
        private bool physicalDualSenseIdentityVerified;
        private readonly byte[] lastR2TriggerFeedback = new byte[DualSenseTriggerEffectLength];
        private readonly byte[] lastL2TriggerFeedback = new byte[DualSenseTriggerEffectLength];
        private byte[] liveValidationLastFeedback;
        private long liveValidationFeedbackFrames;
        private long liveValidationMicrophoneFramesSubmitted;
        private long liveValidationMicrophoneBytesSubmitted;
        private long liveValidationTransportInterruptions;
        private long liveValidationStreamRecoveriesCompleted;

        private enum MicrophoneCodec : byte
        {
            Opus,
            Sbc,
        }

        private readonly struct PendingMicrophoneFrame
        {
            public PendingMicrophoneFrame(MicrophoneCodec codec, byte[] data,
                ushort sequence = 0, bool hasSequence = false)
            {
                Codec = codec;
                Data = data;
                Sequence = sequence;
                HasSequence = hasSequence;
            }

            public MicrophoneCodec Codec { get; }
            public byte[] Data { get; }
            public ushort Sequence { get; }
            public bool HasSequence { get; }
        }

        public ViiperOutDevice(OutContType outputType,
            ViiperVirtualDeviceType viiperType, bool audioOnlySidecar = false)
            : this(outputType, viiperType, audioOnlySidecar, null, null)
        {
        }

        internal ViiperOutDevice(OutContType outputType,
            ViiperVirtualDeviceType viiperType, bool audioOnlySidecar,
            ViiperLiveValidationLease validationLease,
            ViiperNativeRuntimeMetadata validationMetadata)
        {
            if ((validationLease == null) != (validationMetadata == null))
            {
                throw new ArgumentException(
                    "A live-validation lease and its exact metadata must be supplied together.");
            }
            this.outputType = outputType;
            this.viiperType = viiperType;
            this.audioOnlySidecar = audioOnlySidecar;
            liveValidationLease = validationLease;
            feedbackDispatchBuffer = new ViiperFeedbackDispatchBuffer(
                // The buffer implementation requires one preallocated slot.
                // Non-audio devices never enqueue it; their public policy is
                // still zero so they cannot inherit a Sony audio contract.
                Math.Max(1, GetFeedbackSpeakerQueueCapacity(viiperType)),
                FeedbackSpeakerSlotLength,
                DualSenseCombinedExtendedFeedbackLength,
                IsDualSenseVirtualType(viiperType) ?
                    FeedbackOrderedControlQueueCapacity : 0,
                GetFeedbackSpeakerMaximumAgeMilliseconds(viiperType),
                IsDualSenseVirtualType(viiperType) ?
                    FeedbackOrderedControlMaximumAgeMilliseconds : 0);
            client = validationLease == null ?
                new ViiperClient(DefaultHost, DefaultPort) :
                new ViiperClient(DefaultHost, DefaultPort,
                    ViiperTransportMode.NativeUde, validationMetadata);
        }

        internal ViiperLiveValidationSnapshot GetLiveValidationSnapshot(
            ViiperLiveValidationLease lease)
        {
            DemandLiveValidationLease(lease);
            byte[] feedback;
            lock (liveValidationObservationLock)
            {
                feedback = liveValidationLastFeedback == null ? null :
                    (byte[])liveValidationLastFeedback.Clone();
            }

            return new ViiperLiveValidationSnapshot
            {
                HandlerName = GetLiveValidationHandlerName(),
                StreamProtocol = activeStreamFrameVersion switch
                {
                    ViiperStreamFrameVersionV3 => "framed-v3",
                    ViiperStreamFrameVersionV5 => "framed-v5",
                    _ => "inactive",
                },
                StreamFrameVersion = activeStreamFrameVersion,
                Connected = connected,
                SupportsMicrophone = activeStreamSupportsMicrophone,
                SupportsDirectSpeaker = activeStreamSupportsDirectSpeaker,
                SupportsAtomicAudioHaptics =
                    activeStreamSupportsAtomicAudioHaptics,
                BackendIdentity = client.NativeBackendIdentity,
                DeviceIdentity = VirtualDeviceIdentity,
                StatePacketsSubmitted = Interlocked.Read(
                    ref submittedPacketCount),
                StatePacketsWritten = Interlocked.Read(ref writtenPacketCount),
                StatePacketsCoalesced = Interlocked.Read(
                    ref replacedPendingPacketCount),
                StreamRecoveryAttempts = Volatile.Read(
                    ref streamRecoveryAttempts),
                FeedbackFramesObserved = Interlocked.Read(
                    ref liveValidationFeedbackFrames),
                LastFeedbackPayload = feedback,
                ValidationMicrophoneFramesSubmitted = Interlocked.Read(
                    ref liveValidationMicrophoneFramesSubmitted),
                ValidationMicrophoneBytesSubmitted = Interlocked.Read(
                    ref liveValidationMicrophoneBytesSubmitted),
                ValidationTransportInterruptions = Interlocked.Read(
                    ref liveValidationTransportInterruptions),
                ValidationStreamRecoveriesCompleted = Interlocked.Read(
                    ref liveValidationStreamRecoveriesCompleted),
                SpeakerFramesEnqueued = feedbackDispatchBuffer.SpeakerEnqueued,
                SpeakerFramesDequeued = feedbackDispatchBuffer.SpeakerDequeued,
                SpeakerFramesDropped = feedbackDispatchBuffer.SpeakerDropped,
                SpeakerFramesExpired = feedbackDispatchBuffer.SpeakerExpired,
                SpeakerFramesDelivered = Interlocked.Read(
                    ref feedbackSpeakerDelivered),
                SpeakerFramesStale = Interlocked.Read(
                    ref feedbackSpeakerStale),
                SpeakerNoSubscriberDeferrals = Interlocked.Read(
                    ref feedbackSpeakerNoSubscriberDeferrals),
                SpeakerCallbackFailures = Interlocked.Read(
                    ref feedbackSpeakerCallbackFailures),
                ControlFramesEnqueued = feedbackDispatchBuffer.ControlEnqueued,
                ControlFramesDequeued = feedbackDispatchBuffer.ControlDequeued,
                ControlFramesDropped = feedbackDispatchBuffer.ControlDropped,
                OrderedControlFramesEnqueued =
                    feedbackDispatchBuffer.OrderedControlEnqueued,
                OrderedControlFramesDequeued =
                    feedbackDispatchBuffer.OrderedControlDequeued,
                OrderedControlFramesDropped =
                    feedbackDispatchBuffer.OrderedControlDropped,
                OrderedControlFramesExpired =
                    feedbackDispatchBuffer.OrderedControlExpired,
                ControlFramesDelivered = Interlocked.Read(
                    ref feedbackControlDelivered),
                ControlFramesStale = Interlocked.Read(
                    ref feedbackControlStale),
                ControlCallbackFailures = Interlocked.Read(
                    ref feedbackControlCallbackFailures),
            };
        }

        internal void SubmitLiveValidationMicrophonePcm(
            ViiperLiveValidationLease lease, byte[] pcm)
        {
            DemandLiveValidationLease(lease);
            int expectedLength = viiperType ==
                ViiperVirtualDeviceType.DualShock4 ?
                DualShock4VirtualMicrophonePcmFrameLength :
                DualSenseMicrophonePcmFrameLength;
            if (!connected || !activeStreamSupportsMicrophone ||
                !activeStreamUsesFramedProtocol || pcm == null ||
                pcm.Length != expectedLength || pcm.All(value => value == 0))
            {
                throw new ViiperIdentityException(
                    $"Live validation requires one non-silent {expectedLength}-byte microphone PCM frame on an active framed PlayStation stream.");
            }

            ViiperDeviceStream stream = Volatile.Read(ref deviceStream) ??
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            stream.WriteFrame(activeStreamFrameVersion,
                ViiperStreamFrameMicrophonePcm, pcm);
            Interlocked.Increment(
                ref liveValidationMicrophoneFramesSubmitted);
            Interlocked.Add(ref liveValidationMicrophoneBytesSubmitted,
                pcm.Length);
        }

        internal void InterruptLiveValidationTransport(
            ViiperLiveValidationLease lease)
        {
            DemandLiveValidationLease(lease);
            ViiperDeviceStream stream = Volatile.Read(ref deviceStream) ??
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            Interlocked.Increment(ref liveValidationTransportInterruptions);
            stream.CloseTransport();
        }

        private void DemandLiveValidationLease(
            ViiperLiveValidationLease lease)
        {
            if (liveValidationLease == null ||
                !ReferenceEquals(liveValidationLease, lease))
            {
                throw new ViiperIdentityException(
                    "The VIIPER live-validation hook is unavailable without the exact opt-in lease.");
            }
        }

        private string GetLiveValidationHandlerName()
        {
            return viiperType switch
            {
                ViiperVirtualDeviceType.DualShock4 =>
                    audioOnlySidecar ? "dualshock4audioonlyduplexv3" :
                    "dualshock4audioduplexv3",
                ViiperVirtualDeviceType.DualSense =>
                    audioOnlySidecar ? "dualsenseaudioonlyduplexv5" :
                    "dualsensecombinedaudioduplexv5",
                ViiperVirtualDeviceType.DualSenseEdge =>
                    "dualsenseedgecombinedaudioduplexv5",
                _ => ViiperStatePacketBuilder.GetViiperDeviceName(viiperType),
            };
        }

        private void ObserveLiveValidationFeedback(byte[] feedback,
            int feedbackLength)
        {
            if (liveValidationLease == null || feedback == null ||
                feedbackLength <= 0 || feedbackLength > feedback.Length ||
                feedbackLength > DualSenseCombinedExtendedFeedbackLength)
            {
                return;
            }

            byte[] copy = new byte[feedbackLength];
            Buffer.BlockCopy(feedback, 0, copy, 0, feedbackLength);
            lock (liveValidationObservationLock)
            {
                liveValidationLastFeedback = copy;
            }
            Interlocked.Increment(ref liveValidationFeedbackFrames);
        }

        internal static int GetFeedbackSpeakerQueueCapacity(
            ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.DualShock4 =>
                    DualShock4FeedbackSpeakerQueueCapacity,
                ViiperVirtualDeviceType.DualSense or
                    ViiperVirtualDeviceType.DualSenseEdge =>
                    DualSenseFeedbackSpeakerQueueCapacity,
                _ => 0,
            };
        }

        internal static int GetFeedbackSpeakerMaximumAgeMilliseconds(
            ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.DualShock4 =>
                    DualShock4FeedbackSpeakerMaximumAgeMilliseconds,
                ViiperVirtualDeviceType.DualSense or
                    ViiperVirtualDeviceType.DualSenseEdge =>
                    DualSenseFeedbackSpeakerMaximumAgeMilliseconds,
                _ => 0,
            };
        }

        internal static int GetVirtualSpeakerPcmSampleRate(
            ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.DualShock4 =>
                    DualShock4BluetoothAudioProtocol.SpeakerSampleRate,
                ViiperVirtualDeviceType.DualSense or
                    ViiperVirtualDeviceType.DualSenseEdge => 48000,
                _ => 0,
            };
        }

        internal static bool CanDispatchVirtualSpeaker(
            bool streamUsesAtomicFrames, bool hasPcmSubscriber,
            bool hasAtomicSubscriber)
        {
            return streamUsesAtomicFrames ?
                hasAtomicSubscriber || hasPcmSubscriber : hasPcmSubscriber;
        }

        internal static bool TryGetAtomicAudioHapticsLayout(byte[] payload,
            int length, out int feedbackOffset, out int feedbackLength,
            out int speakerPcmOffset, out int speakerPcmLength)
        {
            feedbackOffset = AtomicAudioHapticsFeedbackLengthPrefix;
            feedbackLength = 0;
            speakerPcmOffset = 0;
            speakerPcmLength = 0;
            if (payload == null || length > payload.Length || length <=
                AtomicAudioHapticsFeedbackLengthPrefix)
            {
                return false;
            }

            feedbackLength = BinaryPrimitives.ReadUInt16LittleEndian(
                payload.AsSpan(0,
                    AtomicAudioHapticsFeedbackLengthPrefix));
            speakerPcmOffset = feedbackOffset + feedbackLength;
            speakerPcmLength = length - speakerPcmOffset;
            return feedbackLength == DualSenseCombinedExtendedFeedbackLength &&
                speakerPcmOffset <= length && speakerPcmLength > 0 &&
                (speakerPcmLength & (sizeof(short) * 2 - 1)) == 0;
        }

        internal static bool IsValidV5RealtimeHapticsFrame(byte version,
            byte frameType, int payloadLength)
        {
            return version == ViiperStreamFrameVersionV5 &&
                frameType == ViiperStreamFrameRealtimeHaptics &&
                payloadLength == DualSenseCombinedExtendedFeedbackLength;
        }

        private Action<ViiperOutDevice, byte[], int>
            virtualSpeakerPcmReceived;
        private ViiperAtomicAudioHapticsHandler
            virtualAtomicAudioHapticsReceived;

        internal event Action<ViiperOutDevice, byte[], int>
            VirtualSpeakerPcmReceived
        {
            add
            {
                lock (virtualSpeakerSubscriberLock)
                {
                    virtualSpeakerPcmReceived += value;
                }

                feedbackSpeakerSignal.Set();
            }
            remove
            {
                lock (virtualSpeakerSubscriberLock)
                {
                    virtualSpeakerPcmReceived -= value;
                }
            }
        }

        private Action<ViiperOutDevice, byte[], int>
            GetVirtualSpeakerPcmSubscriber()
        {
            lock (virtualSpeakerSubscriberLock)
            {
                return virtualSpeakerPcmReceived;
            }
        }

        internal event ViiperAtomicAudioHapticsHandler
            VirtualAtomicAudioHapticsReceived
        {
            add
            {
                lock (virtualSpeakerSubscriberLock)
                {
                    virtualAtomicAudioHapticsReceived += value;
                }

                feedbackSpeakerSignal.Set();
            }
            remove
            {
                lock (virtualSpeakerSubscriberLock)
                {
                    virtualAtomicAudioHapticsReceived -= value;
                }
            }
        }

        private ViiperAtomicAudioHapticsHandler
            GetVirtualAtomicAudioHapticsSubscriber()
        {
            lock (virtualSpeakerSubscriberLock)
            {
                return virtualAtomicAudioHapticsReceived;
            }
        }

        internal bool SupportsDirectSpeakerPcm =>
            connected && activeStreamSupportsDirectSpeaker;

        internal bool IsRuntimeConnected =>
            connected && Volatile.Read(ref deviceStream) != null;

        internal ViiperVirtualDeviceIdentity VirtualDeviceIdentity =>
            Volatile.Read(ref deviceStream)?.VirtualDeviceIdentity;

        internal bool SupportsAtomicAudioHaptics =>
            connected && activeStreamSupportsAtomicAudioHaptics;

        internal void ApplyAtomicAudioHapticsFeedback(byte[] feedback,
            int feedbackLength, int expectedDeviceIndex)
        {
            ApplyFeedback(feedback, feedbackLength, expectedDeviceIndex);
        }

        internal bool CanProvideDirectSpeakerPcm =>
            GetVirtualSpeakerPcmSampleRate(viiperType) > 0;

        internal int DirectSpeakerPcmSampleRate =>
            SupportsDirectSpeakerPcm ?
                GetVirtualSpeakerPcmSampleRate(viiperType) : 0;

        internal int DirectSpeakerUsbipPort
        {
            get
            {
                ViiperDeviceStream stream = Volatile.Read(ref deviceStream);
                return stream?.TransportMode == ViiperTransportMode.Usbip ?
                    stream.UsbipPort : -1;
            }
        }

        internal bool SupportsActiveVirtualMicrophone =>
            connected && activeStreamSupportsMicrophone;

        internal OutContType OutputType => outputType;

        internal bool IsAudioOnlySidecar => audioOnlySidecar;

        internal bool UsesAudioOnlyUsbDescriptor =>
            connected && activeStreamUsesAudioOnlyDescriptor;

        internal bool IsVirtualMicrophoneInterfaceActive =>
            Volatile.Read(ref virtualMicrophoneInterfaceActive) == 1;

        internal ViiperMicrophoneBufferSnapshot VirtualMicrophoneBufferSnapshot =>
            Volatile.Read(ref virtualMicrophoneBufferSnapshot);

        internal void BindPhysicalController(int deviceIndex)
        {
            Volatile.Write(ref lastInputDeviceIndex, deviceIndex);
            if (connected)
            {
                ResetState();
            }
        }

        public override void Connect()
        {
            Disconnect();

            if (client.TransportMode == ViiperTransportMode.NativeUde)
            {
                client.ValidateNativeBackend();
            }

            deviceStream = CreateDeviceStreamWithServerFallback();
            Interlocked.Increment(ref streamGeneration);
            Volatile.Write(ref submitFailureLogged, 0);
            Volatile.Write(ref microphoneUnavailableLogged, 0);
            Volatile.Write(ref microphoneNoiseSuppressionUnavailableLogged, 0);
            Volatile.Write(ref microphoneProcessingFailureLogged, 0);
            Volatile.Write(ref microphoneMuted, 0);
            Volatile.Write(ref lastInputDeviceIndex, -1);
            Interlocked.Exchange(ref streamRecoveryAttempts, 0);
            Interlocked.Exchange(ref replacedPendingPacketCount, 0);
            Interlocked.Exchange(ref submittedPacketCount, 0);
            Interlocked.Exchange(ref writtenPacketCount, 0);
            ResetMicrophoneLiveness();
            ResetTriggerLabRumbleState();
            Interlocked.Exchange(ref lastMicrophoneArmTimestamp, 0);
            Interlocked.Exchange(ref microphoneArmAttempts, 0);
            Interlocked.Exchange(ref microphoneArmFailures, 0);
            Interlocked.Exchange(ref microphoneCompressedFramesReceived, 0);
            Interlocked.Exchange(ref microphoneOpusFramesReceived, 0);
            Interlocked.Exchange(ref microphoneSbcFramesReceived, 0);
            Interlocked.Exchange(ref microphoneFramesDecoded, 0);
            Interlocked.Exchange(ref microphoneFramesProcessed, 0);
            Interlocked.Exchange(ref microphoneFramesSubmitted, 0);
            Interlocked.Exchange(ref microphoneFramesDropped, 0);
            Interlocked.Exchange(ref microphoneDecodeFailures, 0);
            Interlocked.Exchange(ref microphoneSequenceGaps, 0);
            Interlocked.Exchange(ref microphoneConcealedFrames, 0);
            Interlocked.Exchange(ref microphoneDuplicateFrames, 0);
            Interlocked.Exchange(ref microphoneOutOfOrderFrames, 0);
            Interlocked.Exchange(ref microphoneDiscontinuities, 0);
            Interlocked.Exchange(ref microphonePhysicalReceiveRecoveries, 0);
            Interlocked.Exchange(ref microphoneDecodeProcessRecoveries, 0);
            Interlocked.Exchange(ref microphoneVirtualSubmissionRecoveries, 0);
            microphoneTelemetry.Reset();
            Volatile.Write(ref lastMicrophoneRecoveryStage,
                (int)MicrophonePipelineHealthStage.None);
            Interlocked.Exchange(ref lastStateQueuedTimestamp, 0);
            Interlocked.Exchange(ref lastStateWrittenTimestamp, 0);
            Interlocked.Exchange(ref maximumStateQueueGapTicks, 0);
            Interlocked.Exchange(ref maximumStatePacketAgeTicks, 0);
            Interlocked.Exchange(ref maximumStateWriteDurationTicks, 0);
            Interlocked.Exchange(ref maximumStateWriteGapTicks, 0);
            Interlocked.Exchange(ref minimumStateWriteStartGapTicks,
                long.MaxValue);
            Interlocked.Exchange(ref lastRateLimitedStateWriteStartedTimestamp,
                0);
            stateWriteRateHz = ViiperStateWriteRateSettings.Parse(
                Environment.GetEnvironmentVariable(
                    ViiperStateWriteRateSettings.EnvironmentVariableName),
                ViiperStateWriteRateSettings.GetDefaultRateHz(viiperType));
            stateWriteMinimumIntervalTicks =
                ViiperStateWriteRateSettings.GetMinimumIntervalTicks(
                    stateWriteRateHz);
            Volatile.Write(ref edgePhysicalMismatchLogged, 0);
            Volatile.Write(ref feedbackSpeakerCallbackFailureLogged, 0);
            Volatile.Write(ref feedbackControlCallbackFailureLogged, 0);
            Interlocked.Exchange(ref lastFeedbackSpeakerDispatchTimestamp, 0);
            Interlocked.Exchange(ref maximumFeedbackSpeakerDispatchGapTicks, 0);
            Interlocked.Exchange(ref maximumFeedbackSpeakerCallbackTicks, 0);
            Interlocked.Exchange(ref feedbackSpeakerDelivered, 0);
            Interlocked.Exchange(ref feedbackSpeakerStale, 0);
            Interlocked.Exchange(ref feedbackSpeakerNoSubscriberDeferrals, 0);
            Interlocked.Exchange(ref feedbackSpeakerCallbackFailures, 0);
            Interlocked.Exchange(ref feedbackControlDelivered, 0);
            Interlocked.Exchange(ref feedbackControlStale, 0);
            Interlocked.Exchange(ref feedbackControlCallbackFailures, 0);
            Interlocked.Exchange(ref liveValidationFeedbackFrames, 0);
            Interlocked.Exchange(
                ref liveValidationMicrophoneFramesSubmitted, 0);
            Interlocked.Exchange(
                ref liveValidationMicrophoneBytesSubmitted, 0);
            Interlocked.Exchange(
                ref liveValidationTransportInterruptions, 0);
            Interlocked.Exchange(
                ref liveValidationStreamRecoveriesCompleted, 0);
            lock (liveValidationObservationLock)
            {
                liveValidationLastFeedback = null;
            }
            feedbackDispatchBuffer.Reset();
            lock (physicalDualSenseIdentityLock)
            {
                physicalDualSenseIdentityPath = null;
                physicalDualSenseIdentityVerified = false;
            }
            lastWriterHealthLogUtc = DateTime.MinValue;
            lastMicrophoneHealthLogUtc = DateTime.MinValue;
            Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);
            Volatile.Write(ref virtualMicrophoneBufferSnapshot,
                ViiperMicrophoneBufferSnapshot.Empty);
            microphoneInterfaceStopSignal.Reset();
            writerRateWaitStopSignal.Reset();
            long writerGeneration = Interlocked.Increment(
                ref stateWriterGeneration);
            writerStopRequested = false;
            feedbackDispatchStopRequested = false;
            connected = true;
            Interlocked.Increment(ref feedbackDispatchGeneration);
            long workerGeneration = Interlocked.Read(
                ref microphoneWorkerGeneration);
            StartStateWriter(writerGeneration);
            StartMicrophoneWriter(workerGeneration);
            StartMicrophoneInterfaceMonitor(workerGeneration);
            StartFeedbackDispatchWorkers();
            ResetState();
            StartFeedbackReader();
            if (stateWriteRateHz > 0)
            {
                AppLogger.LogToGui(
                    $"VIIPER {viiperType} virtual input presentation is capped at {stateWriteRateHz} Hz with latest-state coalescing.",
                    false);
            }
        }

        private ViiperDeviceStream CreateDeviceStream()
        {
            activeStreamUsesFramedProtocol = false;
            activeStreamSupportsMicrophone = false;
            activeStreamSupportsDirectSpeaker = false;
            activeStreamSupportsAtomicAudioHaptics = false;
            activeStreamUsesAudioOnlyDescriptor = false;
            activeStreamFrameVersion = 0;
            Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);

            if (viiperType == ViiperVirtualDeviceType.DualSense)
            {
                ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(
                    audioOnlySidecar ? "dualsenseaudioonlyduplexv5" :
                    "dualsensecombinedaudioduplexv5");
                ConfigureDualSenseV5Stream(audioOnlySidecar);
                return stream;
            }

            if (viiperType == ViiperVirtualDeviceType.DualSenseEdge)
            {
                if (audioOnlySidecar)
                {
                    throw new ViiperIdentityException(
                        "The authoritative VIIPER contract does not expose a DualSense Edge audio-only V5 handler.");
                }
                ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(
                    "dualsenseedgecombinedaudioduplexv5");
                ConfigureDualSenseV5Stream(audioOnly: false);
                return stream;
            }

            if (viiperType == ViiperVirtualDeviceType.DualShock4)
            {
                ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(
                    audioOnlySidecar ? "dualshock4audioonlyduplexv3" :
                    "dualshock4audioduplexv3", 0x05C4);
                activeFeedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(
                    viiperType);
                activeStreamUsesFramedProtocol = true;
                activeStreamSupportsMicrophone = true;
                activeStreamSupportsDirectSpeaker = true;
                activeStreamUsesAudioOnlyDescriptor = audioOnlySidecar;
                activeStreamFrameVersion = ViiperStreamFrameVersionV3;
                return stream;
            }

            activeFeedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(viiperType);
            return client.CreateDeviceAndOpenStream(viiperType);
        }

        private void ConfigureDualSenseV5Stream(bool audioOnly)
        {
            activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
            activeStreamUsesFramedProtocol = true;
            activeStreamSupportsMicrophone = true;
            activeStreamSupportsDirectSpeaker = true;
            activeStreamSupportsAtomicAudioHaptics = true;
            activeStreamUsesAudioOnlyDescriptor = audioOnly;
            activeStreamFrameVersion = ViiperStreamFrameVersionV5;
        }

        private ViiperDeviceStream CreateDeviceStreamWithServerFallback()
        {
            try
            {
                return CreateDeviceStream();
            }
            catch (IOException first)
            {
                AppLogger.LogToGui(
                    $"VIIPER {viiperType} stream open failed once; retrying the selected {client.TransportMode} transport: {first.Message}",
                    false);
                Thread.Sleep(250);
                return CreateDeviceStream();
            }
        }

        public override void Disconnect()
        {
            Interlocked.Increment(ref microphoneWorkerGeneration);
            Interlocked.Increment(ref stateWriterGeneration);
            Interlocked.Increment(ref feedbackDispatchGeneration);
            connected = false;
            lock (microphoneControlTransitionLock)
            {
                // A worker generation is an ownership boundary. Never carry a
                // failed disable into a replacement VIIPER device where the
                // same physical controller may already have been re-enabled.
                microphoneDisableRetries.Clear();
            }
            writerStopRequested = true;
            feedbackDispatchStopRequested = true;
            writerRateWaitStopSignal.Set();
            writerSignal.Set();
            microphoneWriterSignal.Set();
            feedbackSpeakerSignal.Set();
            feedbackControlSignal.Set();
            WaitForFeedbackDispatchCallbacks();
            // A real output-device disconnect must not inherit the interface
            // monitor's debounce period. The generation change prevents the
            // old monitor from reattaching after this synchronous detach.
            DetachBluetoothMicrophoneSource();
            ResetLegacyDualSenseRumbleDeduplication();
            ResetTriggerLabRumbleState();
            StopMicrophoneInterfaceMonitor();
            lock (pendingPacketLock)
            {
                pendingStatePacket = null;
                pendingStatePacketQueuedTimestamp = 0;
            }
            lock (microphoneQueueLock)
            {
                pendingMicrophoneFrames.Clear();
            }

            lock (streamRecoveryLock)
            {
                ViiperDeviceStream stream = Interlocked.Exchange(ref deviceStream, null);
                Interlocked.Increment(ref streamGeneration);
                stream?.Dispose();
            }

            Thread writerThread;
            lock (writerThreadLock)
            {
                writerThread = stateWriterThread;
            }
            if (writerThread != null && writerThread.IsAlive)
            {
                if (Thread.CurrentThread.ManagedThreadId != writerThread.ManagedThreadId)
                {
                    writerThread.Join(500);
                }
            }

            lock (writerThreadLock)
            {
                if (ReferenceEquals(stateWriterThread, writerThread) &&
                    (writerThread == null || !writerThread.IsAlive))
                {
                    stateWriterThread = null;
                    stateWriterThreadGeneration = 0;
                }
            }
            if (microphoneWriterThread != null && microphoneWriterThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != microphoneWriterThread.ManagedThreadId)
            {
                microphoneWriterThread.Join(500);
            }

            microphoneWriterThread = null;
            StopFeedbackReader();
            StopFeedbackDispatchWorkers();
            feedbackDispatchBuffer.ClearPending();
        }

        private void StopFeedbackReader()
        {
            Thread thread;
            lock (feedbackThreadLock)
            {
                thread = feedbackThread;
                feedbackThread = null;
            }

            if (thread != null && thread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != thread.ManagedThreadId)
            {
                thread.Join(500);
            }
        }

        private void StartMicrophoneInterfaceMonitor(long workerGeneration)
        {
            if (!activeStreamSupportsMicrophone ||
                microphoneInterfaceThread != null && microphoneInterfaceThread.IsAlive)
            {
                return;
            }

            microphoneInterfaceThread = new Thread(() =>
                MicrophoneInterfaceMonitorLoop(workerGeneration))
            {
                IsBackground = true,
                Name = $"VIIPER {viiperType} microphone interface",
            };
            microphoneInterfaceThread.Start();
        }

        private void StopMicrophoneInterfaceMonitor()
        {
            microphoneInterfaceStopSignal.Set();
            if (microphoneInterfaceThread != null && microphoneInterfaceThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != microphoneInterfaceThread.ManagedThreadId)
            {
                microphoneInterfaceThread.Join(500);
            }

            microphoneInterfaceThread = null;
            Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);
            Volatile.Write(ref virtualMicrophoneBufferSnapshot,
                ViiperMicrophoneBufferSnapshot.Empty);
        }

        private void MicrophoneInterfaceMonitorLoop(long workerGeneration)
        {
            var activity = new MicrophoneInterfaceActivityTracker();
            DateTime lastFailureLogUtc = DateTime.MinValue;

            while (connected && workerGeneration == Interlocked.Read(
                    ref microphoneWorkerGeneration) &&
                !microphoneInterfaceStopSignal.WaitOne(0))
            {
                ViiperDeviceStream stream = deviceStream;
                try
                {
                    if (stream == null)
                    {
                        // Stream recovery exchanges the stream through null.
                        // That is not an explicit observation that Windows
                        // closed the capture interface.
                        activity.RecordQueryFailure();
                    }
                    else
                    {
                        ViiperMicrophoneInterfaceStatus status =
                            client.GetMicrophoneInterfaceStatus(stream.BusId,
                                stream.DevId);
                        if (workerGeneration != Interlocked.Read(
                            ref microphoneWorkerGeneration))
                        {
                            return;
                        }

                        Volatile.Write(ref virtualMicrophoneBufferSnapshot,
                            status.Buffer);

                        bool stateChanged = activity.RecordObservation(
                            status.IsActive, Stopwatch.GetTimestamp());
                        if (activity.StateKnown)
                        {
                            Volatile.Write(ref virtualMicrophoneInterfaceActive,
                                activity.IsActive ? 1 : 0);
                            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 1);
                        }

                        if (Global.VerboseStartupLogging && stateChanged)
                        {
                            AppLogger.LogToGui(
                                $"VIIPER {viiperType} microphone capture interface active={activity.IsActive}.",
                                false);
                        }
                    }

                    if (workerGeneration != Interlocked.Read(
                        ref microphoneWorkerGeneration))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException ||
                    ex is SocketException || ex is JsonException)
                {
                    if (workerGeneration != Interlocked.Read(
                        ref microphoneWorkerGeneration))
                    {
                        return;
                    }
                    // A failed status request provides no evidence that the
                    // Windows capture handle closed. Preserve the published
                    // state, and require a fresh consecutive inactive run
                    // before teardown after communication recovers.
                    activity.RecordQueryFailure();

                    if (Global.VerboseStartupLogging &&
                        DateTime.UtcNow - lastFailureLogUtc >= TimeSpan.FromSeconds(5))
                    {
                        lastFailureLogUtc = DateTime.UtcNow;
                        AppLogger.LogToGui(
                            $"VIIPER {viiperType} microphone interface query failed; preserving the last known state: {ex.Message}",
                            true);
                    }
                }

                if (workerGeneration != Interlocked.Read(
                    ref microphoneWorkerGeneration))
                {
                    return;
                }
                UpdateBluetoothMicrophoneSource(
                    Volatile.Read(ref lastInputDeviceIndex), workerGeneration);
                MaintainPendingBluetoothMicrophoneDisables(workerGeneration);

                if (microphoneInterfaceStopSignal.WaitOne(125))
                {
                    break;
                }
            }
        }

        public override void ConvertandSendReport(DS4State state, int device)
        {
            Volatile.Write(ref lastInputDeviceIndex, device);
            if (!connected)
            {
                return;
            }

            try
            {
                QueueStatePacket(ViiperStatePacketBuilder.Build(viiperType, state, device));
            }
            catch (IOException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (SocketException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (ObjectDisposedException ex)
            {
                LogSubmitFailure(ex.Message);
            }
        }

        public override void ResetState(bool submit = true)
        {
            if (!submit || !connected)
            {
                return;
            }

            try
            {
                QueueStatePacket(ViiperStatePacketBuilder.BuildNeutral(viiperType));
            }
            catch (IOException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (SocketException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (ObjectDisposedException ex)
            {
                LogSubmitFailure(ex.Message);
            }
        }

        public override string GetDeviceType() => outputType.ToString();

        public override void RemoveFeedbacks()
        {
        }

        public override void RemoveFeedback(int inIdx)
        {
            _ = inIdx;
        }

        public static bool IsViiperType(OutContType type)
        {
            return type == OutContType.ViiperX360 ||
                type == OutContType.ViiperDS4 ||
                type == OutContType.ViiperDualSense ||
                type == OutContType.ViiperDualSenseEdge ||
                type == OutContType.ViiperSwitch2Pro;
        }

        public static bool SupportsVirtualMicrophone(OutContType type)
        {
            return ControllerMicrophoneRoutePolicy
                .SupportsVirtualMicrophoneOutput(type);
        }

        private void QueueStatePacket(byte[] data)
        {
            long queuedAt = Stopwatch.GetTimestamp();
            long previousQueuedAt = Interlocked.Exchange(ref lastStateQueuedTimestamp, queuedAt);
            if (previousQueuedAt > 0)
            {
                RecordMaximum(ref maximumStateQueueGapTicks, queuedAt - previousQueuedAt);
            }

            lock (pendingPacketLock)
            {
                if (pendingStatePacket != null)
                {
                    Interlocked.Increment(ref replacedPendingPacketCount);
                }

                pendingStatePacket = data;
                pendingStatePacketQueuedTimestamp = queuedAt;
            }

            Interlocked.Increment(ref submittedPacketCount);
            EnsureStateWriterAlive();
            writerSignal.Set();
        }

        private void EnsureStateWriterAlive()
        {
            if (!connected || writerStopRequested)
            {
                return;
            }

            long writerGeneration = Interlocked.Read(
                ref stateWriterGeneration);
            if (stateWriterThread == null || !stateWriterThread.IsAlive ||
                Interlocked.Read(ref stateWriterThreadGeneration) !=
                    writerGeneration)
            {
                StartStateWriter(writerGeneration);
            }
        }

        private void StartFeedbackDispatchWorkers()
        {
            if (!activeStreamSupportsDirectSpeaker || !connected ||
                feedbackDispatchStopRequested)
            {
                return;
            }

            long generation = Interlocked.Read(ref feedbackDispatchGeneration);
            lock (feedbackDispatchThreadLock)
            {
                bool newGeneration = feedbackDispatchThreadGeneration !=
                    generation;
                if (newGeneration)
                {
                    feedbackDispatchThreadGeneration = generation;
                }

                if (newGeneration || feedbackSpeakerDispatchThread == null ||
                    !feedbackSpeakerDispatchThread.IsAlive)
                {
                    Thread thread = new Thread(() =>
                        FeedbackSpeakerDispatchLoop(generation))
                    {
                        IsBackground = true,
                        Name = $"VIIPER {viiperType} speaker dispatch",
                        Priority = ThreadPriority.Highest,
                    };
                    feedbackSpeakerDispatchThread = thread;
                    thread.Start();
                }

                if (newGeneration || feedbackControlDispatchThread == null ||
                    !feedbackControlDispatchThread.IsAlive)
                {
                    Thread thread = new Thread(() =>
                        FeedbackControlDispatchLoop(generation))
                    {
                        IsBackground = true,
                        Name = $"VIIPER {viiperType} control dispatch",
                        Priority = ThreadPriority.Highest,
                    };
                    feedbackControlDispatchThread = thread;
                    thread.Start();
                }
            }
        }

        private void StopFeedbackDispatchWorkers()
        {
            Thread speakerThread;
            Thread controlThread;
            lock (feedbackDispatchThreadLock)
            {
                speakerThread = feedbackSpeakerDispatchThread;
                controlThread = feedbackControlDispatchThread;
            }

            feedbackSpeakerSignal.Set();
            feedbackControlSignal.Set();
            JoinFeedbackDispatchThread(speakerThread);
            JoinFeedbackDispatchThread(controlThread);

            lock (feedbackDispatchThreadLock)
            {
                if (ReferenceEquals(feedbackSpeakerDispatchThread,
                    speakerThread) &&
                    (speakerThread == null || !speakerThread.IsAlive))
                {
                    feedbackSpeakerDispatchThread = null;
                }

                if (ReferenceEquals(feedbackControlDispatchThread,
                    controlThread) &&
                    (controlThread == null || !controlThread.IsAlive))
                {
                    feedbackControlDispatchThread = null;
                }
            }
        }

        private static void JoinFeedbackDispatchThread(Thread thread)
        {
            if (thread != null && thread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != thread.ManagedThreadId)
            {
                thread.Join(500);
            }
        }

        private void WaitForFeedbackDispatchCallbacks()
        {
            // A dispatch callback cannot wait for its own read lease. No
            // current callback invokes Disconnect, but keep this guard so a
            // future subscriber cannot create a self-deadlock.
            if (ReferenceEquals(Thread.CurrentThread,
                    feedbackSpeakerDispatchThread) ||
                ReferenceEquals(Thread.CurrentThread,
                    feedbackControlDispatchThread))
            {
                return;
            }

            feedbackDispatchGenerationBarrier.EnterWriteLock();
            feedbackDispatchGenerationBarrier.ExitWriteLock();
        }

        private bool IsFeedbackDispatchGenerationActive(long generation)
        {
            return connected && !feedbackDispatchStopRequested &&
                generation == Interlocked.Read(ref feedbackDispatchGeneration);
        }

        private void FeedbackSpeakerDispatchLoop(long generation)
        {
            byte[] payload = new byte[FeedbackSpeakerSlotLength];
            byte[] atomicFeedbackScratch =
                new byte[DualSenseCombinedExtendedFeedbackLength];
            try
            {
                while (IsFeedbackDispatchGenerationActive(generation))
                {
                    Action<ViiperOutDevice, byte[], int> subscriber =
                        GetVirtualSpeakerPcmSubscriber();
                    ViiperAtomicAudioHapticsHandler atomicSubscriber =
                        GetVirtualAtomicAudioHapticsSubscriber();
                    bool subscriberAvailable = CanDispatchVirtualSpeaker(
                        activeStreamSupportsAtomicAudioHaptics,
                        subscriber != null, atomicSubscriber != null);
                    if (!subscriberAvailable)
                    {
                        if (feedbackDispatchBuffer.PendingSpeakerCount > 0)
                        {
                            Interlocked.Increment(
                                ref feedbackSpeakerNoSubscriberDeferrals);
                        }

                        feedbackSpeakerSignal.WaitOne(25);
                        continue;
                    }

                    if (!feedbackDispatchBuffer.TryDequeueSpeaker(payload,
                        out int length, out long streamItemGeneration,
                        out byte speakerKind, out int targetDeviceIndex))
                    {
                        feedbackSpeakerSignal.WaitOne(100);
                        continue;
                    }

                    feedbackDispatchGenerationBarrier.EnterReadLock();
                    try
                    {
                        if (!IsFeedbackDispatchGenerationActive(generation) ||
                            streamItemGeneration !=
                                Interlocked.Read(ref streamGeneration))
                        {
                            Interlocked.Increment(ref feedbackSpeakerStale);
                            continue;
                        }

                        long dispatchStarted = Stopwatch.GetTimestamp();
                        long previousDispatch = Interlocked.Exchange(
                            ref lastFeedbackSpeakerDispatchTimestamp,
                            dispatchStarted);
                        if (previousDispatch > 0)
                        {
                            RecordMaximum(
                                ref maximumFeedbackSpeakerDispatchGapTicks,
                                dispatchStarted - previousDispatch);
                        }

                        try
                        {
                            if (speakerKind ==
                                FeedbackSpeakerKindAtomicAudioHaptics)
                            {
                                if ((atomicSubscriber == null &&
                                        subscriber == null) ||
                                    !TryGetAtomicAudioHapticsLayout(payload,
                                        length, out int feedbackOffset,
                                        out int atomicFeedbackLength,
                                        out int speakerPcmOffset,
                                        out int speakerPcmLength))
                                {
                                    Interlocked.Increment(
                                        ref feedbackSpeakerStale);
                                    continue;
                                }

                                if (atomicSubscriber != null)
                                {
                                    atomicSubscriber(this, payload,
                                        feedbackOffset,
                                        atomicFeedbackLength, speakerPcmOffset,
                                        speakerPcmLength, targetDeviceIndex);
                                }
                                else
                                {
                                    // A physical DS4 consumes the DualSense
                                    // virtual endpoint's PCM but cannot consume
                                    // its atomic carrier. Translate the native
                                    // feedback first, then present only PCM to
                                    // the proven DS4 speaker encoder. The two
                                    // physical protocols never share a packet,
                                    // queue clock, or transport writer.
                                    Buffer.BlockCopy(payload,
                                        feedbackOffset,
                                        atomicFeedbackScratch, 0,
                                        atomicFeedbackLength);
                                    ApplyFeedback(atomicFeedbackScratch,
                                        atomicFeedbackLength,
                                        targetDeviceIndex);
                                    Buffer.BlockCopy(payload,
                                        speakerPcmOffset, payload, 0,
                                        speakerPcmLength);
                                    subscriber(this, payload,
                                        speakerPcmLength);
                                }
                            }
                            else
                            {
                                subscriber(this, payload, length);
                            }
                            Interlocked.Increment(ref feedbackSpeakerDelivered);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(
                                ref feedbackSpeakerCallbackFailures);
                            if (Interlocked.Exchange(
                                ref feedbackSpeakerCallbackFailureLogged,
                                1) == 0)
                            {
                                AppLogger.LogToGui(
                                    $"VIIPER {viiperType} speaker dispatch failed: {ex.GetType().Name}: {ex.Message}",
                                    true);
                            }
                        }
                        finally
                        {
                            RecordMaximum(
                                ref maximumFeedbackSpeakerCallbackTicks,
                                Stopwatch.GetTimestamp() - dispatchStarted);
                        }
                    }
                    finally
                    {
                        feedbackDispatchGenerationBarrier.ExitReadLock();
                    }
                }
            }
            finally
            {
                lock (feedbackDispatchThreadLock)
                {
                    if (ReferenceEquals(feedbackSpeakerDispatchThread,
                        Thread.CurrentThread))
                    {
                        feedbackSpeakerDispatchThread = null;
                    }
                }
            }
        }

        private void FeedbackControlDispatchLoop(long generation)
        {
            byte[] payload = new byte[DualSenseCombinedExtendedFeedbackLength];
            try
            {
                while (IsFeedbackDispatchGenerationActive(generation))
                {
                    bool dequeued = feedbackDispatchBuffer
                        .TryDequeueOrderedControl(payload, out int length,
                            out long streamItemGeneration,
                            out int targetDeviceIndex);
                    if (!dequeued)
                    {
                        dequeued = feedbackDispatchBuffer.TryTakeControl(
                            payload, out length, out streamItemGeneration,
                            out targetDeviceIndex);
                    }

                    if (!dequeued)
                    {
                        feedbackControlSignal.WaitOne(100);
                        continue;
                    }

                    DispatchFeedbackControl(payload, length,
                        streamItemGeneration, targetDeviceIndex, generation);
                }
            }
            finally
            {
                lock (feedbackDispatchThreadLock)
                {
                    if (ReferenceEquals(feedbackControlDispatchThread,
                        Thread.CurrentThread))
                    {
                        feedbackControlDispatchThread = null;
                    }
                }
            }
        }

        private void DispatchFeedbackControl(byte[] payload, int length,
            long streamItemGeneration, int targetDeviceIndex,
            long dispatchGeneration)
        {
            feedbackDispatchGenerationBarrier.EnterReadLock();
            try
            {
                if (!IsFeedbackDispatchGenerationActive(dispatchGeneration) ||
                    streamItemGeneration !=
                        Interlocked.Read(ref streamGeneration) ||
                    targetDeviceIndex !=
                        Volatile.Read(ref lastInputDeviceIndex))
                {
                    Interlocked.Increment(ref feedbackControlStale);
                    return;
                }

                try
                {
                    ApplyFeedback(payload, length, targetDeviceIndex);
                    Interlocked.Increment(ref feedbackControlDelivered);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref feedbackControlCallbackFailures);
                    if (Interlocked.Exchange(
                        ref feedbackControlCallbackFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            $"VIIPER {viiperType} control dispatch failed: {ex.GetType().Name}: {ex.Message}",
                            true);
                    }
                }
            }
            finally
            {
                feedbackDispatchGenerationBarrier.ExitReadLock();
            }
        }

        private void StartStateWriter(long writerGeneration)
        {
            lock (writerThreadLock)
            {
                // EnsureStateWriterAlive can race a full disconnect/reconnect
                // after reading the generation but before entering this lock.
                // Never let that stale request replace the live worker field.
                if (!IsStateWriterCurrent(writerGeneration))
                {
                    return;
                }

                if (stateWriterThread != null && stateWriterThread.IsAlive &&
                    Interlocked.Read(ref stateWriterThreadGeneration) ==
                        writerGeneration)
                {
                    return;
                }

                Thread writerThread = new Thread(() =>
                    StateWriteLoop(writerGeneration))
                {
                    IsBackground = true,
                    Name = $"VIIPER {viiperType} writer",
                    Priority = ThreadPriority.AboveNormal,
                };
                stateWriterThread = writerThread;
                Interlocked.Exchange(ref stateWriterThreadGeneration,
                    writerGeneration);
                writerThread.Start();
            }
        }

        private bool IsStateWriterCurrent(long writerGeneration)
        {
            return !writerStopRequested && connected && writerGeneration ==
                Interlocked.Read(ref stateWriterGeneration);
        }

        private void StateWriteLoop(long writerGeneration)
        {
            try
            {
                while (IsStateWriterCurrent(writerGeneration))
                {
                    writerSignal.WaitOne();
                    if (!IsStateWriterCurrent(writerGeneration))
                    {
                        return;
                    }

                    while (IsStateWriterCurrent(writerGeneration))
                    {
                        bool hasPendingPacket;
                        lock (pendingPacketLock)
                        {
                            hasPendingPacket = pendingStatePacket != null;
                        }

                        if (!hasPendingPacket)
                        {
                            break;
                        }

                        if (!WaitForStateWriteRateWindow(writerGeneration))
                        {
                            return;
                        }

                        byte[] packet;
                        long queuedAt;
                        lock (pendingPacketLock)
                        {
                            if (!IsStateWriterCurrent(writerGeneration))
                            {
                                return;
                            }
                            packet = pendingStatePacket;
                            pendingStatePacket = null;
                            queuedAt = pendingStatePacketQueuedTimestamp;
                            pendingStatePacketQueuedTimestamp = 0;
                        }

                        if (packet == null)
                        {
                            break;
                        }

                        long writeStreamGeneration = Volatile.Read(
                            ref streamGeneration);
                        ViiperDeviceStream writeStream = deviceStream;
                        if (!IsStateWriterCurrent(writerGeneration))
                        {
                            return;
                        }
                        try
                        {
                            long writeStartedAt = Stopwatch.GetTimestamp();
                            long previousWriteStartedAt = Interlocked.Exchange(
                                ref lastRateLimitedStateWriteStartedTimestamp,
                                writeStartedAt);
                            if (previousWriteStartedAt > 0)
                            {
                                RecordMinimum(ref minimumStateWriteStartGapTicks,
                                    writeStartedAt - previousWriteStartedAt);
                            }
                            if (queuedAt > 0)
                            {
                                RecordMaximum(ref maximumStatePacketAgeTicks,
                                    writeStartedAt - queuedAt);
                            }

                            WriteState(writeStream, packet);
                            long writtenAt = Stopwatch.GetTimestamp();
                            RecordMaximum(ref maximumStateWriteDurationTicks,
                                writtenAt - writeStartedAt);
                            long previousWrittenAt = Interlocked.Exchange(
                                ref lastStateWrittenTimestamp, writtenAt);
                            if (previousWrittenAt > 0)
                            {
                                RecordMaximum(ref maximumStateWriteGapTicks,
                                    writtenAt - previousWrittenAt);
                            }
                            Interlocked.Increment(ref writtenPacketCount);

                            Interlocked.Exchange(ref streamRecoveryAttempts, 0);
                            LogWriterHealthIfNeeded();
                        }
                        catch (IOException ex)
                        {
                            if (IsStateWriterCurrent(writerGeneration) &&
                                TryRecoverStream(ex.Message,
                                    writeStreamGeneration, packet))
                            {
                                continue;
                            }

                            if (IsStateWriterCurrent(writerGeneration))
                            {
                                LogSubmitFailure(ex.Message);
                            }
                            return;
                        }
                        catch (SocketException ex)
                        {
                            if (IsStateWriterCurrent(writerGeneration) &&
                                TryRecoverStream(ex.Message,
                                    writeStreamGeneration, packet))
                            {
                                continue;
                            }

                            if (IsStateWriterCurrent(writerGeneration))
                            {
                                LogSubmitFailure(ex.Message);
                            }
                            return;
                        }
                        catch (ObjectDisposedException ex)
                        {
                            if (IsStateWriterCurrent(writerGeneration) &&
                                TryRecoverStream(ex.Message,
                                    writeStreamGeneration, packet))
                            {
                                continue;
                            }

                            if (IsStateWriterCurrent(writerGeneration))
                            {
                                LogSubmitFailure(ex.Message);
                            }
                            return;
                        }
                    }
                }
            }
            finally
            {
                lock (writerThreadLock)
                {
                    if (ReferenceEquals(stateWriterThread,
                        Thread.CurrentThread))
                    {
                        stateWriterThread = null;
                        stateWriterThreadGeneration = 0;
                    }
                }

                // A superseded worker can consume the shared wakeup just as a
                // replacement queues its first state. Hand the wakeup back to
                // the current generation before exiting.
                if (connected && writerGeneration != Interlocked.Read(
                    ref stateWriterGeneration))
                {
                    writerSignal.Set();
                }
            }
        }

        private void EnsureMicrophoneWriterAlive()
        {
            if (!connected || writerStopRequested || !activeStreamSupportsMicrophone)
            {
                return;
            }

            if (microphoneWriterThread == null || !microphoneWriterThread.IsAlive)
            {
                StartMicrophoneWriter(Interlocked.Read(
                    ref microphoneWorkerGeneration));
            }
        }

        private bool WaitForStateWriteRateWindow(long writerGeneration)
        {
            while (IsStateWriterCurrent(writerGeneration))
            {
                long remainingTicks =
                    ViiperStateWriteRateSettings.GetRemainingTicks(
                        Stopwatch.GetTimestamp(),
                        Interlocked.Read(
                            ref lastRateLimitedStateWriteStartedTimestamp),
                        stateWriteMinimumIntervalTicks);
                if (remainingTicks <= 0)
                {
                    return true;
                }

                int waitMilliseconds = Math.Max(1, (int)Math.Min(int.MaxValue,
                    Math.Ceiling(remainingTicks * 1000.0 /
                        Stopwatch.Frequency)));
                if (writerRateWaitStopSignal.WaitOne(waitMilliseconds))
                {
                    return false;
                }
            }

            return false;
        }

        private void StartMicrophoneWriter(long workerGeneration)
        {
            if (!activeStreamSupportsMicrophone)
            {
                return;
            }

            lock (microphoneWriterThreadLock)
            {
                if (microphoneWriterThread != null && microphoneWriterThread.IsAlive)
                {
                    return;
                }

                microphoneWriterThread = new Thread(() =>
                    MicrophoneWriteLoop(workerGeneration))
                {
                    IsBackground = true,
                    Name = $"VIIPER {viiperType} microphone writer",
                    // Microphone SBC/Opus frames feed a realtime USB capture
                    // endpoint. Match the speaker transport's scheduling class
                    // so ordinary UI/GC activity cannot bunch 10 ms PCM frames.
                    Priority = ThreadPriority.Highest,
                };
                microphoneWriterThread.Start();
            }
        }

        private void MicrophoneWriteLoop(long workerGeneration)
        {
            while (!writerStopRequested && workerGeneration ==
                Interlocked.Read(ref microphoneWorkerGeneration))
            {
                microphoneWriterSignal.WaitOne();
                if (writerStopRequested || workerGeneration !=
                    Interlocked.Read(ref microphoneWorkerGeneration))
                {
                    return;
                }

                while (!writerStopRequested && workerGeneration ==
                    Interlocked.Read(ref microphoneWorkerGeneration))
                {
                    PendingMicrophoneFrame? microphoneFrame;
                    lock (microphoneQueueLock)
                    {
                        microphoneFrame = pendingMicrophoneFrames.Count > 0 ?
                            pendingMicrophoneFrames.Dequeue() :
                            (PendingMicrophoneFrame?)null;
                    }

                    if (!microphoneFrame.HasValue)
                    {
                        break;
                    }

                    if (!TryWriteMicrophoneFrame(
                        microphoneFrame.Value))
                    {
                        return;
                    }
                }
            }
        }

        private bool TryWriteMicrophoneFrame(PendingMicrophoneFrame frame)
        {
            try
            {
                WriteMicrophoneFrame(frame);
                Interlocked.Exchange(ref streamRecoveryAttempts, 0);
                return true;
            }
            catch (IOException ex)
            {
                LogSubmitFailure(ex.Message);
                return false;
            }
            catch (SocketException ex)
            {
                LogSubmitFailure(ex.Message);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref microphoneDecodeFailures);
                if (Global.VerboseStartupLogging &&
                    Interlocked.Exchange(ref microphoneProcessingFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        $"VIIPER microphone processing failed: {ex.GetType().Name}: {ex.Message}",
                        true);
                }

                return true;
            }
        }

        private bool TryRecoverStream(string reason, long failedStreamGeneration,
            byte[] packetToRetry = null)
        {
            if (writerStopRequested || !connected)
            {
                return false;
            }

            if (Volatile.Read(ref streamGeneration) != failedStreamGeneration &&
                deviceStream != null)
            {
                QueueRetryStatePacket(packetToRetry);
                return true;
            }

            lock (streamRecoveryLock)
            {
                if (writerStopRequested || !connected)
                {
                    return false;
                }

                if (Volatile.Read(ref streamGeneration) != failedStreamGeneration &&
                    deviceStream != null)
                {
                    QueueRetryStatePacket(packetToRetry);
                    return true;
                }

                ViiperDeviceStream interruptedStream = deviceStream;
                if (interruptedStream == null)
                {
                    return false;
                }

                AppLogger.LogToGui(
                    $"VIIPER {viiperType} stream interrupted; reopening the existing virtual device: {reason}",
                    true);

                // Closing only the authenticated/raw TCP transport wakes the
                // old feedback reader without releasing transport ownership or
                // removing the virtual controller.
                // Keep the published generation and lifetime intact until a
                // replacement transport has actually opened.
                interruptedStream.CloseTransport();
                Exception lastError = null;
                for (int attempt = 1; attempt <= MaxStreamRecoveryAttempts;
                    attempt++)
                {
                    int backoffMilliseconds =
                        GetStreamRecoveryBackoffMilliseconds(attempt);
                    if (backoffMilliseconds > 0 &&
                        !WaitForStreamRecoveryBackoff(backoffMilliseconds))
                    {
                        return false;
                    }

                    if (writerStopRequested || !connected)
                    {
                        return false;
                    }

                    Volatile.Write(ref streamRecoveryAttempts, attempt);
                    try
                    {
                        ViiperDeviceStream replacement =
                            client.OpenExistingDeviceStream(
                                interruptedStream.BusId,
                                interruptedStream.DevId,
                                interruptedStream.DeviceLifetime);
                        if (writerStopRequested || !connected)
                        {
                            replacement.Dispose();
                            return false;
                        }

                        feedbackDispatchGenerationBarrier.EnterWriteLock();
                        try
                        {
                            deviceStream = replacement;
                            Interlocked.Increment(ref streamGeneration);
                            if (liveValidationLease != null)
                            {
                                Interlocked.Increment(ref
                                    liveValidationStreamRecoveriesCompleted);
                            }
                            Interlocked.Exchange(ref streamRecoveryAttempts, 0);
                            // Publish the new timeline only after every old
                            // callback and reader admission has left its read
                            // lease. Clearing here is therefore a hard
                            // generation boundary, not a best-effort purge.
                            feedbackDispatchBuffer.ClearPending();
                        }
                        finally
                        {
                            feedbackDispatchGenerationBarrier.ExitWriteLock();
                        }

                        // Ensure both independent consumers are alive before
                        // publishing another reader.
                        StartFeedbackDispatchWorkers();
                        StartFeedbackReader();
                        QueueRetryStatePacket(packetToRetry);

                        AppLogger.LogToGui(
                            $"VIIPER {viiperType} stream recovered on the existing virtual device after {attempt} attempt(s).",
                            false);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                AppLogger.LogToGui(
                    $"VIIPER {viiperType} transport recovery exhausted {MaxStreamRecoveryAttempts} attempts without removing the virtual device: {lastError?.Message}",
                    true);
                return false;
            }
        }

        internal static int GetStreamRecoveryBackoffMilliseconds(int attempt)
        {
            if (attempt <= 1)
            {
                return 0;
            }

            int shift = Math.Min(attempt - 2, 20);
            long delay = (long)InitialStreamRecoveryBackoffMilliseconds << shift;
            return (int)Math.Min(delay,
                MaximumStreamRecoveryBackoffMilliseconds);
        }

        private bool WaitForStreamRecoveryBackoff(int milliseconds)
        {
            int remaining = milliseconds;
            while (remaining > 0)
            {
                if (writerStopRequested || !connected)
                {
                    return false;
                }

                int slice = Math.Min(remaining, 50);
                Thread.Sleep(slice);
                remaining -= slice;
            }

            return !writerStopRequested && connected;
        }

        private void WriteState(ViiperDeviceStream stream, byte[] data)
        {
            if (stream == null)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            if (activeStreamUsesFramedProtocol)
            {
                stream.WriteFrame(activeStreamFrameVersion,
                    ViiperStreamFrameInputState, data);
            }
            else
            {
                stream.Write(data);
            }
        }

        private void WriteMicrophoneFrame(PendingMicrophoneFrame frame)
        {
            switch (frame.Codec)
            {
                case MicrophoneCodec.Opus:
                    WriteMicrophoneOpusFrame(frame.Data);
                    break;
                case MicrophoneCodec.Sbc:
                    WriteMicrophoneSbcFrame(frame.Sequence,
                        frame.HasSequence, frame.Data);
                    break;
            }
        }

        private void WriteMicrophoneOpusFrame(byte[] opusFrame)
        {
            if (!activeStreamSupportsMicrophone ||
                !activeStreamUsesFramedProtocol ||
                opusFrame == null ||
                opusFrame.Length != DualSenseMicrophoneOpusFrameLength)
            {
                return;
            }

            lock (microphoneProcessingLock)
            {
                bool muted = Volatile.Read(ref microphoneMuted) == 1;
                IOpusDecoder decoder = microphoneDecoder;
                if (decoder == null)
                {
                    decoder = OpusCodecFactory.CreateDecoder(48000, 1);
                    microphoneDecoder = decoder;
                }

                // Opus prediction state must advance for every physical frame.
                // Muting only the final PCM payload avoids a stale-decoder
                // transient when the user restores microphone audio.
                int decodedSamples = decoder.Decode(opusFrame.AsSpan(),
                    microphoneMonoPcm.AsSpan(),
                    DualSenseMicrophoneFramesPerPacket, false);
                if (decodedSamples <= 0)
                {
                    Interlocked.Increment(ref microphoneDecodeFailures);
                    return;
                }

                Interlocked.Increment(ref microphoneFramesDecoded);
                int frames = Math.Min(decodedSamples,
                    DualSenseMicrophoneFramesPerPacket);
                SubmitMicrophonePcm(frames, muted);
            }
        }

        private void WriteMicrophoneSbcFrame(ushort sequence,
            bool hasSequence, byte[] sbcFrame)
        {
            if (!activeStreamSupportsMicrophone ||
                !activeStreamUsesFramedProtocol ||
                sbcFrame == null || sbcFrame.Length < SbcFrame.HeaderSize)
            {
                return;
            }

            lock (microphoneProcessingLock)
            {
                // A prior stream write can fail after a complete 10 ms packet
                // has been assembled. Flush it before examining the retried
                // compressed frame; the sequence guard below then prevents a
                // second decode of the same physical audio.
                FlushDualShock4MicrophonePackets(
                    Volatile.Read(ref microphoneMuted) == 1);

                int missingFrames = 0;
                if (hasSequence && dualShock4MicrophoneSequenceKnown)
                {
                    ushort delta = unchecked((ushort)(sequence -
                        dualShock4LastMicrophoneSequence));
                    if (delta == 0)
                    {
                        Interlocked.Increment(ref microphoneDuplicateFrames);
                        return;
                    }

                    // Sequence arithmetic is modulo 16 bits. Values in the
                    // upper half of the range are older packets, not a giant
                    // forward jump after wraparound.
                    if (delta >= 0x8000)
                    {
                        Interlocked.Increment(ref microphoneOutOfOrderFrames);
                        return;
                    }

                    missingFrames = delta - 1;
                    if (missingFrames >
                        DualShock4MicrophoneMaximumConcealedFrames)
                    {
                        Interlocked.Add(ref microphoneSequenceGaps,
                            missingFrames);
                        Interlocked.Increment(ref microphoneDiscontinuities);
                        ResetDualShock4MicrophoneDecodeState(
                            preserveSequence: true);
                        missingFrames = 0;
                    }
                }

                SbcDecoder decoder = microphoneSbcDecoder;
                if (decoder == null)
                {
                    decoder = new SbcDecoder();
                    microphoneSbcDecoder = decoder;
                }

                if (!decoder.DecodeInto(sbcFrame, dualShock4DecodedSbcPcm,
                    null, dualShock4DecodedSbcFrame,
                    out int decodedSamples) ||
                    decodedSamples <= 0 ||
                    dualShock4DecodedSbcFrame.Mode != SbcMode.Mono ||
                    dualShock4DecodedSbcFrame.GetFrequencyHz() !=
                        DualShock4MicrophoneSourceSampleRate)
                {
                    Interlocked.Increment(ref microphoneDecodeFailures);
                    return;
                }

                Interlocked.Increment(ref microphoneFramesDecoded);

                if (missingFrames > 0)
                {
                    Interlocked.Add(ref microphoneSequenceGaps, missingFrames);
                    AppendDualShock4Concealment(dualShock4DecodedSbcPcm,
                        decodedSamples, missingFrames);
                    CrossfadeDualShock4MicrophoneRecovery(
                        dualShock4DecodedSbcPcm, decodedSamples,
                        missingFrames);
                }

                AppendDualShock4DecodedPcm(dualShock4DecodedSbcPcm,
                    decodedSamples);
                dualShock4LastDecodedPcmCount = Math.Min(decodedSamples,
                    dualShock4LastDecodedPcm.Length);
                Array.Copy(dualShock4DecodedSbcPcm, 0,
                    dualShock4LastDecodedPcm, 0,
                    dualShock4LastDecodedPcmCount);
                if (hasSequence)
                {
                    dualShock4LastMicrophoneSequence = sequence;
                    dualShock4MicrophoneSequenceKnown = true;
                }

                FlushDualShock4MicrophonePackets(
                    Volatile.Read(ref microphoneMuted) == 1);
            }
        }

        private void AppendDualShock4Concealment(short[] nextFrame,
            int nextFrameCount, int missingFrames)
        {
            int sampleCount = dualShock4LastDecodedPcmCount > 0 ?
                dualShock4LastDecodedPcmCount : nextFrameCount;
            for (int missing = 0; missing < missingFrames; missing++)
            {
                double attenuation = Math.Pow(0.82, missing + 1);
                if (dualShock4LastDecodedPcmCount > 0)
                {
                    for (int sample = 0; sample < sampleCount; sample++)
                    {
                        dualShock4ConcealmentPcm[sample] = (short)Math.Clamp((int)Math.Round(
                            dualShock4LastDecodedPcm[sample] * attenuation),
                            short.MinValue, short.MaxValue);
                    }
                    AppendDualShock4DecodedPcm(dualShock4ConcealmentPcm,
                        sampleCount);
                }
                else
                {
                    AppendDualShock4DecodedPcm(null, sampleCount);
                }

                Interlocked.Increment(ref microphoneConcealedFrames);
            }
        }

        private void CrossfadeDualShock4MicrophoneRecovery(short[] decoded,
            int decodedCount, int missingFrames)
        {
            if (decoded == null || decodedCount <= 0 ||
                dualShock4LastDecodedPcmCount == 0)
            {
                return;
            }

            int count = Math.Min(DualShock4MicrophoneCrossfadeSamples,
                Math.Min(decodedCount, dualShock4LastDecodedPcmCount));
            double attenuation = Math.Pow(0.82, missingFrames);
            int previousOffset = dualShock4LastDecodedPcmCount - count;
            for (int sample = 0; sample < count; sample++)
            {
                double blend = (sample + 1.0) / (count + 1.0);
                double previous = dualShock4LastDecodedPcm[
                    previousOffset + sample] * attenuation;
                decoded[sample] = (short)Math.Clamp((int)Math.Round(
                    previous * (1.0 - blend) + decoded[sample] * blend),
                    short.MinValue, short.MaxValue);
            }
        }

        private void AppendDualShock4DecodedPcm(short[] samples,
            int sampleCount)
        {
            if (sampleCount <= 0)
            {
                return;
            }

            if (dualShock4DecodedPcmFifoCount + sampleCount >
                dualShock4DecodedPcmFifo.Length)
            {
                throw new InvalidOperationException(
                    "The DS4 microphone sample FIFO overflowed.");
            }

            if (samples == null)
            {
                Array.Clear(dualShock4DecodedPcmFifo,
                    dualShock4DecodedPcmFifoCount, sampleCount);
            }
            else
            {
                Array.Copy(samples, 0, dualShock4DecodedPcmFifo,
                    dualShock4DecodedPcmFifoCount, sampleCount);
            }
            dualShock4DecodedPcmFifoCount += sampleCount;
        }

        private void FlushDualShock4MicrophonePackets(bool muted)
        {
            while (dualShock4DecodedPcmFifoCount >=
                DualShock4MicrophoneSourceSamplesPerPacket)
            {
                Array.Copy(dualShock4DecodedPcmFifo, 0,
                    dualShock4SourcePcmPacket, 0,
                    DualShock4MicrophoneSourceSamplesPerPacket);
                UpsampleDualShock4Microphone(dualShock4SourcePcmPacket,
                    microphoneMonoPcm, DualSenseMicrophoneFramesPerPacket);

                // Only remove samples after VIIPER accepts the packet. A stream
                // recovery can therefore retry this exact 10 ms payload.
                SubmitMicrophonePcm(DualSenseMicrophoneFramesPerPacket, muted);

                int remaining = dualShock4DecodedPcmFifoCount -
                    DualShock4MicrophoneSourceSamplesPerPacket;
                if (remaining > 0)
                {
                    Array.Copy(dualShock4DecodedPcmFifo,
                        DualShock4MicrophoneSourceSamplesPerPacket,
                        dualShock4DecodedPcmFifo, 0, remaining);
                }
                dualShock4DecodedPcmFifoCount = remaining;
            }
        }

        private void ResetDualShock4MicrophoneDecodeState(
            bool preserveSequence)
        {
            microphoneSbcDecoder?.Reset();
            dualShock4DecodedPcmFifoCount = 0;
            dualShock4LastDecodedPcmCount = 0;
            dualShock4ResamplePreviousSample = 0;
            dualShock4ResamplePreviousSampleKnown = false;
            Array.Clear(dualShock4DecodedPcmFifo, 0,
                dualShock4DecodedPcmFifo.Length);
            Array.Clear(dualShock4LastDecodedPcm, 0,
                dualShock4LastDecodedPcm.Length);
            Array.Clear(dualShock4ConcealmentPcm, 0,
                dualShock4ConcealmentPcm.Length);
            if (!preserveSequence)
            {
                dualShock4MicrophoneSequenceKnown = false;
                dualShock4LastMicrophoneSequence = 0;
            }
        }

        private void UpsampleDualShock4Microphone(short[] source,
            short[] destination, int destinationCount)
        {
            if (source == null || source.Length == 0 ||
                destination == null || destination.Length < destinationCount ||
                destinationCount != source.Length * 3)
            {
                throw new ArgumentException(
                    "DS4 microphone resampling requires an exact 3x buffer.");
            }

            short previous = dualShock4ResamplePreviousSampleKnown ?
                dualShock4ResamplePreviousSample : source[0];
            for (int index = 0; index < source.Length; index++)
            {
                short current = source[index];
                int delta = current - previous;
                int output = index * 3;
                destination[output] = (short)(previous + delta / 3);
                destination[output + 1] = (short)(previous +
                    delta * 2 / 3);
                destination[output + 2] = current;
                previous = current;
            }

            dualShock4ResamplePreviousSample = previous;
            dualShock4ResamplePreviousSampleKnown = true;
        }

        private void SubmitMicrophonePcm(int frames, bool muted)
        {
            microphoneTelemetry.ObservePreProcessorFrame(microphoneMonoPcm,
                frames);
            DualSenseMicrophoneNoiseSuppression suppression =
                (DualSenseMicrophoneNoiseSuppression)Math.Clamp(
                    Volatile.Read(ref microphoneNoiseSuppression),
                    (int)DualSenseMicrophoneNoiseSuppression.Off,
                    (int)DualSenseMicrophoneNoiseSuppression.Strong);
            microphoneProcessor.Process(microphoneMonoPcm, frames,
                (byte)Math.Clamp(Volatile.Read(ref microphoneVolume), 0,
                    byte.MaxValue), suppression, muteOutput: muted);
            microphoneTelemetry.ObservePostProcessorFrame(microphoneMonoPcm,
                frames, muted);
            if (suppression != DualSenseMicrophoneNoiseSuppression.Off &&
                Global.VerboseStartupLogging &&
                Volatile.Read(ref microphoneNoiseSuppressionUnavailableLogged) == 0 &&
                !microphoneProcessor.NoiseSuppressionAvailable &&
                Interlocked.Exchange(
                    ref microphoneNoiseSuppressionUnavailableLogged, 1) == 0)
            {
                AppLogger.LogToGui(
                    $"VIIPER microphone RNNoise unavailable; safety conditioning remains active: {microphoneProcessor.NoiseSuppressionFailure}",
                    true);
            }

            byte[] payload;
            if (viiperType == ViiperVirtualDeviceType.DualShock4)
            {
                ConvertMicrophoneMono48kToDualShock4Pcm(microphoneMonoPcm,
                    frames, dualShock4MicrophonePcm);
                payload = dualShock4MicrophonePcm;
            }
            else
            {
                Array.Clear(microphoneStereoPcm, 0, microphoneStereoPcm.Length);
                for (int frame = 0; frame < frames; frame++)
                {
                    short sample = microphoneMonoPcm[frame];
                    int offset = frame * 4;
                    microphoneStereoPcm[offset] = (byte)sample;
                    microphoneStereoPcm[offset + 1] = (byte)(sample >> 8);
                    microphoneStereoPcm[offset + 2] = (byte)sample;
                    microphoneStereoPcm[offset + 3] = (byte)(sample >> 8);
                }
                payload = microphoneStereoPcm;
            }

            // This timestamp is intentionally recorded only after decoding,
            // conditioning, resampling, and virtual-format packing all
            // succeeded. Receiving a syntactically sized compressed frame is
            // not proof that usable PCM is moving through the pipeline.
            Interlocked.Increment(ref microphoneFramesProcessed);
            Interlocked.Exchange(ref lastMicrophoneProcessedTimestamp,
                Stopwatch.GetTimestamp());

            WritePreparedMicrophonePayloadWithRecovery(payload);
            Interlocked.Increment(ref microphoneFramesSubmitted);
            long submittedAt = Stopwatch.GetTimestamp();
            microphoneTelemetry.RecordSuccessfulSubmission(submittedAt);
            Interlocked.Exchange(ref lastMicrophoneSubmittedTimestamp,
                submittedAt);
        }

        private void WritePreparedMicrophonePayloadWithRecovery(byte[] payload)
        {
            while (!writerStopRequested && connected &&
                ReferenceEquals(Thread.CurrentThread,
                    microphoneWriterThread))
            {
                long failedStreamGeneration = Volatile.Read(
                    ref streamGeneration);
                ViiperDeviceStream stream = deviceStream;
                if (stream == null)
                {
                    throw new ObjectDisposedException(
                        nameof(ViiperDeviceStream));
                }

                try
                {
                    stream.WriteFrame(activeStreamFrameVersion,
                        ViiperStreamFrameMicrophonePcm, payload);
                    return;
                }
                catch (IOException ex)
                {
                    if (!ReferenceEquals(Thread.CurrentThread,
                            microphoneWriterThread) ||
                        !TryRecoverStream(ex.Message,
                        failedStreamGeneration))
                    {
                        throw;
                    }
                }
                catch (SocketException ex)
                {
                    if (!ReferenceEquals(Thread.CurrentThread,
                            microphoneWriterThread) ||
                        !TryRecoverStream(ex.Message,
                        failedStreamGeneration))
                    {
                        throw;
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    if (writerStopRequested || !connected ||
                        !ReferenceEquals(Thread.CurrentThread,
                            microphoneWriterThread) ||
                        !TryRecoverStream(ex.Message,
                            failedStreamGeneration))
                    {
                        throw;
                    }
                }
            }

            throw new ObjectDisposedException(nameof(ViiperDeviceStream));
        }

        internal static int ConvertMicrophoneMono48kToDualShock4Pcm(
            short[] source, int sourceFrames, byte[] destination)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            Array.Clear(destination, 0, destination.Length);
            int outputFrames = Math.Min(Math.Min(
                DualShock4VirtualMicrophoneFramesPerPacket,
                Math.Max(0, sourceFrames) / 3), destination.Length / sizeof(short));
            outputFrames = Math.Min(outputFrames, source.Length / 3);

            for (int frame = 0; frame < outputFrames; frame++)
            {
                int sourceOffset = frame * 3;
                int averaged = (source[sourceOffset] + source[sourceOffset + 1] +
                    source[sourceOffset + 2]) / 3;
                short sample = (short)averaged;
                int outputOffset = frame * sizeof(short);
                destination[outputOffset] = (byte)sample;
                destination[outputOffset + 1] = (byte)(sample >> 8);
            }

            return outputFrames;
        }

        private void LogWriterHealthIfNeeded()
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now - lastWriterHealthLogUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }

            lastWriterHealthLogUtc = now;
            long maximumQueueGap = Interlocked.Exchange(ref maximumStateQueueGapTicks, 0);
            long maximumPacketAge = Interlocked.Exchange(ref maximumStatePacketAgeTicks, 0);
            long maximumWriteDuration = Interlocked.Exchange(ref maximumStateWriteDurationTicks, 0);
            long maximumWriteGap = Interlocked.Exchange(ref maximumStateWriteGapTicks, 0);
            long minimumWriteStartGap = Interlocked.Exchange(
                ref minimumStateWriteStartGapTicks, long.MaxValue);
            long maximumSpeakerDispatchGap = Interlocked.Exchange(
                ref maximumFeedbackSpeakerDispatchGapTicks, 0);
            long maximumSpeakerCallback = Interlocked.Exchange(
                ref maximumFeedbackSpeakerCallbackTicks, 0);
            AppLogger.LogToGui(
                $"VIIPER {viiperType} writer stats: " +
                $"submitted={Interlocked.Read(ref submittedPacketCount)} " +
                $"written={Interlocked.Read(ref writtenPacketCount)} " +
                $"coalesced={Interlocked.Read(ref replacedPendingPacketCount)} " +
                $"queueGapMaxMs={StopwatchTicksToMilliseconds(maximumQueueGap):F2} " +
                $"packetAgeMaxMs={StopwatchTicksToMilliseconds(maximumPacketAge):F2} " +
                $"writeMaxMs={StopwatchTicksToMilliseconds(maximumWriteDuration):F2} " +
                $"writeGapMaxMs={StopwatchTicksToMilliseconds(maximumWriteGap):F2} " +
                $"writeStartGapMinMs={(minimumWriteStartGap == long.MaxValue ? 0.0 : StopwatchTicksToMilliseconds(minimumWriteStartGap)):F2} " +
                $"rateLimitHz={stateWriteRateHz} " +
                $"speakerQueued={feedbackDispatchBuffer.SpeakerEnqueued} " +
                $"speakerDequeued={feedbackDispatchBuffer.SpeakerDequeued} " +
                $"speakerDelivered={Interlocked.Read(ref feedbackSpeakerDelivered)} " +
                $"speakerDropped={feedbackDispatchBuffer.SpeakerDropped} " +
                $"speakerExpired={feedbackDispatchBuffer.SpeakerExpired} " +
                $"speakerStale={Interlocked.Read(ref feedbackSpeakerStale)} " +
                $"speakerNoSubscriberDeferrals={Interlocked.Read(ref feedbackSpeakerNoSubscriberDeferrals)} " +
                $"speakerCallbackFailures={Interlocked.Read(ref feedbackSpeakerCallbackFailures)} " +
                $"speakerPending={feedbackDispatchBuffer.PendingSpeakerCount} " +
                $"speakerHighWater={feedbackDispatchBuffer.SpeakerHighWater} " +
                $"speakerQueueAgeMaxMs={feedbackDispatchBuffer.SpeakerMaximumQueueAgeMilliseconds:F2} " +
                $"speakerDispatchGapMaxMs={StopwatchTicksToMilliseconds(maximumSpeakerDispatchGap):F2} " +
                $"speakerCallbackMaxMs={StopwatchTicksToMilliseconds(maximumSpeakerCallback):F2} " +
                $"controlQueued={feedbackDispatchBuffer.ControlEnqueued} " +
                $"controlDequeued={feedbackDispatchBuffer.ControlDequeued} " +
                $"controlCoalesced={feedbackDispatchBuffer.ControlCoalesced} " +
                $"controlDropped={feedbackDispatchBuffer.ControlDropped} " +
                $"hapticsQueued={feedbackDispatchBuffer.OrderedControlEnqueued} " +
                $"hapticsDequeued={feedbackDispatchBuffer.OrderedControlDequeued} " +
                $"hapticsDropped={feedbackDispatchBuffer.OrderedControlDropped} " +
                $"hapticsExpired={feedbackDispatchBuffer.OrderedControlExpired} " +
                $"hapticsPending={feedbackDispatchBuffer.PendingOrderedControlCount} " +
                $"hapticsHighWater={feedbackDispatchBuffer.OrderedControlHighWater} " +
                $"hapticsQueueAgeMaxMs={feedbackDispatchBuffer.OrderedControlMaximumQueueAgeMilliseconds:F2} " +
                $"controlDelivered={Interlocked.Read(ref feedbackControlDelivered)} " +
                $"controlStale={Interlocked.Read(ref feedbackControlStale)} " +
                $"controlCallbackFailures={Interlocked.Read(ref feedbackControlCallbackFailures)}",
                false);
        }

        private static void RecordMaximum(ref long target, long candidate)
        {
            if (candidate <= 0)
            {
                return;
            }

            long current = Interlocked.Read(ref target);
            while (candidate > current)
            {
                long observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        private void QueueRetryStatePacket(byte[] packetToRetry)
        {
            if (packetToRetry == null)
            {
                return;
            }

            lock (pendingPacketLock)
            {
                // A state queued while recovery was running is newer than the failed packet.
                if (pendingStatePacket == null)
                {
                    pendingStatePacket = packetToRetry;
                    pendingStatePacketQueuedTimestamp = Stopwatch.GetTimestamp();
                }
            }

            writerSignal.Set();
        }

        private static double StopwatchTicksToMilliseconds(long ticks)
        {
            return ticks <= 0 ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;
        }

        private void StartFeedbackReader()
        {
            int length = activeFeedbackLength > 0 ? activeFeedbackLength : ViiperStatePacketBuilder.GetFeedbackLength(viiperType);
            ViiperDeviceStream stream = deviceStream;
            long readStreamGeneration = Volatile.Read(ref streamGeneration);
            if (length <= 0 || stream == null || !connected)
            {
                return;
            }

            Thread thread = new Thread(() => FeedbackReadLoop(length, stream,
                readStreamGeneration))
            {
                IsBackground = true,
                Name = $"VIIPER {viiperType} feedback",
                Priority = activeStreamSupportsDirectSpeaker ?
                    ThreadPriority.Highest : ThreadPriority.AboveNormal,
            };
            lock (feedbackThreadLock)
            {
                if (!connected || !ReferenceEquals(deviceStream, stream) ||
                    Volatile.Read(ref streamGeneration) != readStreamGeneration)
                {
                    return;
                }

                feedbackThread = thread;
            }
            thread.Start();
        }

        private static void RecordMinimum(ref long target, long candidate)
        {
            if (candidate <= 0)
            {
                return;
            }

            long current = Interlocked.Read(ref target);
            while (candidate < current)
            {
                long observed = Interlocked.CompareExchange(ref target,
                    candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        private void FeedbackReadLoop(int feedbackLength,
            ViiperDeviceStream stream, long readStreamGeneration)
        {
            int bufferLength = IsDualSenseType() ? Math.Max(feedbackLength, DualSenseCombinedExtendedFeedbackLength) : feedbackLength;
            byte[] buffer = new byte[bufferLength];
            byte[] framedPayload = new byte[ushort.MaxValue];
            try
            {
                while (connected && readStreamGeneration ==
                    Volatile.Read(ref streamGeneration))
                {
                    if (activeStreamSupportsDirectSpeaker)
                    {
                        int payloadLength = stream.ReadFrame(
                            activeStreamFrameVersion, out byte frameType,
                            framedPayload);
                        feedbackDispatchGenerationBarrier.EnterReadLock();
                        try
                        {
                            if (!connected || readStreamGeneration !=
                                Volatile.Read(ref streamGeneration) ||
                                !ReferenceEquals(deviceStream, stream))
                            {
                                break;
                            }

                            if (frameType == ViiperStreamFrameOutputState &&
                                payloadLength == feedbackLength)
                            {
                                int targetDeviceIndex = Volatile.Read(
                                    ref lastInputDeviceIndex);
                                bool queued = IsDualSenseType() ?
                                    feedbackDispatchBuffer
                                        .TryEnqueueOrderedControl(
                                            framedPayload, payloadLength,
                                            readStreamGeneration,
                                            targetDeviceIndex) :
                                    feedbackDispatchBuffer.QueueControl(
                                        framedPayload, payloadLength,
                                        readStreamGeneration,
                                        targetDeviceIndex);
                                if (queued)
                                {
                                    feedbackControlSignal.Set();
                                }
                            }
                            else if (frameType ==
                                    ViiperStreamFrameSpeakerPcm &&
                                activeStreamFrameVersion ==
                                    ViiperStreamFrameVersionV3 &&
                                payloadLength > 0 && payloadLength %
                                    (sizeof(short) * 2) == 0)
                            {
                                if (feedbackDispatchBuffer.TryEnqueueSpeaker(
                                    framedPayload, payloadLength,
                                    readStreamGeneration,
                                    FeedbackSpeakerKindPcm,
                                    Volatile.Read(ref lastInputDeviceIndex)))
                                {
                                    feedbackSpeakerSignal.Set();
                                }
                            }
                            else if (frameType ==
                                    ViiperStreamFrameAtomicAudioHaptics &&
                                activeStreamSupportsAtomicAudioHaptics &&
                                activeStreamFrameVersion ==
                                    ViiperStreamFrameVersionV5 &&
                                payloadLength >
                                    AtomicAudioHapticsFeedbackLengthPrefix)
                            {
                                int atomicFeedbackLength =
                                    BinaryPrimitives.ReadUInt16LittleEndian(
                                        framedPayload.AsSpan(0,
                                            AtomicAudioHapticsFeedbackLengthPrefix));
                                int speakerPcmLength = payloadLength -
                                    AtomicAudioHapticsFeedbackLengthPrefix -
                                    atomicFeedbackLength;
                                if (atomicFeedbackLength !=
                                        DualSenseCombinedExtendedFeedbackLength ||
                                    speakerPcmLength !=
                                        DualSenseV5SpeakerPcmLength)
                                {
                                    throw new IOException(
                                        "VIIPER returned a malformed V5 atomic audio/haptics payload.");
                                }
                                if (feedbackDispatchBuffer.TryEnqueueSpeaker(
                                        framedPayload, payloadLength,
                                        readStreamGeneration,
                                        FeedbackSpeakerKindAtomicAudioHaptics,
                                        Volatile.Read(ref lastInputDeviceIndex)))
                                {
                                    feedbackSpeakerSignal.Set();
                                }
                            }
                            else if (IsValidV5RealtimeHapticsFrame(
                                    activeStreamFrameVersion, frameType,
                                    payloadLength))
                            {
                                if (feedbackDispatchBuffer
                                        .TryEnqueueOrderedControl(
                                            framedPayload, payloadLength,
                                            readStreamGeneration,
                                            Volatile.Read(
                                                ref lastInputDeviceIndex)))
                                {
                                    feedbackControlSignal.Set();
                                }
                            }
                            else
                            {
                                throw new IOException(
                                    $"VIIPER returned an unsupported {activeStreamFrameVersion:X2}/{frameType:X2} framed feedback packet of {payloadLength} bytes.");
                            }
                        }
                        finally
                        {
                            feedbackDispatchGenerationBarrier.ExitReadLock();
                        }
                    }
                    else
                    {
                        stream.ReadExactly(buffer, 0, feedbackLength);
                        if (connected && readStreamGeneration ==
                            Volatile.Read(ref streamGeneration) &&
                            ReferenceEquals(deviceStream, stream))
                        {
                            ApplyFeedback(buffer, feedbackLength);
                        }
                    }
                }
            }
            catch (IOException)
            {
                if (connected &&
                    !TryRecoverStream("feedback reader stopped", readStreamGeneration))
                {
                    AppLogger.LogToGui($"VIIPER {viiperType} feedback reader stopped.", true);
                }
            }
            catch (SocketException)
            {
                if (connected &&
                    !TryRecoverStream("feedback reader stopped due to socket error",
                        readStreamGeneration))
                {
                    AppLogger.LogToGui($"VIIPER {viiperType} feedback reader stopped due to socket error.", true);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                lock (feedbackThreadLock)
                {
                    if (ReferenceEquals(feedbackThread, Thread.CurrentThread))
                    {
                        feedbackThread = null;
                    }
                }
            }
        }

        private void ApplyFeedback(byte[] feedback, int feedbackLength,
            int expectedDeviceIndex = -1)
        {
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if (expectedDeviceIndex >= 0 &&
                    expectedDeviceIndex != deviceIndex)
            {
                return;
            }

            ObserveLiveValidationFeedback(feedback, feedbackLength);
            if (
                deviceIndex < 0 ||
                Program.rootHub == null ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                !Global.EnableOutputDataToDS4[deviceIndex])
            {
                return;
            }

            DS4Device device = Program.rootHub.DS4Controllers[deviceIndex];
            if (device == null)
            {
                return;
            }

            // A compatibility sidecar exists only to carry PlayStation audio.
            // If an older VIIPER backend had to expose its neutral HID
            // interface too, do not let applications overwrite the primary
            // Xbox/Switch profile's rumble, lightbar, or trigger state. The
            // V4 atomic 0x36 carrier remains eligible because it is the audio
            // and haptics payload generated by the sidecar endpoint itself.
            if (audioOnlySidecar &&
                !(IsDualSenseType() &&
                    feedbackLength >= DualSenseCombinedExtendedFeedbackLength &&
                    feedback[DualSenseCombinedBluetoothReportOffset] == 0x36))
            {
                return;
            }

            switch (viiperType)
            {
                case ViiperVirtualDeviceType.Xbox360:
                    if (feedbackLength >= 2)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[0], feedback[1], deviceIndex);
                        ApplyGameRumbleTriggerVibration(device, deviceIndex,
                            feedback[1], feedback[0]);
                    }
                    break;

                case ViiperVirtualDeviceType.DualShock4:
                    if (feedbackLength >= 7)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[1], feedback[0], deviceIndex);
                        ApplyGameRumbleTriggerVibration(device, deviceIndex,
                            feedback[0], feedback[1]);
                        ApplyLightbar(device, feedback[2], feedback[3], feedback[4], feedback[5], feedback[6]);
                    }
                    break;

                case ViiperVirtualDeviceType.DualSense:
                case ViiperVirtualDeviceType.DualSenseEdge:
                    if (feedbackLength >= DualSenseBaseFeedbackLength)
                    {
                        bool nativeForwardingAllowed = IsNativeDualSenseFeedbackCompatible(device);
                        if (nativeForwardingAllowed &&
                            TryApplyBluetoothCombinedHapticsOutputReport(device, deviceIndex, feedback, feedbackLength))
                        {
                            break;
                        }

                        if (nativeForwardingAllowed &&
                            TryApplyBluetoothHapticsOutputReport(device,
                                deviceIndex, feedback, feedbackLength))
                        {
                            break;
                        }

                        if (nativeForwardingAllowed &&
                            TryApplyNativeDualSenseOutputReport(device, deviceIndex, feedback, feedbackLength))
                        {
                            break;
                        }

                        byte lightFast = feedback[1];
                        byte heavySlow = feedback[0];
                        if (device is not DualSenseDevice)
                        {
                            int hapticsReportOffset =
                                feedbackLength >= DualSenseCombinedExtendedFeedbackLength &&
                                feedback[DualSenseCombinedBluetoothReportOffset] == 0x36 ?
                                    DualSenseCombinedBluetoothReportOffset :
                                    DualSenseBluetoothHapticsReportOffset;
                            DualSenseHapticsTranslator.Translate(feedback, feedbackLength,
                                hapticsReportOffset, out lightFast, out heavySlow);
                        }

                        if (device is DualSenseDevice ||
                            ShouldApplyLegacyDualSenseRumble(device, lightFast,
                                heavySlow))
                        {
                            Program.rootHub.SetDevRumble(device, lightFast,
                                heavySlow, deviceIndex);
                        }
                        ApplyLightbar(device, feedback[2], feedback[3], feedback[4], 0, 0);
                        ApplyDualSenseTriggerFeedback(device, deviceIndex, feedback, feedbackLength);
                        ApplyGameRumbleTriggerVibration(device, deviceIndex,
                            lightFast, heavySlow);
                    }
                    break;

                case ViiperVirtualDeviceType.Switch2Pro:
                    if (feedbackLength >= 34)
                    {
                        byte left = MaxByte(feedback, 0, 16);
                        byte right = MaxByte(feedback, 16, 16);
                        Program.rootHub.SetDevRumble(device, left, right, deviceIndex);
                        ApplyGameRumbleTriggerVibration(device, deviceIndex,
                            right, left);
                    }
                    break;
            }
        }

        private bool IsDualSenseType()
        {
            return IsDualSenseVirtualType(viiperType);
        }

        private static bool IsDualSenseVirtualType(
            ViiperVirtualDeviceType type)
        {
            return type == ViiperVirtualDeviceType.DualSense ||
                type == ViiperVirtualDeviceType.DualSenseEdge;
        }

        private void UpdateBluetoothMicrophoneSource(int deviceIndex,
            long workerGeneration)
        {
            if (workerGeneration != Interlocked.Read(
                    ref microphoneWorkerGeneration))
            {
                return;
            }

            bool profileEnabled = connected &&
                deviceIndex >= 0 &&
                deviceIndex < Global.DualSenseEnableMicrophonePassthrough.Length &&
                Global.DualSenseEnableMicrophonePassthrough[deviceIndex];

            if (!profileEnabled || Program.rootHub == null ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length)
            {
                DetachBluetoothMicrophoneSource();
                return;
            }

            DS4Device source = Program.rootHub.DS4Controllers[deviceIndex];
            DualSenseDevice dualSenseSource = source as DualSenseDevice;
            bool validDualSense = dualSenseSource != null &&
                IsCurrentPhysicalSonyDualSense(dualSenseSource);
            bool validDualShock4 = source != null &&
                source.DeviceType == InputDeviceType.DS4 &&
                IsCurrentPhysicalSonyDualShock4(source);
            bool eligibleBluetoothSource =
                ControllerMicrophoneRoutePolicy.IsEligibleBluetoothSource(
                    source) && (validDualSense || validDualShock4);
            bool routeEligible =
                ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                    profileEnabled, eligibleBluetoothSource, outputType,
                    activeStreamSupportsMicrophone);
            bool requested = routeEligible &&
                Volatile.Read(ref virtualMicrophoneInterfaceActive) == 1;
            if (!requested)
            {
                if (profileEnabled &&
                    ControllerMicrophoneRoutePolicy
                        .SupportsVirtualMicrophoneOutput(outputType) &&
                    !activeStreamSupportsMicrophone &&
                    Interlocked.Exchange(ref microphoneUnavailableLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        $"VIIPER {viiperType} microphone input requires a microphone-capable VIIPER backend.",
                        true);
                }

                DetachBluetoothMicrophoneSource();
                return;
            }

            Volatile.Write(ref microphoneVolume,
                deviceIndex < Global.DualSenseMicrophoneVolume.Length ?
                    Global.DualSenseMicrophoneVolume[deviceIndex] : 128);
            Volatile.Write(ref microphoneNoiseSuppression,
                deviceIndex < Global.DualSenseMicrophoneNoiseSuppression.Length ?
                    Global.DualSenseMicrophoneNoiseSuppression[deviceIndex] :
                    (byte)DualSenseMicrophoneNoiseSuppression.Balanced);

            Volatile.Write(ref microphoneMuted,
                dualSenseSource?.IsProfileMicrophoneMuted == true ? 1 : 0);

            bool sourceAlreadyAttached;
            lock (microphoneSourceLock)
            {
                sourceAlreadyAttached = ReferenceEquals(microphoneSourceDevice, source);
            }
            if (sourceAlreadyAttached)
            {
                MaintainBluetoothMicrophoneStreaming(source,
                    workerGeneration);
                return;
            }

            DetachBluetoothMicrophoneSource();
            lock (microphoneControlTransitionLock)
            {
                if (!connected || workerGeneration != Interlocked.Read(
                        ref microphoneWorkerGeneration))
                {
                    return;
                }

                // A source that becomes active again supersedes every failed
                // disable from the same physical controller. The transition
                // lock orders a possible in-flight disable before the new
                // enable, so an old retry can never win afterwards.
                microphoneDisableRetries.Cancel(source);
                lock (microphoneSourceLock)
                {
                    microphoneSourceDevice = source;
                    if (source is DualSenseDevice attachedDualSense)
                    {
                        attachedDualSense.BluetoothMicrophoneOpusFrameReceived +=
                            BluetoothMicrophoneOpusFrameReceived;
                    }
                    else
                    {
                        source.BluetoothMicrophoneSbcFrameReceived +=
                            BluetoothMicrophoneSbcFrameReceived;
                    }
                }
            }

            ResetMicrophoneLiveness();
            Interlocked.Exchange(ref lastMicrophoneArmTimestamp, 0);
            Volatile.Write(ref lastMicrophoneRecoveryStage,
                (int)MicrophonePipelineHealthStage.None);
            MaintainBluetoothMicrophoneStreaming(source, workerGeneration);
        }

        private void MaintainBluetoothMicrophoneStreaming(DS4Device source,
            long workerGeneration)
        {
            long now = Stopwatch.GetTimestamp();
            long lastCompressedRx = Interlocked.Read(
                ref lastMicrophoneCompressedRxTimestamp);
            long lastProcessed = Interlocked.Read(
                ref lastMicrophoneProcessedTimestamp);
            long lastSubmitted = Interlocked.Read(
                ref lastMicrophoneSubmittedTimestamp);
            long lastArm = Interlocked.Read(ref lastMicrophoneArmTimestamp);
            long oneSecond = Stopwatch.Frequency;
            // The known-good DualSense bridges resend the silent 0x36 control
            // report at roughly 4 Hz until microphone packets begin. Once the
            // pipeline is healthy no keepalive is needed; a later one-second
            // receive stall re-enters this fast arming cadence.
            long retryPeriod = Stopwatch.Frequency / 4;
            MicrophonePipelineHealthStage healthStage =
                MicrophonePipelineHealth.Classify(now, oneSecond,
                    lastCompressedRx, lastProcessed, lastSubmitted,
                    hasArmedSource: lastArm != 0);
            LogMicrophoneHealthIfNeeded(source, now, healthStage,
                lastCompressedRx, lastProcessed, lastSubmitted);
            if (healthStage == MicrophonePipelineHealthStage.Healthy)
            {
                return;
            }
            if (lastArm != 0 && now - lastArm < retryPeriod)
            {
                return;
            }

            if (healthStage != MicrophonePipelineHealthStage.Starting)
            {
                RecordMicrophoneRecovery(healthStage);
                ResetMicrophonePipelineAfterStall(
                    preserveCompressedRxLiveness: healthStage !=
                        MicrophonePipelineHealthStage.PhysicalReceiveStalled);
                EnsureMicrophoneWriterAlive();
                microphoneWriterSignal.Set();
            }
            bool attempted = false;
            bool armed = false;
            lock (microphoneControlTransitionLock)
            {
                lock (microphoneSourceLock)
                {
                    attempted = connected &&
                        workerGeneration == Interlocked.Read(
                            ref microphoneWorkerGeneration) &&
                        ReferenceEquals(microphoneSourceDevice, source) &&
                        !source.IsRemoved && !source.IsDisconnecting;
                }

                if (attempted)
                {
                    microphoneDisableRetries.Cancel(source);
                    Interlocked.Exchange(ref lastMicrophoneArmTimestamp, now);
                    Interlocked.Increment(ref microphoneArmAttempts);
                    try
                    {
                        armed = SetPhysicalBluetoothMicrophoneStreaming(source,
                            enabled: true);
                    }
                    catch
                    {
                        armed = false;
                    }
                }
            }

            if (!attempted)
            {
                return;
            }
            if (!armed)
            {
                Interlocked.Increment(ref microphoneArmFailures);
            }
        }

        private void ResetTriggerLabRumbleState()
        {
            triggerLabRumbleStateKnown = false;
            lastTriggerLabLeftRumble = 0;
            lastTriggerLabRightRumble = 0;
            lastTriggerLabRumbleSignature = 0;
            lastTriggerLabLeftRumbleEnabled = false;
            lastTriggerLabRightRumbleEnabled = false;
        }

        private void ApplyGameRumbleTriggerVibration(DS4Device device,
            int deviceIndex, byte lightFast, byte heavySlow)
        {
            if (device is not DualSenseDevice dualSenseDevice ||
                !IsCurrentPhysicalSonyDualSense(dualSenseDevice))
            {
                return;
            }

            TriggerLabProfileSettings settings =
                TriggerLabForDevice(deviceIndex);
            bool leftEnabled = settings?.Enabled == true &&
                settings.LeftGameRumbleVibration;
            bool rightEnabled = settings?.Enabled == true &&
                settings.RightGameRumbleVibration;
            int signature = TriggerLabRumbleSignature(settings);
            if (triggerLabRumbleStateKnown &&
                lastTriggerLabLeftRumble == heavySlow &&
                lastTriggerLabRightRumble == lightFast &&
                lastTriggerLabRumbleSignature == signature &&
                lastTriggerLabLeftRumbleEnabled == leftEnabled &&
                lastTriggerLabRightRumbleEnabled == rightEnabled)
            {
                return;
            }

            bool restoreLeft = lastTriggerLabLeftRumbleEnabled &&
                !leftEnabled;
            bool restoreRight = lastTriggerLabRightRumbleEnabled &&
                !rightEnabled;
            triggerLabRumbleStateKnown = true;
            lastTriggerLabLeftRumble = heavySlow;
            lastTriggerLabRightRumble = lightFast;
            lastTriggerLabRumbleSignature = signature;
            lastTriggerLabLeftRumbleEnabled = leftEnabled;
            lastTriggerLabRightRumbleEnabled = rightEnabled;

            if (leftEnabled)
            {
                TriggerLabEffectEncoder.ApplyGameRumbleToDevice(
                    dualSenseDevice, TriggerId.LeftTrigger, settings.Left,
                    settings.LeftActive, heavySlow);
            }
            else if (restoreLeft)
            {
                TriggerLabEffectEncoder.ApplyToDevice(dualSenseDevice,
                    TriggerId.LeftTrigger, settings?.Left,
                    settings?.Enabled == true && settings.LeftActive);
            }

            if (rightEnabled)
            {
                TriggerLabEffectEncoder.ApplyGameRumbleToDevice(
                    dualSenseDevice, TriggerId.RightTrigger, settings.Right,
                    settings.RightActive, lightFast);
            }
            else if (restoreRight)
            {
                TriggerLabEffectEncoder.ApplyToDevice(dualSenseDevice,
                    TriggerId.RightTrigger, settings?.Right,
                    settings?.Enabled == true && settings.RightActive);
            }
        }

        private static int TriggerLabRumbleSignature(
            TriggerLabProfileSettings settings)
        {
            if (settings == null)
            {
                return 0;
            }

            HashCode hash = new HashCode();
            hash.Add(settings.Enabled);
            hash.Add(settings.LeftActive);
            hash.Add(settings.RightActive);
            hash.Add(settings.LeftGameRumbleVibration);
            hash.Add(settings.RightGameRumbleVibration);
            AddTriggerLabEffectSignature(ref hash, settings.Left);
            AddTriggerLabEffectSignature(ref hash, settings.Right);
            return hash.ToHashCode();
        }

        private static void AddTriggerLabEffectSignature(ref HashCode hash,
            TriggerLabEffectSettings effect)
        {
            hash.Add(effect?.Mode ?? TriggerLabMode.Feedback);
            hash.Add(effect?.StartPercent ?? 0);
            hash.Add(effect?.WallPercent ?? 0);
            hash.Add(effect?.ForcePercent ?? 0);
        }

        private bool ShouldApplyLegacyDualSenseRumble(DS4Device device,
            byte lightFast, byte heavySlow)
        {
            lock (legacyDualSenseRumbleLock)
            {
                bool changed = !legacyDualSenseRumbleKnown ||
                    !ReferenceEquals(legacyDualSenseRumbleDevice, device) ||
                    legacyDualSenseLightFast != lightFast ||
                    legacyDualSenseHeavySlow != heavySlow;
                if (changed)
                {
                    legacyDualSenseRumbleDevice = device;
                    legacyDualSenseLightFast = lightFast;
                    legacyDualSenseHeavySlow = heavySlow;
                    legacyDualSenseRumbleKnown = true;
                }

                return changed;
            }
        }

        private void ResetLegacyDualSenseRumbleDeduplication()
        {
            lock (legacyDualSenseRumbleLock)
            {
                legacyDualSenseRumbleDevice = null;
                legacyDualSenseLightFast = 0;
                legacyDualSenseHeavySlow = 0;
                legacyDualSenseRumbleKnown = false;
            }
        }

        private void ResetMicrophonePipelineAfterStall(
            bool preserveCompressedRxLiveness)
        {
            lock (microphoneQueueLock)
            {
                pendingMicrophoneFrames.Clear();
            }
            lock (microphoneProcessingLock)
            {
                microphoneDecoder = null;
                ResetDualShock4MicrophoneDecodeState(
                    preserveSequence: false);
                microphoneSbcDecoder = null;
                microphoneProcessor.Reset();
            }
            if (!preserveCompressedRxLiveness)
            {
                Interlocked.Exchange(ref lastMicrophoneCompressedRxTimestamp,
                    0);
            }
            Interlocked.Exchange(ref lastMicrophoneProcessedTimestamp, 0);
            Interlocked.Exchange(ref lastMicrophoneSubmittedTimestamp, 0);
        }

        private void LogMicrophoneHealthIfNeeded(DS4Device source, long now,
            MicrophonePipelineHealthStage healthStage,
            long lastCompressedRx, long lastProcessed, long lastSubmitted)
        {
            if (!Global.VerboseStartupLogging ||
                DateTime.UtcNow - lastMicrophoneHealthLogUtc < TimeSpan.FromSeconds(5))
            {
                return;
            }

            lastMicrophoneHealthLogUtc = DateTime.UtcNow;
            string compressedRxAge = FormatMicrophoneLivenessAge(now,
                lastCompressedRx);
            string processedAge = FormatMicrophoneLivenessAge(now,
                lastProcessed);
            string submittedAge = FormatMicrophoneLivenessAge(now,
                lastSubmitted);
            DualSenseDevice dualSenseSource = source as DualSenseDevice;
            int rejectedTag = dualSenseSource?.BluetoothLastRejectedInputTag ?? -1;
            string rejectedTagText = rejectedTag < 0 ? "none" : $"0x{rejectedTag:X2}";
            long physicalFrames = dualSenseSource?.BluetoothMicrophoneFramesReceived ??
                source.DualShock4BluetoothMicrophoneFramesReceived;
            long rejectedInputs = dualSenseSource?.BluetoothRejectedInputFrames ?? 0;
            int microphoneQueueDepth;
            lock (microphoneQueueLock)
            {
                microphoneQueueDepth = pendingMicrophoneFrames.Count;
            }
            string armStatus = dualSenseSource?.LastBluetoothMicrophoneWriteStatus ??
                source.LastBluetoothAudioWriteStatus;
            ViiperMicrophoneBufferSnapshot virtualBuffer = Volatile.Read(
                ref virtualMicrophoneBufferSnapshot);
            AppLogger.LogToGui(
                $"VIIPER {viiperType} microphone stats: streamV2={activeStreamUsesFramedProtocol} " +
                $"interfaceKnown={Volatile.Read(ref virtualMicrophoneInterfaceStateKnown) == 1} " +
                $"interfaceActive={Volatile.Read(ref virtualMicrophoneInterfaceActive) == 1} " +
                $"healthStage={MicrophonePipelineHealth.GetDisplayName(healthStage)} " +
                $"lastRecoveryStage={MicrophonePipelineHealth.GetDisplayName((MicrophonePipelineHealthStage)Volatile.Read(ref lastMicrophoneRecoveryStage))} " +
                $"armAttempts={Interlocked.Read(ref microphoneArmAttempts)} " +
                $"armFailures={Interlocked.Read(ref microphoneArmFailures)} " +
                $"physicalFrames={physicalFrames} " +
                $"compressedFrames={Interlocked.Read(ref microphoneCompressedFramesReceived)} " +
                $"opusFrames={Interlocked.Read(ref microphoneOpusFramesReceived)} " +
                $"sbcFrames={Interlocked.Read(ref microphoneSbcFramesReceived)} " +
                $"decodedFrames={Interlocked.Read(ref microphoneFramesDecoded)} " +
                $"processedFrames={Interlocked.Read(ref microphoneFramesProcessed)} " +
                $"submittedFrames={Interlocked.Read(ref microphoneFramesSubmitted)} " +
                $"submitGapsObserved={microphoneTelemetry.ObservedSubmissionGaps} " +
                $"submitGapLastMs={StopwatchTicksToMilliseconds(microphoneTelemetry.LastSubmissionGapTicks):F2} " +
                $"submitGapMaxMs={StopwatchTicksToMilliseconds(microphoneTelemetry.MaximumSubmissionGapTicks):F2} " +
                $"preProcessorZeroFrames={microphoneTelemetry.PreProcessorAllZeroFrames} " +
                $"postProcessorZeroFrames={microphoneTelemetry.PostProcessorAllZeroFrames} " +
                $"postProcessorZeroUnmutedFrames={microphoneTelemetry.PostProcessorAllZeroUnmutedFrames} " +
                $"preProcessorPeak={microphoneTelemetry.PreProcessorPeak} " +
                $"postProcessorPeak={microphoneTelemetry.PostProcessorPeak} " +
                $"queueDepth={microphoneQueueDepth} " +
                $"queueHighWater={microphoneTelemetry.CompressedQueueHighWaterMark} " +
                $"queueDrops={Interlocked.Read(ref microphoneFramesDropped)} " +
                $"decodeFailures={Interlocked.Read(ref microphoneDecodeFailures)} " +
                $"sequenceGaps={Interlocked.Read(ref microphoneSequenceGaps)} " +
                $"concealedFrames={Interlocked.Read(ref microphoneConcealedFrames)} " +
                $"duplicateFrames={Interlocked.Read(ref microphoneDuplicateFrames)} " +
                $"outOfOrderFrames={Interlocked.Read(ref microphoneOutOfOrderFrames)} " +
                $"discontinuities={Interlocked.Read(ref microphoneDiscontinuities)} " +
                $"stageRecoveries={Interlocked.Read(ref microphonePhysicalReceiveRecoveries)}/" +
                    $"{Interlocked.Read(ref microphoneDecodeProcessRecoveries)}/" +
                    $"{Interlocked.Read(ref microphoneVirtualSubmissionRecoveries)} " +
                $"pcmFifoSamples={dualShock4DecodedPcmFifoCount} " +
                $"rejectedInputs={rejectedInputs} " +
                $"lastRejectedTag={rejectedTagText} " +
                $"compressedRxAge={compressedRxAge} " +
                $"processedAge={processedAge} submittedAge={submittedAge} " +
                $"{virtualBuffer.ToLogFields()} " +
                $"armStatus=\"{armStatus}\"",
                false);
        }

        private static string FormatMicrophoneLivenessAge(long now,
            long timestamp)
        {
            return timestamp == 0 ? "never" :
                $"{Math.Max(0, (now - timestamp) * 1000 /
                    Stopwatch.Frequency)}ms";
        }

        private void RecordMicrophoneRecovery(
            MicrophonePipelineHealthStage stage)
        {
            Volatile.Write(ref lastMicrophoneRecoveryStage, (int)stage);
            switch (stage)
            {
                case MicrophonePipelineHealthStage.PhysicalReceiveStalled:
                    Interlocked.Increment(
                        ref microphonePhysicalReceiveRecoveries);
                    break;
                case MicrophonePipelineHealthStage.DecodeOrProcessStalled:
                    Interlocked.Increment(
                        ref microphoneDecodeProcessRecoveries);
                    break;
                case MicrophonePipelineHealthStage.VirtualSubmissionStalled:
                    Interlocked.Increment(
                        ref microphoneVirtualSubmissionRecoveries);
                    break;
            }
        }

        private void ResetMicrophoneLiveness()
        {
            Interlocked.Exchange(ref lastMicrophoneCompressedRxTimestamp, 0);
            Interlocked.Exchange(ref lastMicrophoneProcessedTimestamp, 0);
            Interlocked.Exchange(ref lastMicrophoneSubmittedTimestamp, 0);
        }

        private void DetachBluetoothMicrophoneSource()
        {
            DS4Device source = null;
            bool resetProcessor = false;
            bool retainDisableRetry = false;
            long workerGeneration = Interlocked.Read(
                ref microphoneWorkerGeneration);
            lock (microphoneControlTransitionLock)
            {
                lock (microphoneSourceLock)
                {
                    if (microphoneSourceDevice != null)
                    {
                        source = microphoneSourceDevice;
                        if (source is DualSenseDevice dualSenseSource)
                        {
                            dualSenseSource.BluetoothMicrophoneOpusFrameReceived -=
                                BluetoothMicrophoneOpusFrameReceived;
                        }
                        else
                        {
                            source.BluetoothMicrophoneSbcFrameReceived -=
                                BluetoothMicrophoneSbcFrameReceived;
                        }
                        microphoneSourceDevice = null;
                        resetProcessor = true;
                    }
                }

                if (source != null)
                {
                    retainDisableRetry = connected &&
                        workerGeneration == Interlocked.Read(
                            ref microphoneWorkerGeneration) &&
                        !source.IsRemoved && !source.IsDisconnecting;
                    if (retainDisableRetry)
                    {
                        microphoneDisableRetries.Schedule(source,
                            workerGeneration, Stopwatch.GetTimestamp());
                    }
                    else
                    {
                        // Output teardown has no monitor left to service a
                        // retry. Preserve the former best-effort final attempt;
                        // physical controller removal owns its own final output
                        // shutdown barrier.
                        try
                        {
                            SetPhysicalBluetoothMicrophoneStreaming(source,
                                enabled: false);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            lock (microphoneQueueLock)
            {
                resetProcessor |= pendingMicrophoneFrames.Count > 0;
                pendingMicrophoneFrames.Clear();
            }
            lock (microphoneProcessingLock)
            {
                resetProcessor |= microphoneDecoder != null ||
                    microphoneSbcDecoder != null ||
                    dualShock4DecodedPcmFifoCount > 0 ||
                    dualShock4MicrophoneSequenceKnown;
                microphoneDecoder = null;
                ResetDualShock4MicrophoneDecodeState(
                    preserveSequence: false);
                microphoneSbcDecoder = null;
                if (resetProcessor)
                {
                    microphoneProcessor.Reset();
                }
            }
            ResetMicrophoneLiveness();
            microphoneTelemetry.ResetSubmissionBaseline();
            Interlocked.Exchange(ref lastMicrophoneArmTimestamp, 0);
            Volatile.Write(ref lastMicrophoneRecoveryStage,
                (int)MicrophonePipelineHealthStage.None);
            Volatile.Write(ref microphoneMuted, 0);

            if (retainDisableRetry)
            {
                MaintainPendingBluetoothMicrophoneDisables(workerGeneration);
            }
        }

        private void MaintainPendingBluetoothMicrophoneDisables(
            long workerGeneration)
        {
            if (!connected || workerGeneration != Interlocked.Read(
                    ref microphoneWorkerGeneration))
            {
                return;
            }

            microphoneDisableRetries.DiscardOtherGenerations(
                workerGeneration);

            long retryTicks = Math.Max(1,
                Stopwatch.Frequency * MicrophoneDisableRetryMilliseconds /
                    1000);
            lock (microphoneControlTransitionLock)
            {
                if (!connected || workerGeneration != Interlocked.Read(
                        ref microphoneWorkerGeneration) ||
                    !microphoneDisableRetries.TryBeginAttempt(
                        workerGeneration, Stopwatch.GetTimestamp(), retryTicks,
                        out MicrophoneDisableRetryTracker<DS4Device>.Attempt
                            attempt))
                {
                    return;
                }

                bool sourceReactivated;
                lock (microphoneSourceLock)
                {
                    sourceReactivated = ReferenceEquals(
                        microphoneSourceDevice, attempt.Target);
                }

                if (sourceReactivated)
                {
                    microphoneDisableRetries.Cancel(attempt.Target);
                    return;
                }

                if (attempt.Target.IsRemoved ||
                    attempt.Target.IsDisconnecting ||
                    attempt.Target.ConnectionType != ConnectionType.BT)
                {
                    microphoneDisableRetries.CompleteAttempt(attempt,
                        succeeded: true);
                    return;
                }

                bool disabled;
                try
                {
                    disabled = SetPhysicalBluetoothMicrophoneStreaming(
                        attempt.Target, enabled: false);
                }
                catch
                {
                    disabled = false;
                }

                microphoneDisableRetries.CompleteAttempt(attempt, disabled,
                    nextAttemptTimestamp: Stopwatch.GetTimestamp() +
                        retryTicks);
            }
        }

        private static bool SetPhysicalBluetoothMicrophoneStreaming(
            DS4Device source, bool enabled)
        {
            return source is DualSenseDevice dualSenseSource ?
                dualSenseSource.SetBluetoothMicrophoneStreaming(enabled) :
                source.SetDualShock4BluetoothMicrophoneStreaming(enabled);
        }

        private void BluetoothMicrophoneOpusFrameReceived(DualSenseDevice source,
            byte[] opusFrame)
        {
            if (opusFrame == null || opusFrame.Length != DualSenseMicrophoneOpusFrameLength)
            {
                return;
            }

            lock (microphoneSourceLock)
            {
                if (!connected || !ReferenceEquals(source, microphoneSourceDevice))
                {
                    return;
                }

                Interlocked.Increment(ref microphoneCompressedFramesReceived);
                Interlocked.Increment(ref microphoneOpusFramesReceived);
                Interlocked.Exchange(ref lastMicrophoneCompressedRxTimestamp,
                    Stopwatch.GetTimestamp());
                byte[] copy = new byte[DualSenseMicrophoneOpusFrameLength];
                Buffer.BlockCopy(opusFrame, 0, copy, 0, copy.Length);
                lock (microphoneQueueLock)
                {
                    while (pendingMicrophoneFrames.Count >= MaxPendingMicrophoneFrames)
                    {
                        pendingMicrophoneFrames.Dequeue();
                        Interlocked.Increment(ref microphoneFramesDropped);
                    }
                    pendingMicrophoneFrames.Enqueue(new PendingMicrophoneFrame(
                        MicrophoneCodec.Opus, copy));
                    microphoneTelemetry.ObserveCompressedQueueDepth(
                        pendingMicrophoneFrames.Count);
                }
            }

            EnsureMicrophoneWriterAlive();
            microphoneWriterSignal.Set();
        }

        private void BluetoothMicrophoneSbcFrameReceived(DS4Device source,
            ushort sequence, byte[] sbcFrame)
        {
            if (sbcFrame == null || sbcFrame.Length < SbcFrame.HeaderSize)
            {
                return;
            }

            lock (microphoneSourceLock)
            {
                if (!connected || !ReferenceEquals(source, microphoneSourceDevice))
                {
                    return;
                }

                Interlocked.Increment(ref microphoneCompressedFramesReceived);
                Interlocked.Increment(ref microphoneSbcFramesReceived);
                Interlocked.Exchange(ref lastMicrophoneCompressedRxTimestamp,
                    Stopwatch.GetTimestamp());
                lock (microphoneQueueLock)
                {
                    while (pendingMicrophoneFrames.Count >= MaxPendingMicrophoneFrames)
                    {
                        pendingMicrophoneFrames.Dequeue();
                        Interlocked.Increment(ref microphoneFramesDropped);
                    }
                    pendingMicrophoneFrames.Enqueue(new PendingMicrophoneFrame(
                        MicrophoneCodec.Sbc, sbcFrame, sequence,
                        hasSequence: true));
                    microphoneTelemetry.ObserveCompressedQueueDepth(
                        pendingMicrophoneFrames.Count);
                }
            }

            EnsureMicrophoneWriterAlive();
            microphoneWriterSignal.Set();
        }

        private void ApplyDualSenseTriggerFeedback(DS4Device device, int deviceIndex,
            byte[] feedback, int feedbackLength)
        {
            if (feedbackLength < DualSenseExtendedFeedbackLength ||
                device is not DualSenseDevice dualSenseDevice ||
                !IsCurrentPhysicalSonyDualSense(dualSenseDevice))
            {
                return;
            }

            int r2Offset = DualSenseTriggerFeedbackOffset;
            int l2Offset = DualSenseTriggerFeedbackOffset + DualSenseTriggerEffectLength;
            TriggerLabProfileSettings triggerLab = TriggerLabForDevice(deviceIndex);
            bool r2Changed = !TriggerFeedbackEquals(feedback, r2Offset, lastR2TriggerFeedback);
            bool l2Changed = !TriggerFeedbackEquals(feedback, l2Offset, lastL2TriggerFeedback);

            if (r2Changed)
            {
                CopyTriggerFeedback(feedback, r2Offset, lastR2TriggerFeedback);
                if (triggerLab?.HasActiveOverride == true && triggerLab.RightActive)
                    TriggerLabEffectEncoder.ApplyToDevice(dualSenseDevice,
                        TriggerId.RightTrigger, triggerLab.Right, true);
                else
                    ApplyRawTriggerEffect(dualSenseDevice, TriggerId.RightTrigger, feedback, r2Offset);
            }

            if (l2Changed)
            {
                CopyTriggerFeedback(feedback, l2Offset, lastL2TriggerFeedback);
                if (triggerLab?.HasActiveOverride == true && triggerLab.LeftActive)
                    TriggerLabEffectEncoder.ApplyToDevice(dualSenseDevice,
                        TriggerId.LeftTrigger, triggerLab.Left, true);
                else
                    ApplyRawTriggerEffect(dualSenseDevice, TriggerId.LeftTrigger, feedback, l2Offset);
            }
        }

        private static void ApplyRawTriggerEffect(DualSenseDevice device, TriggerId trigger, byte[] feedback, int offset)
        {
            device.PrepareRawTriggerEffect(trigger,
                feedback[offset],
                feedback[offset + 1],
                feedback[offset + 2],
                feedback[offset + 3],
                feedback[offset + 4],
                feedback[offset + 5],
                feedback[offset + 6],
                feedback[offset + 9]);
        }

        private static bool TryApplyNativeDualSenseOutputReport(DS4Device device, int deviceIndex, byte[] feedback, int feedbackLength)
        {
            if (feedbackLength < DualSenseBluetoothHapticsReportOffset ||
                device is not DualSenseDevice dualSenseDevice ||
                feedback[DualSenseNativeOutputReportOffset] != 0x02)
            {
                return false;
            }

            byte[] report = PrepareNativeDualSenseOutputReportForProfile(feedback,
                deviceIndex);
            return dualSenseDevice.WriteRawOutputReportFromGame(report,
                0,
                DualSenseNativeOutputReportLength);
        }

        private static byte[] PrepareNativeDualSenseOutputReportForProfile(byte[] feedback,
            int deviceIndex)
        {
            byte[] report = new byte[DualSenseNativeOutputReportLength];
            Array.Copy(feedback, DualSenseNativeOutputReportOffset, report, 0, report.Length);

            // Keep game rumble, adaptive triggers, lightbar, and player LEDs.
            // DS4Windows owns the mute button LED/mic mute state so profile
            // mute actions cannot get stuck behind game output reports.
            if (report.Length > 10)
            {
                report[2] &= 0xFC;
                report[9] = 0x00;
                report[10] = 0x00;
            }

            ApplyTriggerLabNativeOverrides(report, 1, 11, 22,
                TriggerLabForDevice(deviceIndex), feedback[1], feedback[0]);

            return report;
        }

        private static bool TryApplyBluetoothHapticsOutputReport(DS4Device device,
            int deviceIndex, byte[] feedback, int feedbackLength)
        {
            if (feedbackLength < DualSenseExtendedFeedbackLength ||
                device is not DualSenseDevice dualSenseDevice ||
                feedback[DualSenseBluetoothHapticsReportOffset] != 0x32)
            {
                return false;
            }

            Program.rootHub?.ApplyAudioHapticsToGameReport(deviceIndex,
                feedback, DualSenseBluetoothHapticsReportOffset + 13, 64);

            return dualSenseDevice.WriteBluetoothHapticsOutputReport(feedback,
                DualSenseBluetoothHapticsReportOffset,
                DualSenseBluetoothHapticsReportLength);
        }

        private bool TryApplyBluetoothCombinedHapticsOutputReport(
            DS4Device device, int deviceIndex, byte[] feedback, int feedbackLength)
        {
            if (feedbackLength < DualSenseCombinedExtendedFeedbackLength ||
                device is not DualSenseDevice dualSenseDevice ||
                feedback[DualSenseCombinedBluetoothReportOffset] != 0x36)
            {
                return false;
            }

            // The dispatch buffer owns this frame until ApplyFeedback returns,
            // so patching it in place avoids a managed allocation on every
            // combined audio/HID feedback packet.
            byte[] report = feedback;
            int reportOffset = DualSenseCombinedBluetoothReportOffset;
            Program.rootHub?.ApplyAudioHapticsToGameReport(deviceIndex,
                report, reportOffset + 78, 64);
            if (!audioOnlySidecar)
            {
                int stateOffset = reportOffset + 13;
                ApplyTriggerLabNativeOverrides(report, stateOffset,
                    stateOffset + 10, stateOffset + 21,
                    TriggerLabForDevice(deviceIndex), feedback[1],
                    feedback[0]);
            }

            return dualSenseDevice.WriteBluetoothCombinedHapticsAudioOutputReport(report,
                DualSenseCombinedBluetoothReportOffset,
                DualSenseCombinedBluetoothReportLength);
        }

        private static void ApplyTriggerLabNativeOverrides(byte[] report,
            int flagsOffset, int rightTriggerOffset, int leftTriggerOffset,
            TriggerLabProfileSettings triggerLab, byte lightFast,
            byte heavySlow)
        {
            if (triggerLab?.Enabled != true)
            {
                return;
            }

            bool rightPersistent = triggerLab.RightActive;
            bool rightRumble = triggerLab.RightGameRumbleVibration;
            if (rightPersistent || rightRumble)
            {
                report[flagsOffset] |= 0x04;
                if (rightRumble)
                {
                    TriggerLabEffectEncoder.WriteGameRumbleNativeBlock(
                        report, rightTriggerOffset, triggerLab.Right,
                        rightPersistent, lightFast);
                }
                else
                {
                    TriggerLabEffectEncoder.WriteNativeBlock(report,
                        rightTriggerOffset, triggerLab.Right, true);
                }
            }

            bool leftPersistent = triggerLab.LeftActive;
            bool leftRumble = triggerLab.LeftGameRumbleVibration;
            if (leftPersistent || leftRumble)
            {
                report[flagsOffset] |= 0x08;
                if (leftRumble)
                {
                    TriggerLabEffectEncoder.WriteGameRumbleNativeBlock(
                        report, leftTriggerOffset, triggerLab.Left,
                        leftPersistent, heavySlow);
                }
                else
                {
                    TriggerLabEffectEncoder.WriteNativeBlock(report,
                        leftTriggerOffset, triggerLab.Left, true);
                }
            }
        }

        private static TriggerLabProfileSettings TriggerLabForDevice(int deviceIndex)
        {
            if (deviceIndex < 0 || deviceIndex >= Global.TEST_PROFILE_ITEM_COUNT)
                return null;
            return Global.store.triggerLabSettings[deviceIndex];
        }

        private bool IsNativeDualSenseFeedbackCompatible(DS4Device device)
        {
            if (device is not DualSenseDevice dualSenseDevice ||
                !IsCurrentPhysicalSonyDualSense(dualSenseDevice))
            {
                return false;
            }

            if (viiperType != ViiperVirtualDeviceType.DualSenseEdge ||
                dualSenseDevice.SubType == DualSenseDevice.DeviceSubType.DSEdge)
            {
                return true;
            }

            if (Interlocked.Exchange(ref edgePhysicalMismatchLogged, 1) == 0)
            {
                AppLogger.LogToGui("VIIPER DualSense Edge native feedback is not being forwarded to a physical non-Edge DualSense. Use DualSense output for normal DualSense controllers, or connect a DualSense Edge for Edge native feedback.", true);
            }

            return false;
        }

        private bool IsCurrentPhysicalSonyDualSense(DualSenseDevice device)
        {
            if (!IsGenuineSonyDualSense(device))
            {
                return false;
            }

            string devicePath = device.HidDevice.DevicePath ?? string.Empty;
            lock (physicalDualSenseIdentityLock)
            {
                if (string.Equals(devicePath, physicalDualSenseIdentityPath, StringComparison.OrdinalIgnoreCase))
                {
                    return physicalDualSenseIdentityVerified;
                }
            }

            bool isPhysical;
            try
            {
                isPhysical = !DS4Devices.IsOwnVirtualDevice(devicePath) &&
                    !Global.CheckIfVirtualDevice(devicePath);
            }
            catch
            {
                // Treat an unverified controller as ineligible for raw output.
                // Generic rumble remains available through the normal fallback.
                isPhysical = false;
            }

            lock (physicalDualSenseIdentityLock)
            {
                physicalDualSenseIdentityPath = devicePath;
                physicalDualSenseIdentityVerified = isPhysical;
            }

            return isPhysical;
        }

        private static bool IsGenuineSonyDualSense(DualSenseDevice device)
        {
            if (device?.HidDevice?.Attributes == null)
            {
                return false;
            }

            int vendorId = device.HidDevice.Attributes.VendorId;
            int productId = device.HidDevice.Attributes.ProductId;
            return vendorId == DS4Devices.SONY_VID && (productId == 0x0CE6 || productId == 0x0DF2);
        }

        private bool IsCurrentPhysicalSonyDualShock4(DS4Device device)
        {
            if (!IsGenuineSonyDualShock4(device))
            {
                return false;
            }

            string devicePath = device.HidDevice.DevicePath ?? string.Empty;
            lock (physicalDualSenseIdentityLock)
            {
                if (string.Equals(devicePath, physicalDualSenseIdentityPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return physicalDualSenseIdentityVerified;
                }
            }

            bool isPhysical;
            try
            {
                isPhysical = !DS4Devices.IsOwnVirtualDevice(devicePath) &&
                    !Global.CheckIfVirtualDevice(devicePath);
            }
            catch
            {
                isPhysical = false;
            }

            lock (physicalDualSenseIdentityLock)
            {
                physicalDualSenseIdentityPath = devicePath;
                physicalDualSenseIdentityVerified = isPhysical;
            }

            return isPhysical;
        }

        private static bool IsGenuineSonyDualShock4(DS4Device device)
        {
            if (device?.HidDevice?.Attributes == null ||
                device.DeviceType != InputDeviceType.DS4)
            {
                return false;
            }

            int vendorId = device.HidDevice.Attributes.VendorId;
            int productId = device.HidDevice.Attributes.ProductId;
            return vendorId == DS4Devices.SONY_VID &&
                (productId == 0x05C4 || productId == 0x09CC);
        }

        public static bool ApplySyntheticDualSenseTriggerFeedback(int deviceIndex, bool rightTrigger, byte mode,
            byte startResistance, byte effectForce, byte rangeForce, byte nearReleaseStrength,
            byte nearMiddleStrength, byte pressedStrength, byte frequency)
        {
            if (Program.rootHub == null ||
                deviceIndex < 0 ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                Program.rootHub.DS4Controllers[deviceIndex] is not DualSenseDevice dualSenseDevice ||
                !IsGenuineSonyDualSense(dualSenseDevice))
            {
                return false;
            }

            byte[] feedback = new byte[DualSenseExtendedFeedbackLength];
            int offset = DualSenseTriggerFeedbackOffset +
                (rightTrigger ? 0 : DualSenseTriggerEffectLength);
            feedback[offset] = mode;
            feedback[offset + 1] = startResistance;
            feedback[offset + 2] = effectForce;
            feedback[offset + 3] = rangeForce;
            feedback[offset + 4] = nearReleaseStrength;
            feedback[offset + 5] = nearMiddleStrength;
            feedback[offset + 6] = pressedStrength;
            feedback[offset + 9] = frequency;

            ApplyRawTriggerEffect(dualSenseDevice,
                rightTrigger ? TriggerId.RightTrigger : TriggerId.LeftTrigger,
                feedback,
                offset);
            return true;
        }

        public static bool ResetSyntheticDualSenseTriggerFeedback(int deviceIndex, bool rightTrigger)
        {
            return ApplySyntheticDualSenseTriggerFeedback(deviceIndex, rightTrigger,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
        }

        public static bool PlaySyntheticDualSenseHapticsTone(int deviceIndex)
        {
            if (Program.rootHub == null ||
                deviceIndex < 0 ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                Program.rootHub.DS4Controllers[deviceIndex] is not DualSenseDevice dualSenseDevice ||
                !IsGenuineSonyDualSense(dualSenseDevice))
            {
                return false;
            }

            return dualSenseDevice.PlayBluetoothHapticsTestTone();
        }

        private static bool TriggerFeedbackEquals(byte[] source, int sourceOffset, byte[] previous)
        {
            for (int i = 0; i < DualSenseTriggerEffectLength; i++)
            {
                if (source[sourceOffset + i] != previous[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void CopyTriggerFeedback(byte[] source, int sourceOffset, byte[] destination)
        {
            Array.Copy(source, sourceOffset, destination, 0, DualSenseTriggerEffectLength);
        }

        private static void ApplyLightbar(DS4Device device, byte red, byte green, byte blue, byte flashOn, byte flashOff)
        {
            DS4LightbarState lightState = new DS4LightbarState
            {
                LightBarColor = new DS4Color(red, green, blue),
                LightBarFlashDurationOn = flashOn,
                LightBarFlashDurationOff = flashOff,
            };
            device.SetLightbarState(ref lightState);
        }

        private static byte MaxByte(byte[] data, int start, int count)
        {
            byte result = 0;
            int end = Math.Min(data.Length, start + count);
            for (int i = start; i < end; i++)
            {
                if (data[i] > result)
                {
                    result = data[i];
                }
            }

            return result;
        }

        private void LogSubmitFailure(string message)
        {
            connected = false;
            Disconnect();
            if (Interlocked.Exchange(ref submitFailureLogged, 1) == 1)
            {
                return;
            }

            AppLogger.LogToGui($"VIIPER {viiperType} output stopped: {message}", true);
        }
    }

    internal enum MicrophonePipelineHealthStage
    {
        None,
        Starting,
        Healthy,
        PhysicalReceiveStalled,
        DecodeOrProcessStalled,
        VirtualSubmissionStalled,
    }

    /// <summary>
    /// Classifies microphone liveness by the last stage that completed. The
    /// final virtual submission is the only green state: fresh compressed
    /// input cannot hide a decoder/processor failure, and fresh processed PCM
    /// cannot hide a stalled VIIPER write.
    /// </summary>
    internal static class MicrophonePipelineHealth
    {
        internal static MicrophonePipelineHealthStage Classify(long now,
            long maximumAgeTicks, long lastCompressedRx,
            long lastProcessed, long lastSubmitted, bool hasArmedSource)
        {
            if (IsRecent(now, lastSubmitted, maximumAgeTicks))
            {
                return MicrophonePipelineHealthStage.Healthy;
            }

            if (!IsRecent(now, lastCompressedRx, maximumAgeTicks))
            {
                bool hasAnyActivity = lastCompressedRx != 0 ||
                    lastProcessed != 0 || lastSubmitted != 0;
                return !hasArmedSource && !hasAnyActivity ?
                    MicrophonePipelineHealthStage.Starting :
                    MicrophonePipelineHealthStage.PhysicalReceiveStalled;
            }

            if (!IsRecent(now, lastProcessed, maximumAgeTicks))
            {
                return MicrophonePipelineHealthStage.DecodeOrProcessStalled;
            }

            return MicrophonePipelineHealthStage.VirtualSubmissionStalled;
        }

        internal static string GetDisplayName(
            MicrophonePipelineHealthStage stage)
        {
            return stage switch
            {
                MicrophonePipelineHealthStage.Starting => "starting",
                MicrophonePipelineHealthStage.Healthy => "healthy",
                MicrophonePipelineHealthStage.PhysicalReceiveStalled =>
                    "physical-rx-stalled",
                MicrophonePipelineHealthStage.DecodeOrProcessStalled =>
                    "decode-process-stalled",
                MicrophonePipelineHealthStage.VirtualSubmissionStalled =>
                    "virtual-submit-stalled",
                _ => "none",
            };
        }

        private static bool IsRecent(long now, long timestamp,
            long maximumAgeTicks)
        {
            if (timestamp <= 0 || maximumAgeTicks <= 0 || now < timestamp)
            {
                return false;
            }

            return now - timestamp < maximumAgeTicks;
        }
    }

    /// <summary>
    /// Tracks completion-aware physical microphone disables independently from
    /// the currently attached virtual-microphone source. A failed disable stays
    /// retryable, while an exact source reactivation or worker-generation change
    /// invalidates it before another physical write can be attempted.
    /// </summary>
    internal sealed class MicrophoneDisableRetryTracker<T> where T : class
    {
        internal readonly struct Attempt
        {
            internal Attempt(T target, long generation, long token)
            {
                Target = target;
                Generation = generation;
                Token = token;
            }

            internal T Target { get; }
            internal long Generation { get; }
            internal long Token { get; }
        }

        private sealed class Entry
        {
            internal T Target;
            internal long Generation;
            internal long NextAttemptTimestamp;
            internal long ActiveAttemptToken;
            internal bool AttemptInFlight;
        }

        private readonly object syncRoot = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private long nextAttemptToken;

        internal int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return entries.Count;
                }
            }
        }

        internal void Schedule(T target, long generation, long now)
        {
            if (target == null)
            {
                return;
            }

            lock (syncRoot)
            {
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    Entry existing = entries[index];
                    if (!ReferenceEquals(existing.Target, target))
                    {
                        continue;
                    }

                    if (existing.Generation == generation)
                    {
                        if (!existing.AttemptInFlight)
                        {
                            existing.NextAttemptTimestamp = Math.Min(
                                existing.NextAttemptTimestamp, now);
                        }
                        return;
                    }

                    entries.RemoveAt(index);
                }

                // The source detached by the current transition gets the first
                // immediate attempt. Older failures remain queued behind it for
                // subsequent monitor ticks.
                entries.Insert(0, new Entry
                {
                    Target = target,
                    Generation = generation,
                    NextAttemptTimestamp = now,
                });
            }
        }

        internal void Cancel(T target)
        {
            if (target == null)
            {
                return;
            }

            lock (syncRoot)
            {
                entries.RemoveAll(entry => ReferenceEquals(entry.Target,
                    target));
            }
        }

        internal bool TryBeginAttempt(long generation, long now,
            long retryTicks, out Attempt attempt)
        {
            lock (syncRoot)
            {
                entries.RemoveAll(entry => entry.Generation != generation);
                foreach (Entry entry in entries)
                {
                    if (entry.AttemptInFlight ||
                        now < entry.NextAttemptTimestamp)
                    {
                        continue;
                    }

                    long token = unchecked(++nextAttemptToken);
                    if (token == 0)
                    {
                        token = unchecked(++nextAttemptToken);
                    }

                    entry.AttemptInFlight = true;
                    entry.ActiveAttemptToken = token;
                    entry.NextAttemptTimestamp = now + Math.Max(1, retryTicks);
                    attempt = new Attempt(entry.Target, entry.Generation,
                        token);
                    return true;
                }
            }

            attempt = default;
            return false;
        }

        internal void CompleteAttempt(Attempt attempt, bool succeeded,
            long nextAttemptTimestamp = long.MinValue)
        {
            lock (syncRoot)
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    Entry entry = entries[index];
                    if (!ReferenceEquals(entry.Target, attempt.Target) ||
                        entry.Generation != attempt.Generation ||
                        !entry.AttemptInFlight ||
                        entry.ActiveAttemptToken != attempt.Token)
                    {
                        continue;
                    }

                    if (succeeded)
                    {
                        entries.RemoveAt(index);
                    }
                    else
                    {
                        entry.AttemptInFlight = false;
                        entry.ActiveAttemptToken = 0;
                        if (nextAttemptTimestamp != long.MinValue)
                        {
                            entry.NextAttemptTimestamp = Math.Max(
                                entry.NextAttemptTimestamp,
                                nextAttemptTimestamp);
                        }
                    }
                    return;
                }
            }
        }

        internal void DiscardOtherGenerations(long generation)
        {
            lock (syncRoot)
            {
                entries.RemoveAll(entry => entry.Generation != generation);
            }
        }

        internal void Clear()
        {
            lock (syncRoot)
            {
                entries.Clear();
            }
        }
    }

    /// <summary>
    /// Debounces VIIPER's capture-interface status without treating an API
    /// failure as an inactive observation. Active observations are published
    /// immediately; inactive observations must be both consecutive and span a
    /// short grace period before they are published.
    /// </summary>
    internal sealed class MicrophoneInterfaceActivityTracker
    {
        internal const int RequiredInactiveObservations = 3;
        internal static readonly long InactiveGraceTicks =
            Math.Max(1L, Stopwatch.Frequency / 4);

        private int consecutiveInactiveObservations;
        private long firstInactiveTimestamp;

        internal bool StateKnown { get; private set; }
        internal bool IsActive { get; private set; }

        /// <summary>
        /// Records a successful VIIPER status query. Returns true only when the
        /// state visible to the rest of DS4Windows changes.
        /// </summary>
        internal bool RecordObservation(bool active, long timestamp)
        {
            if (active)
            {
                ResetInactiveRun();
                bool changed = !StateKnown || !IsActive;
                StateKnown = true;
                IsActive = true;
                return changed;
            }

            if (consecutiveInactiveObservations == 0)
            {
                firstInactiveTimestamp = timestamp;
            }
            consecutiveInactiveObservations++;

            long elapsed = timestamp >= firstInactiveTimestamp ?
                timestamp - firstInactiveTimestamp : 0;
            if (consecutiveInactiveObservations < RequiredInactiveObservations ||
                elapsed < InactiveGraceTicks)
            {
                return false;
            }

            bool stateChanged = !StateKnown || IsActive;
            StateKnown = true;
            IsActive = false;
            return stateChanged;
        }

        /// <summary>
        /// A query failure is neither active nor inactive. It preserves the
        /// published state and breaks a pending consecutive-inactive run.
        /// </summary>
        internal void RecordQueryFailure()
        {
            ResetInactiveRun();
        }

        private void ResetInactiveRun()
        {
            consecutiveInactiveObservations = 0;
            firstInactiveTimestamp = 0;
        }
    }

    internal sealed class ViiperClient
    {
        private const int ApiReceiveTimeoutMs = 5000;
        private const int StreamReceiveTimeoutMs = 0;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private static readonly JsonSerializerOptions NativeJsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            };

        private readonly string host;
        private readonly int port;
        private readonly object nativeSessionLock = new object();
        private readonly ViiperNativeRuntimeMetadata deferredNativeMetadata;
        private readonly IViiperCredentialProvider deferredCredentialProvider;
        private ViiperNativeSession nativeSession;

        public ViiperClient(string host, int port)
            : this(host, port, ViiperTransportSettings.GetManagedMode(), null,
                null)
        {
        }

        internal ViiperClient(string host, int port,
            ViiperTransportMode transportMode,
            ViiperNativeRuntimeMetadata metadata = null,
            IViiperCredentialProvider credentialProvider = null)
        {
            this.host = string.IsNullOrWhiteSpace(host) ?
                throw new ArgumentException("A VIIPER host is required.",
                    nameof(host)) : host.Trim();
            if (port <= 0 || port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }
            this.port = port;
            TransportMode = transportMode;
            if (transportMode == ViiperTransportMode.NativeUde)
            {
                if (!string.Equals(this.host, "localhost",
                        StringComparison.OrdinalIgnoreCase) &&
                    (!System.Net.IPAddress.TryParse(this.host,
                        out System.Net.IPAddress address) ||
                        !System.Net.IPAddress.IsLoopback(address)))
                {
                    throw new ViiperIdentityException(
                        "Managed native VIIPER connections must use a loopback address.");
                }
                deferredNativeMetadata = metadata;
                deferredCredentialProvider = credentialProvider;
            }
        }

        internal ViiperTransportMode TransportMode { get; }

        internal bool HasAuthenticatedNativeConnection =>
            nativeSession?.HasAuthenticatedConnection == true;

        internal ViiperNativeBackendIdentity NativeBackendIdentity =>
            nativeSession?.Identity;

        private ViiperNativeSession GetNativeSession()
        {
            if (TransportMode != ViiperTransportMode.NativeUde)
            {
                throw new ViiperIdentityException(
                    "An authenticated native session was requested for the explicit USB/IP client.");
            }
            lock (nativeSessionLock)
            {
                if (nativeSession == null)
                {
                    ViiperNativeRuntimeMetadata metadata =
                        deferredNativeMetadata ??
                        ViiperNativeRuntimeMetadata.LoadBundled();
                    nativeSession = new ViiperNativeSession(
                        new ViiperNativeRuntimeContract(metadata),
                        deferredCredentialProvider ??
                        new ViiperProgramDataCredentialProvider());
                }
                return nativeSession;
            }
        }

        internal ViiperNativeBackendIdentity ValidateNativeBackend()
        {
            if (TransportMode != ViiperTransportMode.NativeUde)
            {
                throw new ViiperIdentityException(
                    "Native VIIPER validation was requested for the explicit USB/IP client.");
            }
            string raw = SendRequestRawCore("ping", null);
            return GetNativeSession().AdmitPing(raw, reconnect: false);
        }

        private ViiperNativeBackendIdentity RevalidateNativeBackend()
        {
            string raw = SendRequestRawCore("ping", null);
            return GetNativeSession().AdmitPing(raw, reconnect: true);
        }

        private void EnsureNativeBackendValidated()
        {
            if (TransportMode == ViiperTransportMode.NativeUde &&
                nativeSession?.Identity == null)
            {
                ValidateNativeBackend();
            }
        }

        public ViiperDeviceStream CreateDeviceAndOpenStream(ViiperVirtualDeviceType deviceType)
        {
            return CreateDeviceAndOpenStream(ViiperStatePacketBuilder.GetViiperDeviceName(deviceType));
        }

        public ViiperDeviceStream CreateDeviceAndOpenStream(string deviceName,
            ushort? idProduct = null)
        {
            EnsureNativeBackendValidated();
            ushort? effectiveProductId = idProduct;
            if (TransportMode == ViiperTransportMode.NativeUde)
            {
                effectiveProductId = GetNativeSession().Contract
                    .ValidateControllerRequest(deviceName, idProduct);
            }
            if (TransportMode == ViiperTransportMode.Usbip)
            {
                ViiperUsbipPortManager.DetachStaleLocalViiperPorts();
            }

            ViiperBusCreateResponse bus;
            try
            {
                bus = SendRequest<ViiperBusCreateResponse>("bus/create", "0");
                if (bus == null || bus.BusId == 0)
                {
                    throw new ViiperIdentityException(
                        "VIIPER returned an invalid bus identity.");
                }
            }
            catch (Exception failure) when (
                TransportMode == ViiperTransportMode.NativeUde &&
                (failure is ViiperIdentityException || failure is JsonException))
            {
                GetNativeSession().InvalidateIdentity(failure);
                throw;
            }
            ViiperDeviceResponse device = null;
            ViiperVirtualDeviceIdentity nativeIdentity = null;
            int usbipPort = -1;
            try
            {
                string payload = JsonSerializer.Serialize(new ViiperDeviceCreateRequest
                {
                    Type = deviceName,
                    IdProduct = effectiveProductId,
                }, JsonOptions);

                device = SendRequest<ViiperDeviceResponse>($"bus/{bus.BusId}/add", payload);
                if (TransportMode == ViiperTransportMode.NativeUde)
                {
                    nativeIdentity =
                        ValidateNativeDeviceIdentity(bus.BusId, device,
                            deviceName);
                    var lifetime = new ViiperVirtualDeviceLifetime(
                        nativeIdentity, RemoveNativeDevice);
                    return OpenStream(bus.BusId, device.DevId, lifetime);
                }

                ValidateUsbipDeviceResponse(device);
                usbipPort = ViiperUsbipPortManager.FindLocalViiperPort(
                    bus.BusId, device.DevId);
                ViiperUsbipPortManager.RegisterActivePort(usbipPort);
                ViiperUsbipPortManager.DetachDuplicateLocalViiperPorts(
                    bus.BusId, device.DevId, usbipPort);
                ViiperVirtualDeviceIdentity usbipIdentity =
                    CreateUsbipDeviceIdentity(bus.BusId, device, deviceName,
                        usbipPort);
                var usbipLifetime = new ViiperVirtualDeviceLifetime(
                    usbipIdentity, RemoveDevice);
                return OpenStream(bus.BusId, device.DevId, usbipLifetime);
            }
            catch (Exception failure)
            {
                if (TransportMode == ViiperTransportMode.Usbip)
                {
                    ViiperUsbipPortManager.UnregisterActivePort(usbipPort);
                }

                if (TransportMode == ViiperTransportMode.Usbip &&
                    device != null && !string.IsNullOrEmpty(device.DevId))
                {
                    TryRemoveDevice(bus.BusId, device.DevId);
                }

                if (TransportMode == ViiperTransportMode.Usbip)
                {
                    TryRemoveBus(bus.BusId);
                }
                else if (nativeIdentity != null)
                {
                    TryRemoveNativeDevice(nativeIdentity);
                }
                if (TransportMode == ViiperTransportMode.NativeUde &&
                    (failure is ViiperIdentityException ||
                     failure is JsonException))
                {
                    // Keep the authenticated session usable long enough to
                    // remove any partially-created topology, then permanently
                    // fence it before control returns to the caller.
                    GetNativeSession().InvalidateIdentity(failure);
                }
                throw;
            }
        }

        public string SetDualSenseTrafficCapture(bool enabled, bool clear)
        {
            string payload = JsonSerializer.Serialize(new ViiperDualSenseTrafficSetRequest
            {
                Enabled = enabled,
                Clear = clear,
            }, JsonOptions);
            return SendRequestRaw("debug/dualsense-traffic/set", payload);
        }

        public string GetDualSenseTrafficCapture()
        {
            return SendRequestRaw("debug/dualsense-traffic/get");
        }

        public string ClearDualSenseTrafficCapture()
        {
            return SendRequestRaw("debug/dualsense-traffic/clear");
        }

        public bool GetMicrophoneInterfaceActive(uint busId, string devId)
        {
            return GetMicrophoneInterfaceStatus(busId, devId).IsActive;
        }

        internal ViiperMicrophoneInterfaceStatus GetMicrophoneInterfaceStatus(
            uint busId, string devId)
        {
            ViiperBusDevicesResponse response =
                SendRequest<ViiperBusDevicesResponse>($"bus/{busId}/list");
            if (response?.Devices == null)
            {
                throw new IOException(
                    "VIIPER did not return a device list for the microphone-interface query.");
            }

            foreach (ViiperListedDevice device in response.Devices)
            {
                if (!string.Equals(device.DevId, devId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (device.DeviceSpecific.ValueKind != JsonValueKind.Object ||
                    !device.DeviceSpecific.TryGetProperty(
                        "microphoneInterfaceActive", out JsonElement active))
                {
                    throw new IOException(
                        "VIIPER omitted microphoneInterfaceActive from the matching device.");
                }

                bool isActive;
                if (active.ValueKind == JsonValueKind.True ||
                    active.ValueKind == JsonValueKind.False)
                {
                    isActive = active.GetBoolean();
                }
                else if (active.ValueKind == JsonValueKind.String &&
                    bool.TryParse(active.GetString(), out bool parsed))
                {
                    isActive = parsed;
                }
                else
                {
                    throw new IOException(
                        "VIIPER returned an invalid microphoneInterfaceActive value.");
                }

                return new ViiperMicrophoneInterfaceStatus(isActive,
                    ViiperMicrophoneBufferSnapshot.Parse(
                        device.DeviceSpecific));
            }

            throw new IOException(
                "VIIPER did not return the matching device for the microphone-interface query.");
        }

        public ViiperDeviceStream OpenExistingDeviceStream(uint busId,
            string devId, int usbipPort)
        {
            if (TransportMode != ViiperTransportMode.Usbip)
            {
                throw new ArgumentException(
                    "A USB/IP port may only be supplied to an explicit USB/IP VIIPER client.",
                    nameof(usbipPort));
            }
            return OpenExistingDeviceStream(busId, devId, usbipPort, null);
        }

        internal ViiperDeviceStream OpenExistingDeviceStream(uint busId,
            string devId, int usbipPort,
            ViiperVirtualDeviceLifetime deviceLifetime)
        {
            if (TransportMode != ViiperTransportMode.Usbip)
            {
                throw new ViiperIdentityException(
                    "Native VIIPER stream reopen requires the exact captured virtual-device lifetime receipt.");
            }
            if (string.IsNullOrWhiteSpace(devId))
            {
                throw new ArgumentException(
                    "A VIIPER device ID is required to reopen its stream.",
                    nameof(devId));
            }
            if (deviceLifetime != null &&
                (deviceLifetime.BusId != busId ||
                    !string.Equals(deviceLifetime.DevId, devId,
                        StringComparison.Ordinal) ||
                    deviceLifetime.UsbipPort != usbipPort))
            {
                throw new ArgumentException(
                    "The VIIPER stream identity must match its virtual-device lifetime.",
                    nameof(deviceLifetime));
            }

            deviceLifetime ??= new ViiperVirtualDeviceLifetime(busId,
                devId, usbipPort, RemoveDevice);
            return OpenStream(busId, devId, deviceLifetime);
        }

        internal ViiperDeviceStream OpenExistingDeviceStream(uint busId,
            string devId, ViiperVirtualDeviceLifetime deviceLifetime)
        {
            if (deviceLifetime == null)
            {
                throw new ArgumentNullException(nameof(deviceLifetime));
            }
            if (string.IsNullOrWhiteSpace(devId) ||
                deviceLifetime.BusId != busId ||
                !string.Equals(deviceLifetime.DevId, devId,
                    StringComparison.Ordinal) ||
                deviceLifetime.TransportMode != TransportMode)
            {
                throw new ArgumentException(
                    "The VIIPER stream identity must match its virtual-device lifetime.",
                    nameof(deviceLifetime));
            }

            if (TransportMode == ViiperTransportMode.NativeUde)
            {
                try
                {
                    RevalidateNativeBackend();
                    ValidateExistingNativeDevice(deviceLifetime);
                }
                catch (Exception failure) when (
                    failure is ViiperIdentityException ||
                    failure is JsonException)
                {
                    GetNativeSession().InvalidateIdentity(failure);
                    throw;
                }
            }
            return OpenStream(busId, devId, deviceLifetime);
        }

        private ViiperDeviceStream OpenStream(uint busId, string devId,
            ViiperVirtualDeviceLifetime deviceLifetime)
        {
            TcpClient tcp = Connect(TransportMode ==
                ViiperTransportMode.NativeUde ? ApiReceiveTimeoutMs :
                StreamReceiveTimeoutMs);
            Stream stream = null;
            try
            {
                stream = tcp.GetStream();
                if (TransportMode == ViiperTransportMode.NativeUde)
                {
                    stream = GetNativeSession().Authenticate(stream);
                    tcp.ReceiveTimeout = StreamReceiveTimeoutMs;
                }
                byte[] request = Encoding.UTF8.GetBytes($"bus/{busId}/{devId}\0");
                stream.Write(request, 0, request.Length);
                return new ViiperDeviceStream(stream, tcp, deviceLifetime);
            }
            catch
            {
                try
                {
                    stream?.Dispose();
                }
                catch
                {
                }
                tcp.Dispose();
                throw;
            }
        }

        private void RemoveDevice(uint busId, string devId)
        {
            TryRemoveDevice(busId, devId);
            TryRemoveBus(busId);
        }

        private void RemoveNativeDevice(
            ViiperVirtualDeviceIdentity identity)
        {
            if (identity?.TransportMode != ViiperTransportMode.NativeUde ||
                identity.NativePnpAnchor?.IsExact != true ||
                identity.BusId == 0 ||
                string.IsNullOrWhiteSpace(identity.DevId))
            {
                throw new ViiperIdentityException(
                    "Exact native VIIPER identity is required for conditional removal.");
            }

            ViiperNativePnpAnchor anchor = identity.NativePnpAnchor;
            string payload = JsonSerializer.Serialize(
                new ViiperNativeRemoveRequest
                {
                    DevId = identity.DevId,
                    Transport = "native-ude",
                    NativeUde = new ViiperNativeDeviceResponse
                    {
                        DeviceId = anchor.NativeDeviceId.ToString(
                            CultureInfo.InvariantCulture),
                        DeviceGeneration = anchor.NativeDeviceGeneration,
                        ControllerSessionId =
                            anchor.ControllerSessionId.ToString(
                                CultureInfo.InvariantCulture),
                        ControllerInstanceId = anchor.ControllerInstanceId,
                        Usb20PortNumber = anchor.Usb20PortNumber,
                        Usb30PortNumber = anchor.Usb30PortNumber,
                    },
                }, NativeJsonOptions);
            string path = $"bus/{identity.BusId}/remove-native";
            string raw = SendRequestRaw(path, payload);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new IOException(
                    "VIIPER returned an empty native removal response.");
            }

            ViiperNativeRuntimeContract.ValidateNoDuplicateJsonProperties(
                raw, path);
            using JsonDocument document = JsonDocument.Parse(raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new IOException(
                    "VIIPER returned a non-object native removal response.");
            }

            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (root.TryGetProperty("status", out JsonElement status))
            {
                if (properties.Length == 3 &&
                    properties.All(property => property.Name is "status" or
                        "title" or "detail") &&
                    status.ValueKind == JsonValueKind.Number &&
                    status.TryGetInt32(out int statusCode) &&
                    statusCode == 409 &&
                    root.TryGetProperty("title", out JsonElement title) &&
                    title.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(title.GetString()) &&
                    root.TryGetProperty("detail", out JsonElement detail) &&
                    detail.ValueKind == JsonValueKind.String)
                {
                    // The receipt is stale and a successor owns this bus/dev
                    // key. Never retry the legacy ID-only endpoint.
                    return;
                }
                throw new IOException(
                    "VIIPER native removal returned a non-canonical API error.");
            }

            if (properties.Length != 2 ||
                properties.Any(property => property.Name is not "busId" and
                    not "devId") ||
                !root.TryGetProperty("busId", out JsonElement returnedBus) ||
                returnedBus.ValueKind != JsonValueKind.Number ||
                !returnedBus.TryGetUInt32(out uint returnedBusId) ||
                returnedBusId != identity.BusId ||
                !root.TryGetProperty("devId", out JsonElement returnedDevice) ||
                returnedDevice.ValueKind != JsonValueKind.String ||
                !string.Equals(returnedDevice.GetString(), identity.DevId,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "VIIPER native removal success did not echo the exact bus/device identity.");
            }
        }

        private void TryRemoveNativeDevice(
            ViiperVirtualDeviceIdentity identity)
        {
            try
            {
                RemoveNativeDevice(identity);
            }
            catch
            {
            }
        }

        private void TryRemoveDevice(uint busId, string devId)
        {
            try
            {
                SendRequestRaw($"bus/{busId}/remove", devId);
            }
            catch
            {
            }
        }

        private void TryRemoveBus(uint busId)
        {
            try
            {
                SendRequestRaw("bus/remove", busId.ToString());
            }
            catch
            {
            }
        }

        private T SendRequest<T>(string path, string payload = null)
        {
            string raw = SendRequestRaw(path, payload);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new IOException("VIIPER returned an empty response.");
            }

            JsonSerializerOptions responseOptions = TransportMode ==
                ViiperTransportMode.NativeUde ? NativeJsonOptions :
                JsonOptions;
            if (TransportMode == ViiperTransportMode.NativeUde)
            {
                ViiperNativeRuntimeContract.ValidateNoDuplicateJsonProperties(
                    raw, path);
            }
            ViiperApiError apiError = JsonSerializer.Deserialize<ViiperApiError>(
                raw, responseOptions);
            if (apiError != null && (apiError.Status != 0 || !string.IsNullOrEmpty(apiError.Title)))
            {
                throw new IOException($"VIIPER API error {apiError.Status} {apiError.Title}: {apiError.Detail}");
            }

            return JsonSerializer.Deserialize<T>(raw, responseOptions);
        }

        private string SendRequestRaw(string path, string payload = null)
        {
            if (!string.Equals(path, "ping", StringComparison.Ordinal))
            {
                EnsureNativeBackendValidated();
            }
            return SendRequestRawCore(path, payload);
        }

        private string SendRequestRawCore(string path, string payload)
        {
            using TcpClient tcp = Connect(ApiReceiveTimeoutMs);
            using Stream stream = TransportMode == ViiperTransportMode.NativeUde ?
                GetNativeSession().Authenticate(tcp.GetStream()) :
                tcp.GetStream();
            string request = string.IsNullOrEmpty(payload) ? path : $"{path} {payload}";
            byte[] requestBytes = Encoding.UTF8.GetBytes(request + "\0");
            stream.Write(requestBytes, 0, requestBytes.Length);

            using MemoryStream response = new MemoryStream();
            byte[] buffer = new byte[1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                response.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(response.ToArray()).TrimEnd('\n');
        }

        private TcpClient Connect(int receiveTimeout)
        {
            TcpClient tcp = new TcpClient
            {
                NoDelay = true,
                SendTimeout = 1000,
                ReceiveTimeout = receiveTimeout,
            };

            IAsyncResult result = tcp.BeginConnect(host, port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
            {
                tcp.Dispose();
                throw new IOException($"Could not connect to VIIPER at {host}:{port}. Start VIIPER server with its API listening on port {port}.");
            }

            try
            {
                tcp.EndConnect(result);
            }
            catch (SocketException ex)
            {
                tcp.Dispose();
                throw new IOException($"Could not connect to VIIPER at {host}:{port}: {ex.Message}", ex);
            }

            return tcp;
        }

        private sealed class ViiperBusCreateResponse
        {
            [JsonPropertyName("busId")]
            public uint BusId { get; set; }
        }

        private sealed class ViiperDeviceResponse
        {
            private int? usbipPort;
            private string usbipOwnerSerial;

            [JsonPropertyName("busId")]
            public uint BusId { get; set; }

            [JsonPropertyName("devId")]
            public string DevId { get; set; }

            [JsonPropertyName("vid")]
            public string Vid { get; set; }

            [JsonPropertyName("pid")]
            public string Pid { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("transport")]
            public string Transport { get; set; }

            [JsonPropertyName("deviceSpecific")]
            public JsonElement DeviceSpecific { get; set; }

            [JsonPropertyName("usbipPort")]
            public int? UsbipPort
            {
                get => usbipPort;
                set
                {
                    HasUsbipPortProperty = true;
                    usbipPort = value;
                }
            }

            [JsonIgnore]
            public bool HasUsbipPortProperty { get; private set; }

            [JsonPropertyName("usbipOwnerSerial")]
            public string UsbipOwnerSerial
            {
                get => usbipOwnerSerial;
                set
                {
                    HasUsbipOwnerSerialProperty = true;
                    usbipOwnerSerial = value;
                }
            }

            [JsonIgnore]
            public bool HasUsbipOwnerSerialProperty { get; private set; }

            [JsonPropertyName("nativeUde")]
            public ViiperNativeDeviceResponse NativeUde { get; set; }
        }

        private sealed class ViiperNativeDeviceResponse
        {
            [JsonPropertyName("deviceId")]
            public string DeviceId { get; set; }

            [JsonPropertyName("deviceGeneration")]
            public uint DeviceGeneration { get; set; }

            [JsonPropertyName("controllerSessionId")]
            public string ControllerSessionId { get; set; }

            [JsonPropertyName("controllerInstanceId")]
            public string ControllerInstanceId { get; set; }

            [JsonPropertyName("usb20PortNumber")]
            public uint Usb20PortNumber { get; set; }

            [JsonPropertyName("usb30PortNumber")]
            public uint Usb30PortNumber { get; set; }
        }

        private sealed class ViiperDeviceCreateRequest
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("idProduct")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ushort? IdProduct { get; set; }
        }

        private sealed class ViiperNativeRemoveRequest
        {
            [JsonPropertyName("devId")]
            public string DevId { get; set; }

            [JsonPropertyName("transport")]
            public string Transport { get; set; }

            [JsonPropertyName("nativeUde")]
            public ViiperNativeDeviceResponse NativeUde { get; set; }
        }

        private sealed class ViiperBusDevicesResponse
        {
            [JsonPropertyName("devices")]
            public ViiperListedDevice[] Devices { get; set; }
        }

        private sealed class ViiperListedDevice
        {
            private int? usbipPort;
            private string usbipOwnerSerial;

            [JsonPropertyName("devId")]
            public string DevId { get; set; }

            [JsonPropertyName("transport")]
            public string Transport { get; set; }

            [JsonPropertyName("vid")]
            public string Vid { get; set; }

            [JsonPropertyName("pid")]
            public string Pid { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("nativeUde")]
            public ViiperNativeDeviceResponse NativeUde { get; set; }

            [JsonPropertyName("usbipPort")]
            public int? UsbipPort
            {
                get => usbipPort;
                set
                {
                    HasUsbipPortProperty = true;
                    usbipPort = value;
                }
            }

            [JsonIgnore]
            public bool HasUsbipPortProperty { get; private set; }

            [JsonPropertyName("usbipOwnerSerial")]
            public string UsbipOwnerSerial
            {
                get => usbipOwnerSerial;
                set
                {
                    HasUsbipOwnerSerialProperty = true;
                    usbipOwnerSerial = value;
                }
            }

            [JsonIgnore]
            public bool HasUsbipOwnerSerialProperty { get; private set; }

            [JsonPropertyName("deviceSpecific")]
            public JsonElement DeviceSpecific { get; set; }
        }

        private sealed class ViiperDualSenseTrafficSetRequest
        {
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; }

            [JsonPropertyName("clear")]
            public bool Clear { get; set; }
        }

        private sealed class ViiperApiError
        {
            [JsonPropertyName("status")]
            public int Status { get; set; }

            [JsonPropertyName("title")]
            public string Title { get; set; }

            [JsonPropertyName("detail")]
            public string Detail { get; set; }
        }

        private ViiperVirtualDeviceIdentity ValidateNativeDeviceIdentity(
            uint busId, ViiperDeviceResponse device, string expectedType)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.DevId))
            {
                throw new ViiperIdentityException(
                    "VIIPER native add response omitted the device ID.");
            }
            if (device.BusId != busId)
            {
                throw new ViiperIdentityException(
                    "VIIPER native add response returned the wrong bus ID.");
            }
            if (!string.Equals(device.Transport, "native-ude",
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"VIIPER native add response transport is '{device.Transport ?? "<missing>"}'.");
            }
            if (device.HasUsbipPortProperty ||
                device.HasUsbipOwnerSerialProperty)
            {
                throw new ViiperIdentityException(
                    "VIIPER native add response contained USB/IP ownership fields.");
            }
            if (!string.Equals(device.Type, expectedType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ViiperIdentityException(
                    "VIIPER native add response returned the wrong device type.");
            }
            if (!string.Equals(device.Type, expectedType,
                    StringComparison.Ordinal) ||
                !GetNativeSession().Contract.HasExactControllerIdentity(
                    expectedType, device.Vid, device.Pid))
            {
                throw new ViiperIdentityException(
                    "VIIPER native add response did not match the source-bound controller type/VID/PID identity.");
            }
            if (!uint.TryParse(device.DevId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint numericDevId) ||
                numericDevId == 0 ||
                !string.Equals(numericDevId.ToString(
                        CultureInfo.InvariantCulture), device.DevId,
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    "VIIPER native add response devId is not canonical decimal.");
            }

            ViiperNativeDeviceResponse native = device.NativeUde ??
                throw new ViiperIdentityException(
                    "VIIPER native add response omitted nativeUde identity.");
            if (!ulong.TryParse(native.DeviceId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong nativeDeviceId) ||
                nativeDeviceId == 0 ||
                !string.Equals(nativeDeviceId.ToString(
                        CultureInfo.InvariantCulture), native.DeviceId,
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    "VIIPER nativeUde.deviceId is not canonical decimal.");
            }
            ulong expectedNativeDeviceId = (ulong)busId << 32 | numericDevId;
            if (nativeDeviceId != expectedNativeDeviceId)
            {
                throw new ViiperIdentityException(
                    $"VIIPER nativeUde.deviceId={nativeDeviceId} does not match bus/dev identity {expectedNativeDeviceId}.");
            }
            if (native.DeviceGeneration == 0)
            {
                throw new ViiperIdentityException(
                    "VIIPER nativeUde.deviceGeneration is zero.");
            }
            ulong controllerSessionId =
                ViiperNativeRuntimeContract.ParseCanonicalNonZeroUInt64(
                    native.ControllerSessionId,
                    "nativeUde.controllerSessionId");
            ViiperNativeRuntimeContract.ValidateControllerInstanceId(
                native.ControllerInstanceId);
            ViiperNativeBackendIdentity backend = GetNativeSession().Identity ??
                throw new ViiperIdentityException(
                    "VIIPER native backend identity is not pinned.");
            if (!string.Equals(native.ControllerInstanceId,
                    backend.ControllerInstanceId, StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    "VIIPER device controller instance differs from the authenticated ping identity.");
            }
            if (controllerSessionId != backend.ControllerSessionId)
            {
                throw new ViiperIdentityException(
                    "VIIPER device controller session differs from the authenticated ping identity.");
            }
            if (!(native.Usb20PortNumber != 0 ^
                    native.Usb30PortNumber != 0))
            {
                throw new ViiperIdentityException(
                    "VIIPER native device identity must contain exactly one non-zero UdeCx USB port number.");
            }

            return new ViiperVirtualDeviceIdentity
            {
                TransportMode = ViiperTransportMode.NativeUde,
                BusId = busId,
                DevId = device.DevId,
                DeviceType = device.Type,
                Vid = device.Vid,
                Pid = device.Pid,
                DeviceSerialNumber = GetDeviceSerialNumber(
                    device.DeviceSpecific),
                BrokerBuildIdentity = backend.DriverBuildIdentity,
                LogicalLifetimeId = Guid.NewGuid().ToString("N"),
                NativePnpAnchor = new ViiperNativePnpAnchor
                {
                    NativeDeviceId = nativeDeviceId,
                    NativeDeviceGeneration = native.DeviceGeneration,
                    ControllerSessionId = controllerSessionId,
                    ControllerInstanceId = native.ControllerInstanceId,
                    Usb20PortNumber = native.Usb20PortNumber,
                    Usb30PortNumber = native.Usb30PortNumber,
                },
            };
        }

        private static void ValidateUsbipDeviceResponse(
            ViiperDeviceResponse device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.DevId))
            {
                throw new IOException(
                    "VIIPER USB/IP add response omitted the device ID.");
            }
            if (!string.IsNullOrEmpty(device.Transport) &&
                !string.Equals(device.Transport, "usbip",
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"VIIPER explicit USB/IP add response returned transport '{device.Transport}'.");
            }
            if (device.NativeUde != null)
            {
                throw new IOException(
                    "VIIPER explicit USB/IP response unexpectedly contained nativeUde identity.");
            }
        }

        private ViiperVirtualDeviceIdentity CreateUsbipDeviceIdentity(
            uint busId, ViiperDeviceResponse device, string expectedType,
            int usbipPort)
        {
            return new ViiperVirtualDeviceIdentity
            {
                TransportMode = ViiperTransportMode.Usbip,
                BusId = busId,
                DevId = device.DevId,
                DeviceType = string.IsNullOrEmpty(device.Type) ?
                    expectedType : device.Type,
                Vid = device.Vid,
                Pid = device.Pid,
                DeviceSerialNumber = GetDeviceSerialNumber(
                    device.DeviceSpecific),
                LogicalLifetimeId = Guid.NewGuid().ToString("N"),
                LegacyUsbipPort = usbipPort,
                LegacyUsbipOwnerSerial = device.UsbipOwnerSerial,
            };
        }

        private void ValidateExistingNativeDevice(
            ViiperVirtualDeviceLifetime lifetime)
        {
            ViiperBusDevicesResponse response =
                SendRequest<ViiperBusDevicesResponse>(
                    $"bus/{lifetime.BusId}/list");
            ViiperListedDevice[] matches = response?.Devices?.Where(
                candidate => string.Equals(candidate.DevId,
                    lifetime.DevId, StringComparison.Ordinal)).ToArray();
            ViiperListedDevice listed = matches?.Length == 1 ? matches[0] :
                null;
            if (listed == null ||
                !string.Equals(listed.Transport, "native-ude",
                    StringComparison.Ordinal) || listed.NativeUde == null ||
                listed.HasUsbipPortProperty ||
                listed.HasUsbipOwnerSerialProperty ||
                !string.Equals(listed.Type,
                    lifetime.VirtualDeviceIdentity.DeviceType,
                    StringComparison.Ordinal) ||
                !string.Equals(listed.Vid,
                    lifetime.VirtualDeviceIdentity.Vid,
                    StringComparison.Ordinal) ||
                !string.Equals(listed.Pid,
                    lifetime.VirtualDeviceIdentity.Pid,
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    "VIIPER reconnect could not prove the original native device topology.");
            }
            ViiperNativePnpAnchor expected =
                lifetime.VirtualDeviceIdentity.NativePnpAnchor;
            ViiperNativeDeviceResponse actual = listed.NativeUde;
            if (!ulong.TryParse(actual.DeviceId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong deviceId) ||
                deviceId != expected.NativeDeviceId ||
                !string.Equals(deviceId.ToString(
                        CultureInfo.InvariantCulture), actual.DeviceId,
                    StringComparison.Ordinal) ||
                actual.DeviceGeneration != expected.NativeDeviceGeneration ||
                !ulong.TryParse(actual.ControllerSessionId,
                    NumberStyles.None, CultureInfo.InvariantCulture,
                    out ulong controllerSessionId) ||
                !string.Equals(controllerSessionId.ToString(
                        CultureInfo.InvariantCulture),
                    actual.ControllerSessionId, StringComparison.Ordinal) ||
                controllerSessionId != expected.ControllerSessionId ||
                !string.Equals(actual.ControllerInstanceId,
                    expected.ControllerInstanceId, StringComparison.Ordinal) ||
                actual.Usb20PortNumber != expected.Usb20PortNumber ||
                actual.Usb30PortNumber != expected.Usb30PortNumber)
            {
                throw new ViiperIdentityException(
                    "VIIPER native device identity changed across stream reconnect.");
            }
        }

        private static string GetDeviceSerialNumber(JsonElement specific)
        {
            if (specific.ValueKind == JsonValueKind.Object &&
                specific.TryGetProperty("serial_number",
                    out JsonElement serial) &&
                serial.ValueKind == JsonValueKind.String)
            {
                return serial.GetString();
            }
            return null;
        }
    }

    internal static class ViiperUsbipPortManager
    {
        private static readonly string[] KnownViiperDeviceIds =
        {
            "054c:05c4", // DualShock 4 (VIIPER CUH-ZCT1x identity)
            "054c:09cc", // DualShock 4
            "054c:0ce6", // DualSense
            "054c:0df2", // DualSense Edge
            "045e:028e", // Xbox 360
            "057e:2069", // Switch 2 Pro
        };

        private static readonly object ActivePortsLock = new object();
        private static readonly HashSet<int> ActivePorts = new HashSet<int>();

        public static void DetachStaleLocalViiperPorts()
        {
            HashSet<int> activePorts;
            lock (ActivePortsLock)
            {
                activePorts = new HashSet<int>(ActivePorts);
            }

            // USB/IP and PnP update asynchronously. A second stale import can
            // become visible more than half a second after the first detach, so
            // require a sustained clean window before input enumeration starts.
            int cleanSnapshots = 0;
            // A sustained clean window is required only when no device from
            // this process owns a port (startup/crash recovery). Creating or
            // removing a temporary companion while a native output is active
            // can use one clean snapshot; registered ports protect the native
            // device and PnP is already established.
            int requiredCleanSnapshots = activePorts.Count > 0 ? 1 : 10;
            for (int attempt = 0; attempt < 32 && cleanSnapshots < requiredCleanSnapshots; attempt++)
            {
                bool detachedAny = false;
                foreach (UsbipPortBlock port in GetImportedPorts())
                {
                    if (!activePorts.Contains(port.Port) &&
                        IsLocalViiperPort(port, null))
                    {
                        DetachPort(port.Port,
                            "stale local VIIPER controller import");
                        detachedAny = true;
                    }
                }

                cleanSnapshots = detachedAny ? 0 : cleanSnapshots + 1;
                if (cleanSnapshots < requiredCleanSnapshots)
                {
                    Thread.Sleep(100);
                }
            }
        }

        public static int FindLocalViiperPort(uint busId, string devId)
        {
            string remoteBusId = $"{busId}-{devId}";
            for (int attempt = 0; attempt < 15; attempt++)
            {
                foreach (UsbipPortBlock port in GetImportedPorts())
                {
                    if (IsLocalViiperPort(port, remoteBusId))
                    {
                        return port.Port;
                    }
                }

                if (attempt < 14)
                {
                    Thread.Sleep(100);
                }
            }

            return -1;
        }

        public static void DetachDuplicateLocalViiperPorts(uint busId, string devId, int keepPort)
        {
            if (keepPort < 0)
            {
                return;
            }

            string remoteBusId = $"{busId}-{devId}";
            foreach (UsbipPortBlock port in GetImportedPorts())
            {
                if (port.Port != keepPort && IsLocalViiperPort(port, remoteBusId))
                {
                    DetachPort(port.Port, $"duplicate local VIIPER import for {remoteBusId}");
                }
            }
        }

        public static void RegisterActivePort(int port)
        {
            if (port < 0)
            {
                return;
            }

            lock (ActivePortsLock)
            {
                ActivePorts.Add(port);
            }
        }

        public static void UnregisterActivePort(int port)
        {
            if (port < 0)
            {
                return;
            }

            lock (ActivePortsLock)
            {
                ActivePorts.Remove(port);
            }
        }

        internal static bool IsActivePort(int port)
        {
            if (port < 0)
            {
                return false;
            }

            lock (ActivePortsLock)
            {
                return ActivePorts.Contains(port);
            }
        }

        public static void DetachPort(int port, string reason)
        {
            if (port < 0)
            {
                return;
            }

            if (!TryRunUsbip(new[] { "detach", "-p", port.ToString() }, out _, out string error))
            {
                AppLogger.LogToGui($"VIIPER could not detach usbip port {port} ({reason}): {error}", true);
                return;
            }

            AppLogger.LogToGui($"VIIPER detached usbip port {port} ({reason}).", false);
        }

        private static IReadOnlyList<UsbipPortBlock> GetImportedPorts()
        {
            if (!TryRunUsbip(new[] { "port" }, out string output, out string error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    AppLogger.LogToGui($"VIIPER could not query usbip ports: {error}", true);
                }

                return Array.Empty<UsbipPortBlock>();
            }

            List<UsbipPortBlock> ports = new List<UsbipPortBlock>();
            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            int currentPort = -1;
            StringBuilder currentBlock = new StringBuilder();

            foreach (string line in lines)
            {
                if (TryParsePortHeader(line, out int port))
                {
                    AddCurrentBlock();
                    currentPort = port;
                    currentBlock.Clear();
                }

                if (currentPort >= 0)
                {
                    currentBlock.AppendLine(line);
                }
            }

            AddCurrentBlock();
            return ports;

            void AddCurrentBlock()
            {
                if (currentPort >= 0)
                {
                    ports.Add(new UsbipPortBlock(currentPort, currentBlock.ToString()));
                }
            }
        }

        private static bool IsLocalViiperPort(UsbipPortBlock port, string remoteBusId)
        {
            string block = port.Block.ToLowerInvariant();
            bool localHost = block.Contains("usbip://localhost:") ||
                block.Contains("usbip://127.0.0.1:") ||
                block.Contains("usbip://[::1]:") ||
                block.Contains("usbip://::1:");
            bool busMatches = string.IsNullOrEmpty(remoteBusId) ||
                block.Contains("/" + remoteBusId.ToLowerInvariant());

            return localHost && busMatches && (IsKnownViiperDevice(block) || !string.IsNullOrEmpty(remoteBusId));
        }

        private static bool IsKnownViiperDevice(string block)
        {
            foreach (string deviceId in KnownViiperDeviceIds)
            {
                if (block.Contains(deviceId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParsePortHeader(string line, out int port)
        {
            port = -1;
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("Port ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int start = "Port ".Length;
            int colon = trimmed.IndexOf(':', start);
            if (colon < 0)
            {
                return false;
            }

            return int.TryParse(trimmed.Substring(start, colon - start), out port);
        }

        private static bool TryRunUsbip(string[] arguments, out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;
            string usbipPath = FindUsbipPath();
            if (string.IsNullOrEmpty(usbipPath))
            {
                error = "usbip.exe was not found.";
                return false;
            }

            try
            {
                using Process process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = usbipPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };

                foreach (string argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.Start();
                if (!process.WaitForExit(4000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    error = "usbip.exe timed out.";
                    return false;
                }

                output = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd().Trim();
                error = string.IsNullOrWhiteSpace(standardError) ? output.Trim() : standardError;
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string FindUsbipPath()
        {
            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string folder in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    continue;
                }

                string candidate = Path.Combine(folder.Trim(), "usbip.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBip", "usbip.exe"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private readonly struct UsbipPortBlock
        {
            public UsbipPortBlock(int port, string block)
            {
                Port = port;
                Block = block ?? string.Empty;
            }

            public int Port { get; }
            public string Block { get; }
        }
    }

    internal sealed class ViiperVirtualDeviceLifetime : IDisposable
    {
        private readonly ViiperVirtualDeviceIdentity virtualDeviceIdentity;
        private readonly Action<int, string> detachPort;
        private readonly Action<int> unregisterPort;
        private readonly Action<uint, string> removeUsbipDevice;
        private readonly Action<ViiperVirtualDeviceIdentity>
            removeNativeDevice;
        private readonly Action detachStalePorts;
        private long streamGeneration;
        private int disposed;

        internal ViiperVirtualDeviceLifetime(uint busId, string devId,
            int usbipPort, Action<uint, string> removeDevice,
            Action<int, string> detachPort = null,
            Action<int> unregisterPort = null,
            Action detachStalePorts = null)
            : this(new ViiperVirtualDeviceIdentity
            {
                TransportMode = ViiperTransportMode.Usbip,
                BusId = busId,
                DevId = devId,
                LogicalLifetimeId = Guid.NewGuid().ToString("N"),
                LegacyUsbipPort = usbipPort,
            }, removeDevice, null, detachPort, unregisterPort,
                detachStalePorts)
        {
        }

        internal ViiperVirtualDeviceLifetime(
            ViiperVirtualDeviceIdentity virtualDeviceIdentity,
            Action<uint, string> removeDevice,
            Action<int, string> detachPort = null,
            Action<int> unregisterPort = null,
            Action detachStalePorts = null)
            : this(virtualDeviceIdentity, removeDevice, null, detachPort,
                unregisterPort, detachStalePorts)
        {
        }

        internal ViiperVirtualDeviceLifetime(
            ViiperVirtualDeviceIdentity virtualDeviceIdentity,
            Action<ViiperVirtualDeviceIdentity> removeNativeDevice,
            Action<int, string> detachPort = null,
            Action<int> unregisterPort = null,
            Action detachStalePorts = null)
            : this(virtualDeviceIdentity, null, removeNativeDevice,
                detachPort, unregisterPort, detachStalePorts)
        {
        }

        private ViiperVirtualDeviceLifetime(
            ViiperVirtualDeviceIdentity virtualDeviceIdentity,
            Action<uint, string> removeUsbipDevice,
            Action<ViiperVirtualDeviceIdentity> removeNativeDevice,
            Action<int, string> detachPort,
            Action<int> unregisterPort,
            Action detachStalePorts)
        {
            this.virtualDeviceIdentity = virtualDeviceIdentity ??
                throw new ArgumentNullException(nameof(virtualDeviceIdentity));
            if (virtualDeviceIdentity.BusId == 0 ||
                string.IsNullOrWhiteSpace(virtualDeviceIdentity.DevId) ||
                string.IsNullOrWhiteSpace(
                    virtualDeviceIdentity.LogicalLifetimeId))
            {
                throw new ArgumentException(
                    "A complete VIIPER logical device identity is required.",
                    nameof(virtualDeviceIdentity));
            }
            if (virtualDeviceIdentity.TransportMode ==
                    ViiperTransportMode.NativeUde &&
                (virtualDeviceIdentity.NativePnpAnchor?.IsExact != true ||
                 removeNativeDevice == null || removeUsbipDevice != null))
            {
                throw new ArgumentException(
                    "Native VIIPER lifetime identity requires exact UdeCx correlation and a receipt-conditioned removal callback.",
                    nameof(virtualDeviceIdentity));
            }
            if (virtualDeviceIdentity.TransportMode ==
                ViiperTransportMode.Usbip)
            {
                if (removeNativeDevice != null)
                {
                    throw new ArgumentException(
                        "USB/IP lifetime identity cannot use native conditional removal.",
                        nameof(removeNativeDevice));
                }
                this.removeUsbipDevice = removeUsbipDevice;
                this.detachPort = detachPort ??
                    ViiperUsbipPortManager.DetachPort;
                this.unregisterPort = unregisterPort ??
                    ViiperUsbipPortManager.UnregisterActivePort;
                this.detachStalePorts = detachStalePorts ??
                    ViiperUsbipPortManager.DetachStaleLocalViiperPorts;
            }
            else
            {
                this.removeNativeDevice = removeNativeDevice;
            }
        }

        internal uint BusId => virtualDeviceIdentity.BusId;

        internal string DevId => virtualDeviceIdentity.DevId;

        internal int UsbipPort => virtualDeviceIdentity.LegacyUsbipPort;

        internal ViiperTransportMode TransportMode =>
            virtualDeviceIdentity.TransportMode;

        internal ViiperVirtualDeviceIdentity VirtualDeviceIdentity =>
            virtualDeviceIdentity.WithStreamGeneration(
                Interlocked.Read(ref streamGeneration));

        internal bool IsDisposed => Volatile.Read(ref disposed) == 1;

        internal ViiperVirtualDeviceIdentity NextStreamIdentity()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(ViiperVirtualDeviceLifetime));
            }
            long generation = Interlocked.Increment(ref streamGeneration);
            return virtualDeviceIdentity.WithStreamGeneration(generation);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            if (TransportMode == ViiperTransportMode.Usbip)
            {
                try
                {
                    detachPort?.Invoke(UsbipPort,
                        "DS4Windows VIIPER device stopped");
                }
                catch
                {
                }

                try
                {
                    unregisterPort?.Invoke(UsbipPort);
                }
                catch
                {
                }
            }

            try
            {
                if (TransportMode == ViiperTransportMode.NativeUde)
                {
                    removeNativeDevice?.Invoke(virtualDeviceIdentity);
                }
                else
                {
                    removeUsbipDevice?.Invoke(BusId, DevId);
                }
            }
            catch
            {
            }

            if (TransportMode == ViiperTransportMode.Usbip)
            {
                try
                {
                    detachStalePorts?.Invoke();
                }
                catch
                {
                }
            }
        }

    }

    internal sealed class ViiperDeviceStream : IDisposable
    {
        private readonly IDisposable transport;
        private readonly Stream stream;
        private readonly ViiperVirtualDeviceLifetime deviceLifetime;
        private readonly ViiperVirtualDeviceIdentity virtualDeviceIdentity;
        private readonly object writeLock = new object();
        private readonly byte[] incomingFrameHeader =
            new byte[FrameV2HeaderLength];
        // Input state and microphone writers share this buffer under
        // writeLock. Reusing it removes the per-frame managed allocation which
        // could otherwise pause the 4 ms physical speaker presenter during a
        // full process GC.
        private byte[] outgoingFrameBuffer =
            new byte[FrameV2HeaderLength + 2048];
        private uint frameSequence;
        private uint incomingFrameSequence;
        private bool incomingFrameSequenceKnown;
        private int transportClosed;
        private const int FrameV2HeaderLength = 16;
        private const byte FrameMagic0 = (byte)'V';
        private const byte FrameMagic1 = (byte)'P';
        private const byte FrameMagic2 = (byte)'C';
        private const byte FrameMagic3 = (byte)'M';
        private const byte FrameVersionV2 = 0x02;
        private const byte FrameVersionV3 = 0x03;
        private const byte FrameVersionV4 = 0x04;
        private const byte FrameVersionV5 = 0x05;

        public ViiperDeviceStream(TcpClient tcp, Stream stream,
            ViiperVirtualDeviceLifetime deviceLifetime)
            : this(stream, tcp, deviceLifetime)
        {
        }

        internal ViiperDeviceStream(Stream stream, IDisposable transport,
            ViiperVirtualDeviceLifetime deviceLifetime)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.deviceLifetime = deviceLifetime ??
                throw new ArgumentNullException(nameof(deviceLifetime));
            virtualDeviceIdentity = deviceLifetime.NextStreamIdentity();
        }

        public uint BusId => deviceLifetime.BusId;

        public string DevId => deviceLifetime.DevId;

        public int UsbipPort => deviceLifetime.UsbipPort;

        internal ViiperTransportMode TransportMode =>
            virtualDeviceIdentity.TransportMode;

        internal ViiperVirtualDeviceIdentity VirtualDeviceIdentity =>
            virtualDeviceIdentity;

        internal ViiperVirtualDeviceLifetime DeviceLifetime => deviceLifetime;

        internal bool IsTransportClosed =>
            Volatile.Read(ref transportClosed) == 1;

        public void Write(byte[] data)
        {
            if (Volatile.Read(ref transportClosed) == 1)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            lock (writeLock)
            {
                if (Volatile.Read(ref transportClosed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ViiperDeviceStream));
                }

                stream.Write(data, 0, data.Length);
            }
        }

        public void WriteFrame(byte version, byte frameType, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (data.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(data));
            }
            if (version != FrameVersionV2 && version != FrameVersionV3 &&
                version != FrameVersionV4 && version != FrameVersionV5)
            {
                throw new ArgumentOutOfRangeException(nameof(version));
            }

            lock (writeLock)
            {
                if (Volatile.Read(ref transportClosed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ViiperDeviceStream));
                }

                int frameLength = FrameV2HeaderLength + data.Length;
                if (outgoingFrameBuffer.Length < frameLength)
                {
                    Array.Resize(ref outgoingFrameBuffer, Math.Max(frameLength,
                        outgoingFrameBuffer.Length * 2));
                }
                byte[] frame = outgoingFrameBuffer;
                frame[0] = FrameMagic0;
                frame[1] = FrameMagic1;
                frame[2] = FrameMagic2;
                frame[3] = FrameMagic3;
                frame[4] = version;
                frame[5] = frameType;
                frame[6] = (byte)data.Length;
                frame[7] = (byte)(data.Length >> 8);
                uint sequence = frameSequence++;
                frame[8] = (byte)sequence;
                frame[9] = (byte)(sequence >> 8);
                frame[10] = (byte)(sequence >> 16);
                frame[11] = (byte)(sequence >> 24);
                Buffer.BlockCopy(data, 0, frame, FrameV2HeaderLength, data.Length);
                uint crc = ComputeFrameV2Crc(frame, frameLength);
                frame[12] = (byte)crc;
                frame[13] = (byte)(crc >> 8);
                frame[14] = (byte)(crc >> 16);
                frame[15] = (byte)(crc >> 24);
                stream.Write(frame, 0, frameLength);
            }
        }

        public byte[] ReadFrame(byte expectedVersion, out byte frameType)
        {
            byte[] header = new byte[FrameV2HeaderLength];
            ReadExactly(header, 0, header.Length);
            if (header[0] != FrameMagic0 || header[1] != FrameMagic1 ||
                header[2] != FrameMagic2 || header[3] != FrameMagic3 ||
                header[4] != expectedVersion)
            {
                throw new IOException("VIIPER returned an invalid framed stream header.");
            }

            int payloadLength = header[6] | header[7] << 8;
            byte[] payload = new byte[payloadLength];
            ReadExactly(payload, 0, payload.Length);

            uint sequence = (uint)(header[8] | header[9] << 8 |
                header[10] << 16 | header[11] << 24);
            if (incomingFrameSequenceKnown && sequence != incomingFrameSequence)
            {
                throw new IOException(
                    $"VIIPER framed output sequence mismatch (expected {incomingFrameSequence}, received {sequence}).");
            }
            incomingFrameSequence = sequence + 1;
            incomingFrameSequenceKnown = true;

            uint receivedCrc = (uint)(header[12] | header[13] << 8 |
                header[14] << 16 | header[15] << 24);
            uint calculatedCrc = ComputeFrameCrc(header, payload);
            if (receivedCrc != calculatedCrc)
            {
                throw new IOException("VIIPER framed output CRC mismatch.");
            }

            frameType = header[5];
            return payload;
        }

        public int ReadFrame(byte expectedVersion, out byte frameType,
            byte[] payloadBuffer)
        {
            if (payloadBuffer == null)
            {
                throw new ArgumentNullException(nameof(payloadBuffer));
            }

            byte[] header = incomingFrameHeader;
            ReadExactly(header, 0, header.Length);
            if (header[0] != FrameMagic0 || header[1] != FrameMagic1 ||
                header[2] != FrameMagic2 || header[3] != FrameMagic3 ||
                header[4] != expectedVersion)
            {
                throw new IOException(
                    "VIIPER returned an invalid framed stream header.");
            }

            int payloadLength = header[6] | header[7] << 8;
            if (payloadLength > payloadBuffer.Length)
            {
                throw new IOException(
                    $"VIIPER framed payload length {payloadLength} exceeds the receive buffer.");
            }
            ReadExactly(payloadBuffer, 0, payloadLength);

            uint sequence = (uint)(header[8] | header[9] << 8 |
                header[10] << 16 | header[11] << 24);
            if (incomingFrameSequenceKnown && sequence != incomingFrameSequence)
            {
                throw new IOException(
                    $"VIIPER framed output sequence mismatch (expected {incomingFrameSequence}, received {sequence}).");
            }
            incomingFrameSequence = sequence + 1;
            incomingFrameSequenceKnown = true;

            uint receivedCrc = (uint)(header[12] | header[13] << 8 |
                header[14] << 16 | header[15] << 24);
            uint calculatedCrc = ComputeFrameCrc(header, payloadBuffer,
                payloadLength);
            if (receivedCrc != calculatedCrc)
            {
                throw new IOException("VIIPER framed output CRC mismatch.");
            }

            frameType = header[5];
            return payloadLength;
        }

        private static uint ComputeFrameV2Crc(byte[] frame)
        {
            return ComputeFrameV2Crc(frame, frame.Length);
        }

        private static uint ComputeFrameV2Crc(byte[] frame, int frameLength)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 4; i < 12; i++)
            {
                crc = UpdateCrc32(crc, frame[i]);
            }
            for (int i = FrameV2HeaderLength; i < frameLength; i++)
            {
                crc = UpdateCrc32(crc, frame[i]);
            }
            return ~crc;
        }

        private static uint ComputeFrameCrc(byte[] header, byte[] payload)
        {
            return ComputeFrameCrc(header, payload, payload.Length);
        }

        private static uint ComputeFrameCrc(byte[] header, byte[] payload,
            int payloadLength)
        {
            uint crc = 0xFFFFFFFFu;
            for (int index = 4; index < 12; index++)
            {
                crc = UpdateCrc32(crc, header[index]);
            }
            for (int index = 0; index < payloadLength; index++)
            {
                crc = UpdateCrc32(crc, payload[index]);
            }
            return ~crc;
        }

        private static uint UpdateCrc32(uint crc, byte value)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }
            return crc;
        }

        public void ReadExactly(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                if (Volatile.Read(ref transportClosed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ViiperDeviceStream));
                }

                int read = stream.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    throw new IOException("VIIPER device stream closed.");
                }

                total += read;
            }
        }

        internal void CloseTransport()
        {
            if (Interlocked.Exchange(ref transportClosed, 1) == 1)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!ReferenceEquals(transport, stream))
                {
                    transport.Dispose();
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            CloseTransport();
            deviceLifetime.Dispose();
        }
    }

    internal static class ViiperStatePacketBuilder
    {
        private const int X360PacketSize = 20;
        private const int DS4PacketSize = 31;
        private const int DualSensePacketSize = 33;
        private const int Switch2PacketSize = 24;
        private const int DualSenseFeedbackPacketSize = 76;
        private const int DualSenseGyroRestDeadband = 32;
        private const int DualSenseAccelRestZ = -8192;
        private const float X360RecipInputPosResolution = 1 / 127f;
        private const float X360RecipInputNegResolution = 1 / 128f;
        private const int X360OutputResolution = 32767 - (-32768);

        public static string GetViiperDeviceName(ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => "xbox360",
                ViiperVirtualDeviceType.DualShock4 => "dualshock4",
                ViiperVirtualDeviceType.DualSense => "dualsensegamepadv5",
                ViiperVirtualDeviceType.DualSenseEdge =>
                    "dualsenseedgegamepadv5",
                ViiperVirtualDeviceType.Switch2Pro => "ns2pro",
                _ => "xbox360",
            };
        }

        public static int GetFeedbackLength(ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => 2,
                ViiperVirtualDeviceType.DualShock4 => 7,
                ViiperVirtualDeviceType.DualSense => DualSenseFeedbackPacketSize,
                ViiperVirtualDeviceType.DualSenseEdge => DualSenseFeedbackPacketSize,
                ViiperVirtualDeviceType.Switch2Pro => 34,
                _ => 0,
            };
        }

        public static byte[] Build(ViiperVirtualDeviceType type, DS4State state, int device)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => BuildXbox360(state, device),
                ViiperVirtualDeviceType.DualShock4 => BuildDualShock4(state, device),
                ViiperVirtualDeviceType.DualSense => BuildDualSense(state, device),
                ViiperVirtualDeviceType.DualSenseEdge => BuildDualSense(state, device),
                ViiperVirtualDeviceType.Switch2Pro => BuildSwitch2Pro(state, device),
                _ => BuildXbox360(state, device),
            };
        }

        public static byte[] BuildNeutral(ViiperVirtualDeviceType type)
        {
            return Build(type, CreateNeutralState(), -1);
        }

        public static DS4State CreateNeutralState()
        {
            return new DS4State
            {
                LX = 128,
                LY = 128,
                RX = 128,
                RY = 128,
            };
        }

        private static byte[] BuildXbox360(DS4State state, int device)
        {
            byte[] packet = new byte[X360PacketSize];
            uint buttons = 0;
            if (state.DpadUp) buttons |= 0x0001;
            if (state.DpadDown) buttons |= 0x0002;
            if (state.DpadLeft) buttons |= 0x0004;
            if (state.DpadRight) buttons |= 0x0008;
            if (state.Options) buttons |= 0x0010;
            if (state.Share) buttons |= 0x0020;
            if (state.L3) buttons |= 0x0040;
            if (state.R3) buttons |= 0x0080;
            if (state.L1) buttons |= 0x0100;
            if (state.R1) buttons |= 0x0200;
            if (state.PS) buttons |= 0x0400;
            if (state.Cross) buttons |= 0x1000;
            if (state.Circle) buttons |= 0x2000;
            if (state.Square) buttons |= 0x4000;
            if (state.Triangle) buttons |= 0x8000;

            byte l2 = state.L2;
            byte r2 = state.R2;
            short lx = AxisScaleX360(state.LX, false);
            short ly = AxisScaleX360(state.LY, true);
            short rx = AxisScaleX360(state.RX, false);
            short ry = AxisScaleX360(state.RY, true);

            ApplySteeringWheelX360(state, device, ref l2, ref r2, ref lx, ref ly, ref rx, ref ry);

            WriteUInt32(packet, 0, buttons);
            packet[4] = l2;
            packet[5] = r2;
            WriteInt16(packet, 6, lx);
            WriteInt16(packet, 8, ly);
            WriteInt16(packet, 10, rx);
            WriteInt16(packet, 12, ry);
            return packet;
        }

        private static byte[] BuildDualShock4(DS4State state, int device)
        {
            byte[] packet = new byte[DS4PacketSize];
            byte lx = state.LX;
            byte ly = state.LY;
            byte rx = state.RX;
            byte ry = state.RY;
            byte l2 = state.L2;
            byte r2 = state.R2;
            ApplySteeringWheelByteAxes(state, device, ref l2, ref r2, ref lx, ref ly, ref rx, ref ry);

            packet[0] = ToSignedAxisByte(lx);
            packet[1] = ToSignedAxisByte(ly);
            packet[2] = ToSignedAxisByte(rx);
            packet[3] = ToSignedAxisByte(ry);
            WriteUInt16(packet, 4, BuildDualShock4Buttons(state));
            packet[6] = BuildDPadBits(state);
            packet[7] = l2;
            packet[8] = r2;
            WriteTouch(packet, 9, state.TrackPadTouch0, 1920, 942);
            WriteTouch(packet, 14, state.TrackPadTouch1, 1920, 942);
            WriteSonyMotion(packet, 19, state, 0, 0);
            return packet;
        }

        private static byte[] BuildDualSense(DS4State state, int device)
        {
            byte[] packet = new byte[DualSensePacketSize];
            byte lx = state.LX;
            byte ly = state.LY;
            byte rx = state.RX;
            byte ry = state.RY;
            byte l2 = state.L2;
            byte r2 = state.R2;
            ApplySteeringWheelByteAxes(state, device, ref l2, ref r2, ref lx, ref ly, ref rx, ref ry);

            packet[0] = ToSignedAxisByte(lx);
            packet[1] = ToSignedAxisByte(ly);
            packet[2] = ToSignedAxisByte(rx);
            packet[3] = ToSignedAxisByte(ry);
            WriteUInt32(packet, 4, BuildDualSenseButtons(state));
            packet[8] = BuildDPadBits(state);
            packet[9] = l2;
            packet[10] = r2;
            WriteDualSenseTouch(packet, 11, state.TrackPadTouch0, 1920, 1080);
            WriteDualSenseTouch(packet, 16, state.TrackPadTouch1, 1920, 1080);
            WriteSonyMotion(packet, 21, state, DualSenseGyroRestDeadband, DualSenseAccelRestZ);
            return packet;
        }

        private static byte[] BuildSwitch2Pro(DS4State state, int device)
        {
            byte[] packet = new byte[Switch2PacketSize];
            ushort lx = ScaleSwitchAxis(state.LX);
            ushort ly = ScaleSwitchAxis(state.LY);
            ushort rx = ScaleSwitchAxis(state.RX);
            ushort ry = ScaleSwitchAxis(state.RY);
            ApplySteeringWheelSwitchAxes(state, device, ref lx, ref ly, ref rx, ref ry);

            WriteUInt32(packet, 0, BuildSwitch2Buttons(state));
            WriteUInt16(packet, 4, lx);
            WriteUInt16(packet, 6, ly);
            WriteUInt16(packet, 8, rx);
            WriteUInt16(packet, 10, ry);
            WriteInt16(packet, 12, ClampShort(state.Motion?.accelXFull ?? 0));
            WriteInt16(packet, 14, ClampShort(state.Motion?.accelYFull ?? 0));
            WriteInt16(packet, 16, ClampShort(state.Motion?.accelZFull ?? 0));
            WriteInt16(packet, 18, ClampShort(state.Motion?.gyroYawFull ?? 0));
            WriteInt16(packet, 20, ClampShort(state.Motion?.gyroPitchFull ?? 0));
            WriteInt16(packet, 22, ClampShort(state.Motion?.gyroRollFull ?? 0));
            return packet;
        }

        private static ushort BuildDualShock4Buttons(DS4State state)
        {
            ushort buttons = 0;
            if (state.Square) buttons |= 0x0010;
            if (state.Cross) buttons |= 0x0020;
            if (state.Circle) buttons |= 0x0040;
            if (state.Triangle) buttons |= 0x0080;
            if (state.L1) buttons |= 0x0100;
            if (state.R1) buttons |= 0x0200;
            if (state.L2Btn || state.L2 > 0) buttons |= 0x0400;
            if (state.R2Btn || state.R2 > 0) buttons |= 0x0800;
            if (state.Share) buttons |= 0x1000;
            if (state.Options) buttons |= 0x2000;
            if (state.L3) buttons |= 0x4000;
            if (state.R3) buttons |= 0x8000;
            if (state.PS) buttons |= 0x0001;
            if (state.OutputTouchButton || state.TouchButton) buttons |= 0x0002;
            return buttons;
        }

        private static uint BuildDualSenseButtons(DS4State state)
        {
            uint buttons = 0;
            if (state.Square) buttons |= 0x00000010;
            if (state.Cross) buttons |= 0x00000020;
            if (state.Circle) buttons |= 0x00000040;
            if (state.Triangle) buttons |= 0x00000080;
            if (state.L1) buttons |= 0x00000100;
            if (state.R1) buttons |= 0x00000200;
            if (state.L2Btn || state.L2 > 0) buttons |= 0x00000400;
            if (state.R2Btn || state.R2 > 0) buttons |= 0x00000800;
            if (state.Share) buttons |= 0x00001000;
            if (state.Options) buttons |= 0x00002000;
            if (state.L3) buttons |= 0x00004000;
            if (state.R3) buttons |= 0x00008000;
            if (state.PS) buttons |= 0x00010000;
            if (state.OutputTouchButton || state.TouchButton) buttons |= 0x00020000;
            if (state.Mute) buttons |= 0x00040000;
            if (state.FnL) buttons |= 0x00100000;
            if (state.FnR) buttons |= 0x00200000;
            if (state.BLP) buttons |= 0x00400000;
            if (state.BRP) buttons |= 0x00800000;
            return buttons;
        }

        private static uint BuildSwitch2Buttons(DS4State state)
        {
            uint buttons = 0;
            if (state.Cross) buttons |= 1u << 0;
            if (state.Circle) buttons |= 1u << 1;
            if (state.Square) buttons |= 1u << 2;
            if (state.Triangle) buttons |= 1u << 3;
            if (state.R1) buttons |= 1u << 4;
            if (state.R2Btn || state.R2 > 0) buttons |= 1u << 5;
            if (state.Options) buttons |= 1u << 6;
            if (state.R3) buttons |= 1u << 7;
            if (state.DpadDown) buttons |= 1u << 8;
            if (state.DpadRight) buttons |= 1u << 9;
            if (state.DpadLeft) buttons |= 1u << 10;
            if (state.DpadUp) buttons |= 1u << 11;
            if (state.L1) buttons |= 1u << 12;
            if (state.L2Btn || state.L2 > 0) buttons |= 1u << 13;
            if (state.Share) buttons |= 1u << 14;
            if (state.L3) buttons |= 1u << 15;
            if (state.PS) buttons |= 1u << 16;
            if (state.Capture) buttons |= 1u << 17;
            if (state.FnR || state.BRP || state.SideR) buttons |= 1u << 18;
            if (state.FnL || state.BLP || state.SideL) buttons |= 1u << 19;
            if (state.Mute) buttons |= 1u << 21;
            return buttons;
        }

        private static byte BuildDPadBits(DS4State state)
        {
            byte dpad = 0;
            if (state.DpadUp) dpad |= 0x01;
            if (state.DpadDown) dpad |= 0x02;
            if (state.DpadLeft) dpad |= 0x04;
            if (state.DpadRight) dpad |= 0x08;
            return dpad;
        }

        private static void WriteTouch(byte[] packet, int offset, DS4State.TrackPadTouch touch, int maxX, int maxY)
        {
            ushort x = (ushort)Math.Clamp(touch.X, 0, maxX);
            ushort y = (ushort)Math.Clamp(touch.Y, 0, maxY);
            WriteUInt16(packet, offset, x);
            WriteUInt16(packet, offset + 2, y);
            packet[offset + 4] = touch.IsActive ? (byte)1 : (byte)0;
        }

        private static void WriteDualSenseTouch(byte[] packet, int offset, DS4State.TrackPadTouch touch, int maxX, int maxY)
        {
            ushort x = (ushort)Math.Clamp(touch.X, 0, maxX);
            ushort y = (ushort)Math.Clamp(touch.Y, 0, maxY);
            WriteUInt16(packet, offset, x);
            WriteUInt16(packet, offset + 2, y);

            byte tracking = touch.RawTrackingNum;
            if (tracking == 0 && !touch.IsActive)
            {
                tracking = 0x80;
            }
            else if (touch.IsActive)
            {
                tracking = (byte)(tracking & 0x7f);
            }

            packet[offset + 4] = tracking;
        }

        private static void WriteSonyMotion(byte[] packet, int offset, DS4State state, int gyroDeadband, int restAccelZ)
        {
            SixAxis motion = state.Motion;
            if (motion == null)
            {
                WriteInt16(packet, offset, 0);
                WriteInt16(packet, offset + 2, 0);
                WriteInt16(packet, offset + 4, 0);
                WriteInt16(packet, offset + 6, 0);
                WriteInt16(packet, offset + 8, 0);
                WriteInt16(packet, offset + 10, ClampShort(restAccelZ));
                return;
            }

            int gyroX = SnapToZero(motion.gyroPitchFull, gyroDeadband);
            int gyroY = SnapToZero(-motion.gyroYawFull, gyroDeadband);
            int gyroZ = SnapToZero(-motion.gyroRollFull, gyroDeadband);
            int accelX = -motion.accelXFull;
            int accelY = -motion.accelYFull;
            int accelZ = motion.accelZFull;
            if (accelX == 0 && accelY == 0 && accelZ == 0)
            {
                accelZ = restAccelZ;
            }

            WriteInt16(packet, offset, ClampShort(gyroX));
            WriteInt16(packet, offset + 2, ClampShort(gyroY));
            WriteInt16(packet, offset + 4, ClampShort(gyroZ));
            WriteInt16(packet, offset + 6, ClampShort(accelX));
            WriteInt16(packet, offset + 8, ClampShort(accelY));
            WriteInt16(packet, offset + 10, ClampShort(accelZ));
        }

        private static int SnapToZero(int value, int deadband)
        {
            return Math.Abs((long)value) <= deadband ? 0 : value;
        }

        private static void ApplySteeringWheelX360(DS4State state, int device, ref byte l2, ref byte r2, ref short lx, ref short ly, ref short rx, ref short ry)
        {
            if (device < 0)
            {
                return;
            }

            short wheel = (short)Math.Clamp(state.SASteeringWheelEmulationUnit, short.MinValue, short.MaxValue);
            switch (Global.GetSASteeringWheelEmulationAxis(device))
            {
                case SASteeringWheelEmulationAxisType.LX:
                    lx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.LY:
                    ly = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RX:
                    rx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RY:
                    ry = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.L2R2:
                    l2 = r2 = 0;
                    if (wheel >= 0)
                    {
                        l2 = (byte)Math.Clamp(wheel / 128, 0, 255);
                    }
                    else
                    {
                        r2 = (byte)Math.Clamp(-wheel / 128, 0, 255);
                    }
                    break;
            }
        }

        private static void ApplySteeringWheelByteAxes(DS4State state, int device, ref byte l2, ref byte r2, ref byte lx, ref byte ly, ref byte rx, ref byte ry)
        {
            if (device < 0)
            {
                return;
            }

            byte wheel = (byte)Math.Clamp(state.SASteeringWheelEmulationUnit, 0, 255);
            switch (Global.GetSASteeringWheelEmulationAxis(device))
            {
                case SASteeringWheelEmulationAxisType.LX:
                    lx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.LY:
                    ly = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RX:
                    rx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RY:
                    ry = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.L2R2:
                    l2 = wheel >= 128 ? (byte)((wheel - 128) * 2) : (byte)0;
                    r2 = wheel < 128 ? (byte)((128 - wheel) * 2) : (byte)0;
                    break;
            }
        }

        private static void ApplySteeringWheelSwitchAxes(DS4State state, int device, ref ushort lx, ref ushort ly, ref ushort rx, ref ushort ry)
        {
            if (device < 0)
            {
                return;
            }

            ushort wheel = (ushort)Math.Clamp(state.SASteeringWheelEmulationUnit, 0, 4095);
            switch (Global.GetSASteeringWheelEmulationAxis(device))
            {
                case SASteeringWheelEmulationAxisType.LX:
                    lx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.LY:
                    ly = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RX:
                    rx = wheel;
                    break;
                case SASteeringWheelEmulationAxisType.RY:
                    ry = wheel;
                    break;
            }
        }

        private static byte ToSignedAxisByte(byte value)
        {
            return unchecked((byte)((sbyte)Math.Clamp(value - 128, sbyte.MinValue, sbyte.MaxValue)));
        }

        private static short AxisScaleX360(int value, bool flip)
        {
            unchecked
            {
                value -= 0x80;
                float recipRun = value >= 0 ? X360RecipInputPosResolution : X360RecipInputNegResolution;

                float temp = value * recipRun;
                if (flip)
                {
                    temp = -temp;
                }

                temp = (temp + 1.0f) * 0.5f;
                return (short)(temp * X360OutputResolution + (-32768));
            }
        }

        private static ushort ScaleSwitchAxis(byte value)
        {
            return (ushort)Math.Clamp((value * 4095 + 127) / 255, 0, 4095);
        }

        private static short ClampShort(int value)
        {
            return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        private static void WriteUInt16(byte[] packet, int offset, ushort value)
        {
            packet[offset] = (byte)(value & 0xff);
            packet[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        private static void WriteInt16(byte[] packet, int offset, short value)
        {
            WriteUInt16(packet, offset, unchecked((ushort)value));
        }

        private static void WriteUInt32(byte[] packet, int offset, uint value)
        {
            packet[offset] = (byte)(value & 0xff);
            packet[offset + 1] = (byte)((value >> 8) & 0xff);
            packet[offset + 2] = (byte)((value >> 16) & 0xff);
            packet[offset + 3] = (byte)((value >> 24) & 0xff);
        }
    }
}
