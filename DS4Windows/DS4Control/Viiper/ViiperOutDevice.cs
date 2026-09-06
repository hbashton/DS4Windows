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
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Concentus;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
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
        internal const int DefaultControllerRateHz = 1000;
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
                case ViiperVirtualDeviceType.Xbox360:
                case ViiperVirtualDeviceType.XboxOne:
                case ViiperVirtualDeviceType.DualShock4:
                case ViiperVirtualDeviceType.Switch2Pro:
                    // Every virtual controller exposes a one-millisecond
                    // maximum input opportunity. This remains adaptive rather
                    // than becoming a polling loop: the writer wakes only for
                    // fresh mapped physical reports. Xbox 360 journals
                    // discrete boundaries and coalesces continuous state;
                    // the other legacy paths retain latest-state behavior.
                    return DefaultControllerRateHz;
                case ViiperVirtualDeviceType.DualSense:
                case ViiperVirtualDeviceType.DualSenseEdge:
                    // V5 input is event-driven here. VIIPER's advertised
                    // interrupt endpoint is the one authoritative 1 ms
                    // presentation clock.
                    return 0;
                default:
                    return DefaultControllerRateHz;
            }
        }

        internal static int ResolveConfiguredRateHz(
            ViiperVirtualDeviceType deviceType, string configuredValue)
        {
            int defaultRate = GetDefaultRateHz(deviceType);
            return Parse(configuredValue, defaultRate);
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

        internal static long AdvanceAbsoluteDeadline(long deadline, long now,
            long intervalTicks)
        {
            if (intervalTicks <= 0)
            {
                return 0;
            }
            if (deadline <= 0 || now - deadline >= intervalTicks)
            {
                return now + intervalTicks;
            }
            return deadline + intervalTicks;
        }
    }

    public enum ViiperVirtualDeviceType
    {
        Xbox360,
        DualShock4,
        DualSense,
        DualSenseEdge,
        Switch2Pro,
        XboxOne,
    }

    public sealed class ViiperOutDevice : OutputDevice
    {
        internal enum NativeGameOwnerProcessLiveness
        {
            Unknown,
            Running,
            ConfirmedExited,
        }

        private const string DefaultHost = "127.0.0.1";
        private const int DefaultPort = 3242;
        private const int DualSenseBaseFeedbackLength = 6;
        private const int DualSenseTriggerFeedbackOffset = 6;
        // VIIPER sends compact feedback, not a full native HID output report:
        // base rumble/LED bytes plus two native-spaced trigger effect blocks.
        private const int DualSenseTriggerEffectLength = 11;
        private const int DualSenseCompatExtendedFeedbackLength = DualSenseBaseFeedbackLength + (DualSenseTriggerEffectLength * 2);
        private const int DualSenseNativeOutputReportLength = 48;
        private static readonly byte[] SdlDualSenseAutomaticPlayerZeroLedReport =
        {
            0x02, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x40,
        };
        private const int DualSenseNativeOutputReportOffset = DualSenseCompatExtendedFeedbackLength;
        private const int DualSenseBluetoothHapticsReportLength = 141;
        private const int DualSenseBluetoothHapticsReportOffset = DualSenseNativeOutputReportOffset + DualSenseNativeOutputReportLength;
        private const int DualSenseExtendedFeedbackLength = DualSenseBluetoothHapticsReportOffset + DualSenseBluetoothHapticsReportLength;
        private const int DualSenseCombinedBluetoothReportLength = 398;
        private const int DualSenseCombinedBluetoothReportOffset = DualSenseNativeOutputReportOffset + DualSenseNativeOutputReportLength;
        private const int DualSenseCombinedExtendedFeedbackLength = DualSenseCombinedBluetoothReportOffset + DualSenseCombinedBluetoothReportLength;
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
        private const int MaximumCompressedMicrophoneFrameLength = 512;
        private const int MaxPreparedMicrophoneFrames = 16;
        private const int MaximumPreparedMicrophonePayloadLength =
            DualSenseMicrophonePcmFrameLength;
        private const int MaximumInputBurstBeforeDueMicrophone = 8;
        private const byte ViiperStreamFrameInputState = 0x01;
        private const byte ViiperStreamFrameMicrophonePcm = 0x02;
        private const byte ViiperStreamFrameOutputState = 0x81;
        private const byte ViiperStreamFrameSpeakerPcm = 0x82;
        private const byte ViiperStreamFrameAtomicAudioHaptics = 0x83;
        private const byte ViiperStreamFrameRealtimeHaptics = 0x84;
        private const byte ViiperStreamFrameMicrophoneInterfaceState = 0x85;
        private const int MicrophoneInterfaceStatePayloadLength = 9;
        private const byte ViiperStreamFrameVersionV3 = 0x03;
        private const byte ViiperStreamFrameVersionV5 = 0x05;
        private const byte FeedbackSpeakerKindPcm = 0;
        private const byte FeedbackSpeakerKindAtomicAudioHaptics = 1;
        private const byte FeedbackSpeakerKindRealtimeHaptics = 2;
        private const int AtomicAudioHapticsFeedbackLengthPrefix = 2;
        private const int MaxStreamRecoveryAttempts = 8;
        private const int InitialStreamRecoveryBackoffMilliseconds = 50;
        private const int MaximumStreamRecoveryBackoffMilliseconds = 1000;
        private const int MicrophoneDisableRetryMilliseconds = 250;
        private const int MicrophoneInterfaceCompatibilityPollMilliseconds =
            1000;
        // Virtual speaker formats have different proven buffering contracts.
        // V5 bounds DualSense latency with an eight-carrier newest-wins
        // FIFO and never rejects a carrier based on wall-clock age. A paused
        // consumer therefore resumes from the live eight-frame window instead
        // of turning a scheduling pause into an artificial silence interval.
        // DualShock 4 keeps its separate historical reserve. A 4 KiB slot
        // covers either virtual format without allocations.
        private const int FeedbackSpeakerSlotLength = 4096;
        internal const int DualSenseFeedbackSpeakerQueueCapacity = 8;
        internal const int DualSenseFeedbackSpeakerMaximumAgeMilliseconds = 0;
        internal const int DualShock4FeedbackSpeakerQueueCapacity = 16;
        internal const int DualShock4FeedbackSpeakerMaximumAgeMilliseconds = 0;
        // Native DualSense feedback arrives at roughly 150 Hz and is valid for
        // only 30 ms in the physical combined transport. Four ordered reports
        // preserve waveform continuity while preventing a stalled callback
        // from turning old game effects into almost a second of input lag.
        internal const int FeedbackOrderedControlQueueCapacity = 4;
        internal const int FeedbackOrderedControlMaximumAgeMilliseconds = 20;

        // One-shot transport diagnostics identify the persistent VIIPER
        // stream that receives native media without adding periodic logging
        // or allocations to the realtime path.
        private long feedbackFramesRead;
        private long feedbackAtomicFramesRead;
        private long feedbackAtomicFramesQueued;
        private int feedbackFirstFrameLogged;
        private int feedbackFirstAtomicResultLogged;
        private readonly object nativeGameOutputTraceLock = new object();
        private readonly byte[] lastNativeGameOutputReport =
            new byte[DualSenseNativeOutputReportLength];
        private long lastNativeGameOutputTimestamp;
        private long lastNativeGameOutputRevision;
        private long lastNativeGameOutputStreamGeneration;
        private DualSenseDevice lastNativeGameOutputTargetDevice;
        private long sdlAutomaticLedCandidateRevision;
        private long sdlAutomaticLedCandidateStreamGeneration;
        private DualSenseDevice sdlAutomaticLedCandidateTargetDevice;
        private int nativeGameOutputSessionActive;
        private int nativeGameOutputRealFeedbackEpoch;
        private Process nativeGameOutputOwnerProcess;
        private int nativeGameOutputOwnerProcessId;
        private long nativeGameOutputOwnerRevision;
        private long nativeGameOutputOwnerStreamGeneration;
        private DualSenseDevice nativeGameOutputOwnerTargetDevice;
        private bool nativeGameOutputOwnerHasVerifiedVisualClaim;
        private bool nativeGameOutputOwnerHasUnverifiedVisualClaim;

        private readonly OutContType outputType;
        private readonly ViiperVirtualDeviceType viiperType;
        private readonly bool audioOnlySidecar;
        private readonly bool gamepadOnly;
        private readonly ViiperClient client;
        private readonly ViiperInputScheduler inputScheduler = new();
        private readonly Xbox360EgressScheduler xbox360EgressScheduler;
        private readonly XboxOneEgressScheduler xboxOneEgressScheduler;
        private readonly Switch2EgressScheduler switch2EgressScheduler;
        private readonly int xbox360MaximumOrderedAgeMilliseconds;
        private readonly string xbox360PresentationPolicyError;
        private readonly int xboxOneMaximumOrderedAgeMilliseconds;
        private readonly string xboxOnePresentationPolicyError;
        private readonly int switch2MaximumOrderedAgeMilliseconds;
        private readonly string switch2PresentationPolicyError;
        private readonly object orderedEgressLifecycleLock = new object();
        private readonly OrderedEgressWriterAdmissionGate
            orderedEgressWriterAdmissionGate = new();
        private readonly object xbox360ResynchronizationLock = new object();
        private readonly object xboxOneResynchronizationLock = new object();
        private readonly object switch2ResynchronizationLock = new object();
        private readonly ViiperLatencyHistogram mappedReadyToPublishLatency =
            new();
        private readonly ViiperLatencyHistogram publishToWriterClaimLatency =
            new();
        private readonly ViiperLatencyHistogram claimToSocketStartLatency =
            new();
        private readonly ViiperLatencyHistogram socketWriteLatency = new();
        private readonly ViiperLatencyHistogram xboxOneBrokerAcceptanceLatency =
            new();
        private readonly ViiperLatencyHistogram xboxOneAckWakeLatency = new();
        private readonly byte[] stateWriterPacket =
            new byte[53];
        private readonly ViiperHighResolutionWaiter stateRateWaiter = new();
        private readonly object pendingPacketLock = new object();
        private readonly object microphoneQueueLock = new object();
        private readonly object preparedMicrophoneQueueLock = new object();
        private readonly object microphoneProcessingLock = new object();
        private readonly object writerThreadLock = new object();
        private readonly object microphoneWriterThreadLock = new object();
        private readonly object switch2RuntimeStatusLifecycleLock =
            new object();
        private readonly object switch2RuntimeStatusLock = new object();
        private readonly ViiperStreamRecoveryGate streamRecoveryGate = new();
        private readonly object feedbackThreadLock = new object();
        private readonly object feedbackDispatchThreadLock = new object();
        private readonly object virtualSpeakerSubscriberLock = new object();
        private readonly object feedbackCallbackAdmissionLock = new object();
        private readonly ManualResetEvent feedbackCallbacksIdle =
            new ManualResetEvent(true);
        private readonly ManualResetEvent xboxOneTerminalFeedbackAcknowledged =
            new ManualResetEvent(false);
        private const int XboxOneTerminalFeedbackAckWaitMilliseconds = 500;
        internal Action NativeGameLedReleaseAdmissionTestHook;
        private int activeFeedbackCallbacks;
        private readonly object physicalDualSenseIdentityLock = new object();
        private readonly object microphoneSourceLock = new object();
        // Orders physical microphone enable/disable intent without being held
        // across controller I/O. A stale completion repairs the same physical
        // source to the newest desired state before returning.
        private long microphoneControlEpoch;
        private readonly object legacyDualSenseRumbleLock = new object();
        private readonly AutoResetEvent writerSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent writerRateWaitStopSignal =
            new ManualResetEvent(false);
        private readonly AutoResetEvent microphoneWriterSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent feedbackSpeakerSignal =
            new AutoResetEvent(false);
        private readonly AutoResetEvent feedbackControlSignal =
            new AutoResetEvent(false);
        private readonly AutoResetEvent switch2RuntimeStatusSignal =
            new AutoResetEvent(false);
        private readonly ManualResetEvent switch2RuntimeStatusStopSignal =
            new ManualResetEvent(true);
        private readonly ManualResetEvent microphoneInterfaceStopSignal = new ManualResetEvent(false);
        private readonly AutoResetEvent microphoneInterfaceStateSignal =
            new AutoResetEvent(false);
        private readonly byte[][] pendingMicrophoneFrameSlots =
            CreateFixedBuffers(MaxPendingMicrophoneFrames,
                MaximumCompressedMicrophoneFrameLength);
        private readonly MicrophoneCodec[] pendingMicrophoneCodecs =
            new MicrophoneCodec[MaxPendingMicrophoneFrames];
        private readonly int[] pendingMicrophoneLengths =
            new int[MaxPendingMicrophoneFrames];
        private readonly ushort[] pendingMicrophoneSequences =
            new ushort[MaxPendingMicrophoneFrames];
        private readonly bool[] pendingMicrophoneHasSequences =
            new bool[MaxPendingMicrophoneFrames];
        private readonly long[] pendingMicrophoneSourceGenerations =
            new long[MaxPendingMicrophoneFrames];
        private readonly byte[] microphoneCompressedWorkBuffer =
            new byte[MaximumCompressedMicrophoneFrameLength];
        private readonly byte[][] preparedMicrophoneFrames =
            CreateFixedBuffers(MaxPreparedMicrophoneFrames,
                MaximumPreparedMicrophonePayloadLength);
        private readonly int[] preparedMicrophoneLengths =
            new int[MaxPreparedMicrophoneFrames];
        private readonly long[] preparedMicrophoneTimestamps =
            new long[MaxPreparedMicrophoneFrames];
        private readonly byte[] microphoneTransportPayload =
            new byte[MaximumPreparedMicrophonePayloadLength];
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
        private Thread switch2RuntimeStatusThread;
        private byte[] pendingStatePacket;
        private long pendingStatePacketQueuedTimestamp;
        private long orderedEgressOwnedPresentationGeneration;
        private long orderedEgressAdmissionGeneration;
        private OrderedEgressPublicationLease xbox360PendingResynchronizationLease;
        private Xbox360EgressState xbox360PendingResynchronizationState;
        private long xbox360PendingResynchronizationTimestamp;
        private bool xbox360PendingResynchronization;
        private OrderedEgressPublicationLease xboxOnePendingResynchronizationLease;
        private XboxOneEgressState xboxOnePendingResynchronizationState;
        private long xboxOnePendingResynchronizationTimestamp;
        private bool xboxOnePendingResynchronization;
        private OrderedEgressPublicationLease switch2PendingResynchronizationLease;
        private Switch2EgressState switch2PendingResynchronizationState;
        private long switch2PendingResynchronizationTimestamp;
        private bool switch2PendingResynchronization;
        private int preparedMicrophoneHead;
        private int preparedMicrophoneCount;
        private int pendingMicrophoneHead;
        private int pendingMicrophoneCount;
        private IOpusDecoder microphoneDecoder;
        private SbcDecoder microphoneSbcDecoder;
        private DS4Device microphoneSourceDevice;
        private long microphoneSourceGeneration;
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
        private readonly object triggerLabRumbleLock = new object();
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
        private bool activeStreamSupportsRealtimeHaptics;
        private bool activeStreamUsesV5AudioSource;
        private bool activeStreamUsesAudioOnlyDescriptor;
        private bool activeStreamSupportsMicrophoneInterfaceEvents;
        private bool activeStreamSupportsRawInputStatus;
        private byte activeStreamFrameVersion;
        private int microphoneVolume = 128;
        private int microphoneNoiseSuppression = (int)DualSenseMicrophoneNoiseSuppression.Balanced;
        private long lastMicrophoneCompressedRxTimestamp;
        private long lastMicrophoneProcessedTimestamp;
        private long lastMicrophoneSubmittedTimestamp;
        private long lastMicrophoneArmTimestamp;
        private long streamGeneration;
        private long faultedRuntimeStreamGeneration = -1;
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
        private long nextStateWriteDeadline;
        private int stateWriteRateHz;
        private long stateWriteMinimumIntervalTicks;
        private DateTime lastWriterHealthLogUtc = DateTime.MinValue;
        private DateTime lastMicrophoneHealthLogUtc = DateTime.MinValue;
        private int lastInputDeviceIndex = -1;
        private DualSenseDevice publishedPhysicalControllerTargetDevice;
        private int submitFailureLogged;
        private int microphoneUnavailableLogged;
        private int microphoneNoiseSuppressionUnavailableLogged;
        private int microphoneProcessingFailureLogged;
        private int microphoneMuted;
        private int virtualMicrophoneInterfaceActive;
        private int virtualMicrophoneInterfaceStateKnown;
        private long virtualMicrophoneInterfaceRemoteGeneration;
        private int virtualMicrophoneInterfaceRemoteGenerationKnown;
        private ViiperMicrophoneBufferSnapshot virtualMicrophoneBufferSnapshot =
            ViiperMicrophoneBufferSnapshot.Empty;
        private Switch2RuntimeInputDevice switch2RuntimeStatusSource;
        private ViiperSwitch2RuntimeStatusV1 pendingSwitch2RuntimeStatus;
        private bool hasPendingSwitch2RuntimeStatus;
        private int switch2RuntimeStatusFailureLogged;
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
        private long switch2FeedbackValidated;
        private long switch2FeedbackRejected;
        private long switch2RumbleFramesPreserved;
        private long switch2LedOnlyFramesPreserved;
        private Switch2VirtualFeedbackSession switch2FeedbackSession;
        private readonly Switch2DualSenseFeedbackPolicyLane
            switch2DualSenseFeedbackPolicyLane;
        private int switch2DualSensePolicyRefreshRequested;
        private Switch2XboxFeedbackPolicyRequest switch2XboxPolicyRefreshRequested;
        private readonly Func<Switch2XboxFeedbackPolicy> readSwitch2XboxLivePolicy;
        private readonly Predicate<Switch2XboxFeedbackPolicyRequest> isCurrentSwitch2XboxPolicyRequest;
        private XboxOnePhysicalOutputSuppressionRequest xboxOnePhysicalOutputSuppressionRequested;
        private XboxOnePhysicalFeedbackSession xboxOnePhysicalFeedbackSession;
        private XboxOneAuthorizedFeedbackBinding xboxOneFeedbackBinding;
        private ulong xboxOneLastFeedbackSequence;
        private int xboxOneSwitch2FeedbackPreRetired;
        private int activeFeedbackLength;
        private string physicalDualSenseIdentityPath;
        private bool physicalDualSenseIdentityVerified;
        private readonly byte[] lastR2TriggerFeedback = new byte[DualSenseTriggerEffectLength];
        private readonly byte[] lastL2TriggerFeedback = new byte[DualSenseTriggerEffectLength];

        private enum MicrophoneCodec : byte
        {
            Opus,
            Sbc,
        }

        private readonly struct PendingMicrophoneFrame
        {
            public PendingMicrophoneFrame(MicrophoneCodec codec, byte[] data,
                int length, ushort sequence = 0, bool hasSequence = false)
            {
                Codec = codec;
                Data = data;
                Length = length;
                Sequence = sequence;
                HasSequence = hasSequence;
            }

            public MicrophoneCodec Codec { get; }
            public byte[] Data { get; }
            public int Length { get; }
            public ushort Sequence { get; }
            public bool HasSequence { get; }
        }

        /// <summary>
        /// Captures the exact active ordered-egress producer lifecycle before a
        /// physical report is projected. The signed presentation field keeps
        /// all shared 64-bit reads interlocked while preserving the scheduler's
        /// complete unsigned generation bits.
        /// </summary>
        private readonly struct OrderedEgressPublicationLease
        {
            public OrderedEgressPublicationLease(long writerGeneration,
                long presentationGeneration,
                long admissionGeneration,
                OrderedEgressProducerEpoch producerEpoch)
            {
                WriterGeneration = writerGeneration;
                PresentationGenerationBits = presentationGeneration;
                AdmissionGeneration = admissionGeneration;
                ProducerEpoch = producerEpoch;
            }

            public long WriterGeneration { get; }
            public long PresentationGenerationBits { get; }
            public ulong PresentationGeneration => unchecked((ulong)
                PresentationGenerationBits);
            public long AdmissionGeneration { get; }
            public OrderedEgressProducerEpoch ProducerEpoch { get; }
            public bool IsValid => PresentationGenerationBits != 0 &&
                AdmissionGeneration != 0 && ProducerEpoch.IsValid;
        }

        public ViiperOutDevice(OutContType outputType,
            ViiperVirtualDeviceType viiperType, bool audioOnlySidecar = false,
            bool gamepadOnly = false)
        {
            this.outputType = outputType;
            this.viiperType = viiperType;
            this.audioOnlySidecar = audioOnlySidecar;
            this.gamepadOnly = gamepadOnly;
            readSwitch2XboxLivePolicy = ReadSwitch2XboxLivePolicy;
            isCurrentSwitch2XboxPolicyRequest = IsCurrentSwitch2XboxPolicyRequest;
            switch2DualSenseFeedbackPolicyLane = new(
                ReadSwitch2DualSenseConversionPolicy,
                readStreamGeneration: ReadFeedbackStreamGeneration);
            if (audioOnlySidecar && gamepadOnly)
            {
                throw new ArgumentException(
                    "A VIIPER device cannot be both audio-only and gamepad-only.");
            }
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
            client = new ViiperClient(DefaultHost, DefaultPort);
            if (viiperType == ViiperVirtualDeviceType.Xbox360)
            {
                string configuredAge = Environment.GetEnvironmentVariable(
                    Xbox360PresentationPolicy.
                        MaximumOrderedAgeEnvironmentVariable);
                bool validPolicy = Xbox360PresentationPolicy.
                    TryParseMaximumOrderedAgeMilliseconds(configuredAge,
                        out int configuredAgeMilliseconds,
                        out string policyError);
                xbox360MaximumOrderedAgeMilliseconds = validPolicy ?
                    configuredAgeMilliseconds : 0;
                xbox360PresentationPolicyError = policyError;
                xbox360EgressScheduler = new Xbox360EgressScheduler(
                    Xbox360PresentationPolicy.ToStopwatchTicks(
                        xbox360MaximumOrderedAgeMilliseconds));
            }
            else if (viiperType == ViiperVirtualDeviceType.XboxOne)
            {
                string configuredAge = Environment.GetEnvironmentVariable(
                    XboxOnePresentationPolicy.
                        MaximumOrderedAgeEnvironmentVariable);
                bool validPolicy = XboxOnePresentationPolicy.
                    TryParseMaximumOrderedAgeMilliseconds(configuredAge,
                        out int configuredAgeMilliseconds,
                        out string policyError);
                xboxOneMaximumOrderedAgeMilliseconds = validPolicy ?
                    configuredAgeMilliseconds : 0;
                xboxOnePresentationPolicyError = policyError;
                xboxOneEgressScheduler = new XboxOneEgressScheduler(
                    XboxOnePresentationPolicy.ToStopwatchTicks(
                        xboxOneMaximumOrderedAgeMilliseconds));
            }
            else if (viiperType == ViiperVirtualDeviceType.Switch2Pro)
            {
                string configuredAge = Environment.GetEnvironmentVariable(
                    Switch2PresentationPolicy.
                        MaximumOrderedAgeEnvironmentVariable);
                bool validPolicy = Switch2PresentationPolicy.
                    TryParseMaximumOrderedAgeMilliseconds(configuredAge,
                        out int configuredAgeMilliseconds,
                        out string policyError);
                switch2MaximumOrderedAgeMilliseconds = validPolicy ?
                    configuredAgeMilliseconds : 0;
                switch2PresentationPolicyError = policyError;
                switch2EgressScheduler = new Switch2EgressScheduler(
                    Switch2PresentationPolicy.ToStopwatchTicks(
                        switch2MaximumOrderedAgeMilliseconds));
            }
        }

        private static byte[][] CreateFixedBuffers(int count, int length)
        {
            byte[][] buffers = new byte[count][];
            for (int index = 0; index < buffers.Length; index++)
            {
                buffers[index] = new byte[length];
            }
            return buffers;
        }

        private void ClearPendingMicrophoneFrames()
        {
            lock (microphoneQueueLock)
            {
                pendingMicrophoneHead = 0;
                pendingMicrophoneCount = 0;
                Array.Clear(pendingMicrophoneLengths, 0,
                    pendingMicrophoneLengths.Length);
                Array.Clear(pendingMicrophoneHasSequences, 0,
                    pendingMicrophoneHasSequences.Length);
                Array.Clear(pendingMicrophoneSourceGenerations, 0,
                    pendingMicrophoneSourceGenerations.Length);
            }
        }

        private bool TryEnqueuePendingMicrophoneFrame(MicrophoneCodec codec,
            byte[] data, int length, ushort sequence = 0,
            bool hasSequence = false, long sourceGeneration = 0)
        {
            if (data == null || length <= 0 || length > data.Length ||
                length > MaximumCompressedMicrophoneFrameLength)
            {
                return false;
            }

            lock (microphoneQueueLock)
            {
                if (pendingMicrophoneCount == MaxPendingMicrophoneFrames)
                {
                    pendingMicrophoneLengths[pendingMicrophoneHead] = 0;
                    pendingMicrophoneHead = (pendingMicrophoneHead + 1) %
                        MaxPendingMicrophoneFrames;
                    pendingMicrophoneCount--;
                    Interlocked.Increment(ref microphoneFramesDropped);
                }

                int tail = (pendingMicrophoneHead + pendingMicrophoneCount) %
                    MaxPendingMicrophoneFrames;
                Buffer.BlockCopy(data, 0, pendingMicrophoneFrameSlots[tail], 0,
                    length);
                pendingMicrophoneCodecs[tail] = codec;
                pendingMicrophoneLengths[tail] = length;
                pendingMicrophoneSequences[tail] = sequence;
                pendingMicrophoneHasSequences[tail] = hasSequence;
                pendingMicrophoneSourceGenerations[tail] = sourceGeneration;
                pendingMicrophoneCount++;
                microphoneTelemetry.ObserveCompressedQueueDepth(
                    pendingMicrophoneCount);
                return true;
            }
        }

        private bool TryDequeuePendingMicrophoneFrame(
            out PendingMicrophoneFrame frame)
        {
            lock (microphoneQueueLock)
            {
                while (pendingMicrophoneCount > 0 &&
                    pendingMicrophoneSourceGenerations[
                        pendingMicrophoneHead] != Interlocked.Read(
                            ref microphoneSourceGeneration))
                {
                    pendingMicrophoneLengths[pendingMicrophoneHead] = 0;
                    pendingMicrophoneHasSequences[pendingMicrophoneHead] =
                        false;
                    pendingMicrophoneSourceGenerations[
                        pendingMicrophoneHead] = 0;
                    pendingMicrophoneHead = (pendingMicrophoneHead + 1) %
                        MaxPendingMicrophoneFrames;
                    pendingMicrophoneCount--;
                    Interlocked.Increment(ref microphoneFramesDropped);
                }

                if (pendingMicrophoneCount == 0)
                {
                    frame = default;
                    return false;
                }

                int slot = pendingMicrophoneHead;
                int length = pendingMicrophoneLengths[slot];
                Buffer.BlockCopy(pendingMicrophoneFrameSlots[slot], 0,
                    microphoneCompressedWorkBuffer, 0, length);
                frame = new PendingMicrophoneFrame(
                    pendingMicrophoneCodecs[slot],
                    microphoneCompressedWorkBuffer, length,
                    pendingMicrophoneSequences[slot],
                    pendingMicrophoneHasSequences[slot]);
                pendingMicrophoneLengths[slot] = 0;
                pendingMicrophoneHasSequences[slot] = false;
                pendingMicrophoneSourceGenerations[slot] = 0;
                pendingMicrophoneHead = (pendingMicrophoneHead + 1) %
                    MaxPendingMicrophoneFrames;
                pendingMicrophoneCount--;
                return true;
            }
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
            bool hasAtomicSubscriber,
            bool hasRealtimeHaptics = false)
        {
            return hasRealtimeHaptics ||
                (streamUsesAtomicFrames ?
                    hasAtomicSubscriber || hasPcmSubscriber :
                    hasPcmSubscriber);
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
            connected && Volatile.Read(ref deviceStream) != null && !HasRuntimeFault;

        internal bool HasRuntimeFault => Interlocked.Read(ref faultedRuntimeStreamGeneration) ==
            Interlocked.Read(ref streamGeneration);

        private void MarkXboxOneRuntimeStreamFault(ViiperDeviceStream failedStream,
            long failedGeneration)
        {
            lock (feedbackCallbackAdmissionLock)
            {
                if (viiperType != ViiperVirtualDeviceType.XboxOne || !connected ||
                    writerStopRequested || failedStream == null ||
                    failedGeneration != Interlocked.Read(ref streamGeneration) ||
                    !ReferenceEquals(Volatile.Read(ref deviceStream), failedStream)) return;
                // Readiness is separate from the ownership flag. Disconnect
                // still needs connected and exact callback admission for its
                // canonical terminal-Stop protocol; an old EOF cannot poison
                // a successor stream or publish another Ready state.
                Interlocked.Exchange(ref faultedRuntimeStreamGeneration, failedGeneration);
            }
        }

        internal bool SupportsAtomicAudioHaptics =>
            connected && activeStreamSupportsAtomicAudioHaptics;

        internal bool SupportsRealtimeHaptics =>
            connected && activeStreamSupportsRealtimeHaptics;

        /// <summary>
        /// V5 carries untouched 48 kHz front-channel PCM in exact 480-frame
        /// callbacks, independently from VIIPER's 512-frame rear-haptics
        /// assembler. The physical bridge owns the single continuous
        /// 512-to-480 speaker-clock conversion.
        /// </summary>
        internal bool UsesV5AudioSource =>
            connected && activeStreamUsesV5AudioSource;

        internal void ApplyAtomicAudioHapticsFeedback(byte[] feedback,
            int feedbackLength, int expectedDeviceIndex,
            long sourceStreamGeneration = 0)
        {
            ApplyFeedback(feedback, feedbackLength, expectedDeviceIndex,
                freshNativeOutput: false,
                nativeOutputStreamGeneration: sourceStreamGeneration);
        }

        internal bool CanProvideDirectSpeakerPcm =>
            GetVirtualSpeakerPcmSampleRate(viiperType) > 0;

        internal int DirectSpeakerPcmSampleRate =>
            SupportsDirectSpeakerPcm ?
                GetVirtualSpeakerPcmSampleRate(viiperType) : 0;

        internal int DirectSpeakerUsbipPort =>
            Volatile.Read(ref deviceStream)?.UsbipPort ?? -1;

        internal bool SupportsActiveVirtualMicrophone =>
            connected && activeStreamSupportsMicrophone;

        internal OutContType OutputType => outputType;

        internal bool IsAudioOnlySidecar => audioOnlySidecar;

        internal bool IsGamepadOnly => gamepadOnly;

        internal bool UsesAudioOnlyUsbDescriptor =>
            connected && activeStreamUsesAudioOnlyDescriptor;

        internal bool IsVirtualMicrophoneInterfaceActive =>
            Volatile.Read(ref virtualMicrophoneInterfaceActive) == 1;

        internal ViiperMicrophoneBufferSnapshot VirtualMicrophoneBufferSnapshot =>
            Volatile.Read(ref virtualMicrophoneBufferSnapshot);

        internal void BindPhysicalController(int deviceIndex)
        {
            int previousDeviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if (previousDeviceIndex != deviceIndex)
            {
                ReleaseTriggerLabRumbleOverrides(previousDeviceIndex);
            }
            PublishPhysicalControllerBinding(deviceIndex);
            if (connected)
            {
                RebindSwitch2RuntimeStatusBridge();
                ResetState();
            }
        }

        private void PublishPhysicalControllerBinding(int deviceIndex)
        {
            DualSenseDevice targetDevice = ResolvePhysicalControllerTarget(
                deviceIndex);
            if (Volatile.Read(ref lastInputDeviceIndex) == deviceIndex &&
                ReferenceEquals(Volatile.Read(
                    ref publishedPhysicalControllerTargetDevice),
                    targetDevice))
            {
                return;
            }

            // A visual-release commit uses this same short admission boundary.
            // The old physical target therefore either receives the release
            // before this binding change, or the exact-target recheck rejects
            // it after the change. Publish the exact object as well as its
            // slot: a reconnect may replace A with B in the same array index.
            // The 1 kHz input path locks only on a real slot or identity
            // transition.
            lock (feedbackCallbackAdmissionLock)
            {
                targetDevice = ResolvePhysicalControllerTarget(deviceIndex);
                if (Volatile.Read(ref lastInputDeviceIndex) != deviceIndex ||
                    !ReferenceEquals(Volatile.Read(
                        ref publishedPhysicalControllerTargetDevice),
                        targetDevice))
                {
                    Volatile.Write(ref lastInputDeviceIndex, deviceIndex);
                    Volatile.Write(
                        ref publishedPhysicalControllerTargetDevice,
                        targetDevice);
                }
            }
        }

        private static DualSenseDevice ResolvePhysicalControllerTarget(
            int deviceIndex)
        {
            if (Program.rootHub == null || deviceIndex < 0 ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length)
            {
                return null;
            }

            return Program.rootHub.DS4Controllers[deviceIndex] as
                DualSenseDevice;
        }

        public override void Connect()
        {
            int preparedPhysicalControllerIndex = Volatile.Read(
                ref lastInputDeviceIndex);
            Disconnect();
            streamRecoveryGate.Reset();
            ClearNativeGameOutputProcessLease();

            ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
            if (!status.Ready)
            {
                throw new IOException(
                    status.ServerProbeMessage != null
                        ? $"{status.DisplayText}. Check the running broker connection in Settings > VIIPER Virtual Controller Support."
                        : $"{status.DisplayText}. Use Settings > VIIPER Virtual Controller Support to install or repair VIIPER and usbip-win2.");
            }

            deviceStream = CreateDeviceStreamWithServerFallback();
            try
            {
                PrepareSwitch2VirtualFeedbackSession();
            }
            catch
            {
                Interlocked.Exchange(ref deviceStream, null)?.Dispose();
                Interlocked.Exchange(ref switch2FeedbackSession, null)?
                    .TryRetire();
                throw;
            }
            Interlocked.Increment(ref streamGeneration);
            if (UsesOrderedEgressScheduler())
            {
                lock (orderedEgressLifecycleLock)
                {
                    ClearXbox360PendingResynchronization();
                    ClearXboxOnePendingResynchronization();
                    ClearSwitch2PendingResynchronization();
                    ulong presentationGeneration =
                        GetOrderedEgressPresentationGeneration();
                    Interlocked.Exchange(
                        ref orderedEgressOwnedPresentationGeneration,
                        unchecked((long)presentationGeneration));
                    AdvanceOrderedEgressAdmissionGeneration();
                }
                if (!string.IsNullOrEmpty(xbox360PresentationPolicyError))
                {
                    AppLogger.LogToGui(
                        $"{xbox360PresentationPolicyError} Xbox 360 presentation is using compatibility mode with no ordered-age deadline.",
                        true);
                }
                if (!string.IsNullOrEmpty(xboxOnePresentationPolicyError))
                {
                    AppLogger.LogToGui(
                        $"{xboxOnePresentationPolicyError} Xbox One presentation is using compatibility mode with no ordered-age deadline.",
                        true);
                }
                if (!string.IsNullOrEmpty(switch2PresentationPolicyError))
                {
                    AppLogger.LogToGui(
                        $"{switch2PresentationPolicyError} Switch 2 presentation is using compatibility mode with no ordered-age deadline.",
                        true);
                }
            }
            Volatile.Write(ref submitFailureLogged, 0);
            Volatile.Write(ref microphoneUnavailableLogged, 0);
            Volatile.Write(ref microphoneNoiseSuppressionUnavailableLogged, 0);
            Volatile.Write(ref microphoneProcessingFailureLogged, 0);
            Volatile.Write(ref microphoneMuted, 0);
            Volatile.Write(ref lastInputDeviceIndex,
                preparedPhysicalControllerIndex);
            Volatile.Write(ref publishedPhysicalControllerTargetDevice,
                ResolvePhysicalControllerTarget(
                    preparedPhysicalControllerIndex));
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
            Interlocked.Exchange(ref nextStateWriteDeadline, 0);
            stateWriteRateHz = ViiperStateWriteRateSettings.
                ResolveConfiguredRateHz(viiperType,
                    Environment.GetEnvironmentVariable(
                        ViiperStateWriteRateSettings.EnvironmentVariableName));
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
            Interlocked.Exchange(ref switch2FeedbackValidated, 0);
            Interlocked.Exchange(ref switch2FeedbackRejected, 0);
            Interlocked.Exchange(ref switch2RumbleFramesPreserved, 0);
            Interlocked.Exchange(ref switch2LedOnlyFramesPreserved, 0);
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
            Volatile.Write(ref virtualMicrophoneInterfaceRemoteGenerationKnown,
                0);
            Volatile.Write(ref virtualMicrophoneBufferSnapshot,
                ViiperMicrophoneBufferSnapshot.Empty);
            microphoneInterfaceStopSignal.Reset();
            while (microphoneInterfaceStateSignal.WaitOne(0))
            {
            }
            writerRateWaitStopSignal.Reset();
            long writerGeneration = Interlocked.Increment(
                ref stateWriterGeneration);
            if (UsesOrderedEgressScheduler())
            {
                lock (orderedEgressLifecycleLock)
                {
                    orderedEgressWriterAdmissionGate.Activate(
                        writerGeneration,
                        Interlocked.Read(
                            ref orderedEgressOwnedPresentationGeneration),
                        Interlocked.Read(
                            ref orderedEgressAdmissionGeneration));
                }
            }
            inputScheduler.Reset(Volatile.Read(ref streamGeneration));
            lock (preparedMicrophoneQueueLock)
            {
                preparedMicrophoneHead = 0;
                preparedMicrophoneCount = 0;
                Array.Clear(preparedMicrophoneLengths, 0,
                    preparedMicrophoneLengths.Length);
                Array.Clear(preparedMicrophoneTimestamps, 0,
                    preparedMicrophoneTimestamps.Length);
            }
            writerStopRequested = false;
            xboxOneTerminalFeedbackAcknowledged.Reset();
            lock (feedbackCallbackAdmissionLock)
            {
                feedbackDispatchStopRequested = false;
                Volatile.Write(ref connected, true);
                Interlocked.Increment(ref feedbackDispatchGeneration);
            }
            long workerGeneration = Interlocked.Read(
                ref microphoneWorkerGeneration);
            if (viiperType == ViiperVirtualDeviceType.XboxOne)
            {
                // Install the reverse canonical-feedback consumer before
                // Windows can enumerate and send the first GIP output. The
                // specialized activation endpoint refuses to attach until
                // ConsumerReady has completed on this exact stream.
                StartFeedbackDispatchWorkers();
                StartFeedbackReader();
                try
                {
                    client.ActivateAuthorizedXboxOneDevice(deviceStream);
                }
                catch
                {
                    Disconnect();
                    throw;
                }
                StartStateWriter(writerGeneration);
                ResetState();
            }
            else
            {
                StartStateWriter(writerGeneration);
                StartMicrophoneWriter(workerGeneration);
                StartMicrophoneInterfaceMonitor(workerGeneration);
                StartFeedbackDispatchWorkers();
                ResetState();
                StartFeedbackReader();
            }
            StartSwitch2RuntimeStatusBridge();
            if (stateWriteRateHz > 0)
            {
                string queuePolicy = UsesXbox360EgressScheduler() ?
                    "ordered button/trigger boundaries and latest continuous state" :
                    UsesXboxOneEgressScheduler() ?
                    "ordered button/trigger boundaries and latest continuous state" :
                    UsesSwitch2EgressScheduler() ?
                    "ordered button boundaries and latest axes/motion state" :
                    "latest-state coalescing";
                AppLogger.LogToGui(
                    $"DS4Windows -> VIIPER {viiperType} input publication limit: {stateWriteRateHz} Hz; DS4Windows queue: {queuePolicy}. Virtual USB service cadence is determined separately by the device descriptor.",
                    false);
            }
        }

        private ViiperDeviceStream CreateDeviceStream()
        {
            activeStreamUsesFramedProtocol = false;
            activeStreamSupportsMicrophone = false;
            activeStreamSupportsDirectSpeaker = false;
            activeStreamSupportsAtomicAudioHaptics = false;
            activeStreamSupportsRealtimeHaptics = false;
            activeStreamUsesV5AudioSource = false;
            activeStreamUsesAudioOnlyDescriptor = false;
            activeStreamSupportsMicrophoneInterfaceEvents = false;
            activeStreamSupportsRawInputStatus = false;
            activeStreamFrameVersion = 0;
            Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceRemoteGenerationKnown,
                0);
            Interlocked.Exchange(
                ref virtualMicrophoneInterfaceRemoteGeneration, 0);

            if (viiperType == ViiperVirtualDeviceType.DualSense)
            {
                string legacyName = audioOnlySidecar ?
                    "dualsenseaudioonlyduplexv5" :
                    gamepadOnly ? "dualsensegamepadv5" :
                        "dualsensecombinedaudioduplexv5";
                string rawInputName = GetV5RawInputDeviceName(viiperType,
                    audioOnlySidecar, gamepadOnly);
                string eventName = gamepadOnly ? null :
                    GetV5EventDeviceName(viiperType, audioOnlySidecar);
                ViiperDeviceStream stream = CreateRawInputV5Stream(
                    rawInputName, eventName, legacyName,
                    supportsMicrophoneInterfaceEvents: !gamepadOnly);
                activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                activeStreamUsesFramedProtocol = true;
                activeStreamSupportsMicrophone = !gamepadOnly;
                activeStreamSupportsDirectSpeaker = !gamepadOnly;
                activeStreamSupportsAtomicAudioHaptics = !gamepadOnly;
                activeStreamSupportsRealtimeHaptics = !gamepadOnly;
                activeStreamUsesV5AudioSource = !gamepadOnly;
                activeStreamUsesAudioOnlyDescriptor = audioOnlySidecar;
                activeStreamFrameVersion = ViiperStreamFrameVersionV5;
                return stream;
            }

            if (viiperType == ViiperVirtualDeviceType.DualSenseEdge)
            {
                string legacyName = gamepadOnly ?
                    "dualsenseedgegamepadv5" :
                    "dualsenseedgecombinedaudioduplexv5";
                string rawInputName = GetV5RawInputDeviceName(viiperType,
                    audioOnlySidecar: false, gamepadOnly);
                string eventName = gamepadOnly ? null :
                    GetV5EventDeviceName(viiperType,
                        audioOnlySidecar: false);
                ViiperDeviceStream stream = CreateRawInputV5Stream(
                    rawInputName, eventName, legacyName,
                    supportsMicrophoneInterfaceEvents: !gamepadOnly);
                activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                activeStreamUsesFramedProtocol = true;
                activeStreamSupportsMicrophone = !gamepadOnly;
                activeStreamSupportsDirectSpeaker = !gamepadOnly;
                activeStreamSupportsAtomicAudioHaptics = !gamepadOnly;
                activeStreamSupportsRealtimeHaptics = !gamepadOnly;
                activeStreamUsesV5AudioSource = !gamepadOnly;
                activeStreamFrameVersion = ViiperStreamFrameVersionV5;
                return stream;
            }

            if (viiperType == ViiperVirtualDeviceType.DualShock4)
            {
                if (gamepadOnly)
                {
                    activeFeedbackLength = ViiperStatePacketBuilder
                        .GetFeedbackLength(viiperType);
                    return client.CreateDeviceAndOpenStream(viiperType);
                }

                if (audioOnlySidecar)
                {
                    try
                    {
                        ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(
                            "dualshock4audioonlyduplexv3", 0x05C4);
                        activeFeedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(
                            viiperType);
                        activeStreamUsesFramedProtocol = true;
                        activeStreamSupportsMicrophone = true;
                        activeStreamSupportsDirectSpeaker = true;
                        activeStreamUsesAudioOnlyDescriptor = true;
                        activeStreamFrameVersion = ViiperStreamFrameVersionV3;
                        return stream;
                    }
                    catch (IOException ex)
                    {
                        AppLogger.LogToGui(
                            $"VIIPER DualShock 4 audio-only sidecar unavailable: {ex.Message}",
                            true);
                        throw new IOException(
                            "The installed VIIPER build does not support the DualShock 4 audio-only interface. Update VIIPER from Settings before using PlayStation audio with an Xbox or Switch output.",
                            ex);
                    }
                }

                try
                {
                    ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(
                        "dualshock4audioduplexv3", 0x05C4);
                    activeFeedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(
                        viiperType);
                    activeStreamUsesFramedProtocol = true;
                    activeStreamSupportsMicrophone = true;
                    activeStreamSupportsDirectSpeaker = true;
                    activeStreamFrameVersion = ViiperStreamFrameVersionV3;
                    return stream;
                }
                catch (IOException ex)
                {
                    AppLogger.LogToGui(
                        $"VIIPER DualShock 4 audio stream unavailable: {ex.Message}",
                        true);
                    throw new IOException(
                        "The installed VIIPER build does not support the current DualShock 4 audio interface. Update VIIPER from Settings and try again.",
                        ex);
                }
            }

            activeFeedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(viiperType);
            if (viiperType == ViiperVirtualDeviceType.Xbox360)
            {
                object options = xbox360MaximumOrderedAgeMilliseconds > 0 ?
                    new ViiperClient.Xbox360CreateOptions
                    {
                        MaximumOrderedAgeMilliseconds =
                            xbox360MaximumOrderedAgeMilliseconds,
                    } : null;
                return client.CreateDeviceAndOpenStream("xbox360",
                    deviceSpecific: options);
            }
            if (viiperType == ViiperVirtualDeviceType.XboxOne)
            {
                XboxOnePhysicalFeedbackSession previousPhysicalSession =
                    Volatile.Read(ref xboxOnePhysicalFeedbackSession);
                if (previousPhysicalSession != null)
                {
                    if (!previousPhysicalSession.TryRetire())
                    {
                        throw new IOException(
                            "The previous Xbox One physical feedback owner could not be neutralized; a successor was not started.");
                    }
                    _ = Interlocked.CompareExchange(
                        ref xboxOnePhysicalFeedbackSession, null,
                        previousPhysicalSession);
                }
                XboxOneAuthorizedPersonaConfiguration configuration =
                    XboxOneAuthorizedPersonaConfiguration.LoadExplicit();
                XboxOneAuthorizedCreateRequestV1 request;
                Switch2VirtualFeedbackSession switch2Session = null;
                XboxOnePhysicalFeedbackSession physicalFeedbackSession = null;
                int physicalIndex = Volatile.Read(ref lastInputDeviceIndex);
                Switch2RuntimeInputDevice switch2Target =
                    Program.rootHub != null && physicalIndex >= 0 &&
                    physicalIndex < Program.rootHub.DS4Controllers.Length ?
                        Program.rootHub.DS4Controllers[physicalIndex] as
                            Switch2RuntimeInputDevice : null;
                if (switch2Target != null)
                {
                    if (!switch2Target.TryGetFeedbackBinding(
                            out ulong deviceGeneration,
                            out ulong transportGeneration))
                    {
                        throw new IOException(
                            "The bound Switch 2 controller has no active, generation-authenticated feedback output lifetime.");
                    }
                    if (!switch2Target.TryCreateVirtualFeedbackSession(
                            ControllerFeedbackSource.XboxOneVirtualDevice,
                            deviceGeneration, transportGeneration,
                            out switch2Session))
                    {
                        throw new IOException(
                            "The bound Switch 2 controller rejected the Xbox One feedback session.");
                    }
                    request = XboxOneAuthorizedCreateRequestV1.
                        CreateForFeedbackTarget(configuration,
                            deviceGeneration, transportGeneration,
                            switch2Session.OwnershipEpoch);
                }
                else
                {
                    DS4Device physicalTarget = Program.rootHub != null &&
                        physicalIndex >= 0 && physicalIndex <
                            Program.rootHub.DS4Controllers.Length ?
                        Program.rootHub.DS4Controllers[physicalIndex] : null;
                    if (physicalTarget == null)
                    {
                        throw new IOException(
                            "Xbox One output requires an exact bound physical controller before persona creation.");
                    }
                    request = XboxOneAuthorizedCreateRequestV1.Create(
                        configuration);
                    if (!TryCreateXboxOnePhysicalFeedbackSession(
                            request.Feedback, physicalTarget, physicalIndex,
                            out physicalFeedbackSession))
                    {
                        throw new IOException(
                            "The Xbox One physical feedback binding was invalid.");
                    }
                }
                activeFeedbackLength =
                    ControllerFeedbackFrame.SerializedLength;
                try
                {
                    ViiperDeviceStream stream = client.
                        CreateAuthorizedXboxOneDeviceAndOpenStream(request);
                    xboxOneFeedbackBinding = request.Feedback;
                    xboxOneLastFeedbackSequence = 0;
                    Volatile.Write(ref xboxOneSwitch2FeedbackPreRetired, 0);
                    Interlocked.Exchange(ref switch2FeedbackSession,
                        switch2Session)?.TryRetire();
                    if (Interlocked.CompareExchange(
                            ref xboxOnePhysicalFeedbackSession,
                            physicalFeedbackSession, null) != null)
                    {
                        stream.Dispose();
                        throw new IOException(
                            "Another Xbox One physical feedback owner is still active.");
                    }
                    return stream;
                }
                catch
                {
                    switch2Session?.TryRetire();
                    physicalFeedbackSession?.TryRetire();
                    throw;
                }
            }
            if (viiperType == ViiperVirtualDeviceType.Switch2Pro)
            {
                object metadata = null;
                Switch2RuntimeInputDevice source =
                    ResolveSwitch2RuntimeStatusSource();
                if (ViiperSwitch2RuntimeStatusV1.TryCreate(source,
                        out ViiperSwitch2RuntimeStatusV1 status))
                {
                    metadata = status.ToCreationMetadata();
                }
                return client.CreateDeviceAndOpenStream("ns2pro",
                    deviceSpecific: metadata);
            }
            return client.CreateDeviceAndOpenStream(viiperType);
        }

        private Switch2RuntimeInputDevice ResolveSwitch2RuntimeStatusSource()
        {
            if (viiperType != ViiperVirtualDeviceType.Switch2Pro ||
                Program.rootHub == null)
            {
                return null;
            }
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            return deviceIndex >= 0 &&
                    deviceIndex < Program.rootHub.DS4Controllers.Length ?
                Program.rootHub.DS4Controllers[deviceIndex] as
                    Switch2RuntimeInputDevice : null;
        }

        private void StartSwitch2RuntimeStatusBridge()
        {
            if (viiperType != ViiperVirtualDeviceType.Switch2Pro ||
                !connected)
            {
                return;
            }
            lock (switch2RuntimeStatusLifecycleLock)
            {
                StopSwitch2RuntimeStatusBridgeCore();
                Switch2RuntimeInputDevice source =
                    ResolveSwitch2RuntimeStatusSource();
                if (source == null || !connected)
                {
                    return;
                }

                Thread worker;
                bool signalInitial;
                lock (switch2RuntimeStatusLock)
                {
                    switch2RuntimeStatusStopSignal.Reset();
                    switch2RuntimeStatusSource = source;
                    hasPendingSwitch2RuntimeStatus =
                        ViiperSwitch2RuntimeStatusV1.TryCreate(source,
                            out pendingSwitch2RuntimeStatus);
                    signalInitial = hasPendingSwitch2RuntimeStatus;
                    Volatile.Write(ref switch2RuntimeStatusFailureLogged, 0);
                    source.BatteryChanged +=
                        Switch2RuntimeStatusSourceBatteryChanged;
                    worker = new Thread(Switch2RuntimeStatusWorker)
                    {
                        IsBackground = true,
                        Name = "VIIPER Switch 2 status",
                    };
                    switch2RuntimeStatusThread = worker;
                }
                worker.Start();
                if (signalInitial)
                {
                    switch2RuntimeStatusSignal.Set();
                }
            }
        }

        private void RebindSwitch2RuntimeStatusBridge()
        {
            if (viiperType != ViiperVirtualDeviceType.Switch2Pro ||
                !connected)
            {
                return;
            }
            Switch2RuntimeInputDevice resolved =
                ResolveSwitch2RuntimeStatusSource();
            lock (switch2RuntimeStatusLock)
            {
                if (ReferenceEquals(switch2RuntimeStatusSource, resolved))
                {
                    return;
                }
            }
            StartSwitch2RuntimeStatusBridge();
        }

        private void StopSwitch2RuntimeStatusBridge()
        {
            lock (switch2RuntimeStatusLifecycleLock)
            {
                StopSwitch2RuntimeStatusBridgeCore();
            }
        }

        private void StopSwitch2RuntimeStatusBridgeCore()
        {
            Thread worker;
            lock (switch2RuntimeStatusLock)
            {
                Switch2RuntimeInputDevice source = switch2RuntimeStatusSource;
                if (source != null)
                {
                    source.BatteryChanged -=
                        Switch2RuntimeStatusSourceBatteryChanged;
                }
                switch2RuntimeStatusSource = null;
                hasPendingSwitch2RuntimeStatus = false;
                pendingSwitch2RuntimeStatus = default;
                switch2RuntimeStatusStopSignal.Set();
                switch2RuntimeStatusSignal.Set();
                worker = switch2RuntimeStatusThread;
            }
            if (worker != null && worker.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
            {
                worker.Join();
            }
            lock (switch2RuntimeStatusLock)
            {
                if (ReferenceEquals(switch2RuntimeStatusThread, worker))
                {
                    switch2RuntimeStatusThread = null;
                }
            }
        }

        private void Switch2RuntimeStatusSourceBatteryChanged(object sender,
            EventArgs e)
        {
            if (sender is not Switch2RuntimeInputDevice source ||
                !ViiperSwitch2RuntimeStatusV1.TryCreate(source,
                    out ViiperSwitch2RuntimeStatusV1 status))
            {
                return;
            }
            lock (switch2RuntimeStatusLock)
            {
                if (!ReferenceEquals(switch2RuntimeStatusSource, source) ||
                    switch2RuntimeStatusStopSignal.WaitOne(0))
                {
                    return;
                }
                pendingSwitch2RuntimeStatus = status;
                hasPendingSwitch2RuntimeStatus = true;
            }
            switch2RuntimeStatusSignal.Set();
        }

        private void Switch2RuntimeStatusWorker()
        {
            WaitHandle[] waits =
            {
                switch2RuntimeStatusSignal,
                switch2RuntimeStatusStopSignal,
            };
            while (WaitHandle.WaitAny(waits) == 0)
            {
                while (true)
                {
                    ViiperSwitch2RuntimeStatusV1 status;
                    Switch2RuntimeInputDevice source;
                    lock (switch2RuntimeStatusLock)
                    {
                        if (!hasPendingSwitch2RuntimeStatus)
                        {
                            break;
                        }
                        status = pendingSwitch2RuntimeStatus;
                        source = switch2RuntimeStatusSource;
                        hasPendingSwitch2RuntimeStatus = false;
                    }

                    ViiperDeviceStream stream = Volatile.Read(
                        ref deviceStream);
                    if (switch2RuntimeStatusStopSignal.WaitOne(0) ||
                        !connected || stream == null || source == null ||
                        !ReferenceEquals(source,
                            ResolveSwitch2RuntimeStatusSource()))
                    {
                        continue;
                    }
                    try
                    {
                        client.UpdateNS2ProRuntimeStatusV1(stream.BusId,
                            stream.DevId, status);
                        Volatile.Write(ref switch2RuntimeStatusFailureLogged,
                            0);
                    }
                    catch (Exception ex) when (ex is IOException ||
                        ex is SocketException ||
                        ex is ObjectDisposedException)
                    {
                        if (Interlocked.Exchange(
                                ref switch2RuntimeStatusFailureLogged, 1) == 0)
                        {
                            AppLogger.LogToGui(
                                $"VIIPER Switch 2 runtime status update failed: {ex.Message}",
                                true);
                        }
                    }
                }
            }
        }

        private void PrepareSwitch2VirtualFeedbackSession()
        {
            if (viiperType == ViiperVirtualDeviceType.XboxOne ||
                audioOnlySidecar ||
                Volatile.Read(ref switch2FeedbackSession) != null)
            {
                return;
            }

            int physicalIndex = Volatile.Read(ref lastInputDeviceIndex);
            Switch2RuntimeInputDevice target = Program.rootHub != null &&
                    physicalIndex >= 0 && physicalIndex <
                        Program.rootHub.DS4Controllers.Length ?
                Program.rootHub.DS4Controllers[physicalIndex] as
                    Switch2RuntimeInputDevice : null;
            if (target == null)
            {
                return;
            }
            if (!target.TryGetFeedbackBinding(out ulong deviceGeneration,
                    out ulong transportGeneration))
            {
                throw new IOException(
                    "The bound Switch 2 controller has no active, generation-authenticated feedback output lifetime.");
            }

            ControllerFeedbackSource source = viiperType switch
            {
                ViiperVirtualDeviceType.Xbox360 =>
                    ControllerFeedbackSource.Xbox360VirtualDevice,
                ViiperVirtualDeviceType.DualShock4 =>
                    ControllerFeedbackSource.DualShock4VirtualDevice,
                ViiperVirtualDeviceType.DualSense =>
                    ControllerFeedbackSource.DualSenseVirtualDevice,
                ViiperVirtualDeviceType.DualSenseEdge =>
                    ControllerFeedbackSource.DualSenseEdgeVirtualDevice,
                ViiperVirtualDeviceType.Switch2Pro =>
                    ControllerFeedbackSource.Switch2VirtualDevice,
                _ => ControllerFeedbackSource.Invalid,
            };
            if (source == ControllerFeedbackSource.Invalid ||
                !target.TryCreateVirtualFeedbackSession(source,
                    deviceGeneration, transportGeneration,
                    out Switch2VirtualFeedbackSession session))
            {
                throw new IOException(
                    $"The bound Switch 2 controller rejected the {viiperType} feedback session.");
            }

            Interlocked.Exchange(ref switch2FeedbackSession, session)?
                .TryRetire();
        }

        internal static string GetV5RawInputDeviceName(
            ViiperVirtualDeviceType type, bool audioOnlySidecar,
            bool gamepadOnly)
        {
            return type switch
            {
                ViiperVirtualDeviceType.DualSense when audioOnlySidecar =>
                    "dualsenseaudioonlyduplexv5rawinputevents",
                ViiperVirtualDeviceType.DualSense when gamepadOnly =>
                    "dualsensegamepadv5rawinput",
                ViiperVirtualDeviceType.DualSense =>
                    "dualsensecombinedaudioduplexv5rawinputevents",
                ViiperVirtualDeviceType.DualSenseEdge when gamepadOnly =>
                    "dualsenseedgegamepadv5rawinput",
                ViiperVirtualDeviceType.DualSenseEdge =>
                    "dualsenseedgecombinedaudioduplexv5rawinputevents",
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }

        internal static string GetV5EventDeviceName(
            ViiperVirtualDeviceType type, bool audioOnlySidecar)
        {
            return type switch
            {
                ViiperVirtualDeviceType.DualSense when audioOnlySidecar =>
                    "dualsenseaudioonlyduplexv5events",
                ViiperVirtualDeviceType.DualSense =>
                    "dualsensecombinedaudioduplexv5events",
                ViiperVirtualDeviceType.DualSenseEdge =>
                    "dualsenseedgecombinedaudioduplexv5events",
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }

        private ViiperDeviceStream CreateRawInputV5Stream(
            string rawInputDeviceName, string eventDeviceName,
            string legacyDeviceName,
            bool supportsMicrophoneInterfaceEvents)
        {
            ViiperDeviceStream stream = OpenRawInputV5StreamWithFallback(
                name => client.CreateDeviceAndOpenStream(name),
                rawInputDeviceName, eventDeviceName, legacyDeviceName,
                supportsMicrophoneInterfaceEvents,
                out bool rawInputStatus, out bool microphoneEvents);
            activeStreamSupportsRawInputStatus = rawInputStatus;
            activeStreamSupportsMicrophoneInterfaceEvents = microphoneEvents;
            return stream;
        }

        internal static ViiperDeviceStream OpenRawInputV5StreamWithFallback(
            Func<string, ViiperDeviceStream> open,
            string rawInputDeviceName, string eventDeviceName,
            string legacyDeviceName,
            bool supportsMicrophoneInterfaceEvents,
            out bool rawInputStatus, out bool microphoneEvents)
        {
            ArgumentNullException.ThrowIfNull(open);
            rawInputStatus = false;
            microphoneEvents = false;
            try
            {
                ViiperDeviceStream stream = open(rawInputDeviceName);
                rawInputStatus = true;
                microphoneEvents =
                    supportsMicrophoneInterfaceEvents;
                return stream;
            }
            catch (ViiperApiException ex) when (
                ex.IsUnknownDeviceType(rawInputDeviceName))
            {
                // The raw-input alias is an exact payload capability. Existing
                // v5events aliases shipped with a 33-byte input contract, so
                // they may provide microphone events but must never enable the
                // 53-byte input frame.
            }

            if (supportsMicrophoneInterfaceEvents &&
                !string.IsNullOrEmpty(eventDeviceName))
            {
                try
                {
                    ViiperDeviceStream stream = open(eventDeviceName);
                    microphoneEvents = true;
                    return stream;
                }
                catch (ViiperApiException ex) when (
                    ex.IsUnknownDeviceType(eventDeviceName))
                {
                    // Only a typed, exact unknown-alias response permits the
                    // next compatibility tier. Every other failure remains
                    // visible instead of silently changing the device.
                }
            }

            return open(legacyDeviceName);
        }

        private ViiperDeviceStream CreateDeviceStreamWithServerFallback()
        {
            try
            {
                return CreateDeviceStream();
            }
            catch (IOException first)
            {
                ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
                if (!status.Ready)
                {
                    throw;
                }

                AppLogger.LogToGui($"VIIPER {viiperType} stream open failed once; server is available, retrying: {first.Message}", false);
                Thread.Sleep(250);
                return CreateDeviceStream();
            }
        }

        public override void Disconnect()
        {
            // Invalidate producer admission before any worker generation or
            // stream teardown work. A callback which was paused after capture
            // can then neither stage into nor adopt a successor lifecycle.
            long orderedPresentationGenerationToRetire = 0;
            if (UsesOrderedEgressScheduler())
            {
                lock (orderedEgressLifecycleLock)
                {
                    // This is the final-write linearization point. It must be
                    // the first ordered-egress teardown mutation so a writer
                    // paused before TryAdmit either wins admission entirely
                    // before Disconnect or is rejected before socket I/O.
                    orderedEgressWriterAdmissionGate.Invalidate();
                    writerStopRequested = true;
                    orderedPresentationGenerationToRetire =
                        Interlocked.Exchange(
                            ref orderedEgressOwnedPresentationGeneration, 0);
                    AdvanceOrderedEgressAdmissionGeneration();
                }
            }
            else
            {
                writerStopRequested = true;
            }
            Interlocked.Increment(ref microphoneWorkerGeneration);
            Interlocked.Increment(ref stateWriterGeneration);

            if (viiperType == ViiperVirtualDeviceType.XboxOne)
            {
                // The retained Xbox persona publishes its terminal Stop over
                // this exact authenticated broker while usbip detaches. Fence
                // and join the input writer first, but keep feedback admission,
                // the physical feedback session, and the broker transport
                // alive until that Stop has been delivered and acknowledged.
                // Closing the broker first makes the disconnect ambiguous and
                // correctly quarantines the one-shot retained import, which in
                // turn breaks ordinary profile-driven output reconstruction.
                writerRateWaitStopSignal.Set();
                writerSignal.Set();
                JoinStateWriterThread();
                Switch2VirtualFeedbackSession switch2Session = Volatile.Read(
                    ref switch2FeedbackSession);
                if (switch2Session != null && switch2Session.TryRetire() &&
                    !switch2Session.WasRetiredDisconnected)
                {
                    // The physical Switch 2 lifetime has now delivered its
                    // own authenticated terminal neutral and retired the exact
                    // canonical ingress. VIIPER still sends the corresponding
                    // retained-persona Stop after usbip detach. That later
                    // value must be authenticated and acknowledged, but must
                    // not attempt a second write through the retired physical
                    // session.
                    Volatile.Write(ref xboxOneSwitch2FeedbackPreRetired, 1);
                }
                ViiperDeviceStream retiringStream = Volatile.Read(
                    ref deviceStream);
                // Connect resets this exact incarnation's acknowledgement.
                // A history fault may already have delivered its terminal Stop
                // before the input writer enters Disconnect; keep that proof.
                retiringStream?.DisposeDeviceLifetimeBeforeTransportClose();
                if (retiringStream?.IsXboxOneBrokerEnabled == true &&
                    retiringStream.UsbipPort > 0)
                {
                    // usbip-win2 detach returns before VIIPER's retained
                    // import goroutine has necessarily published and received
                    // the ACK for its terminal Stop. Keep callback admission
                    // and the physical feedback lease alive for that bounded
                    // handoff. The feedback value itself carries a 250 ms TTL;
                    // this wait is only a shutdown fence, never a hot-path
                    // delay.
                    xboxOneTerminalFeedbackAcknowledged.WaitOne(
                        XboxOneTerminalFeedbackAckWaitMilliseconds);
                }
            }

            lock (feedbackCallbackAdmissionLock)
            {
                Interlocked.Increment(ref feedbackDispatchGeneration);
                Volatile.Write(ref connected, false);
                feedbackDispatchStopRequested = true;
                Volatile.Write(ref publishedPhysicalControllerTargetDevice,
                    null);
            }
            StopSwitch2RuntimeStatusBridge();
            // A worker generation is an ownership boundary. Never carry a
            // failed disable into a replacement VIIPER device where the same
            // physical controller may already have been re-enabled.
            microphoneDisableRetries.Clear();
            Interlocked.Increment(ref microphoneControlEpoch);
            Interlocked.Increment(ref microphoneSourceGeneration);
            writerRateWaitStopSignal.Set();
            writerSignal.Set();
            microphoneWriterSignal.Set();
            feedbackSpeakerSignal.Set();
            feedbackControlSignal.Set();
            WaitForFeedbackDispatchCallbacks();
            Switch2VirtualFeedbackSession retiringSwitch2Feedback =
                Interlocked.Exchange(ref switch2FeedbackSession, null);
            Interlocked.Exchange(ref switch2XboxPolicyRefreshRequested, null);
            Interlocked.Exchange(ref xboxOnePhysicalOutputSuppressionRequested, null);
            retiringSwitch2Feedback?.TryRetire();
            XboxOnePhysicalFeedbackSession retiringXboxOneFeedback =
                Volatile.Read(ref xboxOnePhysicalFeedbackSession);
            if (retiringXboxOneFeedback != null)
            {
                if (!retiringXboxOneFeedback.TryRetire())
                {
                    AppLogger.LogToGui(
                        "Xbox One physical feedback stopped, but neutral state acceptance could not be confirmed.", true);
                }
                else
                {
                    _ = Interlocked.CompareExchange(
                        ref xboxOnePhysicalFeedbackSession, null,
                        retiringXboxOneFeedback);
                }
            }
            xboxOneFeedbackBinding = null;
            xboxOneLastFeedbackSequence = 0;
            Volatile.Write(ref xboxOneSwitch2FeedbackPreRetired, 0);
            ClearNativeGameOutputProcessLease();
            ReleaseNativeDualSenseFeedbackOwnership();
            // A real output-device disconnect must not inherit the interface
            // monitor's debounce period. The generation change prevents the
            // old monitor from reattaching after this synchronous detach.
            DetachBluetoothMicrophoneSource();
            ResetLegacyDualSenseRumbleDeduplication();
            if (viiperType != ViiperVirtualDeviceType.XboxOne)
            {
                // Xbox ownership was released by the exact captured session
                // above. A second slot-based release could touch a successor.
                ReleaseTriggerLabRumbleOverrides(
                    Volatile.Read(ref lastInputDeviceIndex));
            }
            ResetTriggerLabRumbleState();
            StopMicrophoneInterfaceMonitor();
            lock (pendingPacketLock)
            {
                pendingStatePacket = null;
                pendingStatePacketQueuedTimestamp = 0;
            }
            ClearPendingMicrophoneFrames();
            lock (preparedMicrophoneQueueLock)
            {
                preparedMicrophoneHead = 0;
                preparedMicrophoneCount = 0;
            }
            inputScheduler.Reset(Interlocked.Read(ref streamGeneration) + 1);

            ViiperDeviceStream stream = Interlocked.Exchange(
                ref deviceStream, null);
            Interlocked.Increment(ref streamGeneration);
            stream?.Dispose();
            JoinStateWriterThread();
            if (UsesOrderedEgressScheduler())
            {
                if (orderedPresentationGenerationToRetire != 0)
                {
                    if (UsesXbox360EgressScheduler())
                    {
                        xbox360EgressScheduler.RetirePresentationGeneration(
                            unchecked((ulong)
                                orderedPresentationGenerationToRetire),
                            Stopwatch.GetTimestamp());
                    }
                    else if (UsesXboxOneEgressScheduler())
                    {
                        xboxOneEgressScheduler.RetirePresentationGeneration(
                            unchecked((ulong)
                                orderedPresentationGenerationToRetire),
                            Stopwatch.GetTimestamp());
                    }
                    else
                    {
                        switch2EgressScheduler.RetirePresentationGeneration(
                            unchecked((ulong)
                                orderedPresentationGenerationToRetire),
                            Stopwatch.GetTimestamp());
                    }
                }
                ClearXbox360PendingResynchronization();
                ClearXboxOnePendingResynchronization();
                ClearSwitch2PendingResynchronization();
            }
            if (microphoneWriterThread != null && microphoneWriterThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != microphoneWriterThread.ManagedThreadId)
            {
                microphoneWriterThread.Join();
            }

            microphoneWriterThread = null;
            StopFeedbackReader();
            // A recovering feedback reader can already have installed its
            // replacement thread in feedbackThread before the old reader
            // returns. Retire the elected old-generation recovery owner too,
            // so a later Connect can safely reset the reusable gate and no
            // stale reopen/log action survives the lifecycle boundary.
            streamRecoveryGate.WaitForIdle();
            StopFeedbackDispatchWorkers();
            feedbackDispatchBuffer.ClearPending();
        }

        private void JoinStateWriterThread()
        {
            Thread writerThread;
            lock (writerThreadLock)
            {
                writerThread = stateWriterThread;
            }
            if (writerThread != null && writerThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId !=
                    writerThread.ManagedThreadId)
            {
                writerThread.Join();
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
        }

        private void ReleaseNativeDualSenseFeedbackOwnership()
        {
            if (!IsDualSenseType() || audioOnlySidecar || Program.rootHub == null)
            {
                return;
            }

            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if (deviceIndex < 0 ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                Program.rootHub.DS4Controllers[deviceIndex] is not
                    DualSenseDevice dualSenseDevice)
            {
                return;
            }

            dualSenseDevice.ReleaseNativeGameOutputOwnership();
        }

        private bool RequestNativeDualSenseLedOwnershipRelease(
            DualSenseDevice expectedTargetDevice,
            long expectedNativeOutputRevision,
            long expectedStreamGeneration)
        {
            if (!IsDualSenseType() || audioOnlySidecar ||
                expectedTargetDevice == null ||
                expectedNativeOutputRevision <= 0 ||
                expectedStreamGeneration <= 0 || Program.rootHub == null)
            {
                return false;
            }

            lock (feedbackCallbackAdmissionLock)
            {
                if (!connected || feedbackDispatchStopRequested ||
                    activeFeedbackCallbacks != 0 ||
                    expectedStreamGeneration != Interlocked.Read(
                        ref streamGeneration))
                {
                    return false;
                }

                int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
                if (deviceIndex < 0 ||
                    deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                    !ReferenceEquals(Volatile.Read(
                        ref publishedPhysicalControllerTargetDevice),
                        expectedTargetDevice) ||
                    !ReferenceEquals(
                        Program.rootHub.DS4Controllers[deviceIndex],
                        expectedTargetDevice))
                {
                    return false;
                }

                NativeGameLedReleaseAdmissionTestHook?.Invoke();
                return expectedTargetDevice.
                    RequestNativeGameLedOwnershipRelease(
                        expectedNativeOutputRevision);
            }
        }

        private bool TryReleaseExitedForegroundOwnerLedOwnership(
            Process expectedOwnerProcess,
            NativeGameOwnerProcessLiveness ownerProcessLiveness)
        {
            if (expectedOwnerProcess == null || ownerProcessLiveness !=
                    NativeGameOwnerProcessLiveness.ConfirmedExited ||
                !IsDualSenseType() || audioOnlySidecar ||
                Program.rootHub == null)
            {
                return false;
            }

            Process ownerToDispose = null;
            bool releaseQueued = false;
            lock (feedbackCallbackAdmissionLock)
            {
                // The legacy reader applies reports directly rather than on
                // the ordered control worker. An already admitted callback
                // must finish Trace/Capture before an exit can be committed.
                if (!connected || feedbackDispatchStopRequested ||
                    activeFeedbackCallbacks != 0)
                {
                    return false;
                }

                DualSenseDevice targetDevice;
                long nativeOutputRevision;
                long ownerStreamGeneration;
                bool releaseIsCurrent;
                lock (nativeGameOutputTraceLock)
                {
                    targetDevice = nativeGameOutputOwnerTargetDevice;
                    nativeOutputRevision = nativeGameOutputOwnerRevision;
                    ownerStreamGeneration =
                        nativeGameOutputOwnerStreamGeneration;
                    int deviceIndex = Volatile.Read(
                        ref lastInputDeviceIndex);
                    bool targetBindingMatches = targetDevice != null &&
                        deviceIndex >= 0 && deviceIndex <
                            Program.rootHub.DS4Controllers.Length &&
                        ReferenceEquals(Volatile.Read(
                            ref publishedPhysicalControllerTargetDevice),
                            targetDevice) &&
                        ReferenceEquals(
                            Program.rootHub.DS4Controllers[deviceIndex],
                            targetDevice);
                    releaseIsCurrent =
                        ShouldReleaseForegroundOwnerLedOwnership(
                            ownerProcessLiveness,
                            retainedOwnerStillCurrent: ReferenceEquals(
                                nativeGameOutputOwnerProcess,
                                expectedOwnerProcess),
                            ownerTargetMatchesLatest: ReferenceEquals(
                                targetDevice,
                                lastNativeGameOutputTargetDevice),
                            targetBindingMatches: targetBindingMatches,
                            latestReportControlsVisuals:
                                NativeReportControlsVisuals(
                                    lastNativeGameOutputReport, 0),
                            verifiedVisualClaim:
                                nativeGameOutputOwnerHasVerifiedVisualClaim,
                            unverifiedVisualClaim:
                                nativeGameOutputOwnerHasUnverifiedVisualClaim,
                            ownerStreamGeneration: ownerStreamGeneration,
                            latestReportStreamGeneration:
                                lastNativeGameOutputStreamGeneration,
                            currentStreamGeneration: Interlocked.Read(
                                ref streamGeneration),
                            ownerRevision: nativeOutputRevision,
                            currentRevision: lastNativeGameOutputRevision);
                }

                if (!releaseIsCurrent)
                {
                    // A confirmed-dead lease that failed an identity,
                    // newest-report, visual-claim, or stream fence can never
                    // become safe again. Retire it without touching the
                    // controller so it cannot poll forever or block a later
                    // same-slot game from being captured.
                    lock (nativeGameOutputTraceLock)
                    {
                        ownerToDispose =
                            DetachNativeGameOutputOwnerNoLock(
                                expectedOwnerProcess);
                    }
                }
                else
                {
                    // No callback can begin while this admission lease is
                    // held. The device request is a bounded CAS + signal
                    // publication; it performs no HID I/O or wait.
                    NativeGameLedReleaseAdmissionTestHook?.Invoke();
                    releaseQueued = targetDevice.
                        RequestNativeGameLedOwnershipRelease(
                            nativeOutputRevision);

                    // A device-side revision rejection is terminal too: some
                    // newer native command already won. Clear the dead
                    // heuristic in either case; only an active callback above
                    // is a transient condition that retains it for retry.
                    lock (nativeGameOutputTraceLock)
                    {
                        ownerToDispose =
                            DetachNativeGameOutputOwnerNoLock(
                                expectedOwnerProcess);
                    }
                }
            }

            ownerToDispose?.Dispose();
            return releaseQueued;
        }

        private bool IsCurrentNativeOutputTarget(
            DualSenseDevice expectedTargetDevice)
        {
            if (expectedTargetDevice == null || Program.rootHub == null)
            {
                return false;
            }

            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            return deviceIndex >= 0 &&
                deviceIndex < Program.rootHub.DS4Controllers.Length &&
                NativeOutputTargetBindingMatches(expectedTargetDevice,
                    Volatile.Read(
                        ref publishedPhysicalControllerTargetDevice)) &&
                NativeOutputTargetBindingMatches(expectedTargetDevice,
                    Program.rootHub.DS4Controllers[deviceIndex]);
        }

        internal static bool NativeOutputTargetBindingMatches(
            object expectedTarget, object currentTarget)
        {
            return expectedTarget != null &&
                ReferenceEquals(expectedTarget, currentTarget);
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
                thread.Join();
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
            microphoneInterfaceStateSignal.Set();
            if (microphoneInterfaceThread != null && microphoneInterfaceThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != microphoneInterfaceThread.ManagedThreadId)
            {
                microphoneInterfaceThread.Join();
            }

            microphoneInterfaceThread = null;
            Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceRemoteGenerationKnown,
                0);
            Volatile.Write(ref virtualMicrophoneBufferSnapshot,
                ViiperMicrophoneBufferSnapshot.Empty);
        }

        private void MicrophoneInterfaceMonitorLoop(long workerGeneration)
        {
            if (activeStreamSupportsMicrophoneInterfaceEvents)
            {
                MicrophoneInterfaceEventLoop(workerGeneration);
                return;
            }

            var activity = new MicrophoneInterfaceActivityTracker();
            DateTime lastFailureLogUtc = DateTime.MinValue;
            bool narrowEndpointUnavailable = false;

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
                        if (narrowEndpointUnavailable)
                        {
                            // An old backend without the narrow route cannot
                            // be queried safely from this latency-sensitive
                            // compatibility lane. Preserve unknown/last state;
                            // never fall back to the broad bus/device listing.
                            activity.RecordQueryFailure();
                        }
                        else
                        {
                            try
                            {
                                ViiperMicrophoneInterfaceStatus status = client.
                                    GetNarrowMicrophoneInterfaceStatus(
                                        stream.BusId, stream.DevId);

                                if (workerGeneration != Interlocked.Read(
                                    ref microphoneWorkerGeneration))
                                {
                                    return;
                                }

                                Volatile.Write(
                                    ref virtualMicrophoneBufferSnapshot,
                                    status.Buffer);

                                bool stateChanged = activity.RecordObservation(
                                    status.IsActive,
                                    Stopwatch.GetTimestamp());
                                if (activity.StateKnown)
                                {
                                    Volatile.Write(
                                        ref virtualMicrophoneInterfaceActive,
                                        activity.IsActive ? 1 : 0);
                                    Volatile.Write(
                                        ref virtualMicrophoneInterfaceStateKnown,
                                        1);
                                }

                                if (Global.VerboseStartupLogging &&
                                    stateChanged)
                                {
                                    AppLogger.LogToGui(
                                        $"VIIPER {viiperType} microphone capture interface active={activity.IsActive}.",
                                        false);
                                }
                            }
                            catch (ViiperApiException ex) when (
                                ex.Status == 400 || ex.Status == 404)
                            {
                                narrowEndpointUnavailable = true;
                                activity.RecordQueryFailure();
                                if (Global.VerboseStartupLogging)
                                {
                                    AppLogger.LogToGui(
                                        $"VIIPER {viiperType} backend has no narrow microphone-interface route; preserving the last known state.",
                                        false);
                                }
                            }
                        }
                    }

                    if (workerGeneration != Interlocked.Read(
                        ref microphoneWorkerGeneration))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException ||
                    ex is SocketException || ex is JsonException ||
                    ex is ObjectDisposedException)
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

                if (microphoneInterfaceStopSignal.WaitOne(
                        MicrophoneInterfaceCompatibilityPollMilliseconds))
                {
                    break;
                }
            }
        }

        private void MicrophoneInterfaceEventLoop(long workerGeneration)
        {
            while (connected && workerGeneration == Interlocked.Read(
                    ref microphoneWorkerGeneration) &&
                !microphoneInterfaceStopSignal.WaitOne(0))
            {
                // State events wake immediately. The bounded timeout exists
                // only to advance a previously failed physical disable retry;
                // it performs no VIIPER API/status request.
                microphoneInterfaceStateSignal.WaitOne(
                    MicrophoneDisableRetryMilliseconds);
                if (!connected || microphoneInterfaceStopSignal.WaitOne(0) ||
                    workerGeneration != Interlocked.Read(
                        ref microphoneWorkerGeneration))
                {
                    break;
                }

                UpdateBluetoothMicrophoneSource(
                    Volatile.Read(ref lastInputDeviceIndex), workerGeneration);
                MaintainPendingBluetoothMicrophoneDisables(workerGeneration);
            }
        }

        internal static bool TryParseMicrophoneInterfaceStateEvent(
            byte[] payload, int payloadLength, out bool active,
            out ulong remoteGeneration)
        {
            active = false;
            remoteGeneration = 0;
            if (payload == null ||
                payloadLength != MicrophoneInterfaceStatePayloadLength ||
                payloadLength > payload.Length || payload[0] > 1)
            {
                return false;
            }

            active = payload[0] == 1;
            remoteGeneration = BinaryPrimitives.ReadUInt64LittleEndian(
                payload.AsSpan(1, sizeof(ulong)));
            return true;
        }

        private void PublishMicrophoneInterfaceStateEvent(byte[] payload,
            int payloadLength, long readStreamGeneration)
        {
            if (!activeStreamSupportsMicrophoneInterfaceEvents ||
                !TryParseMicrophoneInterfaceStateEvent(payload,
                    payloadLength, out bool active,
                    out ulong remoteGeneration) ||
                readStreamGeneration != Volatile.Read(ref streamGeneration))
            {
                return;
            }

            if (Volatile.Read(
                    ref virtualMicrophoneInterfaceRemoteGenerationKnown) != 0)
            {
                ulong previous = unchecked((ulong)Interlocked.Read(
                    ref virtualMicrophoneInterfaceRemoteGeneration));
                if (remoteGeneration < previous)
                {
                    return;
                }
            }

            Interlocked.Exchange(ref virtualMicrophoneInterfaceRemoteGeneration,
                unchecked((long)remoteGeneration));
            Volatile.Write(ref virtualMicrophoneInterfaceRemoteGenerationKnown,
                1);
            Volatile.Write(ref virtualMicrophoneInterfaceActive,
                active ? 1 : 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 1);
            microphoneInterfaceStateSignal.Set();
        }

        public override void ConvertandSendReport(DS4State state, int device)
        {
            bool usesXbox360EgressScheduler = UsesXbox360EgressScheduler();
            bool usesXboxOneEgressScheduler = UsesXboxOneEgressScheduler();
            bool usesSwitch2EgressScheduler = UsesSwitch2EgressScheduler();
            OrderedEgressPublicationLease orderedLease = default;
            if (usesXbox360EgressScheduler || usesXboxOneEgressScheduler ||
                usesSwitch2EgressScheduler)
            {
                bool captured = usesXbox360EgressScheduler ?
                    TryCaptureXbox360PublicationLease(out orderedLease) :
                    usesXboxOneEgressScheduler ?
                    TryCaptureXboxOnePublicationLease(out orderedLease) :
                    TryCaptureSwitch2PublicationLease(out orderedLease);
                if (!captured)
                {
                    return;
                }
                PublishPhysicalControllerBinding(device);
                bool current = usesXbox360EgressScheduler ?
                    IsXbox360PublicationLifecycleCurrent(orderedLease) :
                    usesXboxOneEgressScheduler ?
                    IsXboxOnePublicationLifecycleCurrent(orderedLease) :
                    IsSwitch2PublicationLifecycleCurrent(orderedLease);
                if (!current)
                {
                    return;
                }
            }
            else
            {
                PublishPhysicalControllerBinding(device);
                if (!Volatile.Read(ref connected))
                {
                    return;
                }
            }

            try
            {
                if (usesXbox360EgressScheduler)
                {
                    Xbox360EgressState projected = ViiperStatePacketBuilder.
                        BuildXbox360State(state, device);
                    long projectedAt = Stopwatch.GetTimestamp();
                    if (PublishXbox360State(orderedLease, projected,
                            projectedAt))
                    {
                        RecordStateQueued(projectedAt);
                        Interlocked.Increment(ref submittedPacketCount);
                        writerSignal.Set();
                    }
                }
                else if (usesXboxOneEgressScheduler)
                {
                    XboxOneEgressState projected = XboxOneEgressState.
                        FromLegacyMappedState(state, device);
                    long projectedAt = Stopwatch.GetTimestamp();
                    if (PublishXboxOneState(orderedLease, projected,
                            projectedAt))
                    {
                        RecordStateQueued(projectedAt);
                        Interlocked.Increment(ref submittedPacketCount);
                        writerSignal.Set();
                    }
                }
                else if (usesSwitch2EgressScheduler)
                {
                    Switch2EgressState projected = ViiperStatePacketBuilder.
                        BuildSwitch2State(state, device);
                    long projectedAt = Stopwatch.GetTimestamp();
                    if (PublishSwitch2State(orderedLease, projected,
                            projectedAt))
                    {
                        RecordStateQueued(projectedAt);
                        Interlocked.Increment(ref submittedPacketCount);
                        writerSignal.Set();
                    }
                }
                else if (UsesMappedInputScheduler())
                {
                    ViiperMappedInputState mapped = ViiperStatePacketBuilder.
                        BuildMappedState(state, device);
                    long mappedReadyAt = Stopwatch.GetTimestamp();
                    ViiperInputPublication publication = inputScheduler.Publish(
                        mapped, mappedReadyAt);
                    mappedReadyToPublishLatency.Observe(
                        Stopwatch.GetTimestamp() - mappedReadyAt);
                    if (publication.Accepted)
                    {
                        RecordStateQueued(mappedReadyAt);
                        Interlocked.Increment(ref submittedPacketCount);
                        writerSignal.Set();
                    }
                }
                else
                {
                    QueueStatePacket(ViiperStatePacketBuilder.Build(viiperType,
                        state, device));
                }
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
            if (!submit)
            {
                return;
            }

            bool usesXbox360EgressScheduler = UsesXbox360EgressScheduler();
            bool usesXboxOneEgressScheduler = UsesXboxOneEgressScheduler();
            bool usesSwitch2EgressScheduler = UsesSwitch2EgressScheduler();
            if (!usesXbox360EgressScheduler &&
                !usesXboxOneEgressScheduler &&
                !usesSwitch2EgressScheduler &&
                !Volatile.Read(ref connected))
            {
                return;
            }

            try
            {
                if (usesXbox360EgressScheduler ||
                    usesXboxOneEgressScheduler ||
                    usesSwitch2EgressScheduler)
                {
                    long queuedAt = Stopwatch.GetTimestamp();
                    OrderedEgressPublicationLease resetLease;
                    bool resetStarted = usesXbox360EgressScheduler ?
                        TryBeginXbox360LifecycleReset(queuedAt,
                            out resetLease) :
                        usesXboxOneEgressScheduler ?
                        TryBeginXboxOneLifecycleReset(queuedAt,
                            out resetLease) :
                        TryBeginSwitch2LifecycleReset(queuedAt,
                            out resetLease);
                    if (!resetStarted)
                    {
                        return;
                    }

                    bool staged = usesXbox360EgressScheduler ?
                        StageXbox360Resynchronization(resetLease,
                            Xbox360EgressState.Neutral, queuedAt) :
                        usesXboxOneEgressScheduler ?
                        StageXboxOneResynchronization(resetLease,
                            XboxOneEgressState.Neutral, queuedAt) :
                        StageSwitch2Resynchronization(resetLease,
                            Switch2EgressState.Neutral, queuedAt);
                    if (staged)
                    {
                        RecordStateQueued(queuedAt);
                        Interlocked.Increment(ref submittedPacketCount);
                        StartStateWriter(resetLease.WriterGeneration);
                        writerSignal.Set();
                    }
                }
                else if (UsesMappedInputScheduler())
                {
                    long queuedAt = Stopwatch.GetTimestamp();
                    if (inputScheduler.Publish(ViiperMappedInputState.Neutral,
                            queuedAt).Accepted)
                    {
                        RecordStateQueued(queuedAt);
                        Interlocked.Increment(ref submittedPacketCount);
                        EnsureStateWriterAlive();
                        writerSignal.Set();
                    }
                }
                else
                {
                    QueueStatePacket(ViiperStatePacketBuilder.BuildNeutral(
                        viiperType));
                }
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
                type == OutContType.ViiperXboxOne ||
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
            RecordStateQueued(queuedAt);

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

        private bool UsesMappedInputScheduler()
        {
            return IsDualSenseType() && activeStreamUsesFramedProtocol &&
                activeStreamFrameVersion == ViiperStreamFrameVersionV5;
        }

        private bool UsesXbox360EgressScheduler() =>
            viiperType == ViiperVirtualDeviceType.Xbox360 &&
            xbox360EgressScheduler != null;

        private bool UsesXboxOneEgressScheduler() =>
            viiperType == ViiperVirtualDeviceType.XboxOne &&
            xboxOneEgressScheduler != null;

        private bool UsesSwitch2EgressScheduler() =>
            viiperType == ViiperVirtualDeviceType.Switch2Pro &&
            switch2EgressScheduler != null;

        private bool UsesOrderedEgressScheduler() =>
            UsesXbox360EgressScheduler() || UsesXboxOneEgressScheduler() ||
            UsesSwitch2EgressScheduler();

        private ulong GetOrderedEgressPresentationGeneration()
        {
            if (UsesXbox360EgressScheduler())
            {
                return xbox360EgressScheduler.PresentationGeneration;
            }
            if (UsesXboxOneEgressScheduler())
            {
                return xboxOneEgressScheduler.PresentationGeneration;
            }
            if (UsesSwitch2EgressScheduler())
            {
                return switch2EgressScheduler.PresentationGeneration;
            }
            return 0;
        }

        private long AdvanceOrderedEgressAdmissionGeneration()
        {
            long generation = Interlocked.Increment(
                ref orderedEgressAdmissionGeneration);
            if (generation == 0)
            {
                generation = Interlocked.Increment(
                    ref orderedEgressAdmissionGeneration);
            }
            return generation;
        }

        private bool TryCaptureXbox360PublicationLease(
            out OrderedEgressPublicationLease lease)
        {
            lease = default;
            lock (orderedEgressLifecycleLock)
            {
                if (!UsesXbox360EgressScheduler())
                {
                    return false;
                }

                long writerGeneration = Interlocked.Read(
                    ref stateWriterGeneration);
                long presentationGeneration = Interlocked.Read(
                    ref orderedEgressOwnedPresentationGeneration);
                long admissionGeneration = Interlocked.Read(
                    ref orderedEgressAdmissionGeneration);
                if (presentationGeneration == 0 ||
                    admissionGeneration == 0 ||
                    !xbox360EgressScheduler.TryCaptureProducerEpoch(
                        unchecked((ulong)presentationGeneration),
                        out OrderedEgressProducerEpoch producerEpoch))
                {
                    return false;
                }

                OrderedEgressPublicationLease candidate = new(
                    writerGeneration, presentationGeneration,
                    admissionGeneration, producerEpoch);
                if (writerGeneration != Interlocked.Read(
                        ref stateWriterGeneration) ||
                    presentationGeneration != Interlocked.Read(
                        ref orderedEgressOwnedPresentationGeneration) ||
                    admissionGeneration != Interlocked.Read(
                        ref orderedEgressAdmissionGeneration) ||
                    !IsXbox360PublicationLifecycleCurrent(candidate))
                {
                    return false;
                }

                lease = candidate;
                return true;
            }
        }

        private bool TryCaptureSwitch2PublicationLease(
            out OrderedEgressPublicationLease lease)
        {
            lease = default;
            lock (orderedEgressLifecycleLock)
            {
                if (!UsesSwitch2EgressScheduler())
                {
                    return false;
                }

                long writerGeneration = Interlocked.Read(
                    ref stateWriterGeneration);
                long presentationGeneration = Interlocked.Read(
                    ref orderedEgressOwnedPresentationGeneration);
                long admissionGeneration = Interlocked.Read(
                    ref orderedEgressAdmissionGeneration);
                if (presentationGeneration == 0 ||
                    admissionGeneration == 0 ||
                    !switch2EgressScheduler.TryCaptureProducerEpoch(
                        unchecked((ulong)presentationGeneration),
                        out OrderedEgressProducerEpoch producerEpoch))
                {
                    return false;
                }

                OrderedEgressPublicationLease candidate = new(
                    writerGeneration, presentationGeneration,
                    admissionGeneration, producerEpoch);
                if (writerGeneration != Interlocked.Read(
                        ref stateWriterGeneration) ||
                    presentationGeneration != Interlocked.Read(
                        ref orderedEgressOwnedPresentationGeneration) ||
                    admissionGeneration != Interlocked.Read(
                        ref orderedEgressAdmissionGeneration) ||
                    !IsSwitch2PublicationLifecycleCurrent(candidate))
                {
                    return false;
                }

                lease = candidate;
                return true;
            }
        }

        private bool TryCaptureXboxOnePublicationLease(
            out OrderedEgressPublicationLease lease)
        {
            lease = default;
            lock (orderedEgressLifecycleLock)
            {
                if (!UsesXboxOneEgressScheduler())
                {
                    return false;
                }

                long writerGeneration = Interlocked.Read(
                    ref stateWriterGeneration);
                long presentationGeneration = Interlocked.Read(
                    ref orderedEgressOwnedPresentationGeneration);
                long admissionGeneration = Interlocked.Read(
                    ref orderedEgressAdmissionGeneration);
                if (presentationGeneration == 0 ||
                    admissionGeneration == 0 ||
                    !xboxOneEgressScheduler.TryCaptureProducerEpoch(
                        unchecked((ulong)presentationGeneration),
                        out OrderedEgressProducerEpoch producerEpoch))
                {
                    return false;
                }

                OrderedEgressPublicationLease candidate = new(
                    writerGeneration, presentationGeneration,
                    admissionGeneration, producerEpoch);
                if (writerGeneration != Interlocked.Read(
                        ref stateWriterGeneration) ||
                    presentationGeneration != Interlocked.Read(
                        ref orderedEgressOwnedPresentationGeneration) ||
                    admissionGeneration != Interlocked.Read(
                        ref orderedEgressAdmissionGeneration) ||
                    !IsXboxOnePublicationLifecycleCurrent(candidate))
                {
                    return false;
                }

                lease = candidate;
                return true;
            }
        }

        private bool IsXbox360PublicationLifecycleCurrent(
            in OrderedEgressPublicationLease lease)
        {
            return lease.IsValid &&
                IsStateWriterCurrent(lease.WriterGeneration) &&
                lease.PresentationGenerationBits == Interlocked.Read(
                    ref orderedEgressOwnedPresentationGeneration) &&
                lease.AdmissionGeneration == Interlocked.Read(
                    ref orderedEgressAdmissionGeneration);
        }

        private bool IsSwitch2PublicationLifecycleCurrent(
            in OrderedEgressPublicationLease lease) =>
            lease.IsValid &&
            IsStateWriterCurrent(lease.WriterGeneration) &&
            lease.PresentationGenerationBits == Interlocked.Read(
                ref orderedEgressOwnedPresentationGeneration) &&
            lease.AdmissionGeneration == Interlocked.Read(
                ref orderedEgressAdmissionGeneration);

        private bool IsXboxOnePublicationLifecycleCurrent(
            in OrderedEgressPublicationLease lease) =>
            lease.IsValid &&
            IsStateWriterCurrent(lease.WriterGeneration) &&
            lease.PresentationGenerationBits == Interlocked.Read(
                ref orderedEgressOwnedPresentationGeneration) &&
            lease.AdmissionGeneration == Interlocked.Read(
                ref orderedEgressAdmissionGeneration);

        private bool TryBeginXbox360LifecycleReset(long resetTimestamp,
            out OrderedEgressPublicationLease lease)
        {
            lease = default;
            lock (orderedEgressLifecycleLock)
            {
                if (!UsesXbox360EgressScheduler() ||
                    !Volatile.Read(ref connected))
                {
                    return false;
                }

                long writerGeneration = Interlocked.Read(
                    ref stateWriterGeneration);
                long presentationGeneration = Interlocked.Read(
                    ref orderedEgressOwnedPresentationGeneration);
                if (presentationGeneration == 0)
                {
                    return false;
                }

                // Retire final writer admission before changing any of the
                // externally visible lifecycle generations. This is the reset
                // boundary: a claim which has not already won TryAdmit cannot
                // be admitted in the interval before the scheduler installs
                // its mandatory-neutral successor epoch.
                orderedEgressWriterAdmissionGate.Invalidate();
                long admissionGeneration =
                    AdvanceOrderedEgressAdmissionGeneration();
                ClearXbox360PendingResynchronization();
                if (!orderedEgressWriterAdmissionGate.BeginLifecycleReset(
                        writerGeneration, presentationGeneration,
                        admissionGeneration, xbox360EgressScheduler,
                        resetTimestamp,
                        out OrderedEgressProducerEpoch producerEpoch))
                {
                    return false;
                }

                OrderedEgressPublicationLease candidate = new(
                    writerGeneration, presentationGeneration,
                    admissionGeneration, producerEpoch);
                if (!IsXbox360PublicationLifecycleCurrent(candidate))
                {
                    return false;
                }
                lease = candidate;
                return true;
            }
        }

        private bool TryBeginSwitch2LifecycleReset(long resetTimestamp,
            out OrderedEgressPublicationLease lease)
        {
            lease = default;
            lock (orderedEgressLifecycleLock)
            {
                if (!UsesSwitch2EgressScheduler() ||
                    !Volatile.Read(ref connected))
                {
                    return false;
                }

                long writerGeneration = Interlocked.Read(
                    ref stateWriterGeneration);
                long presentationGeneration = Interlocked.Read(
                    ref orderedEgressOwnedPresentationGeneration);
                if (presentationGeneration == 0)
                {
                    return false;
                }

                // Use the same final-admission boundary as Xbox. Keeping the
                // gate inactive while the scheduler retires its producer epoch
                // prevents a pre-reset Switch claim from entering the new
                // lifecycle through the otherwise-small reset window.
                orderedEgressWriterAdmissionGate.Invalidate();
                long admissionGeneration =
                    AdvanceOrderedEgressAdmissionGeneration();
                ClearSwitch2PendingResynchronization();
                if (!orderedEgressWriterAdmissionGate.BeginLifecycleReset(
                        writerGeneration, presentationGeneration,
                        admissionGeneration, switch2EgressScheduler,
                        resetTimestamp,
                        out OrderedEgressProducerEpoch producerEpoch))
                {
                    return false;
                }

                OrderedEgressPublicationLease candidate = new(
                    writerGeneration, presentationGeneration,
                    admissionGeneration, producerEpoch);
                if (!IsSwitch2PublicationLifecycleCurrent(candidate))
                {
                    return false;
                }
                lease = candidate;
                return true;
            }
        }

        private bool TryBeginXboxOneLifecycleReset(long resetTimestamp,
            out OrderedEgressPublicationLease lease)
        {
            lease = default;
            lock (orderedEgressLifecycleLock)
            {
                if (!UsesXboxOneEgressScheduler() ||
                    !Volatile.Read(ref connected))
                {
                    return false;
                }

                long writerGeneration = Interlocked.Read(
                    ref stateWriterGeneration);
                long presentationGeneration = Interlocked.Read(
                    ref orderedEgressOwnedPresentationGeneration);
                if (presentationGeneration == 0)
                {
                    return false;
                }

                orderedEgressWriterAdmissionGate.Invalidate();
                long admissionGeneration =
                    AdvanceOrderedEgressAdmissionGeneration();
                ClearXboxOnePendingResynchronization();
                if (!orderedEgressWriterAdmissionGate.BeginLifecycleReset(
                        writerGeneration, presentationGeneration,
                        admissionGeneration, xboxOneEgressScheduler,
                        resetTimestamp,
                        out OrderedEgressProducerEpoch producerEpoch))
                {
                    return false;
                }

                OrderedEgressPublicationLease candidate = new(
                    writerGeneration, presentationGeneration,
                    admissionGeneration, producerEpoch);
                if (!IsXboxOnePublicationLifecycleCurrent(candidate))
                {
                    return false;
                }
                lease = candidate;
                return true;
            }
        }

        private bool PublishXbox360State(
            in OrderedEgressPublicationLease lease,
            in Xbox360EgressState state, long receivedTimestamp)
        {
            if (!IsXbox360PublicationLifecycleCurrent(lease))
            {
                return false;
            }

            OrderedEgressPublishDisposition disposition =
                xbox360EgressScheduler.Publish(lease.ProducerEpoch, state,
                    receivedTimestamp);
            if (IsAcceptedOrderedEgressPublication(disposition))
            {
                return IsXbox360PublicationLifecycleCurrent(lease);
            }

            if (disposition == OrderedEgressPublishDisposition.
                    FaultedOverflow ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedFaultNeutralPending ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedStaleProducerEpoch ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedResynchronizationRequired)
            {
                if (StageXbox360Resynchronization(lease, state,
                        receivedTimestamp))
                {
                    writerSignal.Set();
                }
            }

            return false;
        }

        private static bool IsAcceptedOrderedEgressPublication(
            OrderedEgressPublishDisposition disposition)
        {
            return disposition ==
                    OrderedEgressPublishDisposition.AcceptedContinuous ||
                disposition ==
                    OrderedEgressPublishDisposition.AcceptedOrdered ||
                disposition == OrderedEgressPublishDisposition.
                    AcceptedResynchronization;
        }

        private bool StageXbox360Resynchronization(
            in OrderedEgressPublicationLease lease,
            in Xbox360EgressState state, long receivedTimestamp)
        {
            if (receivedTimestamp < 0)
            {
                return false;
            }

            lock (xbox360ResynchronizationLock)
            {
                if (!IsXbox360PublicationLifecycleCurrent(lease))
                {
                    return false;
                }

                if (!xbox360PendingResynchronization ||
                    receivedTimestamp >=
                        xbox360PendingResynchronizationTimestamp)
                {
                    xbox360PendingResynchronizationLease = lease;
                    xbox360PendingResynchronizationState = state;
                    xbox360PendingResynchronizationTimestamp =
                        receivedTimestamp;
                    Volatile.Write(ref xbox360PendingResynchronization, true);
                }
                return true;
            }
        }

        /// <summary>
        /// The state writer is the sole recovery producer. It consumes the
        /// newest staged snapshot under the same lock used by callbacks. A
        /// delayed old-epoch callback which arrives after recovery is handed
        /// back through the current writer rather than adopting that epoch
        /// itself.
        /// </summary>
        private bool TryPublishXbox360PendingResynchronization(
            long writerGeneration)
        {
            if (!Volatile.Read(ref xbox360PendingResynchronization))
            {
                return false;
            }

            long queuedAt = 0;
            bool accepted = false;
            lock (xbox360ResynchronizationLock)
            {
                if (!xbox360PendingResynchronization)
                {
                    return false;
                }

                OrderedEgressPublicationLease lease =
                    xbox360PendingResynchronizationLease;
                if (lease.WriterGeneration != writerGeneration ||
                    !IsXbox360PublicationLifecycleCurrent(lease))
                {
                    ClearXbox360PendingResynchronizationLocked();
                    return false;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    OrderedEgressSchedulerSnapshot snapshot =
                        xbox360EgressScheduler.Snapshot();
                    if (snapshot.PresentationGeneration !=
                            lease.PresentationGeneration)
                    {
                        ClearXbox360PendingResynchronizationLocked();
                        return false;
                    }
                    if (snapshot.MandatoryNeutralPending)
                    {
                        return false;
                    }

                    OrderedEgressProducerEpoch epoch =
                        xbox360EgressScheduler.CurrentProducerEpoch;
                    OrderedEgressPublishDisposition disposition =
                        snapshot.ResynchronizationRequired ?
                            xbox360EgressScheduler.Resynchronize(epoch,
                                xbox360PendingResynchronizationState,
                                xbox360PendingResynchronizationTimestamp) :
                            xbox360EgressScheduler.Publish(epoch,
                                xbox360PendingResynchronizationState,
                                xbox360PendingResynchronizationTimestamp);
                    if (IsAcceptedOrderedEgressPublication(disposition))
                    {
                        queuedAt =
                            xbox360PendingResynchronizationTimestamp;
                        ClearXbox360PendingResynchronizationLocked();
                        accepted = true;
                        break;
                    }

                    if (disposition == OrderedEgressPublishDisposition.
                            RejectedInvalidTimestamp)
                    {
                        // A newer state in this producer epoch already won.
                        ClearXbox360PendingResynchronizationLocked();
                        return false;
                    }
                    if (disposition == OrderedEgressPublishDisposition.
                            FaultedOverflow ||
                        disposition == OrderedEgressPublishDisposition.
                            RejectedFaultNeutralPending)
                    {
                        return false;
                    }
                    if (disposition != OrderedEgressPublishDisposition.
                            RejectedStaleProducerEpoch &&
                        disposition != OrderedEgressPublishDisposition.
                            RejectedResynchronizationRequired &&
                        disposition != OrderedEgressPublishDisposition.
                            RejectedResynchronizationNotRequired)
                    {
                        return false;
                    }
                }
            }

            if (accepted)
            {
                RecordStateQueued(queuedAt);
                Interlocked.Increment(ref submittedPacketCount);
                writerSignal.Set();
            }
            return accepted;
        }

        private void ClearXbox360PendingResynchronization()
        {
            lock (xbox360ResynchronizationLock)
            {
                ClearXbox360PendingResynchronizationLocked();
            }
        }

        private void ClearXbox360PendingResynchronizationLocked()
        {
            xbox360PendingResynchronizationLease = default;
            xbox360PendingResynchronizationState = default;
            xbox360PendingResynchronizationTimestamp = 0;
            Volatile.Write(ref xbox360PendingResynchronization, false);
        }

        private bool PublishSwitch2State(
            in OrderedEgressPublicationLease lease,
            in Switch2EgressState state, long receivedTimestamp)
        {
            if (!IsSwitch2PublicationLifecycleCurrent(lease))
            {
                return false;
            }

            OrderedEgressPublishDisposition disposition =
                switch2EgressScheduler.Publish(lease.ProducerEpoch, state,
                    receivedTimestamp);
            if (IsAcceptedOrderedEgressPublication(disposition))
            {
                return IsSwitch2PublicationLifecycleCurrent(lease);
            }

            if (disposition == OrderedEgressPublishDisposition.
                    FaultedOverflow ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedFaultNeutralPending ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedStaleProducerEpoch ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedResynchronizationRequired)
            {
                if (StageSwitch2Resynchronization(lease, state,
                        receivedTimestamp))
                {
                    writerSignal.Set();
                }
            }
            return false;
        }

        private bool PublishXboxOneState(
            in OrderedEgressPublicationLease lease,
            in XboxOneEgressState state, long receivedTimestamp)
        {
            if (!IsXboxOnePublicationLifecycleCurrent(lease))
            {
                return false;
            }

            OrderedEgressPublishDisposition disposition =
                xboxOneEgressScheduler.Publish(lease.ProducerEpoch, state,
                    receivedTimestamp);
            if (IsAcceptedOrderedEgressPublication(disposition))
            {
                return IsXboxOnePublicationLifecycleCurrent(lease);
            }

            if (disposition == OrderedEgressPublishDisposition.
                    FaultedOverflow ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedFaultNeutralPending ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedStaleProducerEpoch ||
                disposition == OrderedEgressPublishDisposition.
                    RejectedResynchronizationRequired)
            {
                if (StageXboxOneResynchronization(lease, state,
                        receivedTimestamp))
                {
                    writerSignal.Set();
                }
            }

            return false;
        }

        private bool StageXboxOneResynchronization(
            in OrderedEgressPublicationLease lease,
            in XboxOneEgressState state, long receivedTimestamp)
        {
            if (receivedTimestamp < 0)
            {
                return false;
            }

            lock (xboxOneResynchronizationLock)
            {
                if (!IsXboxOnePublicationLifecycleCurrent(lease))
                {
                    return false;
                }

                if (!xboxOnePendingResynchronization ||
                    receivedTimestamp >=
                        xboxOnePendingResynchronizationTimestamp)
                {
                    xboxOnePendingResynchronizationLease = lease;
                    xboxOnePendingResynchronizationState = state;
                    xboxOnePendingResynchronizationTimestamp =
                        receivedTimestamp;
                    Volatile.Write(ref xboxOnePendingResynchronization, true);
                }
                return true;
            }
        }

        private bool TryPublishXboxOnePendingResynchronization(
            long writerGeneration)
        {
            if (!Volatile.Read(ref xboxOnePendingResynchronization))
            {
                return false;
            }

            long queuedAt = 0;
            bool accepted = false;
            lock (xboxOneResynchronizationLock)
            {
                if (!xboxOnePendingResynchronization)
                {
                    return false;
                }

                OrderedEgressPublicationLease lease =
                    xboxOnePendingResynchronizationLease;
                if (lease.WriterGeneration != writerGeneration ||
                    !IsXboxOnePublicationLifecycleCurrent(lease))
                {
                    ClearXboxOnePendingResynchronizationLocked();
                    return false;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    OrderedEgressSchedulerSnapshot snapshot =
                        xboxOneEgressScheduler.Snapshot();
                    if (snapshot.PresentationGeneration !=
                            lease.PresentationGeneration)
                    {
                        ClearXboxOnePendingResynchronizationLocked();
                        return false;
                    }
                    if (snapshot.MandatoryNeutralPending)
                    {
                        return false;
                    }

                    OrderedEgressProducerEpoch epoch =
                        xboxOneEgressScheduler.CurrentProducerEpoch;
                    OrderedEgressPublishDisposition disposition =
                        snapshot.ResynchronizationRequired ?
                            xboxOneEgressScheduler.Resynchronize(epoch,
                                xboxOnePendingResynchronizationState,
                                xboxOnePendingResynchronizationTimestamp) :
                            xboxOneEgressScheduler.Publish(epoch,
                                xboxOnePendingResynchronizationState,
                                xboxOnePendingResynchronizationTimestamp);
                    if (IsAcceptedOrderedEgressPublication(disposition))
                    {
                        queuedAt = xboxOnePendingResynchronizationTimestamp;
                        ClearXboxOnePendingResynchronizationLocked();
                        accepted = true;
                        break;
                    }

                    if (disposition == OrderedEgressPublishDisposition.
                            RejectedInvalidTimestamp)
                    {
                        ClearXboxOnePendingResynchronizationLocked();
                        return false;
                    }
                    if (disposition == OrderedEgressPublishDisposition.
                            FaultedOverflow ||
                        disposition == OrderedEgressPublishDisposition.
                            RejectedFaultNeutralPending)
                    {
                        return false;
                    }
                    if (disposition != OrderedEgressPublishDisposition.
                            RejectedStaleProducerEpoch &&
                        disposition != OrderedEgressPublishDisposition.
                            RejectedResynchronizationRequired &&
                        disposition != OrderedEgressPublishDisposition.
                            RejectedResynchronizationNotRequired)
                    {
                        return false;
                    }
                }
            }

            if (accepted)
            {
                RecordStateQueued(queuedAt);
                Interlocked.Increment(ref submittedPacketCount);
                writerSignal.Set();
            }
            return accepted;
        }

        private void ClearXboxOnePendingResynchronization()
        {
            lock (xboxOneResynchronizationLock)
            {
                ClearXboxOnePendingResynchronizationLocked();
            }
        }

        private void ClearXboxOnePendingResynchronizationLocked()
        {
            xboxOnePendingResynchronizationLease = default;
            xboxOnePendingResynchronizationState = default;
            xboxOnePendingResynchronizationTimestamp = 0;
            Volatile.Write(ref xboxOnePendingResynchronization, false);
        }

        private bool StageSwitch2Resynchronization(
            in OrderedEgressPublicationLease lease,
            in Switch2EgressState state, long receivedTimestamp)
        {
            if (receivedTimestamp < 0)
            {
                return false;
            }

            lock (switch2ResynchronizationLock)
            {
                if (!IsSwitch2PublicationLifecycleCurrent(lease))
                {
                    return false;
                }
                if (!switch2PendingResynchronization ||
                    receivedTimestamp >=
                        switch2PendingResynchronizationTimestamp)
                {
                    switch2PendingResynchronizationLease = lease;
                    switch2PendingResynchronizationState = state;
                    switch2PendingResynchronizationTimestamp =
                        receivedTimestamp;
                    Volatile.Write(ref switch2PendingResynchronization, true);
                }
                return true;
            }
        }

        private bool TryPublishSwitch2PendingResynchronization(
            long writerGeneration)
        {
            if (!Volatile.Read(ref switch2PendingResynchronization))
            {
                return false;
            }

            long queuedAt = 0;
            bool accepted = false;
            lock (switch2ResynchronizationLock)
            {
                if (!switch2PendingResynchronization)
                {
                    return false;
                }

                OrderedEgressPublicationLease lease =
                    switch2PendingResynchronizationLease;
                if (lease.WriterGeneration != writerGeneration ||
                    !IsSwitch2PublicationLifecycleCurrent(lease))
                {
                    ClearSwitch2PendingResynchronizationLocked();
                    return false;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    OrderedEgressSchedulerSnapshot snapshot =
                        switch2EgressScheduler.Snapshot();
                    if (snapshot.PresentationGeneration !=
                            lease.PresentationGeneration)
                    {
                        ClearSwitch2PendingResynchronizationLocked();
                        return false;
                    }
                    if (snapshot.MandatoryNeutralPending)
                    {
                        return false;
                    }

                    OrderedEgressProducerEpoch epoch =
                        switch2EgressScheduler.CurrentProducerEpoch;
                    OrderedEgressPublishDisposition disposition =
                        snapshot.ResynchronizationRequired ?
                            switch2EgressScheduler.Resynchronize(epoch,
                                switch2PendingResynchronizationState,
                                switch2PendingResynchronizationTimestamp) :
                            switch2EgressScheduler.Publish(epoch,
                                switch2PendingResynchronizationState,
                                switch2PendingResynchronizationTimestamp);
                    if (IsAcceptedOrderedEgressPublication(disposition))
                    {
                        queuedAt = switch2PendingResynchronizationTimestamp;
                        ClearSwitch2PendingResynchronizationLocked();
                        accepted = true;
                        break;
                    }
                    if (disposition == OrderedEgressPublishDisposition.
                            RejectedInvalidTimestamp)
                    {
                        ClearSwitch2PendingResynchronizationLocked();
                        return false;
                    }
                    if (disposition == OrderedEgressPublishDisposition.
                            FaultedOverflow ||
                        disposition == OrderedEgressPublishDisposition.
                            RejectedFaultNeutralPending)
                    {
                        return false;
                    }
                    if (disposition != OrderedEgressPublishDisposition.
                            RejectedStaleProducerEpoch &&
                        disposition != OrderedEgressPublishDisposition.
                            RejectedResynchronizationRequired &&
                        disposition != OrderedEgressPublishDisposition.
                            RejectedResynchronizationNotRequired)
                    {
                        return false;
                    }
                }
            }

            if (accepted)
            {
                RecordStateQueued(queuedAt);
                Interlocked.Increment(ref submittedPacketCount);
                writerSignal.Set();
            }
            return accepted;
        }

        private void ClearSwitch2PendingResynchronization()
        {
            lock (switch2ResynchronizationLock)
            {
                ClearSwitch2PendingResynchronizationLocked();
            }
        }

        private void ClearSwitch2PendingResynchronizationLocked()
        {
            switch2PendingResynchronizationLease = default;
            switch2PendingResynchronizationState = default;
            switch2PendingResynchronizationTimestamp = 0;
            Volatile.Write(ref switch2PendingResynchronization, false);
        }

        private void RecordStateQueued(long queuedAt)
        {
            long previousQueuedAt = Interlocked.Exchange(
                ref lastStateQueuedTimestamp, queuedAt);
            if (previousQueuedAt > 0)
            {
                RecordMaximum(ref maximumStateQueueGapTicks,
                    queuedAt - previousQueuedAt);
            }
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
                thread.Join();
            }
        }

        private void WaitForFeedbackDispatchCallbacks()
        {
            // A dispatch callback cannot wait for its own active claim. No
            // current callback invokes Disconnect, but keep this guard so a
            // future subscriber cannot create a self-deadlock.
            if (ReferenceEquals(Thread.CurrentThread,
                    feedbackSpeakerDispatchThread) ||
                ReferenceEquals(Thread.CurrentThread,
                    feedbackControlDispatchThread))
            {
                return;
            }

            feedbackCallbacksIdle.WaitOne();
        }

        private bool TryBeginFeedbackDispatchCallback(long dispatchGeneration,
            long streamItemGeneration, int targetDeviceIndex,
            bool validateTargetDevice)
        {
            lock (feedbackCallbackAdmissionLock)
            {
                if (!connected || feedbackDispatchStopRequested ||
                    dispatchGeneration != Interlocked.Read(
                        ref feedbackDispatchGeneration) ||
                    streamItemGeneration != Interlocked.Read(
                        ref streamGeneration) ||
                    validateTargetDevice && targetDeviceIndex !=
                        Volatile.Read(ref lastInputDeviceIndex))
                {
                    return false;
                }

                if (activeFeedbackCallbacks++ == 0)
                {
                    feedbackCallbacksIdle.Reset();
                }
                return true;
            }
        }

        private bool TryBeginFeedbackReaderCallback(ViiperDeviceStream stream,
            long readStreamGeneration)
        {
            lock (feedbackCallbackAdmissionLock)
            {
                if (!connected || feedbackDispatchStopRequested ||
                    readStreamGeneration != Interlocked.Read(
                        ref streamGeneration) ||
                    !ReferenceEquals(Volatile.Read(ref deviceStream), stream))
                {
                    return false;
                }

                if (activeFeedbackCallbacks++ == 0)
                {
                    feedbackCallbacksIdle.Reset();
                }
                return true;
            }
        }

        private void EndFeedbackCallback()
        {
            lock (feedbackCallbackAdmissionLock)
            {
                if (--activeFeedbackCallbacks == 0)
                {
                    feedbackCallbacksIdle.Set();
                }
            }
        }

        private bool IsFeedbackDispatchGenerationActive(long generation)
        {
            return connected && !feedbackDispatchStopRequested &&
                generation == Interlocked.Read(ref feedbackDispatchGeneration);
        }

        private void FeedbackSpeakerDispatchLoop(long generation)
        {
            using MultimediaThreadRegistration mmcss =
                MultimediaThreadRegistration.EnterProAudio();
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
                        subscriber != null, atomicSubscriber != null,
                        activeStreamSupportsRealtimeHaptics);
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

                    if (!TryBeginFeedbackDispatchCallback(generation,
                            streamItemGeneration, targetDeviceIndex,
                            validateTargetDevice: false))
                    {
                        Interlocked.Increment(ref feedbackSpeakerStale);
                        continue;
                    }
                    try
                    {
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
                                        targetDeviceIndex,
                                        freshNativeOutput: false,
                                        nativeOutputStreamGeneration: streamItemGeneration);
                                    Buffer.BlockCopy(payload,
                                        speakerPcmOffset, payload, 0,
                                        speakerPcmLength);
                                    subscriber(this, payload,
                                        speakerPcmLength);
                                }
                            }
                            else if (speakerKind ==
                                FeedbackSpeakerKindRealtimeHaptics)
                            {
                                if (!activeStreamSupportsRealtimeHaptics ||
                                    length !=
                                        DualSenseCombinedExtendedFeedbackLength)
                                {
                                    Interlocked.Increment(
                                        ref feedbackSpeakerStale);
                                    continue;
                                }

                                // This frame is already one complete native
                                // rear-channel generation. Publish it straight
                                // into the physical compositor template; the
                                // helper merges it atomically into the next
                                // controller-clocked report immediately before
                                // CRC and the single HID write.
                                ApplyAtomicAudioHapticsFeedback(payload,
                                    length, targetDeviceIndex, streamItemGeneration);
                            }
                            else
                            {
                                if (subscriber == null)
                                {
                                    Interlocked.Increment(
                                        ref feedbackSpeakerStale);
                                    continue;
                                }
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
                        EndFeedbackCallback();
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
            using MultimediaThreadRegistration mmcss =
                MultimediaThreadRegistration.EnterProAudio();
            byte[] payload = new byte[DualSenseCombinedExtendedFeedbackLength];
            byte[] nativeOutputScratch =
                new byte[DualSenseNativeOutputReportLength];
            try
            {
                while (IsFeedbackDispatchGenerationActive(generation))
                {
                    if (Interlocked.Exchange(ref switch2DualSensePolicyRefreshRequested, 0) != 0)
                    {
                        RefreshSwitch2DualSenseConversionPolicy(
                            Volatile.Read(ref lastInputDeviceIndex));
                    }
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
                        TraceNativeGameOutputIdleBoundary(
                            feedbackDispatchBuffer.ControlAdmissionRevision);
                        feedbackControlSignal.WaitOne(100);
                        continue;
                    }

                    DispatchFeedbackControl(payload, length,
                        streamItemGeneration, targetDeviceIndex, generation,
                        nativeOutputScratch);
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
            long dispatchGeneration, byte[] nativeOutputScratch)
        {
            if (!TryBeginFeedbackDispatchCallback(dispatchGeneration,
                    streamItemGeneration, targetDeviceIndex,
                    validateTargetDevice: true))
            {
                Interlocked.Increment(ref feedbackControlStale);
                return;
            }
            try
            {
                try
                {
                    ApplyFeedback(payload, length, targetDeviceIndex,
                        freshNativeOutput: true, nativeOutputScratch,
                        nativeOutputStreamGeneration: streamItemGeneration);
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
                EndFeedbackCallback();
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
                    Priority = ThreadPriority.Highest,
                };
                stateWriterThread = writerThread;
                Interlocked.Exchange(ref stateWriterThreadGeneration,
                    writerGeneration);
                writerThread.Start();
            }
        }

        private bool IsStateWriterCurrent(long writerGeneration)
        {
            return !writerStopRequested && Volatile.Read(ref connected) &&
                writerGeneration == Interlocked.Read(
                    ref stateWriterGeneration);
        }

        private void StateWriteLoop(long writerGeneration)
        {
            using MultimediaThreadRegistration mmcss =
                MultimediaThreadRegistration.EnterGames();
            int consecutiveInputWrites = 0;
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
                        if (UsesXbox360EgressScheduler())
                        {
                            TryPublishXbox360PendingResynchronization(
                                writerGeneration);
                        }
                        else if (UsesXboxOneEgressScheduler())
                        {
                            TryPublishXboxOnePendingResynchronization(
                                writerGeneration);
                        }
                        else if (UsesSwitch2EgressScheduler())
                        {
                            TryPublishSwitch2PendingResynchronization(
                                writerGeneration);
                        }

                        if (consecutiveInputWrites >=
                                MaximumInputBurstBeforeDueMicrophone &&
                            HasPreparedMicrophoneFrame())
                        {
                            if (!TryWritePreparedMicrophoneFromWriter(
                                    writerGeneration))
                            {
                                return;
                            }
                            consecutiveInputWrites = 0;
                            continue;
                        }

                        bool mappedClaim = false;
                        ViiperInputClaim inputClaim = default;
                        bool xbox360Claim = false;
                        OrderedEgressClaim<Xbox360EgressState> xbox360InputClaim = default;
                        OrderedEgressWriterAdmissionLease
                            xbox360WriterAdmissionLease = default;
                        bool xboxOneClaim = false;
                        OrderedEgressClaim<XboxOneEgressState>
                            xboxOneInputClaim = default;
                        OrderedEgressWriterAdmissionLease
                            xboxOneWriterAdmissionLease = default;
                        bool switch2Claim = false;
                        OrderedEgressClaim<Switch2EgressState>
                            switch2InputClaim = default;
                        OrderedEgressWriterAdmissionLease
                            switch2WriterAdmissionLease = default;
                        byte[] packet = null;
                        long queuedAt = 0;
                        long claimedAt = 0;
                        if (UsesXbox360EgressScheduler())
                        {
                            claimedAt = Stopwatch.GetTimestamp();
                            xbox360Claim = orderedEgressWriterAdmissionGate.
                                TryClaim(writerGeneration,
                                xbox360EgressScheduler, claimedAt,
                                out xbox360InputClaim,
                                out xbox360WriterAdmissionLease,
                                includeIdle: false);
                            if (xbox360Claim)
                            {
                                queuedAt = xbox360InputClaim.
                                    ReceivedTimestamp;
                                if (queuedAt > 0)
                                {
                                    publishToWriterClaimLatency.Observe(
                                        claimedAt - queuedAt);
                                }
                            }
                        }
                        else if (UsesXboxOneEgressScheduler())
                        {
                            claimedAt = Stopwatch.GetTimestamp();
                            xboxOneClaim = orderedEgressWriterAdmissionGate.
                                TryClaim(writerGeneration,
                                xboxOneEgressScheduler, claimedAt,
                                out xboxOneInputClaim,
                                out xboxOneWriterAdmissionLease,
                                includeIdle: false);
                            if (xboxOneClaim)
                            {
                                queuedAt = xboxOneInputClaim.
                                    ReceivedTimestamp;
                                if (queuedAt > 0)
                                {
                                    publishToWriterClaimLatency.Observe(
                                        claimedAt - queuedAt);
                                }
                            }
                        }
                        else if (UsesSwitch2EgressScheduler())
                        {
                            claimedAt = Stopwatch.GetTimestamp();
                            switch2Claim = orderedEgressWriterAdmissionGate.
                                TryClaim(writerGeneration,
                                switch2EgressScheduler, claimedAt,
                                out switch2InputClaim,
                                out switch2WriterAdmissionLease,
                                includeIdle: false);
                            if (switch2Claim)
                            {
                                queuedAt = switch2InputClaim.
                                    ReceivedTimestamp;
                                if (queuedAt > 0)
                                {
                                    publishToWriterClaimLatency.Observe(
                                        claimedAt - queuedAt);
                                }
                            }
                        }
                        else if (UsesMappedInputScheduler())
                        {
                            mappedClaim = inputScheduler.TryClaim(
                                out inputClaim);
                            if (mappedClaim)
                            {
                                queuedAt = inputClaim.QueuedTimestamp;
                                claimedAt = Stopwatch.GetTimestamp();
                                if (queuedAt > 0)
                                {
                                    publishToWriterClaimLatency.Observe(
                                        claimedAt - queuedAt);
                                }
                            }
                        }
                        if (!mappedClaim && !xbox360Claim && !xboxOneClaim &&
                            !switch2Claim)
                        {
                            lock (pendingPacketLock)
                            {
                                packet = pendingStatePacket;
                                if (packet != null)
                                {
                                    pendingStatePacket = null;
                                    queuedAt =
                                        pendingStatePacketQueuedTimestamp;
                                    pendingStatePacketQueuedTimestamp = 0;
                                }
                            }
                        }

                        if (!mappedClaim && !xbox360Claim && !xboxOneClaim &&
                            !switch2Claim &&
                            packet == null)
                        {
                            if (HasPreparedMicrophoneFrame())
                            {
                                if (!TryWritePreparedMicrophoneFromWriter(
                                        writerGeneration))
                                {
                                    return;
                                }
                                consecutiveInputWrites = 0;
                                continue;
                            }
                            break;
                        }

                        ViiperDeadlineWaitResult rateWait =
                            WaitForStateWriteRateWindow(writerGeneration);
                        if (rateWait !=
                            ViiperDeadlineWaitResult.DeadlineReached)
                        {
                            if (mappedClaim)
                            {
                                inputScheduler.CompleteFailure(inputClaim);
                            }
                            else if (xbox360Claim)
                            {
                                xbox360EgressScheduler.Complete(
                                    xbox360InputClaim,
                                    OrderedEgressCompletion.Defer);
                            }
                            else if (xboxOneClaim)
                            {
                                xboxOneEgressScheduler.Complete(
                                    xboxOneInputClaim,
                                    OrderedEgressCompletion.Defer);
                            }
                            else if (switch2Claim)
                            {
                                switch2EgressScheduler.Complete(
                                    switch2InputClaim,
                                    OrderedEgressCompletion.Defer);
                            }
                            else
                            {
                                QueueRetryStatePacket(packet);
                            }
                            if (rateWait ==
                                    ViiperDeadlineWaitResult.Interrupted &&
                                HasPreparedMicrophoneFrame())
                            {
                                if (!TryWritePreparedMicrophoneFromWriter(
                                        writerGeneration))
                                {
                                    return;
                                }
                                consecutiveInputWrites = 0;
                                continue;
                            }
                            return;
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

                            ViiperFrameWriteTiming writeTiming;
                            if (mappedClaim)
                            {
                                bool includeRawInputStatus =
                                    activeStreamSupportsRawInputStatus;
                                ViiperStatePacketBuilder.BuildInto(
                                    inputClaim.State, stateWriterPacket,
                                    includeRawInputStatus);
                                writeTiming = WriteState(writeStream,
                                    stateWriterPacket,
                                    ViiperStatePacketBuilder.
                                        GetDualSenseInputPacketSize(
                                            includeRawInputStatus));
                            }
                            else if (xbox360Claim)
                            {
                                xbox360InputClaim.BuildInto(
                                    stateWriterPacket.AsSpan(0,
                                        Xbox360EgressState.WireSize));
                                long admittedAt = Stopwatch.GetTimestamp();
                                if (!orderedEgressWriterAdmissionGate.TryAdmit(
                                        xbox360WriterAdmissionLease,
                                        xbox360EgressScheduler,
                                        xbox360InputClaim, admittedAt))
                                {
                                    xbox360EgressScheduler.Complete(
                                        xbox360InputClaim,
                                        OrderedEgressCompletion.Defer);
                                    writerSignal.Set();
                                    continue;
                                }
                                writeTiming = WriteState(writeStream,
                                    stateWriterPacket,
                                    Xbox360EgressState.WireSize);
                            }
                            else if (xboxOneClaim)
                            {
                                xboxOneInputClaim.BuildInto(
                                    stateWriterPacket.AsSpan(0,
                                        XboxOneEgressState.WireSize));
                                long admittedAt = Stopwatch.GetTimestamp();
                                if (!orderedEgressWriterAdmissionGate.TryAdmit(
                                        xboxOneWriterAdmissionLease,
                                        xboxOneEgressScheduler,
                                        xboxOneInputClaim, admittedAt))
                                {
                                    xboxOneEgressScheduler.Complete(
                                        xboxOneInputClaim,
                                        OrderedEgressCompletion.Defer);
                                    writerSignal.Set();
                                    continue;
                                }
                                writeTiming = WriteState(writeStream,
                                    stateWriterPacket,
                                    XboxOneEgressState.WireSize);
                            }
                            else if (switch2Claim)
                            {
                                switch2InputClaim.BuildInto(
                                    stateWriterPacket.AsSpan(0,
                                        Switch2EgressState.WireSize));
                                long admittedAt = Stopwatch.GetTimestamp();
                                if (!orderedEgressWriterAdmissionGate.TryAdmit(
                                        switch2WriterAdmissionLease,
                                        switch2EgressScheduler,
                                        switch2InputClaim, admittedAt))
                                {
                                    switch2EgressScheduler.Complete(
                                        switch2InputClaim,
                                        OrderedEgressCompletion.Defer);
                                    writerSignal.Set();
                                    continue;
                                }
                                writeTiming = WriteState(writeStream,
                                    stateWriterPacket,
                                    Switch2EgressState.WireSize);
                            }
                            else
                            {
                                writeTiming = WriteState(writeStream, packet,
                                    packet.Length);
                            }
                            long writtenAt = Stopwatch.GetTimestamp();
                            if ((mappedClaim || xbox360Claim || xboxOneClaim ||
                                    switch2Claim) &&
                                claimedAt > 0 &&
                                writeTiming.SocketWriteStartedTimestamp > 0)
                            {
                                claimToSocketStartLatency.Observe(
                                    writeTiming.SocketWriteStartedTimestamp -
                                        claimedAt);
                                socketWriteLatency.Observe(
                                    writeTiming.SocketWriteCompletedTimestamp -
                                        writeTiming.SocketWriteStartedTimestamp);
                                if (xboxOneClaim &&
                                    writeTiming.AcceptanceAcknowledgedTimestamp >
                                        writeTiming.SocketWriteStartedTimestamp)
                                {
                                    xboxOneBrokerAcceptanceLatency.Observe(
                                        writeTiming.
                                            AcceptanceAcknowledgedTimestamp -
                                        writeTiming.
                                            SocketWriteStartedTimestamp);
                                }
                                if (xboxOneClaim &&
                                    writeTiming.WaitCompletedTimestamp >=
                                        writeTiming.
                                            AcceptanceAcknowledgedTimestamp &&
                                    writeTiming.
                                        AcceptanceAcknowledgedTimestamp > 0)
                                {
                                    xboxOneAckWakeLatency.Observe(
                                        writeTiming.WaitCompletedTimestamp -
                                        writeTiming.
                                            AcceptanceAcknowledgedTimestamp);
                                }
                            }
                            if (mappedClaim)
                            {
                                inputScheduler.CompleteSuccess(inputClaim,
                                    writtenAt);
                            }
                            else if (xbox360Claim)
                            {
                                bool completed = xbox360EgressScheduler.
                                    Complete(xbox360InputClaim,
                                        OrderedEgressCompletion.Commit);
                                if (completed && xbox360InputClaim.Kind ==
                                        OrderedEgressClaimKind.
                                            MandatoryNeutral)
                                {
                                    TryPublishXbox360PendingResynchronization(
                                        writerGeneration);
                                }
                            }
                            else if (xboxOneClaim)
                            {
                                bool completed = xboxOneEgressScheduler.
                                    Complete(xboxOneInputClaim,
                                        OrderedEgressCompletion.Commit);
                                if (completed && xboxOneInputClaim.Kind ==
                                        OrderedEgressClaimKind.
                                            MandatoryNeutral)
                                {
                                    TryPublishXboxOnePendingResynchronization(
                                        writerGeneration);
                                }
                            }
                            else if (switch2Claim)
                            {
                                bool completed = switch2EgressScheduler.
                                    Complete(switch2InputClaim,
                                        OrderedEgressCompletion.Commit);
                                if (completed && switch2InputClaim.Kind ==
                                        OrderedEgressClaimKind.
                                            MandatoryNeutral)
                                {
                                    TryPublishSwitch2PendingResynchronization(
                                        writerGeneration);
                                }
                            }
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
                            consecutiveInputWrites++;
                        }
                        catch (Exception ex) when (ex is IOException ||
                            ex is SocketException ||
                            ex is ObjectDisposedException)
                        {
                            if (mappedClaim)
                            {
                                inputScheduler.CompleteFailure(inputClaim);
                            }
                            else if (xbox360Claim)
                            {
                                xbox360EgressScheduler.Complete(
                                    xbox360InputClaim,
                                    OrderedEgressCompletion.Defer);
                            }
                            else if (xboxOneClaim)
                            {
                                xboxOneEgressScheduler.Complete(
                                    xboxOneInputClaim,
                                    OrderedEgressCompletion.Defer);
                            }
                            else if (switch2Claim)
                            {
                                switch2EgressScheduler.Complete(
                                    switch2InputClaim,
                                    OrderedEgressCompletion.Defer);
                            }
                            if (ex is XboxOneSemanticInputRejectedException)
                            {
                                StopXboxOneRejectedInput(writeStream,
                                    writeStreamGeneration, writerGeneration,
                                    ex.Message);
                                return;
                            }
                            if (IsStateWriterCurrent(writerGeneration) &&
                                TryRecoverStream(ex.Message,
                                    writeStreamGeneration,
                                    mappedClaim || xbox360Claim ||
                                        xboxOneClaim || switch2Claim ?
                                        null :
                                        packet))
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

        private bool HasPreparedMicrophoneFrame()
        {
            lock (preparedMicrophoneQueueLock)
            {
                return preparedMicrophoneCount > 0;
            }
        }

        private bool TryWritePreparedMicrophoneFromWriter(
            long writerGeneration)
        {
            int payloadLength;
            long queuedAt;
            lock (preparedMicrophoneQueueLock)
            {
                if (preparedMicrophoneCount == 0)
                {
                    return true;
                }

                int slot = preparedMicrophoneHead;
                payloadLength = preparedMicrophoneLengths[slot];
                queuedAt = preparedMicrophoneTimestamps[slot];
                Buffer.BlockCopy(preparedMicrophoneFrames[slot], 0,
                    microphoneTransportPayload, 0, payloadLength);
                preparedMicrophoneLengths[slot] = 0;
                preparedMicrophoneTimestamps[slot] = 0;
                preparedMicrophoneHead = (preparedMicrophoneHead + 1) %
                    MaxPreparedMicrophoneFrames;
                preparedMicrophoneCount--;
            }

            while (IsStateWriterCurrent(writerGeneration))
            {
                long failedStreamGeneration = Volatile.Read(
                    ref streamGeneration);
                ViiperDeviceStream stream = deviceStream;
                try
                {
                    ApplyFinalMicrophoneMuteInPlace(
                        microphoneTransportPayload, payloadLength,
                        Volatile.Read(ref microphoneMuted) == 1);
                    stream.WriteFrameFromOwnerTimed(
                        activeStreamFrameVersion,
                        ViiperStreamFrameMicrophonePcm,
                        microphoneTransportPayload, payloadLength);
                    long submittedAt = Stopwatch.GetTimestamp();
                    if (queuedAt > 0)
                    {
                        microphoneTelemetry.RecordTransportQueueAge(
                            submittedAt - queuedAt);
                    }
                    Interlocked.Increment(ref microphoneFramesSubmitted);
                    microphoneTelemetry.RecordSuccessfulSubmission(
                        submittedAt);
                    Interlocked.Exchange(ref lastMicrophoneSubmittedTimestamp,
                        submittedAt);
                    Interlocked.Exchange(ref streamRecoveryAttempts, 0);
                    return true;
                }
                catch (Exception ex) when (ex is IOException ||
                    ex is SocketException || ex is ObjectDisposedException)
                {
                    if (!TryRecoverStream(ex.Message,
                            failedStreamGeneration))
                    {
                        LogSubmitFailure(ex.Message);
                        return false;
                    }
                }
            }

            return false;
        }

        internal static void ApplyFinalMicrophoneMuteInPlace(byte[] payload,
            int payloadLength, bool muted)
        {
            if (!muted)
            {
                return;
            }
            if (payload == null || payloadLength < 0 ||
                payloadLength > payload.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }

            Array.Clear(payload, 0, payloadLength);
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

        private ViiperDeadlineWaitResult WaitForStateWriteRateWindow(
            long writerGeneration)
        {
            long interval = stateWriteMinimumIntervalTicks;
            if (interval <= 0)
            {
                return IsStateWriterCurrent(writerGeneration) ?
                    ViiperDeadlineWaitResult.DeadlineReached :
                    ViiperDeadlineWaitResult.Stopped;
            }

            long now = Stopwatch.GetTimestamp();
            long deadline = Interlocked.Read(ref nextStateWriteDeadline);
            if (deadline <= 0)
            {
                Interlocked.Exchange(ref nextStateWriteDeadline,
                    now + interval);
                return IsStateWriterCurrent(writerGeneration) ?
                    ViiperDeadlineWaitResult.DeadlineReached :
                    ViiperDeadlineWaitResult.Stopped;
            }

            while (now < deadline)
            {
                ViiperDeadlineWaitResult result = stateRateWaiter.WaitUntil(
                    deadline, writerRateWaitStopSignal, writerSignal);
                if (result == ViiperDeadlineWaitResult.Stopped)
                {
                    return result;
                }
                if (result == ViiperDeadlineWaitResult.Interrupted &&
                    HasPreparedMicrophoneFrame())
                {
                    return result;
                }
                now = Stopwatch.GetTimestamp();
            }

            now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref nextStateWriteDeadline,
                ViiperStateWriteRateSettings.AdvanceAbsoluteDeadline(
                    deadline, now, interval));
            return IsStateWriterCurrent(writerGeneration) ?
                ViiperDeadlineWaitResult.DeadlineReached :
                ViiperDeadlineWaitResult.Stopped;
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
            using MultimediaThreadRegistration mmcss =
                MultimediaThreadRegistration.EnterProAudio();
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
                    if (!TryDequeuePendingMicrophoneFrame(
                            out PendingMicrophoneFrame microphoneFrame))
                    {
                        break;
                    }

                    if (!TryWriteMicrophoneFrame(microphoneFrame))
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

            // The authorized Xbox persona is a one-shot capability. A broken
            // broker connection leaves input acceptance and canonical
            // feedback acceptance potentially ambiguous, so reopening the
            // same retained device and replaying a claim could cross the
            // persona's exact acknowledgement boundary. Its owner must tear
            // down this entire output and construct a fresh authorized
            // incarnation instead.
            if (!SupportsInPlaceStreamRecovery(viiperType))
            {
                return false;
            }

            if (Volatile.Read(ref streamGeneration) != failedStreamGeneration &&
                deviceStream != null)
            {
                QueueRetryStatePacket(packetToRetry);
                return true;
            }

            bool recovered;
            try
            {
                recovered = streamRecoveryGate.ExecuteOrWait(
                    failedStreamGeneration,
                    () => RecoverStreamAsOwner(reason,
                        failedStreamGeneration),
                    () => writerStopRequested || !connected);
            }
            catch (Exception ex)
            {
                // Recovery is cold-path lifecycle work. Keep an unexpected
                // API/thread-start failure from terminating the sole writer;
                // the elected owner has already released every waiter before
                // this diagnostic executes.
                AppLogger.LogToGui(
                    $"VIIPER {viiperType} stream recovery failed unexpectedly: {ex.GetType().Name}: {ex.Message}",
                    true);
                return false;
            }
            if (recovered)
            {
                QueueRetryStatePacket(packetToRetry);
            }
            return recovered;
        }

        internal static bool SupportsInPlaceStreamRecovery(
            ViiperVirtualDeviceType type) =>
            type != ViiperVirtualDeviceType.XboxOne;

        private bool RecoverStreamAsOwner(string reason,
            long failedStreamGeneration)
        {
            if (writerStopRequested || !connected)
            {
                return false;
            }
            if (Volatile.Read(ref streamGeneration) !=
                    failedStreamGeneration &&
                Volatile.Read(ref deviceStream) != null)
            {
                return true;
            }

            ViiperDeviceStream interruptedStream = Volatile.Read(
                ref deviceStream);
            if (interruptedStream == null)
            {
                return false;
            }

            AppLogger.LogToGui(
                $"VIIPER {viiperType} stream interrupted; reopening the existing virtual device: {reason}",
                true);

            // Closing only the TCP transport wakes the old feedback reader
            // without detaching usbip or removing the virtual controller.
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
                ViiperDeviceStream replacement;
                try
                {
                    replacement = client.OpenExistingDeviceStream(
                        interruptedStream.BusId,
                        interruptedStream.DevId,
                        interruptedStream.UsbipPort,
                        interruptedStream.DeviceLifetime);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    continue;
                }

                if (writerStopRequested || !connected)
                {
                    replacement.CloseTransport();
                    return false;
                }

                bool published = false;
                lock (feedbackCallbackAdmissionLock)
                {
                    if (!writerStopRequested && connected &&
                        Volatile.Read(ref streamGeneration) ==
                            failedStreamGeneration &&
                        ReferenceEquals(Volatile.Read(ref deviceStream),
                            interruptedStream))
                    {
                        // The lease contains only the stream/generation
                        // publication. It never nests the dispatch-buffer
                        // monitor or performs logging, waits, API calls,
                        // socket I/O, or thread creation.
                        Volatile.Write(ref deviceStream, replacement);
                        Interlocked.Increment(ref streamGeneration);
                        Volatile.Write(
                            ref virtualMicrophoneInterfaceRemoteGenerationKnown,
                            0);
                        Interlocked.Exchange(ref streamRecoveryAttempts, 0);
                        published = true;
                    }
                }

                if (!published)
                {
                    replacement.CloseTransport();
                    return Volatile.Read(ref streamGeneration) !=
                            failedStreamGeneration &&
                        Volatile.Read(ref deviceStream) != null;
                }

                // The new generation is visible, so no old reader or queued
                // dispatch can gain a callback claim. Wait outside the short
                // admission lock for callbacks that had already claimed the
                // retired generation before starting its replacement reader.
                WaitForFeedbackDispatchCallbacks();

                // The physical session can survive stream recovery, but a
                // pre-recovery source packet must not be re-presented by a
                // later profile edit. Release through its exact session and
                // publication watermark, outside the callback admission lock.
                RefreshSwitch2DualSenseConversionPolicy(
                    Volatile.Read(ref lastInputDeviceIndex));
                switch2DualSenseFeedbackPolicyLane.Invalidate();

                // No new feedback reader can enqueue this generation until
                // StartFeedbackReader below. Old workers revalidate the item
                // stream generation under their independent callback lease,
                // so clearing outside the generation lease is race-safe and
                // avoids nested locks.
                feedbackDispatchBuffer.ClearPending();

                // Thread creation and logging occur only after the short
                // generation publication lease is released. Publication is
                // already durable; never close the live replacement merely
                // because a cold-path worker startup diagnostic fails.
                try
                {
                    StartFeedbackDispatchWorkers();
                    StartFeedbackReader();
                }
                catch (Exception ex)
                {
                    AppLogger.LogToGui(
                        $"VIIPER {viiperType} stream transport recovered, but feedback worker startup failed: {ex.GetType().Name}: {ex.Message}",
                        true);
                }

                AppLogger.LogToGui(
                    $"VIIPER {viiperType} stream recovered on the existing virtual device after {attempt} attempt(s).",
                    false);
                return true;
            }

            AppLogger.LogToGui(
                $"VIIPER {viiperType} transport recovery exhausted {MaxStreamRecoveryAttempts} attempts without removing the virtual device: {lastError?.Message}",
                true);
            return false;
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

        private ViiperFrameWriteTiming WriteState(ViiperDeviceStream stream,
            byte[] data,
            int length)
        {
            if (stream == null)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            if (viiperType == ViiperVirtualDeviceType.XboxOne)
            {
                return stream.WriteXboxOneInputAndWaitForAck(data, length);
            }

            if (activeStreamUsesFramedProtocol)
            {
                return stream.WriteFrameFromOwnerTimed(
                    activeStreamFrameVersion,
                    ViiperStreamFrameInputState, data, length);
            }

            long started = Stopwatch.GetTimestamp();
            stream.Write(data, length);
            return new ViiperFrameWriteTiming(started,
                Stopwatch.GetTimestamp());
        }

        private void WriteMicrophoneFrame(PendingMicrophoneFrame frame)
        {
            switch (frame.Codec)
            {
                case MicrophoneCodec.Opus:
                    WriteMicrophoneOpusFrame(frame.Data, frame.Length);
                    break;
                case MicrophoneCodec.Sbc:
                    WriteMicrophoneSbcFrame(frame.Sequence,
                        frame.HasSequence, frame.Data, frame.Length);
                    break;
            }
        }

        private void WriteMicrophoneOpusFrame(byte[] opusFrame,
            int opusFrameLength)
        {
            if (!activeStreamSupportsMicrophone ||
                !activeStreamUsesFramedProtocol ||
                opusFrame == null ||
                opusFrameLength != DualSenseMicrophoneOpusFrameLength ||
                opusFrameLength > opusFrame.Length)
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
                int decodedSamples = decoder.Decode(
                    opusFrame.AsSpan(0, opusFrameLength),
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
            bool hasSequence, byte[] sbcFrame, int sbcFrameLength)
        {
            if (!activeStreamSupportsMicrophone ||
                !activeStreamUsesFramedProtocol ||
                sbcFrame == null || sbcFrameLength < SbcFrame.HeaderSize ||
                sbcFrameLength > sbcFrame.Length)
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
                    (int)DualSenseMicrophoneNoiseSuppression.NvidiaAi);
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

            QueuePreparedMicrophonePayload(payload);
        }

        private void QueuePreparedMicrophonePayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length >
                    MaximumPreparedMicrophonePayloadLength ||
                writerStopRequested || !connected)
            {
                return;
            }

            long queuedAt = Stopwatch.GetTimestamp();
            lock (preparedMicrophoneQueueLock)
            {
                if (preparedMicrophoneCount == MaxPreparedMicrophoneFrames)
                {
                    // Audio is a time-indexed stream. At complete frame
                    // boundaries, discard the oldest stale audio instead of
                    // blocking physical input or replaying a delayed backlog.
                    preparedMicrophoneLengths[preparedMicrophoneHead] = 0;
                    preparedMicrophoneTimestamps[preparedMicrophoneHead] = 0;
                    preparedMicrophoneHead = (preparedMicrophoneHead + 1) %
                        MaxPreparedMicrophoneFrames;
                    preparedMicrophoneCount--;
                    Interlocked.Increment(ref microphoneFramesDropped);
                }

                int tail = (preparedMicrophoneHead +
                    preparedMicrophoneCount) % MaxPreparedMicrophoneFrames;
                Buffer.BlockCopy(payload, 0, preparedMicrophoneFrames[tail],
                    0, payload.Length);
                preparedMicrophoneLengths[tail] = payload.Length;
                preparedMicrophoneTimestamps[tail] = queuedAt;
                preparedMicrophoneCount++;
            }

            writerSignal.Set();
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
            // Formatting the full transport snapshot allocates heavily and
            // publishes through the WPF logger. A live speaker stream has a
            // 10.667 ms deadline, so defer diagnostics instead of allowing a
            // telemetry-induced GC pause to interrupt its source callback.
            if (!Global.VerboseStartupLogging &&
                !ViiperLatencyHistogram.Enabled)
            {
                return;
            }
            if (!ViiperLatencyHistogram.Enabled &&
                IsFeedbackSpeakerDispatchRecentlyActive())
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
            string orderedEgressStats = string.Empty;
            if (UsesOrderedEgressScheduler())
            {
                OrderedEgressSchedulerSnapshot orderedSnapshot =
                    UsesXbox360EgressScheduler() ?
                        xbox360EgressScheduler.Snapshot() :
                    UsesXboxOneEgressScheduler() ?
                        xboxOneEgressScheduler.Snapshot() :
                        switch2EgressScheduler.Snapshot();
                orderedEgressStats =
                    $" orderedDepth={orderedSnapshot.OrderedDepth}" +
                    $" orderedHighWater={orderedSnapshot.OrderedHighWater}" +
                    $" orderedContinuousPending={orderedSnapshot.ContinuousPending}" +
                    $" orderedRetryPending={orderedSnapshot.RetryPending}" +
                    $" orderedClaimPending={orderedSnapshot.ClaimPending}" +
                    $" orderedClaimAdmitted={orderedSnapshot.ClaimAdmitted}" +
                    $" orderedAccepted={orderedSnapshot.AcceptedPublications}" +
                    $" orderedRejected={orderedSnapshot.RejectedPublications}" +
                    $" orderedReplacements={orderedSnapshot.ContinuousReplacements}" +
                    $" orderedPromotions={orderedSnapshot.ContinuousPromotions}" +
                    $" orderedRetries={orderedSnapshot.RetryCount}" +
                    $" orderedOverflowFaults={orderedSnapshot.OverflowFaults}" +
                    $" orderedStaleFaults={orderedSnapshot.OrderedAgeFaults}" +
                    $" orderedLifecycleFaults={orderedSnapshot.LifecycleResetFaults}" +
                    $" orderedStaleProducerRejects={orderedSnapshot.StaleProducerRejections}" +
                    $" orderedNeutralPending={orderedSnapshot.MandatoryNeutralPending}" +
                    $" orderedNeutralCommits={orderedSnapshot.MandatoryNeutralCommits}" +
                    $" orderedResyncRequired={orderedSnapshot.ResynchronizationRequired}" +
                    $" orderedResyncs={orderedSnapshot.ResynchronizationCount}" +
                    $" orderedInvalidTimestamps={orderedSnapshot.InvalidTimestampCount}";
            }
            string latencyDistributions = string.Empty;
            if (ViiperLatencyHistogram.Enabled)
            {
                latencyDistributions = " " +
                    mappedReadyToPublishLatency.Snapshot().Format(
                        "mappedReady->publish") + " " +
                    publishToWriterClaimLatency.Snapshot().Format(
                        "publish->claim") + " " +
                    claimToSocketStartLatency.Snapshot().Format(
                        "claim->socket") + " " +
                    socketWriteLatency.Snapshot().Format("socketWrite");
                if (UsesXboxOneEgressScheduler())
                {
                    latencyDistributions += " " +
                        xboxOneBrokerAcceptanceLatency.Snapshot().Format(
                            "xboxSocketStart->accepted") + " " +
                        xboxOneAckWakeLatency.Snapshot().Format(
                            "xboxAckReceived->writerWake");
                }

                int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
                if (Program.rootHub != null && deviceIndex >= 0 &&
                    deviceIndex < Program.rootHub.DS4Controllers.Length &&
                    Program.rootHub.DS4Controllers[deviceIndex] is
                        DualSenseDevice physical)
                {
                    latencyDistributions += " " +
                        physical.
                            PhysicalReportObservationIntervalLatencySnapshot.
                            Format("physicalReportObservationInterval") + " " +
                        physical.PhysicalReadObservationWaitLatencySnapshot.
                            Format("readCall->completionObservation") + " " +
                        physical.PhysicalReadRearmLatencySnapshot.Format(
                            "physicalReadRearm") + " " +
                        physical.PhysicalReadToReportLatencySnapshot.Format(
                            "hidReadObservation->reportEntry") + " " +
                        physical.PhysicalReportCallbackLatencySnapshot.Format(
                            "reportCallback") + " " +
                        physical.PhysicalReadToReportReturnLatencySnapshot.
                            Format("hidReadObservation->reportReturn") + " " +
                        physical.PhysicalOutputQueueLatencySnapshot.Format(
                            "physicalOutputQueue") + " " +
                        physical.PhysicalOutputWriteLatencySnapshot.Format(
                            "physicalOutputWrite") + " " +
                        physical.PhysicalMicrophoneDispatchLatencySnapshot.
                            Format("physicalMicDispatch");
                }
            }
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
                $"controlCallbackFailures={Interlocked.Read(ref feedbackControlCallbackFailures)} " +
                $"switch2FeedbackValidated={Interlocked.Read(ref switch2FeedbackValidated)} " +
                $"switch2FeedbackRejected={Interlocked.Read(ref switch2FeedbackRejected)} " +
                $"switch2RumblePreserved={Interlocked.Read(ref switch2RumbleFramesPreserved)} " +
                $"switch2LedOnlyPreserved={Interlocked.Read(ref switch2LedOnlyFramesPreserved)}" +
                orderedEgressStats +
                latencyDistributions,
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
            XboxOnePhysicalFeedbackSession physicalFeedbackSession =
                Volatile.Read(ref xboxOnePhysicalFeedbackSession);
            if (length <= 0 || stream == null || !connected)
            {
                return;
            }

            Thread thread = new Thread(() => FeedbackReadLoop(length, stream,
                readStreamGeneration, physicalFeedbackSession))
            {
                IsBackground = true,
                Name = $"VIIPER {viiperType} feedback",
                Priority = GetFeedbackReaderThreadPriority(viiperType,
                    activeStreamSupportsDirectSpeaker),
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

        internal static ThreadPriority GetFeedbackReaderThreadPriority(
            ViiperVirtualDeviceType deviceType, bool supportsDirectSpeaker) =>
            supportsDirectSpeaker ||
                deviceType == ViiperVirtualDeviceType.XboxOne ?
                ThreadPriority.Highest : ThreadPriority.AboveNormal;

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
            ViiperDeviceStream stream, long readStreamGeneration,
            XboxOnePhysicalFeedbackSession physicalFeedbackSession)
        {
            using MultimediaThreadRegistration mmcss =
                activeStreamSupportsDirectSpeaker ?
                    MultimediaThreadRegistration.EnterProAudio() :
                    MultimediaThreadRegistration.EnterGames();
            using XboxOneFeedbackDeliveryDispatcher xboxOneFeedbackDispatcher =
                viiperType == ViiperVirtualDeviceType.XboxOne ?
                    new XboxOneFeedbackDeliveryDispatcher(
                        (payload, length) =>
                            DeliverXboxOneFeedback(stream,
                                readStreamGeneration, payload, length),
                        stream.AcknowledgeXboxOneFeedback,
                        stream.CloseTransport,
                        $"VIIPER {viiperType} physical feedback delivery",
                        (payload, length, correlation, delivered, acknowledged) =>
                            OnXboxOneFeedbackDispatchCompleted(stream,
                                readStreamGeneration, payload, length,
                                correlation, delivered, acknowledged),
                        feedbackControlSignal, ProcessXboxFeedbackPolicyRefresh) :
                    null;
            int bufferLength = IsDualSenseType() ? Math.Max(feedbackLength, DualSenseCombinedExtendedFeedbackLength) : feedbackLength;
            byte[] buffer = new byte[bufferLength];
            byte[] framedPayload = new byte[ushort.MaxValue];
            byte[] nativeOutputScratch =
                new byte[DualSenseNativeOutputReportLength];
            try
            {
                while (connected && readStreamGeneration ==
                    Volatile.Read(ref streamGeneration))
                {
                    if (viiperType == ViiperVirtualDeviceType.XboxOne)
                    {
                        int payloadLength = stream.ReadXboxOneBrokerFrame(
                            out byte frameType, out ulong correlation,
                            framedPayload);
                        if (frameType == ViiperDeviceStream.
                                XboxOneBrokerSemanticInputAck)
                        {
                            stream.AcceptXboxOneInputAck(correlation,
                                framedPayload[0]);
                            continue;
                        }
                        if (frameType != ViiperDeviceStream.
                                XboxOneBrokerCanonicalFeedback ||
                            payloadLength !=
                                ControllerFeedbackFrame.SerializedLength)
                        {
                            throw new IOException(
                                "VIIPER returned an unexpected Xbox One broker frame.");
                        }

                        if (xboxOneFeedbackDispatcher == null ||
                            !xboxOneFeedbackDispatcher.TryEnqueue(
                                framedPayload.AsSpan(0, payloadLength),
                                correlation))
                        {
                            throw new IOException(
                                "DS4Windows rejected an overlapping or invalid canonical Xbox One feedback value; the one-shot persona was retired.");
                        }
                    }
                    else if (activeStreamSupportsDirectSpeaker)
                    {
                        int payloadLength = stream.ReadFrame(
                            activeStreamFrameVersion, out byte frameType,
                            framedPayload);
                        long frameNumber = Interlocked.Increment(
                            ref feedbackFramesRead);
                        if (Interlocked.CompareExchange(
                                ref feedbackFirstFrameLogged, 1, 0) == 0)
                        {
                            AppLogger.LogToGui(
                                $"VIIPER feedback stream active: bus={stream.BusId} device={stream.DevId} port={stream.UsbipPort} sidecar={audioOnlySidecar} gamepadOnly={gamepadOnly} frame=0x{frameType:X2} payload={payloadLength} sequenceCount={frameNumber}",
                                false);
                        }
                        if (!connected || readStreamGeneration !=
                            Volatile.Read(ref streamGeneration) ||
                            !ReferenceEquals(deviceStream, stream))
                        {
                            break;
                        }

                        if (frameType == ViiperStreamFrameOutputState)
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
                                ViiperStreamFrameMicrophoneInterfaceState)
                            {
                                PublishMicrophoneInterfaceStateEvent(
                                    framedPayload, payloadLength,
                                    readStreamGeneration);
                            }
                            else if (frameType ==
                                    ViiperStreamFrameSpeakerPcm &&
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
                                payloadLength >
                                    AtomicAudioHapticsFeedbackLengthPrefix)
                            {
                                Interlocked.Increment(
                                    ref feedbackAtomicFramesRead);
                                int atomicFeedbackLength =
                                    BinaryPrimitives.ReadUInt16LittleEndian(
                                        framedPayload.AsSpan(0,
                                            AtomicAudioHapticsFeedbackLengthPrefix));
                                int speakerPcmLength = payloadLength -
                                    AtomicAudioHapticsFeedbackLengthPrefix -
                                    atomicFeedbackLength;
                                if (atomicFeedbackLength ==
                                        DualSenseCombinedExtendedFeedbackLength &&
                                    speakerPcmLength > 0 &&
                                    (speakerPcmLength &
                                        (sizeof(short) * 2 - 1)) == 0 &&
                                    feedbackDispatchBuffer.TryEnqueueSpeaker(
                                        framedPayload, payloadLength,
                                        readStreamGeneration,
                                        FeedbackSpeakerKindAtomicAudioHaptics,
                                        Volatile.Read(ref lastInputDeviceIndex)))
                                {
                                    long queued = Interlocked.Increment(
                                        ref feedbackAtomicFramesQueued);
                                    if (Interlocked.CompareExchange(
                                            ref feedbackFirstAtomicResultLogged,
                                            1, 0) == 0)
                                    {
                                        AppLogger.LogToGui(
                                            $"VIIPER atomic media accepted: bus={stream.BusId} device={stream.DevId} sidecar={audioOnlySidecar} payload={payloadLength} feedback={atomicFeedbackLength} pcm={speakerPcmLength} queued={queued}",
                                            false);
                                    }
                                    feedbackSpeakerSignal.Set();
                                }
                                else if (Interlocked.CompareExchange(
                                             ref feedbackFirstAtomicResultLogged,
                                             1, 0) == 0)
                                {
                                    AppLogger.LogToGui(
                                        $"VIIPER atomic media rejected: bus={stream.BusId} device={stream.DevId} sidecar={audioOnlySidecar} payload={payloadLength} feedback={atomicFeedbackLength} pcm={speakerPcmLength} pending={feedbackDispatchBuffer.PendingSpeakerCount}",
                                        true);
                                }
                            }
                            else if (frameType ==
                                    ViiperStreamFrameRealtimeHaptics &&
                                activeStreamSupportsRealtimeHaptics &&
                                payloadLength ==
                                    DualSenseCombinedExtendedFeedbackLength &&
                                framedPayload[
                                    DualSenseCombinedBluetoothReportOffset] ==
                                    0x36)
                            {
                                // This is a complete 512-frame rear-channel
                                // generation, not ordinary control or speaker
                                // work. Apply it on the framed reader boundary
                                // so it cannot age behind the 480-frame media
                                // queue before the physical compositor sees it.
                                if (TryBeginFeedbackReaderCallback(stream,
                                        readStreamGeneration))
                                {
                                    try
                                    {
                                        ApplyAtomicAudioHapticsFeedback(
                                            framedPayload, payloadLength,
                                            Volatile.Read(
                                                ref lastInputDeviceIndex),
                                            readStreamGeneration);
                                    }
                                    finally
                                    {
                                        EndFeedbackCallback();
                                    }
                                }
                            }
                    }
                    else
                    {
                        stream.ReadExactly(buffer, 0, feedbackLength);
                        long frameNumber = Interlocked.Increment(
                            ref feedbackFramesRead);
                        if (Interlocked.CompareExchange(
                                ref feedbackFirstFrameLogged, 1, 0) == 0)
                        {
                            AppLogger.LogToGui(
                                $"VIIPER feedback stream active: bus={stream.BusId} device={stream.DevId} port={stream.UsbipPort} sidecar={audioOnlySidecar} gamepadOnly={gamepadOnly} rawPayload={feedbackLength} sequenceCount={frameNumber}",
                                false);
                        }
                        if (TryBeginFeedbackReaderCallback(stream,
                                readStreamGeneration))
                        {
                            try
                            {
                                ApplyFeedback(buffer, feedbackLength,
                                    freshNativeOutput: true,
                                    nativeOutputScratch: nativeOutputScratch,
                                    nativeOutputStreamGeneration:
                                        readStreamGeneration);
                            }
                            finally
                            {
                                EndFeedbackCallback();
                            }
                        }
                    }
                }
            }
            catch (IOException exception)
            {
                MarkXboxOneRuntimeStreamFault(stream, readStreamGeneration);
                if (connected && !writerStopRequested &&
                    !TryRecoverStream("feedback reader stopped", readStreamGeneration))
                {
                    AppLogger.LogToGui($"VIIPER {viiperType} feedback reader stopped: {exception.Message}", true);
                }
            }
            catch (SocketException)
            {
                MarkXboxOneRuntimeStreamFault(stream, readStreamGeneration);
                if (connected && !writerStopRequested &&
                    !TryRecoverStream("feedback reader stopped due to socket error",
                        readStreamGeneration))
                {
                    AppLogger.LogToGui($"VIIPER {viiperType} feedback reader stopped due to socket error.", true);
                }
            }
            catch (ObjectDisposedException)
            {
                MarkXboxOneRuntimeStreamFault(stream, readStreamGeneration);
            }
            finally
            {
                // The captured reader owns this exact session. A stale reader
                // must never retire whichever successor is now in the field.
                if (physicalFeedbackSession != null &&
                    !physicalFeedbackSession.TryRetire())
                {
                    AppLogger.LogToGui(
                        "Xbox One feedback stream ended, but physical neutral state acceptance could not be confirmed.", true);
                }
                lock (feedbackThreadLock)
                {
                    if (ReferenceEquals(feedbackThread, Thread.CurrentThread))
                    {
                        feedbackThread = null;
                    }
                }
            }
        }

        private bool DeliverXboxOneFeedback(ViiperDeviceStream stream,
            long readStreamGeneration, byte[] feedback, int feedbackLength)
        {
            if (!TryBeginFeedbackReaderCallback(stream, readStreamGeneration))
            {
                return false;
            }

            try
            {
                return TryApplyXboxOneFeedback(feedback, feedbackLength);
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"VIIPER Xbox One feedback delivery failed: {ex.GetType().Name}: {ex.Message}",
                    true);
                return false;
            }
            finally
            {
                EndFeedbackCallback();
            }
        }

        private void OnXboxOneFeedbackDispatchCompleted(
            ViiperDeviceStream stream, long readStreamGeneration, byte[] payload,
            int payloadLength, ulong correlation, bool delivered,
            bool acknowledged)
        {
            if (IsAcknowledgedXboxOneTerminalFeedback(payload,
                    payloadLength, correlation, delivered, acknowledged))
            {
                lock (feedbackCallbackAdmissionLock)
                {
                    if (connected && !feedbackDispatchStopRequested &&
                        readStreamGeneration == Interlocked.Read(
                            ref streamGeneration) &&
                        ReferenceEquals(Volatile.Read(ref deviceStream), stream))
                    {
                        xboxOneTerminalFeedbackAcknowledged.Set();
                    }
                }
            }
        }

        internal static bool IsAcknowledgedXboxOneTerminalFeedback(
            byte[] payload, int payloadLength, ulong correlation,
            bool delivered, bool acknowledged)
        {
            return delivered && acknowledged && correlation != 0 &&
                payload != null && payloadLength ==
                    ControllerFeedbackFrame.SerializedLength &&
                payloadLength <= payload.Length &&
                ControllerFeedbackFrame.TryReadFrom(
                    payload.AsSpan(0, payloadLength),
                    out ControllerFeedbackFrame frame) && frame.IsStop;
        }

        private void ApplyFeedback(byte[] feedback, int feedbackLength,
            int expectedDeviceIndex = -1,
            bool freshNativeOutput = true,
            byte[] nativeOutputScratch = null,
            long nativeOutputStreamGeneration = 0)
        {
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if ((expectedDeviceIndex >= 0 &&
                    expectedDeviceIndex != deviceIndex) ||
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

            if (device is Switch2RuntimeInputDevice && IsDualSenseType() &&
                TryHandleSwitch2DualSenseHdRumbleFeedback(device, feedback,
                    feedbackLength, nativeOutputStreamGeneration))
            {
                return;
            }

            if (device is Switch2RuntimeInputDevice &&
                viiperType != ViiperVirtualDeviceType.XboxOne &&
                TryDecodeCanonicalFeedbackForSwitch2(viiperType, feedback,
                    feedbackLength, out ControllerFeedbackActuatorState
                        switch2State))
            {
                TryApplySwitch2VirtualFeedback(switch2State);
                return;
            }

            switch (viiperType)
            {
                case ViiperVirtualDeviceType.XboxOne:
                    TryApplyXboxOneFeedback(feedback, feedbackLength);
                    break;

                case ViiperVirtualDeviceType.Xbox360:
                    if (Xbox360CanonicalFeedbackAdapter.TryDecode(feedback,
                            feedbackLength,
                            out ControllerFeedbackActuatorState xboxState))
                    {
                        Xbox360CanonicalFeedbackAdapter.ProjectLegacy(
                            xboxState, out byte heavySlow,
                            out byte lightFast);
                        Program.rootHub.SetDevRumble(device, heavySlow,
                            lightFast, deviceIndex);
                        ApplyGameRumbleTriggerVibration(device, deviceIndex,
                            lightFast, heavySlow);
                    }
                    break;

                case ViiperVirtualDeviceType.DualShock4:
                    if (feedbackLength >= 7)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[1], feedback[0], deviceIndex);
                        ApplyGameRumbleTriggerVibration(device, deviceIndex,
                            feedback[0], feedback[1]);
                        if (ShouldApplyGameLightbar(deviceIndex))
                        {
                            ApplyLightbar(device, feedback[2], feedback[3],
                                feedback[4], feedback[5], feedback[6]);
                        }
                    }
                    break;

                case ViiperVirtualDeviceType.DualSense:
                case ViiperVirtualDeviceType.DualSenseEdge:
                    if (feedbackLength >= DualSenseBaseFeedbackLength)
                    {
                        bool nativeForwardingAllowed = IsNativeDualSenseFeedbackCompatible(device);
                        if (nativeForwardingAllowed &&
                            TryApplyBluetoothCombinedHapticsOutputReport(device,
                                deviceIndex, feedback, feedbackLength,
                                freshNativeOutput, nativeOutputScratch,
                                nativeOutputStreamGeneration))
                        {
                            break;
                        }

                        if (nativeForwardingAllowed &&
                            TryApplyBluetoothHapticsOutputReport(device,
                                deviceIndex, feedback, feedbackLength))
                        {
                            break;
                        }

                        if (nativeForwardingAllowed && !freshNativeOutput)
                        {
                            // An audio interval is not a new HID command.
                            // USB targets cannot consume the BT carrier, and
                            // BT template admission can temporarily be busy.
                            // Neither rejection permits replaying the cached
                            // native state or converting its compatibility
                            // motor bytes (usually zero) into local rumble:
                            // that can disarm the game's waveform haptics.
                            // Keep transport recovery in the native media lane.
                            break;
                        }

                        if (nativeForwardingAllowed &&
                            TryApplyNativeDualSenseOutputReport(device,
                                deviceIndex, feedback, feedbackLength,
                                nativeOutputScratch,
                                nativeOutputStreamGeneration))
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
                        if (ShouldApplyGameLightbar(deviceIndex))
                        {
                            ApplyLightbar(device, feedback[2], feedback[3],
                                feedback[4], 0, 0);
                        }
                        ApplyDualSenseTriggerFeedback(device, deviceIndex, feedback, feedbackLength);
                        ApplyGameRumbleTriggerVibration(device, deviceIndex,
                            lightFast, heavySlow);
                    }
                    break;

                case ViiperVirtualDeviceType.Switch2Pro:
                    if (feedbackLength ==
                            Switch2VirtualOutputState.WireLength &&
                        Switch2VirtualOutputState.TryDecode(
                            feedback.AsSpan(0, feedbackLength),
                            out Switch2VirtualOutputState output,
                            out _))
                    {
                        Interlocked.Increment(
                            ref switch2FeedbackValidated);
                        Switch2VirtualFeedbackSession switch2Session =
                            device is Switch2RuntimeInputDevice ?
                                Volatile.Read(
                                    ref switch2FeedbackSession) : null;
                        if (output.HasRumble)
                        {
                            if (device is Switch2RuntimeInputDevice)
                            {
                                bool hasAmplitude =
                                    HasHdRumbleAmplitude(output.LeftRumble) ||
                                    HasHdRumbleAmplitude(output.RightRumble);
                                ControllerFeedbackActuatorState marker =
                                    hasAmplitude ?
                                        new ControllerFeedbackActuatorState(
                                            1, 0, 0, 0) : default;
                                int bodyStrengthPercent =
                                    GetSwitch2BodyStrengthPercent(
                                        deviceIndex);
                                bool xboxBodyCarrierMode =
                                    GetSwitch2XboxBodyRumbleMode(
                                        deviceIndex);
                                int xboxBodyFrequencyLevel =
                                    GetSwitch2XboxBodyRumbleFrequency(
                                        deviceIndex);
                                int rumbleDelayMilliseconds =
                                    GetSwitch2RumbleDelayMilliseconds(
                                        deviceIndex);
                                long feedbackProfileRevision =
                                    GetSwitch2FeedbackProfileRevision(
                                        deviceIndex);
                                bool published = switch2Session != null &&
                                    (hasAmplitude ?
                                        switch2Session.
                                            TryPublishSourcePreserved(
                                            marker,
                                            Switch2HdRumbleFeedbackFidelity.
                                                NativeSwitch2PassThrough,
                                            output.LeftRumble,
                                            output.RightRumble,
                                            bodyStrengthPercent,
                                            xboxBodyCarrierMode,
                                            xboxBodyFrequencyLevel,
                                            rumbleDelayMilliseconds,
                                            feedbackProfileRevision) :
                                        switch2Session.TryPublish(marker,
                                            bodyStrengthPercent:
                                                bodyStrengthPercent,
                                            xboxBodyCarrierMode:
                                                xboxBodyCarrierMode,
                                            xboxBodyFrequencyLevel:
                                                xboxBodyFrequencyLevel,
                                            rumbleDelayMilliseconds:
                                                rumbleDelayMilliseconds,
                                            profileRevision:
                                                feedbackProfileRevision));
                                if (!published)
                                {
                                    Interlocked.Increment(
                                        ref switch2FeedbackRejected);
                                }
                            }
                            else
                            {
                                // Preserve the validated oscillator fields for
                                // a future basis-backed translator. Raw maxima
                                // mix headers, frequency/control, and amplitude
                                // and can dangerously mis-drive a physical
                                // controller.
                                Interlocked.Increment(
                                    ref switch2RumbleFramesPreserved);
                                if (TryTranslateSwitch2VirtualOutputToLegacyRumble(
                                        output, out byte lightFast,
                                        out byte heavySlow))
                                {
                                    Program.rootHub.SetDevRumble(device,
                                        lightFast, heavySlow, deviceIndex);
                                    ApplyGameRumbleTriggerVibration(device,
                                        deviceIndex, lightFast, heavySlow);
                                }
                            }
                        }
                        if (output.HasPlayerLed)
                        {
                            if (device is Switch2RuntimeInputDevice)
                            {
                                if (switch2Session == null ||
                                    !switch2Session.TryRequestPlayerLedMask(
                                        output.PlayerLedMask))
                                {
                                    Interlocked.Increment(
                                        ref switch2FeedbackRejected);
                                }
                            }
                            else
                            {
                                // Other physical protocols do not expose an
                                // equivalent four-segment player indicator.
                                Interlocked.Increment(
                                    ref switch2LedOnlyFramesPreserved);
                            }
                        }
                    }
                    else
                    {
                        // Unknown or malformed output is ignored. In
                        // particular, do not convert rejection into a
                        // synthetic stop which could clobber another owner.
                        Interlocked.Increment(ref switch2FeedbackRejected);
                    }
                    break;
            }
        }

        private static bool HasHdRumbleAmplitude(
            in Switch2HdRumbleGroup group) =>
            group.First.HasNonzeroAmplitude ||
            group.Second.HasNonzeroAmplitude ||
            group.Third.HasNonzeroAmplitude;

        private bool TryHandleSwitch2DualSenseHdRumbleFeedback(
            DS4Device device, byte[] feedback, int feedbackLength,
            long sourceStreamGeneration)
        {
            if (!TryDecodeCanonicalFeedbackForSwitch2(viiperType,
                    feedback, feedbackLength, out _))
            {
                return false;
            }
            DS4State state = device?.GetRawCurrentStateRef();
            bool leftTriggerActive = state != null &&
                (state.L2Btn || state.L2 != 0);
            bool rightTriggerActive = state != null &&
                (state.R2Btn || state.R2 != 0);
            int hapticsReportOffset = feedback != null && feedbackLength >=
                    DualSenseCombinedExtendedFeedbackLength &&
                feedback[DualSenseCombinedBluetoothReportOffset] == 0x36 ?
                DualSenseCombinedBluetoothReportOffset :
                DualSenseBluetoothHapticsReportOffset;
            Switch2VirtualFeedbackSession session =
                Volatile.Read(ref switch2FeedbackSession);
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            int bodyStrengthPercent = GetSwitch2BodyStrengthPercent(
                deviceIndex);
            bool xboxBodyCarrierMode = GetSwitch2XboxBodyRumbleMode(
                deviceIndex);
            int xboxBodyFrequencyLevel =
                GetSwitch2XboxBodyRumbleFrequency(deviceIndex);
            int rumbleDelayMilliseconds =
                GetSwitch2RumbleDelayMilliseconds(deviceIndex);
            long feedbackProfileRevision =
                GetSwitch2FeedbackProfileRevision(deviceIndex);
            bool published = switch2DualSenseFeedbackPolicyLane.TryPublish(
                session, deviceIndex, feedback, feedbackLength,
                hapticsReportOffset, leftTriggerActive, rightTriggerActive,
                bodyStrengthPercent, xboxBodyCarrierMode,
                xboxBodyFrequencyLevel, rumbleDelayMilliseconds,
                feedbackProfileRevision, sourceStreamGeneration != 0 ?
                    sourceStreamGeneration : Interlocked.Read(ref streamGeneration));
            if (published)
            {
                Interlocked.Increment(ref switch2FeedbackValidated);
            }
            else
            {
                Interlocked.Increment(ref switch2FeedbackRejected);
            }
            return true;
        }

        internal void RefreshSwitch2DualSenseConversionPolicy(
            int expectedDeviceIndex)
        {
            // Profile UI only, never input publication. The captured session's
            // existing owner still authenticates its exact physical lifetime.
            if (!connected || !IsDualSenseType() || audioOnlySidecar ||
                expectedDeviceIndex != Volatile.Read(ref lastInputDeviceIndex))
            {
                return;
            }
            ViiperDeviceStream stream = Volatile.Read(ref deviceStream);
            long generation = Interlocked.Read(ref streamGeneration);
            if (!TryBeginFeedbackReaderCallback(stream, generation))
            {
                return;
            }
            try
            {
                DS4Device target = Program.rootHub != null && expectedDeviceIndex >= 0 &&
                    expectedDeviceIndex < Program.rootHub.DS4Controllers.Length ?
                    Program.rootHub.DS4Controllers[expectedDeviceIndex] : null;
                if (target is not Switch2RuntimeInputDevice)
                {
                    return;
                }
                DS4State state = target.GetRawCurrentStateRef();
                _ = switch2DualSenseFeedbackPolicyLane.TryRefresh(
                    Volatile.Read(ref switch2FeedbackSession), expectedDeviceIndex,
                    GetSwitch2FeedbackProfileRevision(expectedDeviceIndex), generation,
                    state != null && (state.L2Btn || state.L2 != 0),
                    state != null && (state.R2Btn || state.R2 != 0));
            }
            finally
            {
                EndFeedbackCallback();
            }
        }

        internal void QueueSwitch2DualSenseConversionPolicyRefresh(int expectedDeviceIndex)
        {
            // CheckProfileOptions can execute on the physical input queue.
            // This is only a bounded wake request; the existing feedback
            // worker owns the release/publication and physical pump.
            if (IsDualSenseType() && !audioOnlySidecar &&
                expectedDeviceIndex == Volatile.Read(ref lastInputDeviceIndex))
            {
                Interlocked.Exchange(ref switch2DualSensePolicyRefreshRequested, 1);
                feedbackControlSignal.Set();
            }
        }

        internal void QueueXboxFeedbackPolicyRefresh(int expectedDeviceIndex)
        {
            if (viiperType != ViiperVirtualDeviceType.XboxOne || audioOnlySidecar ||
                expectedDeviceIndex != Volatile.Read(ref lastInputDeviceIndex) ||
                expectedDeviceIndex < 0 || expectedDeviceIndex >= Global.EnableOutputDataToDS4.Length) return;
            var session = Volatile.Read(ref switch2FeedbackSession);
            if (session == null)
            {
                if (Volatile.Read(ref Global.EnableOutputDataToDS4[expectedDeviceIndex])) return;
                var physical = Volatile.Read(ref xboxOnePhysicalFeedbackSession);
                if (physical == null || !physical.TryCaptureOutputPolicySequence(out ulong sequence)) return;
                EnqueueXboxOnePhysicalOutputSuppression(
                    new(physical, expectedDeviceIndex, Interlocked.Read(ref streamGeneration), sequence));
                feedbackControlSignal.Set();
                return;
            }
            if (expectedDeviceIndex >= Global.Switch2MapXboxImpulseTriggersToHdRumble.Length ||
                !session.TryCaptureXboxPolicyRevision(out ulong revision)) return;
            var policy = new Switch2XboxFeedbackPolicy(
                Volatile.Read(ref Global.EnableOutputDataToDS4[expectedDeviceIndex]),
                Volatile.Read(ref Global.Switch2MapXboxImpulseTriggersToHdRumble[expectedDeviceIndex]));
            Switch2XboxFeedbackPolicyRequest.Enqueue(ref switch2XboxPolicyRefreshRequested,
                new(session, expectedDeviceIndex, Interlocked.Read(ref streamGeneration), revision, policy),
                isCurrent: isCurrentSwitch2XboxPolicyRequest);
            feedbackControlSignal.Set();
        }

        private bool ProcessXboxFeedbackPolicyRefresh()
        {
            // Both source families use the existing Xbox delivery worker and
            // callback admission. Neither policy change invents a broker ACK.
            bool physicalComplete = ProcessXboxOnePhysicalOutputSuppression();
            bool switch2Complete = ProcessSwitch2XboxFeedbackPolicyRefreshCore();
            return physicalComplete && switch2Complete;
        }

        private bool ProcessXboxOnePhysicalOutputSuppression()
        {
            var request = Interlocked.Exchange(ref xboxOnePhysicalOutputSuppressionRequested, null);
            if (request == null) return true;
            bool completed = false;
            try
            {
                if (!connected || viiperType != ViiperVirtualDeviceType.XboxOne || audioOnlySidecar ||
                    request.DeviceIndex != Volatile.Read(ref lastInputDeviceIndex) ||
                    request.StreamGeneration != Interlocked.Read(ref streamGeneration) ||
                    !ReferenceEquals(request.Session, Volatile.Read(ref xboxOnePhysicalFeedbackSession)))
                    return completed = true;
                ViiperDeviceStream stream = Volatile.Read(ref deviceStream);
                if (!TryBeginFeedbackReaderCallback(stream, request.StreamGeneration)) return false;
                try
                {
                    var hub = Program.rootHub;
                    if (hub == null || request.DeviceIndex < 0 || request.DeviceIndex >= hub.DS4Controllers.Length ||
                        !request.Session.Targets(hub.DS4Controllers[request.DeviceIndex])) return completed = true;
                    return completed = request.Session.TrySuppressCurrentOutput(request.Sequence);
                }
                finally { EndFeedbackCallback(); }
            }
            finally
            {
                // Retry uses the same worker's bounded backoff, and can never
                // replace a newer profile request published while it ran.
                if (!completed) EnqueueXboxOnePhysicalOutputSuppression(request);
            }
        }

        private void EnqueueXboxOnePhysicalOutputSuppression(XboxOnePhysicalOutputSuppressionRequest request)
        {
            while (true)
            {
                // Read the pending identity before the live checks. If a new
                // owner publishes during this check, CAS retries those checks;
                // a delayed predecessor cannot overwrite its queued request.
                var previous = Volatile.Read(ref xboxOnePhysicalOutputSuppressionRequested);
                if (request.DeviceIndex != Volatile.Read(ref lastInputDeviceIndex) ||
                    request.StreamGeneration != Interlocked.Read(ref streamGeneration) ||
                    !ReferenceEquals(request.Session, Volatile.Read(ref xboxOnePhysicalFeedbackSession))) return;
                if (previous != null && ReferenceEquals(previous.Session, request.Session) &&
                    previous.DeviceIndex == request.DeviceIndex && previous.StreamGeneration == request.StreamGeneration &&
                    previous.Sequence >= request.Sequence) return;
                if (ReferenceEquals(Interlocked.CompareExchange(ref xboxOnePhysicalOutputSuppressionRequested,
                        request, previous), previous)) return;
            }
        }

        private bool ProcessSwitch2XboxFeedbackPolicyRefreshCore()
        {
            var request = Interlocked.Exchange(ref switch2XboxPolicyRefreshRequested, null);
            if (request == null) return true;
            bool completed = false;
            try
            {
                completed = RefreshSwitch2XboxFeedbackPolicy(request);
                return completed;
            }
            finally
            {
                // Preserve cleanup authority even if a dependency throws; the
                // dispatcher owns retry pacing and must not lose the request.
                if (!completed) Switch2XboxFeedbackPolicyRequest.Enqueue(
                    ref switch2XboxPolicyRefreshRequested, request, retry: true,
                    isCurrent: isCurrentSwitch2XboxPolicyRequest);
            }
        }

        private bool IsCurrentSwitch2XboxPolicyRequest(Switch2XboxFeedbackPolicyRequest request) =>
            request.DeviceIndex == Volatile.Read(ref lastInputDeviceIndex) &&
            request.StreamGeneration == Interlocked.Read(ref streamGeneration) &&
            ReferenceEquals(request.Session, Volatile.Read(ref switch2FeedbackSession));

        private Switch2XboxFeedbackPolicy ReadSwitch2XboxLivePolicy()
        {
            int index = Volatile.Read(ref lastInputDeviceIndex);
            if (index < 0 || index >= Global.EnableOutputDataToDS4.Length ||
                index >= Global.Switch2MapXboxImpulseTriggersToHdRumble.Length) return new(false, false);
            return new(Volatile.Read(ref Global.EnableOutputDataToDS4[index]),
                Volatile.Read(ref Global.Switch2MapXboxImpulseTriggersToHdRumble[index]));
        }

        private bool RefreshSwitch2XboxFeedbackPolicy(Switch2XboxFeedbackPolicyRequest request)
        {
            // UI/profile edits wake this existing feedback worker. A request
            // for a replaced stream, slot or session is obsolete, not retryable.
            if (!connected || viiperType != ViiperVirtualDeviceType.XboxOne || audioOnlySidecar ||
                request.DeviceIndex != Volatile.Read(ref lastInputDeviceIndex) ||
                request.StreamGeneration != Interlocked.Read(ref streamGeneration) ||
                !ReferenceEquals(request.Session, Volatile.Read(ref switch2FeedbackSession))) return true;
            ViiperDeviceStream stream = Volatile.Read(ref deviceStream);
            if (!TryBeginFeedbackReaderCallback(stream, request.StreamGeneration)) return false;
            try
            {
                var hub = Program.rootHub;
                if (hub == null || request.DeviceIndex < 0 || request.DeviceIndex >= hub.DS4Controllers.Length ||
                    hub.DS4Controllers[request.DeviceIndex] is not Switch2RuntimeInputDevice) return true;
                if (!request.Session.TryCaptureXboxPolicyRevision(out _)) return true;
                return request.Session.TryRefreshXboxOutputPolicy(request.Policy, request.PublicationRevision);
            }
            finally { EndFeedbackCallback(); }
        }

        private static Switch2DualSenseConversionPolicy
            ReadSwitch2DualSenseConversionPolicy(int deviceIndex) =>
                Switch2DualSenseConversionPolicy.ReadProfile(deviceIndex);

        private long ReadFeedbackStreamGeneration() =>
            Volatile.Read(ref streamGeneration);

        /// <summary>
        /// Builds one atomic DualSense-to-Switch-2 haptic representation. PCM
        /// keeps its three chronological stereo slices, compatibility motors
        /// are added rather than discarded, and a supported adaptive-trigger
        /// program is overlaid only on its pressed physical side. The latter
        /// is an explicit approximation because Switch 2 has digital triggers
        /// and no resistance actuator.
        /// </summary>
        internal static bool TryBuildSwitch2DualSenseHdRumbleGroups(
            byte[] feedback, int feedbackLength, int hapticsReportOffset,
            bool leftTriggerActive, bool rightTriggerActive,
            out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right,
            out Switch2HdRumbleFeedbackFidelity fidelity,
            bool audioHapticsEnabled = true,
            bool adaptiveTriggersEnabled = true)
        {
            left = default;
            right = default;
            fidelity = Switch2HdRumbleFeedbackFidelity.Invalid;
            if (feedback == null || feedbackLength <
                    DualSenseBaseFeedbackLength ||
                feedbackLength > feedback.Length)
            {
                return false;
            }

            ushort bodyLow = (ushort)(feedback[0] * 257);
            ushort bodyHigh = (ushort)(feedback[1] * 257);
            Switch2HdRumbleGroup body =
                Switch2HdRumbleFeedbackTranslator.
                    CreateCompatibilityGroup(bodyLow, bodyHigh);
            bool hasPcm = audioHapticsEnabled && DualSenseHapticsTranslator.
                TryTranslateToSwitch2Groups(feedback, feedbackLength,
                    hapticsReportOffset, out left, out right);
            if (hasPcm)
            {
                // Compatibility motor bytes can coexist with the audio lane.
                // Preserve both instead of letting a silent PCM carrier erase
                // conventional game rumble.
                left = DualSenseAdaptiveTriggerHdRumbleTranslator.
                    MixPcmWithCompatibility(left, body);
                right = DualSenseAdaptiveTriggerHdRumbleTranslator.
                    MixPcmWithCompatibility(right, body);
            }
            else
            {
                left = body;
                right = body;
            }

            bool hasAdaptiveTrigger = false;
            if (adaptiveTriggersEnabled &&
                feedbackLength >= DualSenseCompatExtendedFeedbackLength)
            {
                if (rightTriggerActive &&
                    DualSenseAdaptiveTriggerHdRumbleTranslator.TryTranslate(
                        feedback.AsSpan(DualSenseTriggerFeedbackOffset,
                            DualSenseTriggerEffectLength),
                        out Switch2HdRumbleGroup rightTrigger))
                {
                    right = DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(
                        right, rightTrigger);
                    hasAdaptiveTrigger = true;
                }

                int leftOffset = DualSenseTriggerFeedbackOffset +
                    DualSenseTriggerEffectLength;
                if (leftTriggerActive &&
                    DualSenseAdaptiveTriggerHdRumbleTranslator.TryTranslate(
                        feedback.AsSpan(leftOffset,
                            DualSenseTriggerEffectLength),
                        out Switch2HdRumbleGroup leftTrigger))
                {
                    left = DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(
                        left, leftTrigger);
                    hasAdaptiveTrigger = true;
                }
            }

            if (!hasPcm && !hasAdaptiveTrigger)
            {
                // Let the canonical body-rumble path retain its normal
                // arbitration and fidelity label when no richer source data
                // was present.
                left = right = default;
                return false;
            }

            fidelity = hasAdaptiveTrigger ?
                Switch2HdRumbleFeedbackFidelity.
                    DualSenseAdaptiveTriggerApproximation :
                Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand;
            return true;
        }

        internal static bool TryDecodeCanonicalFeedbackForSwitch2(
            ViiperVirtualDeviceType sourceType, byte[] feedback,
            int feedbackLength, out ControllerFeedbackActuatorState state)
        {
            state = default;
            if (feedback == null || feedbackLength < 0 ||
                feedbackLength > feedback.Length)
            {
                return false;
            }

            if (sourceType == ViiperVirtualDeviceType.Xbox360)
            {
                return Xbox360CanonicalFeedbackAdapter.TryDecode(feedback,
                    feedbackLength, out state);
            }

            byte lightFast;
            byte heavySlow;
            switch (sourceType)
            {
                case ViiperVirtualDeviceType.DualShock4:
                    if (feedbackLength < 7)
                    {
                        return false;
                    }
                    lightFast = feedback[0];
                    heavySlow = feedback[1];
                    break;

                case ViiperVirtualDeviceType.DualSense:
                case ViiperVirtualDeviceType.DualSenseEdge:
                    if (feedbackLength < DualSenseBaseFeedbackLength)
                    {
                        return false;
                    }
                    lightFast = feedback[1];
                    heavySlow = feedback[0];
                    int hapticsReportOffset = feedbackLength >=
                            DualSenseCombinedExtendedFeedbackLength &&
                        feedback[DualSenseCombinedBluetoothReportOffset] ==
                            0x36 ?
                        DualSenseCombinedBluetoothReportOffset :
                        DualSenseBluetoothHapticsReportOffset;
                    DualSenseHapticsTranslator.Translate(feedback,
                        feedbackLength, hapticsReportOffset, out lightFast,
                        out heavySlow);
                    break;

                default:
                    return false;
            }

            state = new ControllerFeedbackActuatorState(
                (ushort)(heavySlow * 257),
                (ushort)(lightFast * 257), 0, 0);
            return true;
        }

        private bool TryApplySwitch2VirtualFeedback(
            in ControllerFeedbackActuatorState state)
        {
            Switch2VirtualFeedbackSession session =
                Volatile.Read(ref switch2FeedbackSession);
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            int bodyStrengthPercent = GetSwitch2BodyStrengthPercent(
                deviceIndex);
            bool xboxBodyCarrierMode = GetSwitch2XboxBodyRumbleMode(
                deviceIndex);
            int xboxBodyFrequencyLevel =
                GetSwitch2XboxBodyRumbleFrequency(deviceIndex);
            bool published = session != null && session.TryPublish(state,
                bodyStrengthPercent: bodyStrengthPercent,
                xboxBodyCarrierMode: xboxBodyCarrierMode,
                xboxBodyFrequencyLevel: xboxBodyFrequencyLevel,
                rumbleDelayMilliseconds:
                    GetSwitch2RumbleDelayMilliseconds(deviceIndex),
                profileRevision:
                    GetSwitch2FeedbackProfileRevision(deviceIndex));
            if (published)
            {
                Interlocked.Increment(ref switch2FeedbackValidated);
            }
            else
            {
                Interlocked.Increment(ref switch2FeedbackRejected);
            }
            return published;
        }

        private bool TryApplyXboxOneFeedback(byte[] feedback,
            int feedbackLength)
        {
            if (feedback == null || feedbackLength !=
                    ControllerFeedbackFrame.SerializedLength ||
                feedbackLength > feedback.Length ||
                !ControllerFeedbackFrame.TryReadFrom(
                    feedback.AsSpan(0, feedbackLength),
                    out ControllerFeedbackFrame canonicalFrame))
            {
                return false;
            }

            if (Volatile.Read(ref xboxOneSwitch2FeedbackPreRetired) == 1)
            {
                bool accepted =
                    IsAuthorizedXboxOneTerminalStopAfterPhysicalRetirement(
                        canonicalFrame, xboxOneFeedbackBinding,
                        xboxOneLastFeedbackSequence);
                if (accepted)
                {
                    xboxOneLastFeedbackSequence = canonicalFrame.Sequence;
                    Interlocked.Increment(ref switch2FeedbackValidated);
                }
                else
                {
                    Interlocked.Increment(ref switch2FeedbackRejected);
                }
                return accepted;
            }

            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if (deviceIndex < 0 ||
                Program.rootHub == null ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                deviceIndex >= Global.EnableOutputDataToDS4.Length)
            {
                return false;
            }

            DS4Device device = Program.rootHub.DS4Controllers[deviceIndex];
            if (device == null)
            {
                return false;
            }

            // A profile's output policy is not a broker rejection. Consume
            // the exact ordering/lifetime watermark as Neutral through the
            // ordinary canonical owner so old rumble cannot survive or be
            // resurrected when output is re-enabled. Stop must remain Stop.
            bool outputEnabled = Volatile.Read(
                ref Global.EnableOutputDataToDS4[deviceIndex]);
            if (!TryApplyXboxOneFeedbackOutputPolicy(canonicalFrame,
                    outputEnabled, out ControllerFeedbackFrame effectiveFrame))
            {
                return false;
            }
            Span<byte> effectiveFeedback = stackalloc byte[
                ControllerFeedbackFrame.SerializedLength];
            if (!effectiveFrame.TryWriteTo(effectiveFeedback))
            {
                return false;
            }

            Switch2VirtualFeedbackSession switch2Session =
                Volatile.Read(ref switch2FeedbackSession);
            if (switch2Session != null)
            {
                bool mapImpulseTriggers = outputEnabled && deviceIndex <
                        Global.Switch2MapXboxImpulseTriggersToHdRumble.Length &&
                    Volatile.Read(ref Global.
                        Switch2MapXboxImpulseTriggersToHdRumble[deviceIndex]);
                bool dynamicImpulseFrequency = deviceIndex >=
                        Global.Switch2XboxImpulseDynamicFrequency.Length ||
                    Volatile.Read(ref Global.
                        Switch2XboxImpulseDynamicFrequency[deviceIndex]);
                int impulseFrequency = deviceIndex <
                        Global.Switch2XboxImpulseFrequency.Length ?
                    Volatile.Read(ref Global.
                        Switch2XboxImpulseFrequency[deviceIndex]) :
                    Switch2HdRumbleImpulseTuning.DefaultFixedFrequencyLevel;
                int impulseStrength = deviceIndex <
                        Global.Switch2XboxImpulseStrength.Length ?
                    Volatile.Read(ref Global.
                        Switch2XboxImpulseStrength[deviceIndex]) :
                    Switch2HdRumbleImpulseTuning.DefaultStrengthLevel;
                int bodyStrengthPercent =
                    GetSwitch2BodyStrengthPercent(deviceIndex);
                bool xboxBodyCarrierMode =
                    GetSwitch2XboxBodyRumbleMode(deviceIndex);
                int xboxBodyFrequencyLevel =
                    GetSwitch2XboxBodyRumbleFrequency(deviceIndex);
                int rumbleDelayMilliseconds = outputEnabled ?
                    GetSwitch2RumbleDelayMilliseconds(deviceIndex) : 0;
                long feedbackProfileRevision =
                    GetSwitch2FeedbackProfileRevision(deviceIndex);
                bool published = switch2Session.TryPublish(
                    effectiveFeedback, mapImpulseTriggers,
                    dynamicImpulseFrequency, impulseFrequency,
                    impulseStrength, bodyStrengthPercent,
                    xboxBodyCarrierMode, xboxBodyFrequencyLevel,
                    rumbleDelayMilliseconds, feedbackProfileRevision,
                    readLiveXboxPolicy: readSwitch2XboxLivePolicy);
                if (published)
                {
                    Interlocked.Increment(ref switch2FeedbackValidated);
                }
                else
                {
                    Interlocked.Increment(ref switch2FeedbackRejected);
                }
                if (published)
                {
                    xboxOneLastFeedbackSequence = canonicalFrame.Sequence;
                }
                return published;
            }

            XboxOnePhysicalFeedbackSession physicalSession =
                Volatile.Read(ref xboxOnePhysicalFeedbackSession);
            if (physicalSession == null || !physicalSession.Targets(device) ||
                !physicalSession.TryPublish(effectiveFeedback))
            {
                return false;
            }
            xboxOneLastFeedbackSequence = canonicalFrame.Sequence;
            return true;
        }

        internal bool TryCreateXboxOnePhysicalFeedbackSession(
            XboxOneAuthorizedFeedbackBinding binding, DS4Device target,
            int deviceIndex, out XboxOnePhysicalFeedbackSession session,
            TimeProvider timeProvider = null)
        {
            XboxOnePhysicalFeedbackSession created = null;
            bool accepted = XboxOnePhysicalFeedbackSession.TryCreateOwned(
                binding, target, (state, release) =>
                    TryPublishXboxOnePhysicalFeedbackState(created, target,
                        deviceIndex, state, release), out created, timeProvider,
                onFailure: () => AppLogger.LogToGui(
                    "Xbox One physical feedback owner was fenced after a state delivery or expiry-watchdog failure.", true),
                isOutputEnabled: () => deviceIndex >= 0 && deviceIndex < Global.EnableOutputDataToDS4.Length &&
                    Volatile.Read(ref Global.EnableOutputDataToDS4[deviceIndex]));
            session = created;
            return accepted;
        }

        private bool TryPublishXboxOnePhysicalFeedbackState(
            XboxOnePhysicalFeedbackSession owner, DS4Device device,
            int deviceIndex, in ControllerFeedbackActuatorState state,
            bool release)
        {
            ControlService hub = Program.rootHub;
            if (owner == null || !ReferenceEquals(owner,
                    Volatile.Read(ref xboxOnePhysicalFeedbackSession)) ||
                deviceIndex != Volatile.Read(ref lastInputDeviceIndex) ||
                hub == null || deviceIndex < 0 || deviceIndex >=
                    hub.DS4Controllers.Length ||
                !ReferenceEquals(device, hub.DS4Controllers[deviceIndex]))
            {
                return false;
            }

            // This publishes to the existing sole physical output owner; it
            // does not perform or acknowledge a hardware HID flush.
            bool hasIndependentTriggerActuators =
                device is DualSenseDevice dualSense &&
                IsCurrentPhysicalSonyDualSense(dualSense);
            XboxOneCanonicalFeedbackAdapter.ProjectPhysical(state,
                hasIndependentTriggerActuators,
                out byte heavySlow, out byte lightFast,
                out byte leftImpulse, out byte rightImpulse);
            hub.SetDevRumble(device, heavySlow, lightFast,
                deviceIndex);
            if (hasIndependentTriggerActuators)
            {
                ApplyGameRumbleTriggerVibration(device, deviceIndex,
                    rightImpulse, leftImpulse);
                if (release)
                {
                    ReleaseTriggerLabRumbleOverrides(deviceIndex, device);
                }
            }
            return true;
        }

        /// <summary>
        /// Disabling profile output suppresses actuator state, never its
        /// authenticated lifetime, anti-replay sequence, expiry, or terminal
        /// Stop. The resulting frame must still pass the exact existing
        /// physical/session ingress before feedback may be acknowledged.
        /// </summary>
        internal static bool TryApplyXboxOneFeedbackOutputPolicy(
            in ControllerFeedbackFrame frame, bool outputEnabled,
            out ControllerFeedbackFrame effectiveFrame)
        {
            effectiveFrame = default;
            if (!frame.HasValidInvariants() || frame.Source is not (
                    ControllerFeedbackSource.XboxOneVirtualDevice or
                    ControllerFeedbackSource.XboxSeriesVirtualDevice))
            {
                return false;
            }
            if (outputEnabled || frame.Command !=
                    ControllerFeedbackCommand.Apply)
            {
                effectiveFrame = frame;
                return true;
            }

            return ControllerFeedbackFrame.TryCreate(frame.Source,
                ControllerFeedbackCommand.Neutral, frame.Actuators,
                0, 0, 0, 0, frame.Sequence, frame.DeviceGeneration,
                frame.TransportGeneration, frame.OwnershipEpoch,
                frame.TimestampMicroseconds, frame.TimeToLiveMicroseconds,
                out effectiveFrame);
        }

        internal static bool
            IsAuthorizedXboxOneTerminalStopAfterPhysicalRetirement(
                in ControllerFeedbackFrame frame,
                XboxOneAuthorizedFeedbackBinding binding,
                ulong lastAcceptedSequence)
        {
            return binding != null && frame.HasValidInvariants() &&
                frame.IsStop && frame.Sequence > lastAcceptedSequence &&
                frame.Source == (ControllerFeedbackSource)binding.Source &&
                frame.DeviceGeneration == binding.DeviceGeneration &&
                frame.TransportGeneration == binding.TransportGeneration &&
                frame.OwnershipEpoch == binding.OwnershipEpoch &&
                frame.TimeToLiveMicroseconds ==
                    binding.TimeToLiveMicroseconds &&
                ControllerFeedbackClock.TryGetTimestampMicroseconds(
                    out ulong nowMicroseconds) &&
                frame.IsFreshAt(nowMicroseconds);
        }

        private static int GetSwitch2BodyStrengthPercent(int deviceIndex)
        {
            if (deviceIndex < 0 || deviceIndex >= Global.RumbleBoost.Length)
            {
                return Switch2HdRumbleBodyTuning.DefaultStrengthPercent;
            }

            int strengthPercent = Global.RumbleBoost[deviceIndex];
            return strengthPercent <=
                    Switch2HdRumbleBodyTuning.MaximumStrengthPercent ?
                strengthPercent :
                Switch2HdRumbleBodyTuning.DefaultStrengthPercent;
        }

        private static bool GetSwitch2XboxBodyRumbleMode(int deviceIndex) =>
            deviceIndex >= 0 &&
            deviceIndex < Global.Switch2XboxBodyRumbleMode.Length &&
            Volatile.Read(ref Global.
                Switch2XboxBodyRumbleMode[deviceIndex]);

        private static int GetSwitch2XboxBodyRumbleFrequency(int deviceIndex)
        {
            if (deviceIndex < 0 || deviceIndex >=
                    Global.Switch2XboxBodyRumbleFrequency.Length)
            {
                return Switch2HdRumbleBodyTuning.
                    DefaultXboxFrequencyLevel;
            }

            int level = Volatile.Read(ref Global.
                Switch2XboxBodyRumbleFrequency[deviceIndex]);
            return level is >=
                    Switch2HdRumbleBodyTuning.MinimumXboxFrequencyLevel and <=
                    Switch2HdRumbleBodyTuning.MaximumXboxFrequencyLevel ?
                level :
                Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel;
        }

        private static int GetSwitch2RumbleDelayMilliseconds(int deviceIndex)
        {
            if (deviceIndex < 0 || deviceIndex >=
                    Global.Switch2RumbleDelayMilliseconds.Length)
            {
                return Switch2RumbleDelay.DefaultMilliseconds;
            }
            return Switch2RumbleDelay.Normalize(Volatile.Read(ref Global.
                Switch2RumbleDelayMilliseconds[deviceIndex]));
        }

        private static long GetSwitch2FeedbackProfileRevision(int deviceIndex)
        {
            if (deviceIndex < 0 || deviceIndex >= Global.
                    TEST_PROFILE_ITEM_COUNT)
            {
                return 0;
            }
            long revision = Global.ReadProfileSwitchRevision(deviceIndex);
            return revision < 0 ? 0 : revision;
        }

        /// <summary>
        /// Explicit safety gate for the unvalidated Switch 2 oscillator basis.
        /// A decoded frame is retained semantically, but neither raw maxima nor
        /// guessed frequency/amplitude routing may drive legacy motors.
        /// </summary>
        internal static bool TryTranslateSwitch2VirtualOutputToLegacyRumble(
            in Switch2VirtualOutputState output, out byte lightFast,
            out byte heavySlow)
        {
            _ = output;
            lightFast = 0;
            heavySlow = 0;
            return false;
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
            if (!connected || workerGeneration != Interlocked.Read(
                    ref microphoneWorkerGeneration))
            {
                return;
            }

            // A source that becomes active again supersedes every failed
            // disable from the same physical controller. Physical writes use
            // the epoch below to repair a stale completion without holding a
            // state lock across controller I/O.
            microphoneDisableRetries.Cancel(source);
            lock (microphoneSourceLock)
            {
                if (!connected || workerGeneration != Interlocked.Read(
                        ref microphoneWorkerGeneration))
                {
                    return;
                }

                microphoneSourceDevice = source;
                Interlocked.Increment(ref microphoneSourceGeneration);
                if (source is DualSenseDevice attachedDualSense)
                {
                    attachedDualSense.BluetoothMicrophoneOpusFrameReceived +=
                        BluetoothMicrophoneOpusFrameReceived;
                    attachedDualSense.ProfileMicrophoneMuteStateChanged +=
                        ProfileMicrophoneMuteStateChanged;
                    // Subscribe before the second read. A transition racing
                    // attachment is therefore observed either here or by the
                    // callback, with no polling-sized mute window.
                    Volatile.Write(ref microphoneMuted,
                        attachedDualSense.IsProfileMicrophoneMuted ? 1 : 0);
                }
                else
                {
                    source.BluetoothMicrophoneSbcFrameReceived +=
                        BluetoothMicrophoneSbcFrameReceived;
                    Volatile.Write(ref microphoneMuted, 0);
                }
            }
            Interlocked.Increment(ref microphoneControlEpoch);

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
                Interlocked.Increment(ref microphoneControlEpoch);
                armed = ApplyPhysicalBluetoothMicrophoneState(source,
                    enabled: true, out bool enabledAtCompletion);
                attempted = enabledAtCompletion;
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
            lock (triggerLabRumbleLock)
            {
                triggerLabRumbleStateKnown = false;
                lastTriggerLabLeftRumble = 0;
                lastTriggerLabRightRumble = 0;
                lastTriggerLabRumbleSignature = 0;
                lastTriggerLabLeftRumbleEnabled = false;
                lastTriggerLabRightRumbleEnabled = false;
            }
        }

        private void ReleaseTriggerLabRumbleOverrides(int deviceIndex,
            DS4Device expectedDevice = null)
        {
            lock (triggerLabRumbleLock)
            {
                if (!triggerLabRumbleStateKnown ||
                    (!lastTriggerLabLeftRumbleEnabled &&
                        !lastTriggerLabRightRumbleEnabled) ||
                    deviceIndex < 0 || Program.rootHub == null ||
                    deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                    Program.rootHub.DS4Controllers[deviceIndex] is not
                        DualSenseDevice dualSenseDevice ||
                    expectedDevice != null &&
                        !ReferenceEquals(expectedDevice, dualSenseDevice) ||
                    !IsCurrentPhysicalSonyDualSense(dualSenseDevice))
                {
                    return;
                }

                TriggerLabProfileSettings settings =
                    TriggerLabForDevice(deviceIndex);
                if (lastTriggerLabLeftRumbleEnabled)
                {
                    TriggerLabEffectEncoder.ApplyToDevice(dualSenseDevice,
                        TriggerId.LeftTrigger, settings?.Left,
                        settings?.Enabled == true &&
                            settings.LeftActive);
                }
                if (lastTriggerLabRightRumbleEnabled)
                {
                    TriggerLabEffectEncoder.ApplyToDevice(dualSenseDevice,
                        TriggerId.RightTrigger, settings?.Right,
                        settings?.Enabled == true &&
                            settings.RightActive);
                }
            }
        }

        private void ApplyGameRumbleTriggerVibration(DS4Device device,
            int deviceIndex, byte lightFast, byte heavySlow)
        {
            if (device is not DualSenseDevice dualSenseDevice ||
                !IsCurrentPhysicalSonyDualSense(dualSenseDevice))
            {
                return;
            }

            lock (triggerLabRumbleLock)
            {
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
                        dualSenseDevice, TriggerId.RightTrigger,
                        settings.Right, settings.RightActive, lightFast);
                }
                else if (restoreRight)
                {
                    TriggerLabEffectEncoder.ApplyToDevice(dualSenseDevice,
                        TriggerId.RightTrigger, settings?.Right,
                        settings?.Enabled == true &&
                            settings.RightActive);
                }
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
            ClearPendingMicrophoneFrames();
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
                IsFeedbackSpeakerDispatchRecentlyActive() ||
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
                microphoneQueueDepth = pendingMicrophoneCount;
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

        private bool IsFeedbackSpeakerDispatchRecentlyActive()
        {
            long lastDispatch = Interlocked.Read(
                ref lastFeedbackSpeakerDispatchTimestamp);
            if (lastDispatch <= 0)
            {
                return false;
            }

            long age = Stopwatch.GetTimestamp() - lastDispatch;
            return age >= 0 && age <= Stopwatch.Frequency;
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
            bool performFinalDisable = false;
            long workerGeneration = Interlocked.Read(
                ref microphoneWorkerGeneration);
            lock (microphoneSourceLock)
            {
                if (microphoneSourceDevice != null)
                {
                    source = microphoneSourceDevice;
                    if (source is DualSenseDevice dualSenseSource)
                    {
                        dualSenseSource.BluetoothMicrophoneOpusFrameReceived -=
                            BluetoothMicrophoneOpusFrameReceived;
                        dualSenseSource.ProfileMicrophoneMuteStateChanged -=
                            ProfileMicrophoneMuteStateChanged;
                    }
                    else
                    {
                        source.BluetoothMicrophoneSbcFrameReceived -=
                            BluetoothMicrophoneSbcFrameReceived;
                    }
                    microphoneSourceDevice = null;
                    Interlocked.Increment(ref microphoneSourceGeneration);
                    resetProcessor = true;
                }
            }

            if (source != null)
            {
                Interlocked.Increment(ref microphoneControlEpoch);
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
                    // Output teardown has no monitor left to service a retry.
                    // The lifecycle caller may wait here, but no source,
                    // generation, or queue lock is held during physical I/O.
                    performFinalDisable = true;
                }
            }

            if (performFinalDisable)
            {
                ApplyPhysicalBluetoothMicrophoneState(source, enabled: false,
                    out _);
            }

            lock (microphoneQueueLock)
            {
                resetProcessor |= pendingMicrophoneCount > 0;
                pendingMicrophoneHead = 0;
                pendingMicrophoneCount = 0;
                Array.Clear(pendingMicrophoneLengths, 0,
                    pendingMicrophoneLengths.Length);
                Array.Clear(pendingMicrophoneHasSequences, 0,
                    pendingMicrophoneHasSequences.Length);
                Array.Clear(pendingMicrophoneSourceGenerations, 0,
                    pendingMicrophoneSourceGenerations.Length);
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

        private void ProfileMicrophoneMuteStateChanged(
            object sender, EventArgs e)
        {
            lock (microphoneSourceLock)
            {
                if (!connected ||
                    !(sender is DualSenseDevice dualSenseSource) ||
                    !ReferenceEquals(microphoneSourceDevice,
                        dualSenseSource))
                {
                    return;
                }

                Volatile.Write(ref microphoneMuted,
                    dualSenseSource.IsProfileMicrophoneMuted ? 1 : 0);
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

            Interlocked.Increment(ref microphoneControlEpoch);
            bool applied = ApplyPhysicalBluetoothMicrophoneState(
                attempt.Target, enabled: false,
                out bool enabledAtCompletion);
            bool disabled = applied && !enabledAtCompletion;
            microphoneDisableRetries.CompleteAttempt(attempt,
                disabled || enabledAtCompletion,
                nextAttemptTimestamp: Stopwatch.GetTimestamp() + retryTicks);
        }

        /// <summary>
        /// Applies one physical microphone intent without holding any state or
        /// generation lock across controller I/O. If a newer attach/detach
        /// changes the desired state while the call is in flight, this caller
        /// repairs the same physical source to the newest state before it
        /// returns. All callers execute on lifecycle/media workers, never the
        /// physical input callback.
        /// </summary>
        private bool ApplyPhysicalBluetoothMicrophoneState(DS4Device source,
            bool enabled, out bool enabledAtCompletion)
        {
            bool requestedState = enabled;
            while (true)
            {
                bool applied;
                try
                {
                    applied = SetPhysicalBluetoothMicrophoneStreaming(source,
                        requestedState);
                }
                catch
                {
                    applied = false;
                }

                DS4Device currentSource;
                long sourceEpoch;
                do
                {
                    sourceEpoch = Interlocked.Read(
                        ref microphoneControlEpoch);
                    lock (microphoneSourceLock)
                    {
                        currentSource = microphoneSourceDevice;
                    }
                }
                while (sourceEpoch != Interlocked.Read(
                    ref microphoneControlEpoch));
                bool desiredState = connected &&
                    ReferenceEquals(currentSource, source) &&
                    !source.IsRemoved && !source.IsDisconnecting;
                if (desiredState == requestedState)
                {
                    enabledAtCompletion = desiredState;
                    return applied;
                }

                // A stale enable that completed after detach is repaired by a
                // disable; a stale disable that completed after reattach is
                // repaired by an enable. A concurrent newer command will make
                // the same check, so the latest desired state always wins.
                requestedState = desiredState;
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

            long sourceGeneration;
            lock (microphoneSourceLock)
            {
                if (!connected || !ReferenceEquals(source, microphoneSourceDevice))
                {
                    return;
                }
                sourceGeneration = Interlocked.Read(
                    ref microphoneSourceGeneration);
            }

            Interlocked.Increment(ref microphoneCompressedFramesReceived);
            Interlocked.Increment(ref microphoneOpusFramesReceived);
            Interlocked.Exchange(ref lastMicrophoneCompressedRxTimestamp,
                Stopwatch.GetTimestamp());
            TryEnqueuePendingMicrophoneFrame(MicrophoneCodec.Opus,
                opusFrame, DualSenseMicrophoneOpusFrameLength,
                sourceGeneration: sourceGeneration);

            microphoneWriterSignal.Set();
        }

        private void BluetoothMicrophoneSbcFrameReceived(DS4Device source,
            ushort sequence, byte[] sbcFrame)
        {
            if (sbcFrame == null || sbcFrame.Length < SbcFrame.HeaderSize)
            {
                return;
            }

            long sourceGeneration;
            lock (microphoneSourceLock)
            {
                if (!connected || !ReferenceEquals(source, microphoneSourceDevice))
                {
                    return;
                }
                sourceGeneration = Interlocked.Read(
                    ref microphoneSourceGeneration);
            }

            Interlocked.Increment(ref microphoneCompressedFramesReceived);
            Interlocked.Increment(ref microphoneSbcFramesReceived);
            Interlocked.Exchange(ref lastMicrophoneCompressedRxTimestamp,
                Stopwatch.GetTimestamp());
            TryEnqueuePendingMicrophoneFrame(MicrophoneCodec.Sbc,
                sbcFrame, sbcFrame.Length, sequence,
                hasSequence: true, sourceGeneration: sourceGeneration);

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

        private bool TryApplyNativeDualSenseOutputReport(DS4Device device,
            int deviceIndex, byte[] feedback, int feedbackLength,
            byte[] nativeOutputScratch,
            long nativeOutputStreamGeneration)
        {
            if (feedbackLength < DualSenseBluetoothHapticsReportOffset ||
                device is not DualSenseDevice dualSenseDevice ||
                feedback[DualSenseNativeOutputReportOffset] != 0x02 ||
                nativeOutputScratch == null || nativeOutputScratch.Length <
                    DualSenseNativeOutputReportLength)
            {
                return false;
            }

            PrepareNativeDualSenseOutputReportForProfileInto(feedback,
                deviceIndex, nativeOutputScratch);
            bool applied = dualSenseDevice.WriteRawOutputReportFromGame(
                nativeOutputScratch,
                0,
                DualSenseNativeOutputReportLength,
                out long nativeOutputRevision);
            if (applied)
            {
                TraceNativeGameOutput(feedback,
                    DualSenseNativeOutputReportOffset,
                    nativeOutputRevision, dualSenseDevice,
                    nativeOutputStreamGeneration);
            }
            return applied;
        }

        internal static void PrepareNativeDualSenseOutputReportForProfileInto(
            byte[] feedback, int deviceIndex, byte[] destination)
        {
            if (feedback == null || feedback.Length <
                    DualSenseNativeOutputReportOffset +
                        DualSenseNativeOutputReportLength)
            {
                throw new ArgumentException(
                    "The feedback buffer does not contain a native DualSense output report.",
                    nameof(feedback));
            }
            if (destination == null || destination.Length <
                    DualSenseNativeOutputReportLength)
            {
                throw new ArgumentException(
                    "The destination is too small for a native DualSense output report.",
                    nameof(destination));
            }

            Buffer.BlockCopy(feedback, DualSenseNativeOutputReportOffset,
                destination, 0, DualSenseNativeOutputReportLength);

            ApplyTriggerLabNativeOverrides(destination, 1, 11, 22,
                TriggerLabForDevice(deviceIndex), feedback[1], feedback[0]);
        }

        internal static void CopyPreparedNativeDualSenseStateIntoCombinedCarrier(
            byte[] nativeOutput, byte[] combinedCarrier, int carrierOffset)
        {
            if (nativeOutput == null || nativeOutput.Length <
                    DualSenseNativeOutputReportLength)
            {
                throw new ArgumentException(
                    "The native output buffer is too small.",
                    nameof(nativeOutput));
            }
            if (combinedCarrier == null || carrierOffset < 0 ||
                carrierOffset + DualSenseNativeOutputReportLength - 1 >
                    combinedCarrier.Length)
            {
                throw new ArgumentException(
                    "The combined carrier destination is too small.",
                    nameof(combinedCarrier));
            }

            Buffer.BlockCopy(nativeOutput, 1, combinedCarrier, carrierOffset,
                DualSenseNativeOutputReportLength - 1);
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

            return dualSenseDevice.WriteBluetoothHapticsSamples(feedback,
                DualSenseBluetoothHapticsReportOffset + 13, 64);
        }

        private bool TryApplyBluetoothCombinedHapticsOutputReport(
            DS4Device device, int deviceIndex, byte[] feedback,
            int feedbackLength, bool freshNativeOutput,
            byte[] nativeOutputScratch,
            long nativeOutputStreamGeneration)
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
            bool hasNativeGameState =
                freshNativeOutput &&
                nativeOutputScratch != null && nativeOutputScratch.Length >=
                    DualSenseNativeOutputReportLength &&
                feedback.Length >= DualSenseNativeOutputReportOffset +
                    DualSenseNativeOutputReportLength &&
                feedback[DualSenseNativeOutputReportOffset] == 0x02;
            if (hasNativeGameState)
            {
                // VIIPER's combined carrier contains the persistent media
                // snapshot. This callback represents one exact game-authored
                // SET_REPORT, so replace its common-state section before the
                // atomic compositor applies local overrides.
                PrepareNativeDualSenseOutputReportForProfileInto(feedback,
                    deviceIndex, nativeOutputScratch);
                CopyPreparedNativeDualSenseStateIntoCombinedCarrier(
                    nativeOutputScratch, report, reportOffset + 13);
            }

            Program.rootHub?.ApplyAudioHapticsToGameReport(deviceIndex,
                report, reportOffset + 78, 64);
            if (!audioOnlySidecar && !hasNativeGameState)
            {
                int stateOffset = reportOffset + 13;
                ApplyTriggerLabNativeOverrides(report, stateOffset,
                    stateOffset + 10, stateOffset + 21,
                    TriggerLabForDevice(deviceIndex), feedback[1],
                    feedback[0]);
            }

            bool publishedImmediately = dualSenseDevice.
                WriteBluetoothCombinedHapticsAudioOutputReport(report,
                DualSenseCombinedBluetoothReportOffset,
                DualSenseCombinedBluetoothReportLength,
                hasNativeGameState,
                out long nativeOutputRevision);
            bool nativeStateAdmitted = hasNativeGameState &&
                nativeOutputRevision > 0;
            if (nativeStateAdmitted)
            {
                TraceNativeGameOutput(feedback,
                    DualSenseNativeOutputReportOffset,
                    nativeOutputRevision, dualSenseDevice,
                    nativeOutputStreamGeneration);
            }

            // Once the combined compositor assigns a revision, its recovery
            // cache owns this exact native state even if the current pacer
            // cannot publish it immediately. Falling through to the raw FIFO
            // would admit the same SET_REPORT twice and could let that stale
            // fallback follow a newer combined state. Only failures before
            // revision assignment remain eligible for raw fallback.
            return publishedImmediately || nativeStateAdmitted;
        }

        private void TraceNativeGameOutput(byte[] feedback, int offset,
            long nativeOutputRevision, DualSenseDevice targetDevice,
            long nativeOutputStreamGeneration)
        {
            if (feedback == null || offset < 0 ||
                offset + DualSenseNativeOutputReportLength > feedback.Length ||
                nativeOutputRevision <= 0 || targetDevice == null ||
                nativeOutputStreamGeneration <= 0)
            {
                return;
            }

            bool meaningfulOutput = HasMeaningfulNativeGameOutput(feedback,
                offset);
            bool sdlAutomaticLedInitialization =
                IsExactSdlDualSenseAutomaticLedInitialization(feedback,
                    offset);
            int visualOwnershipUpdate =
                GetNativeReportVisualOwnershipUpdate(feedback, offset);
            bool controlsVisuals = visualOwnershipUpdate > 0;
            Process observedOwnerForVisualClaim = null;
            int observedOwnerProcessId = 0;
            uint observedForegroundProcessId = 0;
            if (controlsVisuals)
            {
                lock (nativeGameOutputTraceLock)
                {
                    observedOwnerForVisualClaim =
                        nativeGameOutputOwnerProcess;
                    observedOwnerProcessId = nativeGameOutputOwnerProcessId;
                }
                observedForegroundProcessId =
                    GetForegroundProcessId();
            }

            // PID is not present on the HID wire. For a newer visual claim,
            // use the retained foreground lifetime only as a conservative
            // association: only the same foreground PID is affirmative
            // evidence. Game Bar and shell exclusions are negative capture
            // filters, never proof that the retained game authored a report.
            // Any other claim fences the old process from restoring over a
            // potentially newer writer.
            bool retainedOwnerVisualClaimVerified = controlsVisuals &&
                observedOwnerForVisualClaim != null &&
                IsForegroundCompatibleWithRetainedOwner(
                    observedOwnerForVisualClaim,
                    observedOwnerProcessId,
                    observedForegroundProcessId);
            DualSenseDevice foregroundCaptureTargetDevice = null;
            long foregroundCaptureRevision = 0;
            long foregroundCaptureStreamGeneration = 0;
            bool foregroundCaptureRequiresSameLiveOwner = false;
            bool foregroundCaptureVisualClaimVerified = false;
            uint foregroundCaptureVisualProcessId = 0;
            bool sessionStarted = false;
            long traceStreamGeneration = nativeOutputStreamGeneration;
            lock (nativeGameOutputTraceLock)
            {
                // VIIPER publishes one neutral 0x02 snapshot while the
                // virtual pad is being armed. It is transport initialization,
                // not a game claiming feedback ownership. Treating it as a
                // claim binds the lease to whichever unrelated window happens
                // to be foreground while a game starts.
                if (!meaningfulOutput &&
                    nativeGameOutputSessionActive == 0 &&
                    nativeGameOutputOwnerProcess == null)
                {
                    // A later neutral report still fences an already tracked
                    // exact SDL candidate. Ignore only the virtual pad's
                    // initial neutral arming snapshot; never let a fresh
                    // revision leave an older visual lease eligible.
                    if (sdlAutomaticLedCandidateRevision > 0)
                    {
                        sdlAutomaticLedCandidateRevision = 0;
                        sdlAutomaticLedCandidateStreamGeneration = 0;
                        sdlAutomaticLedCandidateTargetDevice = null;
                        lastNativeGameOutputRevision =
                            nativeOutputRevision;
                        nativeGameOutputRealFeedbackEpoch = 1;
                    }
                    return;
                }

                sessionStarted = nativeGameOutputSessionActive == 0;
                Buffer.BlockCopy(feedback, offset,
                    lastNativeGameOutputReport, 0,
                    DualSenseNativeOutputReportLength);
                nativeGameOutputSessionActive = 1;
                lastNativeGameOutputTimestamp = Stopwatch.GetTimestamp();
                lastNativeGameOutputRevision = nativeOutputRevision;
                lastNativeGameOutputStreamGeneration =
                    traceStreamGeneration;
                lastNativeGameOutputTargetDevice = targetDevice;

                // Make the ambiguity sticky before testing target/stream.
                // A visual claim on B or S+1 must remain a fence if capture
                // fails and a later neutral report rebinds the old PID.
                UpdateForegroundOwnerVisualLeaseState(
                    visualOwnershipUpdate,
                    nativeGameOutputOwnerProcess != null,
                    ReferenceEquals(nativeGameOutputOwnerProcess,
                        observedOwnerForVisualClaim) &&
                    retainedOwnerVisualClaimVerified,
                    ref nativeGameOutputOwnerHasVerifiedVisualClaim,
                    ref nativeGameOutputOwnerHasUnverifiedVisualClaim);

                // The retained foreground process is only a lifecycle
                // heuristic, but its visual lease still has to follow the
                // newest native revision on the exact target and stream it was
                // bound to. Hades II, for example, finishes with a neutral
                // report rather than Sony's explicit LED-release bit.
                if (ShouldAdvanceForegroundOwnerLease(
                        nativeGameOutputOwnerProcess != null,
                        ReferenceEquals(nativeGameOutputOwnerTargetDevice,
                            targetDevice),
                        nativeGameOutputOwnerStreamGeneration,
                        traceStreamGeneration,
                        nativeOutputRevision))
                {
                    nativeGameOutputOwnerRevision = nativeOutputRevision;
                }

                // SDL assigns the first virtual PS5 pad its stock blue
                // player-index LEDs during enumeration. Its public
                // SDL_SetJoystickPlayerIndex path can emit the identical
                // bytes, so this is a deliberately narrow recovery policy,
                // not sender provenance or a zero-false-positive classifier.
                // Track only the exact all-zero player-zero signature. Any
                // other native report establishes a real feedback epoch and
                // permanently fences this visual-only expiry.
                if (sdlAutomaticLedInitialization &&
                    nativeGameOutputRealFeedbackEpoch == 0)
                {
                    sdlAutomaticLedCandidateRevision = nativeOutputRevision;
                    sdlAutomaticLedCandidateStreamGeneration =
                        traceStreamGeneration;
                    sdlAutomaticLedCandidateTargetDevice = targetDevice;
                }
                else
                {
                    sdlAutomaticLedCandidateRevision = 0;
                    sdlAutomaticLedCandidateStreamGeneration = 0;
                    sdlAutomaticLedCandidateTargetDevice = null;
                    nativeGameOutputRealFeedbackEpoch = 1;
                }

                bool captureForegroundOwner =
                    ShouldCaptureForegroundOwnerLease(
                        meaningfulOutput,
                        sdlAutomaticLedInitialization,
                        sessionStarted,
                        nativeGameOutputOwnerProcess != null,
                        ReferenceEquals(
                            nativeGameOutputOwnerTargetDevice,
                            targetDevice),
                        nativeGameOutputOwnerStreamGeneration,
                        traceStreamGeneration);
                if (captureForegroundOwner)
                {
                    foregroundCaptureTargetDevice = targetDevice;
                    foregroundCaptureRevision = nativeOutputRevision;
                    foregroundCaptureStreamGeneration =
                        traceStreamGeneration;
                    foregroundCaptureRequiresSameLiveOwner =
                        !meaningfulOutput;
                    foregroundCaptureVisualClaimVerified =
                        controlsVisuals &&
                        ReferenceEquals(nativeGameOutputOwnerProcess,
                        observedOwnerForVisualClaim) &&
                        retainedOwnerVisualClaimVerified;
                    foregroundCaptureVisualProcessId = controlsVisuals
                        ? observedForegroundProcessId
                        : 0;
                }
            }

            if (foregroundCaptureTargetDevice != null)
            {
                CaptureForegroundNativeGameOutputOwner(
                    foregroundCaptureTargetDevice,
                    foregroundCaptureRevision,
                    foregroundCaptureStreamGeneration,
                    foregroundCaptureRequiresSameLiveOwner,
                    foregroundCaptureVisualClaimVerified,
                    foregroundCaptureVisualProcessId);
            }
        }

        internal static bool HasMeaningfulNativeGameOutput(byte[] feedback,
            int offset)
        {
            if (feedback == null || offset < 0 ||
                offset + DualSenseNativeOutputReportLength > feedback.Length ||
                feedback[offset] != 0x02)
            {
                return false;
            }

            for (int i = offset + 1;
                 i < offset + DualSenseNativeOutputReportLength; i++)
            {
                if (feedback[i] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        internal static int GetNativeReportVisualOwnershipUpdate(
            byte[] feedback, int offset)
        {
            const int nativeStateOffset = 1;
            return feedback != null && offset >= 0 &&
                offset + DualSenseNativeOutputReportLength <=
                    feedback.Length &&
                feedback[offset] == 0x02
                ? DualSenseDevice.GetNativeGameLedOwnershipUpdate(feedback,
                    offset + nativeStateOffset)
                : 0;
        }

        internal static bool NativeReportControlsVisuals(byte[] feedback,
            int offset)
        {
            return GetNativeReportVisualOwnershipUpdate(feedback, offset) > 0;
        }

        internal static void UpdateForegroundOwnerVisualLeaseState(
            int visualOwnershipUpdate, bool retainedOwnerPresent,
            bool retainedOwnerVerifiedForReport, ref bool verifiedClaim,
            ref bool unverifiedClaim)
        {
            if (!retainedOwnerPresent || visualOwnershipUpdate == 0)
            {
                return;
            }

            if (visualOwnershipUpdate < 0)
            {
                // Sony's explicit release is authoritative. Once the
                // controller has returned visual ownership to the profile,
                // an older foreground association must not authorize a later
                // heuristic release.
                verifiedClaim = false;
                unverifiedClaim = false;
                return;
            }

            if (retainedOwnerVerifiedForReport)
            {
                verifiedClaim = true;
                unverifiedClaim = false;
            }
            else
            {
                unverifiedClaim = true;
            }
        }

        internal static void RebindForegroundOwnerVisualLeaseState(
            bool targetChanged, int latestVisualOwnershipUpdate,
            bool latestVisualClaimVerified,
            ref bool verifiedClaim, ref bool unverifiedClaim)
        {
            if (targetChanged)
            {
                // Visual proof is scoped to one physical controller. A
                // neutral A-to-B rebind may carry lifecycle and ambiguity,
                // but never A's positive authority.
                verifiedClaim = false;
            }

            UpdateForegroundOwnerVisualLeaseState(
                latestVisualOwnershipUpdate,
                retainedOwnerPresent: true,
                retainedOwnerVerifiedForReport:
                    latestVisualClaimVerified,
                ref verifiedClaim, ref unverifiedClaim);
        }

        internal static bool UpdateForegroundOwnerVisualClaimFence(
            bool previousUnverifiedClaim, bool reportControlsVisuals,
            bool retainedOwnerPresent, bool retainedOwnerVerifiedForReport)
        {
            bool verifiedClaim = false;
            bool unverifiedClaim = previousUnverifiedClaim;
            UpdateForegroundOwnerVisualLeaseState(
                reportControlsVisuals ? 1 : 0, retainedOwnerPresent,
                retainedOwnerVerifiedForReport, ref verifiedClaim,
                ref unverifiedClaim);
            return unverifiedClaim;
        }

        internal static bool
            IsExactSdlDualSenseAutomaticLedInitialization(byte[] feedback,
                int offset)
        {
            if (feedback == null || offset < 0 ||
                offset + SdlDualSenseAutomaticPlayerZeroLedReport.Length >
                    feedback.Length)
            {
                return false;
            }

            for (int index = 0;
                 index < SdlDualSenseAutomaticPlayerZeroLedReport.Length;
                 index++)
            {
                if (feedback[offset + index] !=
                    SdlDualSenseAutomaticPlayerZeroLedReport[index])
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool ShouldExpireSdlAutomaticLedInitialization(
            bool exactCandidate, bool realFeedbackEpoch,
            bool foregroundCandidatePresent,
            bool targetBindingMatches,
            long candidateStreamGeneration, long currentStreamGeneration,
            long candidateRevision, long currentRevision,
            long elapsedTicks, long graceTicks)
        {
            // Windows' HID callback does not expose the writer PID, so the
            // foreground candidate is accepted only to prove it cannot alter
            // this decision. It is useful lifecycle telemetry but never
            // verified provenance. Only the exact SDL bootstrap report can
            // expire, and a stream change or one newer report fences it.
            _ = foregroundCandidatePresent;
            return exactCandidate && !realFeedbackEpoch &&
                targetBindingMatches &&
                candidateStreamGeneration == currentStreamGeneration &&
                candidateRevision > 0 &&
                candidateRevision == currentRevision &&
                graceTicks > 0 && elapsedTicks >= graceTicks;
        }

        internal static bool ShouldAdvanceForegroundOwnerLease(
            bool retainedOwnerPresent, bool ownerTargetMatchesReport,
            long ownerStreamGeneration, long reportStreamGeneration,
            long nativeOutputRevision)
        {
            return retainedOwnerPresent && ownerTargetMatchesReport &&
                ownerStreamGeneration == reportStreamGeneration &&
                nativeOutputRevision > 0;
        }

        internal static bool ShouldCaptureForegroundOwnerLease(
            bool meaningfulOutput, bool automaticLedInitialization,
            bool sessionStarted, bool retainedOwnerPresent,
            bool ownerTargetMatchesReport, long ownerStreamGeneration,
            long reportStreamGeneration)
        {
            bool retainedBindingChanged = retainedOwnerPresent &&
                (!ownerTargetMatchesReport ||
                 ownerStreamGeneration != reportStreamGeneration);
            return !automaticLedInitialization &&
                reportStreamGeneration > 0 &&
                (meaningfulOutput &&
                    (sessionStarted || !retainedOwnerPresent ||
                     retainedBindingChanged) ||
                 !meaningfulOutput && retainedBindingChanged);
        }

        internal static bool ShouldInstallForegroundOwnerLease(
            bool retainedOwnerUnchanged, bool targetMatchesLatest,
            bool targetBindingMatches,
            long expectedStreamGeneration, long latestReportStreamGeneration,
            long currentStreamGeneration,
            long expectedRevision, long latestRevision)
        {
            return retainedOwnerUnchanged && targetMatchesLatest &&
                targetBindingMatches && expectedStreamGeneration > 0 &&
                expectedStreamGeneration == latestReportStreamGeneration &&
                expectedStreamGeneration == currentStreamGeneration &&
                expectedRevision > 0 && expectedRevision == latestRevision;
        }

        internal static bool ShouldReleaseForegroundOwnerLedOwnership(
            NativeGameOwnerProcessLiveness ownerProcessLiveness,
            bool retainedOwnerStillCurrent,
            bool ownerTargetMatchesLatest, bool targetBindingMatches,
            bool latestReportControlsVisuals,
            bool verifiedVisualClaim,
            bool unverifiedVisualClaim,
            long ownerStreamGeneration, long latestReportStreamGeneration,
            long currentStreamGeneration,
            long ownerRevision, long currentRevision)
        {
            // Foreground-window association is not writer attribution. Its
            // only permitted consequence is therefore a revision-fenced
            // lightbar/player restore. Every identity and ordering fence must
            // still name the exact retained process lease, physical target,
            // virtual stream, and newest admitted native report.
            return ownerProcessLiveness ==
                    NativeGameOwnerProcessLiveness.ConfirmedExited &&
                retainedOwnerStillCurrent &&
                ownerTargetMatchesLatest && targetBindingMatches &&
                !latestReportControlsVisuals && verifiedVisualClaim &&
                !unverifiedVisualClaim &&
                ownerStreamGeneration == latestReportStreamGeneration &&
                ownerStreamGeneration == currentStreamGeneration &&
                ownerRevision > 0 && ownerRevision == currentRevision;
        }

        private void TraceNativeGameOutputIdleBoundary(
            long controlAdmissionRevision)
        {
            Process ownerProcess;
            bool captureOwnerAtBoundary;
            DualSenseDevice foregroundCaptureTargetDevice = null;
            long foregroundCaptureRevision = 0;
            long foregroundCaptureStreamGeneration = 0;
            bool automaticLedExpiryCandidate = false;
            long automaticLedRevision = 0;
            long automaticLedStreamGeneration = 0;
            long automaticLedElapsedTicks = 0;
            DualSenseDevice automaticLedTargetDevice = null;
            lock (nativeGameOutputTraceLock)
            {
                long now = Stopwatch.GetTimestamp();
                captureOwnerAtBoundary =
                    nativeGameOutputRealFeedbackEpoch != 0 &&
                    nativeGameOutputOwnerProcess == null &&
                    lastNativeGameOutputTimestamp > 0 &&
                    HasMeaningfulNativeGameOutput(
                        lastNativeGameOutputReport, 0);
                if (captureOwnerAtBoundary)
                {
                    foregroundCaptureTargetDevice =
                        lastNativeGameOutputTargetDevice;
                    foregroundCaptureRevision =
                        lastNativeGameOutputRevision;
                    foregroundCaptureStreamGeneration =
                        lastNativeGameOutputStreamGeneration;
                }
                automaticLedElapsedTicks =
                    lastNativeGameOutputTimestamp > 0 ?
                        now - lastNativeGameOutputTimestamp : 0;
                automaticLedRevision =
                    sdlAutomaticLedCandidateRevision;
                automaticLedStreamGeneration =
                    sdlAutomaticLedCandidateStreamGeneration;
                automaticLedTargetDevice =
                    sdlAutomaticLedCandidateTargetDevice;
                automaticLedExpiryCandidate =
                    ShouldExpireSdlAutomaticLedInitialization(
                        automaticLedRevision > 0 &&
                            IsExactSdlDualSenseAutomaticLedInitialization(
                                lastNativeGameOutputReport, 0),
                        nativeGameOutputRealFeedbackEpoch != 0,
                        nativeGameOutputOwnerProcess != null,
                        IsCurrentNativeOutputTarget(
                            automaticLedTargetDevice),
                        automaticLedStreamGeneration,
                        Volatile.Read(ref streamGeneration),
                        automaticLedRevision,
                        lastNativeGameOutputRevision,
                        automaticLedElapsedTicks,
                        Stopwatch.Frequency);
                if (nativeGameOutputSessionActive != 0 &&
                    lastNativeGameOutputTimestamp > 0 &&
                    automaticLedElapsedTicks >= Stopwatch.Frequency)
                {
                    nativeGameOutputSessionActive = 0;
                    lastNativeGameOutputTimestamp = 0;
                    // A quiet HID interval is not an ownership release. Games
                    // commonly latch their LED and trigger state, then stop
                    // writing until it changes.
                }

                ownerProcess = nativeGameOutputOwnerProcess;
            }

            bool automaticLedReleaseQueued = false;
            if (automaticLedExpiryCandidate &&
                feedbackDispatchBuffer.TryObserveControlIdle(
                    controlAdmissionRevision))
            {
                // This short buffer observation is the admission
                // linearization point; it executes no callback. A control
                // report admitted afterward is logically later, and the sole
                // dispatcher plus the device revision fence ensures that it
                // follows or cancels this visual-only request.
                bool traceFenceCurrent = false;
                lock (nativeGameOutputTraceLock)
                {
                    traceFenceCurrent =
                        ShouldExpireSdlAutomaticLedInitialization(
                            sdlAutomaticLedCandidateRevision > 0 &&
                                IsExactSdlDualSenseAutomaticLedInitialization(
                                    lastNativeGameOutputReport, 0),
                            nativeGameOutputRealFeedbackEpoch != 0,
                            nativeGameOutputOwnerProcess != null,
                            IsCurrentNativeOutputTarget(
                                sdlAutomaticLedCandidateTargetDevice),
                            sdlAutomaticLedCandidateStreamGeneration,
                            Volatile.Read(ref streamGeneration),
                            sdlAutomaticLedCandidateRevision,
                            lastNativeGameOutputRevision,
                            automaticLedElapsedTicks,
                            Stopwatch.Frequency) &&
                        sdlAutomaticLedCandidateRevision ==
                            automaticLedRevision &&
                        sdlAutomaticLedCandidateStreamGeneration ==
                            automaticLedStreamGeneration &&
                        ReferenceEquals(
                            sdlAutomaticLedCandidateTargetDevice,
                            automaticLedTargetDevice);
                }

                if (traceFenceCurrent)
                {
                    automaticLedReleaseQueued =
                        RequestNativeDualSenseLedOwnershipRelease(
                            automaticLedTargetDevice,
                            automaticLedRevision,
                            automaticLedStreamGeneration);
                    if (automaticLedReleaseQueued)
                    {
                        lock (nativeGameOutputTraceLock)
                        {
                            if (sdlAutomaticLedCandidateRevision ==
                                    automaticLedRevision &&
                                sdlAutomaticLedCandidateStreamGeneration ==
                                    automaticLedStreamGeneration &&
                                ReferenceEquals(
                                    sdlAutomaticLedCandidateTargetDevice,
                                    automaticLedTargetDevice))
                            {
                                sdlAutomaticLedCandidateRevision = 0;
                                sdlAutomaticLedCandidateStreamGeneration = 0;
                                sdlAutomaticLedCandidateTargetDevice = null;
                            }
                        }
                    }
                }
            }

            if (automaticLedReleaseQueued)
            {
                return;
            }

            // A freshly launched game can publish its first SET_REPORT before
            // Windows has foregrounded its window. Retry once at the idle
            // boundary, when the game's top-level window is established, so a
            // one-shot LED/trigger claim still receives a process lifetime.
            if (captureOwnerAtBoundary && ownerProcess == null)
            {
                CaptureForegroundNativeGameOutputOwner(
                    foregroundCaptureTargetDevice,
                    foregroundCaptureRevision,
                    foregroundCaptureStreamGeneration);
                lock (nativeGameOutputTraceLock)
                {
                    ownerProcess = nativeGameOutputOwnerProcess;
                }
            }

            NativeGameOwnerProcessLiveness ownerProcessLiveness =
                GetNativeGameOwnerProcessLiveness(ownerProcess);
            if (ownerProcess == null || ownerProcessLiveness !=
                    NativeGameOwnerProcessLiveness.ConfirmedExited)
            {
                return;
            }

            // The control-admission revision is the linearization fence for
            // the exit heuristic. A report admitted before this observation
            // cancels the stale restore; a report admitted afterward is
            // ordered later and either follows it or defeats the device-side
            // native revision check.
            if (!feedbackDispatchBuffer.TryObserveControlIdle(
                    controlAdmissionRevision))
            {
                return;
            }

            // Keep the exact dead lease only if callback admission is
            // temporarily blocked. A later idle pass retries it. Successful
            // visual admission retires the Process handle; a permanent
            // target/stream/revision/visual fence retires the dead heuristic
            // without touching controller state.
            TryReleaseExitedForegroundOwnerLedOwnership(ownerProcess,
                ownerProcessLiveness);
        }

        private void CaptureForegroundNativeGameOutputOwner(
            DualSenseDevice expectedTargetDevice,
            long expectedNativeOutputRevision,
            long expectedStreamGeneration,
            bool requireSameLiveOwner = false,
            bool reportVisualClaimVerified = false,
            uint reportVisualForegroundProcessId = 0)
        {
            if (expectedTargetDevice == null ||
                expectedNativeOutputRevision <= 0 ||
                expectedStreamGeneration <= 0)
            {
                return;
            }

            Process candidate = TryOpenForegroundGameProcess();
            if (candidate == null)
            {
                return;
            }

            int candidateId;
            try
            {
                candidateId = candidate.Id;
            }
            catch
            {
                candidate.Dispose();
                return;
            }

            bool candidateMatchesReportVisualProcess =
                ForegroundCandidateMatchesObservedVisualProcess(
                    reportVisualForegroundProcessId, candidateId);
            if (reportVisualForegroundProcessId > 0 &&
                !candidateMatchesReportVisualProcess)
            {
                // Foreground changed after the report was observed. Never
                // retroactively assign that visual claim to the later window.
                candidate.Dispose();
                return;
            }

            Process observedOwner;
            lock (nativeGameOutputTraceLock)
            {
                observedOwner = nativeGameOutputOwnerProcess;
            }

            bool sameLiveOwner = false;
            if (observedOwner != null)
            {
                NativeGameOwnerProcessLiveness observedOwnerLiveness =
                    GetNativeGameOwnerProcessLiveness(observedOwner);
                if (observedOwnerLiveness ==
                    NativeGameOwnerProcessLiveness.Unknown)
                {
                    // A transient Process query failure is not evidence that
                    // the retained lifecycle candidate ended. Keep its lease
                    // and retry on a later idle boundary.
                    candidate.Dispose();
                    return;
                }

                if (observedOwnerLiveness ==
                    NativeGameOwnerProcessLiveness.Running)
                {
                    try
                    {
                        sameLiveOwner = observedOwner.Id == candidateId;
                    }
                    catch
                    {
                        candidate.Dispose();
                        return;
                    }
                }
            }

            // A neutral report carries no writer identity at all. It may move
            // an already retained game's lease across an internal stream or
            // physical-target replacement only when Windows still identifies
            // the exact same live foreground PID. Never let an unrelated
            // foreground process inherit a neutral transport report.
            if (!ShouldAcceptForegroundOwnerCandidate(
                    requireSameLiveOwner, sameLiveOwner))
            {
                candidate.Dispose();
                return;
            }

            bool targetBindingMatches =
                IsCurrentNativeOutputTarget(expectedTargetDevice);
            bool installed = false;
            lock (nativeGameOutputTraceLock)
            {
                bool leaseCommitIsCurrent =
                    ShouldInstallForegroundOwnerLease(
                        retainedOwnerUnchanged: ReferenceEquals(
                            nativeGameOutputOwnerProcess, observedOwner),
                        targetMatchesLatest: ReferenceEquals(
                            lastNativeGameOutputTargetDevice,
                            expectedTargetDevice),
                        targetBindingMatches: targetBindingMatches,
                        expectedStreamGeneration:
                            expectedStreamGeneration,
                        latestReportStreamGeneration:
                            lastNativeGameOutputStreamGeneration,
                        currentStreamGeneration:
                            Volatile.Read(ref streamGeneration),
                        expectedRevision: expectedNativeOutputRevision,
                        latestRevision: lastNativeGameOutputRevision);
                if (leaseCommitIsCurrent)
                {
                    // A stream recovery or physical-controller rebind keeps
                    // the same game process alive. Retain that exact Process
                    // object, but move its visual lease to the freshly
                    // validated report target/stream/revision. The redundant
                    // foreground Process handle is disposed below.
                    bool targetChanged =
                        nativeGameOutputOwnerProcess != null &&
                        !ReferenceEquals(nativeGameOutputOwnerTargetDevice,
                            expectedTargetDevice);
                    int latestVisualOwnershipUpdate =
                        GetNativeReportVisualOwnershipUpdate(
                            lastNativeGameOutputReport, 0);
                    if (!sameLiveOwner)
                    {
                        nativeGameOutputOwnerProcess = candidate;
                        nativeGameOutputOwnerProcessId = candidateId;
                        nativeGameOutputOwnerHasVerifiedVisualClaim = false;
                        nativeGameOutputOwnerHasUnverifiedVisualClaim = false;
                        UpdateForegroundOwnerVisualLeaseState(
                            latestVisualOwnershipUpdate,
                            retainedOwnerPresent: true,
                            retainedOwnerVerifiedForReport:
                                candidateMatchesReportVisualProcess,
                            ref nativeGameOutputOwnerHasVerifiedVisualClaim,
                            ref nativeGameOutputOwnerHasUnverifiedVisualClaim);
                        installed = true;
                    }
                    else
                    {
                        RebindForegroundOwnerVisualLeaseState(
                            targetChanged,
                            latestVisualOwnershipUpdate,
                            reportVisualClaimVerified &&
                                candidateMatchesReportVisualProcess,
                            ref nativeGameOutputOwnerHasVerifiedVisualClaim,
                            ref nativeGameOutputOwnerHasUnverifiedVisualClaim);
                    }
                    nativeGameOutputOwnerTargetDevice = expectedTargetDevice;
                    nativeGameOutputOwnerRevision =
                        expectedNativeOutputRevision;
                    nativeGameOutputOwnerStreamGeneration =
                        expectedStreamGeneration;
                }
            }

            if (sameLiveOwner)
            {
                candidate.Dispose();
                return;
            }

            if (!installed)
            {
                candidate.Dispose();
                return;
            }

            observedOwner?.Dispose();
        }

        private Process DetachNativeGameOutputOwnerNoLock(
            Process expectedOwnerProcess)
        {
            if (!ReferenceEquals(nativeGameOutputOwnerProcess,
                    expectedOwnerProcess))
            {
                return null;
            }

            Process owner = nativeGameOutputOwnerProcess;
            nativeGameOutputOwnerProcess = null;
            nativeGameOutputOwnerProcessId = 0;
            nativeGameOutputOwnerRevision = 0;
            nativeGameOutputOwnerStreamGeneration = 0;
            nativeGameOutputOwnerTargetDevice = null;
            nativeGameOutputOwnerHasVerifiedVisualClaim = false;
            nativeGameOutputOwnerHasUnverifiedVisualClaim = false;
            return owner;
        }

        private void ClearNativeGameOutputProcessLease()
        {
            Process owner;
            lock (nativeGameOutputTraceLock)
            {
                owner = DetachNativeGameOutputOwnerNoLock(
                    nativeGameOutputOwnerProcess);
                nativeGameOutputSessionActive = 0;
                lastNativeGameOutputTimestamp = 0;
                lastNativeGameOutputRevision = 0;
                lastNativeGameOutputStreamGeneration = 0;
                lastNativeGameOutputTargetDevice = null;
                sdlAutomaticLedCandidateRevision = 0;
                sdlAutomaticLedCandidateStreamGeneration = 0;
                sdlAutomaticLedCandidateTargetDevice = null;
                nativeGameOutputRealFeedbackEpoch = 0;
            }

            owner?.Dispose();
        }

        private static bool IsForegroundCompatibleWithRetainedOwner(
            Process retainedOwnerProcess, int retainedOwnerProcessId,
            uint foregroundProcessId)
        {
            if (retainedOwnerProcess == null ||
                retainedOwnerProcessId <= 0 ||
                foregroundProcessId != (uint)retainedOwnerProcessId)
            {
                return false;
            }

            // Check the retained Process object last. A numeric PID can be
            // reused only after the old process exits, so a reused foreground
            // PID must not verify a dead lifecycle lease.
            return ForegroundProcessMatchesRetainedOwner(
                retainedOwnerProcessId, foregroundProcessId,
                GetNativeGameOwnerProcessLiveness(retainedOwnerProcess));
        }

        private static uint GetForegroundProcessId()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return 0;
            }

            GetWindowThreadProcessId(window, out uint rawProcessId);
            return rawProcessId;
        }

        internal static bool ForegroundProcessMatchesRetainedOwner(
            int retainedOwnerProcessId, uint foregroundProcessId,
            NativeGameOwnerProcessLiveness retainedOwnerLiveness)
        {
            return retainedOwnerProcessId > 0 &&
                foregroundProcessId == (uint)retainedOwnerProcessId &&
                retainedOwnerLiveness ==
                    NativeGameOwnerProcessLiveness.Running;
        }

        internal static bool ForegroundCandidateMatchesObservedVisualProcess(
            uint observedForegroundProcessId, int candidateProcessId)
        {
            return observedForegroundProcessId > 0 &&
                observedForegroundProcessId <= int.MaxValue &&
                candidateProcessId > 0 &&
                candidateProcessId == (int)observedForegroundProcessId;
        }

        internal static bool ShouldAcceptForegroundOwnerCandidate(
            bool requireSameLiveOwner, bool sameLiveOwner)
        {
            return !requireSameLiveOwner || sameLiveOwner;
        }

        private static Process TryOpenForegroundGameProcess()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }

            GetWindowThreadProcessId(window, out uint rawProcessId);
            if (rawProcessId == 0 || rawProcessId == Environment.ProcessId ||
                rawProcessId > int.MaxValue)
            {
                return null;
            }

            Process process = null;
            try
            {
                process = Process.GetProcessById((int)rawProcessId);
                string name = process.ProcessName;
                if (process.HasExited || IsExcludedNativeGameOwner(name))
                {
                    process.Dispose();
                    return null;
                }

                return process;
            }
            catch
            {
                process?.Dispose();
                return null;
            }
        }

        internal static NativeGameOwnerProcessLiveness
            ClassifyNativeGameOwnerProcessLiveness(
                bool queryCompleted, bool hasExited)
        {
            if (!queryCompleted)
            {
                return NativeGameOwnerProcessLiveness.Unknown;
            }

            return hasExited ?
                NativeGameOwnerProcessLiveness.ConfirmedExited :
                NativeGameOwnerProcessLiveness.Running;
        }

        private static NativeGameOwnerProcessLiveness
            GetNativeGameOwnerProcessLiveness(Process process)
        {
            if (process == null)
            {
                return NativeGameOwnerProcessLiveness.Unknown;
            }

            try
            {
                return ClassifyNativeGameOwnerProcessLiveness(
                    queryCompleted: true, hasExited: process.HasExited);
            }
            catch
            {
                return ClassifyNativeGameOwnerProcessLiveness(
                    queryCompleted: false, hasExited: false);
            }
        }

        internal static bool IsExcludedNativeGameOwner(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return true;
            }

            return GameBarIntegration.IsStrictGameBarProcessName(
                    processName) ||
                processName.Equals("GameBarPresenceWriter",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("SpotifyXboxGamebarWebView",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("explorer",
                       StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("dwm",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ApplicationFrameHost",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ShellExperienceHost",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("SearchHost",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("StartMenuExperienceHost",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("steam",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ChatGPT",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("Codex",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("WindowsTerminal",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("powershell",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("pwsh",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("cmd",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("viiper",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("DS4Windows",
                    StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window,
            out uint processId);

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

        private static bool ShouldApplyGameLightbar(int deviceIndex)
        {
            return deviceIndex >= 0 &&
                deviceIndex < Global.TEST_PROFILE_ITEM_COUNT &&
                Global.getLightbarSettingsInfo(deviceIndex).mode ==
                    LightbarMode.Passthru;
        }

        private void StopXboxOneRejectedInput(ViiperDeviceStream failedStream,
            long failedStreamGeneration, long failedWriterGeneration,
            string message)
        {
            lock (orderedEgressLifecycleLock)
            {
                if (viiperType != ViiperVirtualDeviceType.XboxOne ||
                    failedStream == null || !failedStream.IsXboxOneInputRejected ||
                    !IsStateWriterCurrent(failedWriterGeneration) ||
                    failedWriterGeneration != Interlocked.Read(
                        ref stateWriterThreadGeneration) ||
                    !ReferenceEquals(Volatile.Read(ref stateWriterThread),
                        Thread.CurrentThread) ||
                    failedStreamGeneration != Interlocked.Read(
                        ref streamGeneration) ||
                    !ReferenceEquals(Volatile.Read(ref deviceStream), failedStream))
                {
                    return;
                }

                // Elect the exact writer's single failure owner and stop new
                // producer/final-write admission before any blocking teardown.
                // Do not clear connected: the existing Disconnect path needs
                // its canonical reader and callback admission until Stop ACK.
                orderedEgressWriterAdmissionGate.Invalidate();
                writerStopRequested = true;
            }

            // This is the authenticated state writer itself, not a queued
            // cleanup callback. Connect first calls Disconnect, whose Xbox
            // path joins this exact writer before it can retire/replace the
            // stream. A concurrent lifecycle owner therefore cannot install a
            // successor between the election above and this self-teardown.
            Disconnect();
            LogSubmitFailureOnce(message);
        }

        private void LogSubmitFailure(string message)
        {
            connected = false;
            Disconnect();
            LogSubmitFailureOnce(message);
        }

        private void LogSubmitFailureOnce(string message)
        {
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
        private const int RuntimeStatusReceiveTimeoutMs = 1000;
        private const int StreamReceiveTimeoutMs = 0;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly string host;
        private readonly int port;
        private readonly Func<Stream, Stream> authenticate;

        public ViiperClient(string host, int port)
            : this(host, port, ViiperAuthentication.Authenticate)
        {
        }

        internal ViiperClient(string host, int port,
            Func<Stream, Stream> authenticate)
        {
            this.host = host;
            this.port = port;
            this.authenticate = authenticate ??
                throw new ArgumentNullException(nameof(authenticate));
        }

        public ViiperDeviceStream CreateDeviceAndOpenStream(ViiperVirtualDeviceType deviceType)
        {
            return CreateDeviceAndOpenStream(ViiperStatePacketBuilder.GetViiperDeviceName(deviceType));
        }

        public ViiperDeviceStream CreateDeviceAndOpenStream(string deviceName,
            ushort? idProduct = null, object deviceSpecific = null)
        {
            string payload = SerializeDeviceCreateRequest(deviceName,
                idProduct, deviceSpecific);
            return CreateDeviceAndOpenStream(busId =>
                SendRequest<ViiperDeviceResponse>($"bus/{busId}/add",
                    payload));
        }

        internal ViiperDeviceStream CreateAuthorizedXboxOneDeviceAndOpenStream(
            XboxOneAuthorizedCreateRequestV1 request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Identity);
            string payload = SerializeAuthorizedXboxOneCreateRequest(request);
            ViiperVirtualDeviceLifetime lifetime = null;
            ViiperDeviceStream stream = null;
            try
            {
                uint busId = XboxOneAuthorizedRegistrationV1.ParseBusCreateResponse(
                    SendBoundedXboxOneManagementRequest(XboxOneManagementOperation.CreateBus,
                        "bus/create", "0", ApiReceiveTimeoutMs));
                JsonElement device = SendBoundedXboxOneManagementRequest(
                    XboxOneManagementOperation.CreatePersona,
                    $"bus/{busId}/add-authorized-xboxone", payload, ApiReceiveTimeoutMs);
                XboxOneAuthorizedRegistrationV1 registration =
                    XboxOneAuthorizedRegistrationV1.ParseCreateResponse(device,
                        busId, request.Identity.VendorId, request.Identity.ProductId);
                lifetime = new ViiperVirtualDeviceLifetime(registration,
                    value => RemoveAuthorizedXboxOneRegistration(value,
                        value.RemovalResponseTimeoutMilliseconds));
                stream = OpenStream(busId, registration.DevId, -1, lifetime);
                return stream;
            }
            catch (Exception error)
            {
                if (stream != null)
                {
                    stream.Dispose();
                }
                else
                {
                    // No numeric fallback, even if creation failed or its
                    // receipt was lost. Server exact orphan cleanup owns that
                    // case; a reused bus/device address is not authority.
                    lifetime?.Dispose();
                }
                if (error is XboxOneManagementException) throw;
                if (error is IOException || error is JsonException ||
                    error is ObjectDisposedException)
                    throw new IOException("VIIPER did not complete exact Xbox One startup.");
                throw;
            }
        }

        internal void ActivateAuthorizedXboxOneDevice(
            ViiperDeviceStream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            XboxOneAuthorizedRegistrationV1 registration =
                stream.DeviceLifetime.XboxOneRegistration;
            if (!stream.IsXboxOneBrokerEnabled || stream.UsbipPort >= 0 ||
                registration == null || stream.DeviceLifetime.IsDisposed)
            {
                throw new InvalidOperationException(
                    "The VIIPER Xbox One broker is not awaiting activation.");
            }
            using XboxOneActivationRequest request =
                stream.DeviceLifetime.BeginXboxOneActivationRequest();
            try
            {
                ViiperUsbipPortManager.WithNativePortMutationLock(() =>
                {
                    JsonElement activation;
                    try
                    {
                        activation = SendBoundedXboxOneManagementRequest(
                            XboxOneManagementOperation.ActivatePersona,
                            registration.ActivationPath, registration.SerializeRemovalRequest(),
                            registration.RemovalResponseTimeoutMilliseconds, request.Token);
                    }
                    catch (Exception error) when (error is not XboxOneManagementException &&
                        (error is IOException ||
                        error is JsonException || error is ObjectDisposedException ||
                        error is OperationCanceledException))
                    {
                        throw new IOException("VIIPER did not acknowledge exact Xbox One activation.");
                    }
                    int port = registration.ParseActivationResponse(activation);
                    ViiperXboxOnePortLease lease =
                        ViiperUsbipPortManager.RegisterXboxOnePort(port, registration.UsbipBusId);
                    try
                    {
                        stream.DeviceLifetime.BindXboxOnePort(lease);
                    }
                    catch
                    {
                        lease.Dispose();
                        // Exact server removal owns rollback, never a bare port.
                        stream.DeviceLifetime.Dispose();
                        throw;
                    }
                    return true;
                }, request.Token);
            }
            catch (OperationCanceledException)
            {
                throw new IOException("VIIPER Xbox One activation was canceled before native attach admission.");
            }
        }

        internal static string SerializeAuthorizedXboxOneCreateRequest(
            XboxOneAuthorizedCreateRequestV1 request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return JsonSerializer.Serialize(request, JsonOptions);
        }

        internal static string SerializeNS2ProRuntimeStatusV1(
            ViiperSwitch2RuntimeStatusV1 status)
        {
            if (!status.IsValid)
            {
                throw new ArgumentException(
                    "A complete VIIPER Switch 2 runtime status v1 snapshot is required.",
                    nameof(status));
            }
            return JsonSerializer.Serialize(status, JsonOptions);
        }

        internal void UpdateNS2ProRuntimeStatusV1(uint busId, string devId,
            ViiperSwitch2RuntimeStatusV1 status)
        {
            if (string.IsNullOrWhiteSpace(devId))
            {
                throw new ArgumentException(
                    "A VIIPER device ID is required.", nameof(devId));
            }
            ViiperRuntimeStatusUpdateResponse response =
                SendRequest<ViiperRuntimeStatusUpdateResponse>(
                    $"bus/{busId}/{devId}/ns2pro-status-v1",
                    SerializeNS2ProRuntimeStatusV1(status),
                    RuntimeStatusReceiveTimeoutMs);
            if (response == null || response.Version !=
                    ViiperSwitch2RuntimeStatusV1.ContractVersion ||
                !response.Updated)
            {
                throw new IOException(
                    "VIIPER did not acknowledge Switch 2 runtime status v1.");
            }
        }

        private ViiperDeviceStream CreateDeviceAndOpenStream(
            Func<uint, ViiperDeviceResponse> createDevice)
        {
            return ViiperUsbipPortManager.WithNativePortMutationLock(() =>
                CreateDeviceAndOpenStreamCore(createDevice));
        }

        private ViiperDeviceStream CreateDeviceAndOpenStreamCore(
            Func<uint, ViiperDeviceResponse> createDevice)
        {
            ArgumentNullException.ThrowIfNull(createDevice);
            ViiperUsbipPortManager.DetachStaleLocalViiperPorts();

            ViiperBusCreateResponse bus = SendRequest<ViiperBusCreateResponse>(
                "bus/create", "0");
            ViiperDeviceResponse device = null;
            int usbipPort = -1;
            try
            {
                device = createDevice(bus.BusId);
                usbipPort = device.UsbipPort;
                if (!ViiperUsbipPortManager.IsTrustedCreateResponse(
                    usbipPort, device.UsbipOwnerSerial))
                {
                    throw new IOException(
                        $"VIIPER created {bus.BusId}-{device.DevId}, but its native usbip-win2 attach response did not contain a positive port and supported ownership metadata.");
                }

                ViiperUsbipPortManager.DetachDuplicateLocalViiperPorts(
                    bus.BusId, device.DevId, usbipPort);
                ViiperUsbipPortManager.RegisterActivePort(usbipPort,
                    $"{bus.BusId}-{device.DevId}");
                return OpenStream(bus.BusId, device.DevId, usbipPort);
            }
            catch
            {
                ViiperUsbipPortManager.UnregisterActivePort(usbipPort);

                if (device != null && !string.IsNullOrEmpty(device.DevId))
                {
                    TryRemoveDevice(bus.BusId, device.DevId);
                }

                TryRemoveBus(bus.BusId);
                throw;
            }
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

        internal static string SerializeDeviceCreateRequest(
            string deviceName, ushort? idProduct = null,
            object deviceSpecific = null)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                throw new ArgumentException("A VIIPER device name is required.",
                    nameof(deviceName));
            }

            return JsonSerializer.Serialize(new ViiperDeviceCreateRequest
            {
                Type = deviceName,
                IdProduct = idProduct,
                DeviceSpecific = deviceSpecific,
            }, JsonOptions);
        }

        internal ViiperMicrophoneInterfaceStatus
            GetNarrowMicrophoneInterfaceStatus(uint busId, string devId)
        {
            JsonElement response = SendRequest<JsonElement>(
                $"bus/{busId}/{devId}/microphone-interface");
            if (response.ValueKind != JsonValueKind.Object ||
                !TryReadBoolean(response, "active", out bool active))
            {
                throw new IOException(
                    "VIIPER returned an invalid narrow microphone-interface response.");
            }

            return new ViiperMicrophoneInterfaceStatus(active,
                ViiperMicrophoneBufferSnapshot.ParseNarrow(response));
        }

        private static bool TryReadBoolean(JsonElement parent, string name,
            out bool result)
        {
            if (!parent.TryGetProperty(name, out JsonElement value))
            {
                result = false;
                return false;
            }
            if (value.ValueKind == JsonValueKind.True ||
                value.ValueKind == JsonValueKind.False)
            {
                result = value.GetBoolean();
                return true;
            }
            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out result))
            {
                return true;
            }
            result = false;
            return false;
        }

        public ViiperDeviceStream OpenExistingDeviceStream(uint busId,
            string devId, int usbipPort)
        {
            return OpenExistingDeviceStream(busId, devId, usbipPort, null);
        }

        internal ViiperDeviceStream OpenExistingDeviceStream(uint busId,
            string devId, int usbipPort,
            ViiperVirtualDeviceLifetime deviceLifetime)
        {
            if (deviceLifetime?.XboxOneRegistration != null)
                throw new InvalidOperationException("Xbox One streams cannot reopen in place.");
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

            return OpenStream(busId, devId, usbipPort, deviceLifetime);
        }

        private ViiperDeviceStream OpenStream(uint busId, string devId,
            int usbipPort,
            ViiperVirtualDeviceLifetime deviceLifetime = null)
        {
            XboxOneAuthorizedRegistrationV1 registration =
                deviceLifetime?.XboxOneRegistration;
            TcpClient tcp = Connect(registration == null ?
                StreamReceiveTimeoutMs : ApiReceiveTimeoutMs);
            Stream stream = null;
            ViiperDeviceStream result = null;
            try
            {
                using CancellationTokenSource startupDeadline = registration == null ?
                    null : new CancellationTokenSource(ApiReceiveTimeoutMs);
                using CancellationTokenRegistration cancellation = startupDeadline == null ?
                    default : startupDeadline.Token.Register(
                        static state => ((TcpClient)state).Dispose(), tcp);
                stream = authenticate(tcp.GetStream());
                string command = registration == null ? $"bus/{busId}/{devId}" :
                    registration.StreamPath + " " + registration.SerializeRemovalRequest();
                byte[] request = Encoding.UTF8.GetBytes(command + "\0");
                stream.Write(request, 0, request.Length);
                deviceLifetime ??= new ViiperVirtualDeviceLifetime(busId,
                    devId, usbipPort, RemoveDevice);
                result = new ViiperDeviceStream(tcp, stream, deviceLifetime);
                if (registration != null)
                {
                    result.EnableXboxOneBroker();
                    cancellation.Dispose();
                    if (startupDeadline.IsCancellationRequested)
                        throw new IOException("Xbox One broker startup timed out.");
                    tcp.ReceiveTimeout = StreamReceiveTimeoutMs;
                }
                return result;
            }
            catch
            {
                tcp.Dispose();
                // Startup has not handed the stream to its feedback reader.
                // Release wrappers/wait handles even if ConsumerReady failed;
                // exact lifetime disposal is joined by the caller's rollback.
                if (result != null)
                    result.Dispose();
                else
                {
                    try { stream?.Dispose(); }
                    catch { }
                }
                throw;
            }
        }

        // Neither response value authorizes detaching a bare numeric port.
        internal bool RemoveAuthorizedXboxOneRegistration(
            XboxOneAuthorizedRegistrationV1 registration,
            int receiveTimeoutMs = ApiReceiveTimeoutMs)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (receiveTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(receiveTimeoutMs));
            }
            try
            {
                JsonElement response = SendBoundedXboxOneManagementRequest(
                    XboxOneManagementOperation.RemovePersona,
                    registration.RemovalPath,
                    registration.SerializeRemovalRequest(), receiveTimeoutMs);
                return XboxOneAuthorizedRegistrationV1.ParseRemovalResponse(response);
            }
            catch (Exception error) when (error is not XboxOneManagementException &&
                (error is IOException || error is JsonException || error is ObjectDisposedException))
            {
                // A remote error may echo its request. Do not retain that
                // error (including as InnerException) in ordinary logs.
                throw new IOException(
                    "VIIPER did not acknowledge exact Xbox One registration removal.");
            }
        }

        private JsonElement SendBoundedXboxOneManagementRequest(XboxOneManagementOperation operation,
            string path,
            string payload, int receiveTimeoutMs,
            CancellationToken cancellationToken = default)
        {
            // Connect has its own three-second bound. After connection, one
            // absolute deadline includes authentication, write, and the whole
            // response; partial reads never extend it. The live caller must
            // choose a budget compatible with the server's close/join limit.
            cancellationToken.ThrowIfCancellationRequested();
            using TcpClient tcp = Connect(receiveTimeoutMs, cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(receiveTimeoutMs);
            using CancellationTokenRegistration cancellation = deadline.Token.Register(
                static state => ((TcpClient)state).Dispose(), tcp);
            using Stream stream = authenticate(tcp.GetStream());
            byte[] request = Encoding.UTF8.GetBytes(path + " " + payload + "\0");
            stream.Write(request, 0, request.Length);

            // Bound factory, activation, removal, and malformed/error replies
            // without inheriting the legacy unbounded API
            // accumulator. This allocation is management-only, never input.
            const int maximumResponseBytes = 1024;
            byte[] response = new byte[maximumResponseBytes];
            int length = 0;
            while (true)
            {
                int read = stream.Read(response, length, response.Length - length);
                if (read == 0)
                    break;
                length += read;
                if (length == response.Length)
                    throw new IOException("Xbox One management response exceeds its limit.");
            }
            if (deadline.IsCancellationRequested)
                throw new IOException("Xbox One management response timed out.");
            using JsonDocument document = JsonDocument.Parse(
                response.AsMemory(0, length));
            ThrowIfXboxOneManagementError(document.RootElement, operation);
            return document.RootElement.Clone();
        }

        internal static void ThrowIfXboxOneManagementError(JsonElement response,
            XboxOneManagementOperation operation)
        {
            if (response.ValueKind != JsonValueKind.Object) return;
            bool errorEnvelope = false;
            int statusCount = 0;
            int status = 0;
            foreach (JsonProperty property in response.EnumerateObject())
            {
                if (property.Name == "status")
                {
                    errorEnvelope = true;
                    statusCount++;
                    if (property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetInt32(out int numeric) && numeric is >= 400 and <= 599)
                        status = numeric;
                    else
                        status = 0;
                }
                else if (property.Name is "title" or "detail")
                    errorEnvelope = true;
            }
            if (errorEnvelope)
            {
                // No title/detail, request path, response fragment, or inner
                // exception: the peer may echo an authentication capability.
                throw new XboxOneManagementException(operation,
                    statusCount == 1 ? status : 0);
            }
        }

        private void RemoveDevice(uint busId, string devId)
        {
            TryRemoveDevice(busId, devId);
            TryRemoveBus(busId);
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

        private T SendRequest<T>(string path, string payload = null,
            int receiveTimeout = ApiReceiveTimeoutMs)
        {
            string raw = SendRequestRaw(path, payload, receiveTimeout);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new IOException("VIIPER returned an empty response.");
            }

            ViiperApiError apiError = JsonSerializer.Deserialize<ViiperApiError>(raw, JsonOptions);
            if (apiError != null && (apiError.Status != 0 || !string.IsNullOrEmpty(apiError.Title)))
            {
                throw new ViiperApiException(apiError.Status, apiError.Title,
                    apiError.Detail);
            }

            return JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }

        private string SendRequestRaw(string path, string payload = null,
            int receiveTimeout = ApiReceiveTimeoutMs)
        {
            using TcpClient tcp = Connect(receiveTimeout);
            using Stream stream = authenticate(tcp.GetStream());
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

        private TcpClient Connect(int receiveTimeout,
            CancellationToken cancellationToken = default)
        {
            TcpClient tcp = new TcpClient
            {
                NoDelay = true,
                SendTimeout = 1000,
                ReceiveTimeout = receiveTimeout,
            };

            try
            {
                // The legacy BeginConnect AsyncWaitHandle can be disposed by
                // the socket completion path while a profile transition is
                // simultaneously retiring the virtual device. Waiting on that
                // handle caused an unhandled ObjectDisposedException in the
                // microphone monitor. The cancellable socket API has no shared
                // wait handle and gives every request its own timeout owner.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                tcp.ConnectAsync(host, port, timeout.Token).AsTask()
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex)
            {
                tcp.Dispose();
                throw new IOException(
                    $"Could not connect to VIIPER at {host}:{port} within 3 seconds. Start VIIPER server with its API listening on port {port}.",
                    ex);
            }
            catch (SocketException ex)
            {
                tcp.Dispose();
                throw new IOException($"Could not connect to VIIPER at {host}:{port}: {ex.Message}", ex);
            }
            catch
            {
                tcp.Dispose();
                throw;
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
            [JsonPropertyName("devId")]
            public string DevId { get; set; }

            [JsonPropertyName("usbipPort")]
            public int UsbipPort { get; set; }

            [JsonPropertyName("usbipOwnerSerial")]
            public string UsbipOwnerSerial { get; set; }
        }

        private sealed class ViiperRuntimeStatusUpdateResponse
        {
            [JsonPropertyName("version")]
            public ushort Version { get; set; }

            [JsonPropertyName("updated")]
            public bool Updated { get; set; }
        }

        private sealed class ViiperDeviceCreateRequest
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("idProduct")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ushort? IdProduct { get; set; }

            [JsonPropertyName("deviceSpecific")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public object DeviceSpecific { get; set; }
        }

        internal sealed class Xbox360CreateOptions
        {
            [JsonPropertyName("maximumOrderedAgeMilliseconds")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? MaximumOrderedAgeMilliseconds { get; set; }
        }

        private sealed class ViiperBusDevicesResponse
        {
            [JsonPropertyName("devices")]
            public ViiperListedDevice[] Devices { get; set; }
        }

        private sealed class ViiperListedDevice
        {
            [JsonPropertyName("devId")]
            public string DevId { get; set; }

            [JsonPropertyName("deviceSpecific")]
            public JsonElement DeviceSpecific { get; set; }
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
    }

    internal enum XboxOneManagementOperation : byte
    {
        CreateBus,
        CreatePersona,
        ActivatePersona,
        RemovePersona,
    }

    internal sealed class XboxOneManagementException : IOException
    {
        internal XboxOneManagementException(XboxOneManagementOperation operation, int status)
            : base($"VIIPER rejected Xbox One {OperationLabel(operation)}" +
                (status is >= 400 and <= 599 ? $" (API status {status})." :
                    " (invalid API error response)."))
        {
            Operation = operation;
            Status = status is >= 400 and <= 599 ? status : 0;
        }

        internal XboxOneManagementOperation Operation { get; }
        internal int Status { get; }

        private static string OperationLabel(XboxOneManagementOperation operation) => operation switch
        {
            XboxOneManagementOperation.CreateBus => "bus creation",
            XboxOneManagementOperation.CreatePersona => "persona creation",
            XboxOneManagementOperation.ActivatePersona => "activation",
            XboxOneManagementOperation.RemovePersona => "removal",
            _ => "management",
        };
    }

    internal sealed class ViiperApiException : IOException
    {
        internal ViiperApiException(int status, string title, string detail)
            : base($"VIIPER API error {status} {title}: {detail}")
        {
            Status = status;
            Title = title ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        internal int Status { get; }
        internal string Title { get; }
        internal string Detail { get; }

        internal bool IsUnknownDeviceType(string requestedName)
        {
            return Status == 400 &&
                string.Equals(Title, "Bad Request",
                    StringComparison.Ordinal) &&
                string.Equals(Detail,
                    $"unknown device type: {requestedName?.ToLowerInvariant()}",
                    StringComparison.Ordinal);
        }
    }

    internal static class ViiperUsbipPortManager
    {
        private const string OwnershipSerialPrefix = "DS4W";
        private const int OwnershipSerialLength = 15;
        private const int ViiperUsbipServerPort = 3241;
        private static readonly object ActivePortsLock = new object();
        private static readonly ViiperNativeMutationGate NativePortMutationGate = new();
        private static readonly Dictionary<int, string> ActivePorts =
            new Dictionary<int, string>();
        private static readonly Dictionary<int, ViiperXboxOnePortLease> XboxOnePorts = new();

        internal static T WithNativePortMutationLock<T>(Func<T> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            using (NativePortMutationGate.Enter(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
        }

        internal static ViiperXboxOnePortLease RegisterXboxOnePort(int port, string alias)
        {
            if (port <= 0 || !XboxOneAuthorizedRegistrationV1.IsCanonicalUsbipBusId(alias))
                throw new ArgumentException("An exact Xbox One port and export alias are required.");
            var lease = new ViiperXboxOnePortLease(port, alias);
            lock (ActivePortsLock)
            {
                ActivePorts.Remove(port);
                XboxOnePorts[port] = lease;
            }
            return lease;
        }

        internal static void UnregisterXboxOnePort(ViiperXboxOnePortLease lease)
        {
            lock (ActivePortsLock)
            {
                if (XboxOnePorts.TryGetValue(lease.Port, out var current) &&
                    ReferenceEquals(current, lease))
                    XboxOnePorts.Remove(lease.Port);
            }
        }

        internal delegate bool UsbipCommandRunner(string[] arguments,
            out string output, out string error);

        public static void DetachStaleLocalViiperPorts()
        {
            using (NativePortMutationGate.Enter())
                DetachStaleLocalViiperPortsCore();
        }

        private static void DetachStaleLocalViiperPortsCore()
        {
            HashSet<int> activePorts;
            lock (ActivePortsLock)
            {
                activePorts = new HashSet<int>(ActivePorts.Keys);
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
                if (!TryGetImportedPorts(out IReadOnlyList<UsbipPortBlock> ports,
                    out string queryError))
                {
                    throw CreatePortQueryException("clean stale VIIPER imports",
                        queryError);
                }

                bool detachedAny = false;
                foreach (UsbipPortBlock port in ports)
                {
                    if (!activePorts.Contains(port.Port) &&
                        IsDs4WindowsOwnedLocalPort(port, null))
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

        public static void DetachDuplicateLocalViiperPorts(uint busId, string devId, int keepPort)
        {
            using (NativePortMutationGate.Enter())
                DetachDuplicateLocalViiperPortsCore(busId, devId, keepPort);
        }

        private static void DetachDuplicateLocalViiperPortsCore(uint busId, string devId, int keepPort)
        {
            if (keepPort < 0)
            {
                return;
            }

            string remoteBusId = $"{busId}-{devId}";
            if (!TryGetImportedPorts(out IReadOnlyList<UsbipPortBlock> ports,
                out string queryError))
            {
                throw CreatePortQueryException(
                    $"remove duplicate VIIPER import {remoteBusId}", queryError);
            }

            HashSet<int> activePorts;
            lock (ActivePortsLock)
            {
                activePorts = new HashSet<int>(ActivePorts.Keys);
            }

            foreach (UsbipPortBlock port in ports)
            {
                if (port.Port != keepPort && !activePorts.Contains(port.Port) &&
                    IsDs4WindowsOwnedLocalPort(port, remoteBusId))
                {
                    DetachPort(port.Port, $"duplicate local VIIPER import for {remoteBusId}");
                }
            }
        }

        public static void RegisterActivePort(int port)
        {
            RegisterActivePort(port, null);
        }

        internal static void RegisterActivePort(int port, string remoteBusId)
        {
            if (port < 0)
            {
                return;
            }

            lock (ActivePortsLock)
            {
                ActivePorts[port] = remoteBusId;
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
                return ActivePorts.ContainsKey(port) || XboxOnePorts.ContainsKey(port);
            }
        }

        internal static void DetachRegisteredPort(int port, string reason)
        {
            using (NativePortMutationGate.Enter())
                DetachRegisteredPortCore(port, reason);
        }

        private static void DetachRegisteredPortCore(int port, string reason)
        {
            string remoteBusId;
            lock (ActivePortsLock)
            {
                if (XboxOnePorts.ContainsKey(port) ||
                    !ActivePorts.TryGetValue(port, out remoteBusId))
                {
                    return;
                }
            }

            if (!TryGetImportedPorts(out IReadOnlyList<UsbipPortBlock> ports,
                out string queryError))
            {
                throw CreatePortQueryException(
                    $"verify registered VIIPER port {port} before detaching it",
                    queryError);
            }

            UsbipPortBlock ownedPort = default;
            bool found = false;
            foreach (UsbipPortBlock candidate in ports)
            {
                if (candidate.Port == port)
                {
                    ownedPort = candidate;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return;
            }

            if (!IsDs4WindowsOwnedLocalPort(ownedPort, remoteBusId))
            {
                AppLogger.LogToGui(
                    $"VIIPER refused to detach usbip port {port} ({reason}) because its DS4Windows ownership token or device identity no longer matches.",
                    true);
                return;
            }

            DetachPort(port, reason);
        }

        private static void DetachPort(int port, string reason)
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

        private static bool TryGetImportedPorts(
            out IReadOnlyList<UsbipPortBlock> ports, out string error)
        {
            return TryGetImportedPorts(TryRunUsbip, out ports, out error);
        }

        internal static bool TryGetImportedPorts(UsbipCommandRunner runner,
            out IReadOnlyList<UsbipPortBlock> ports, out string error)
        {
            if (runner == null)
            {
                throw new ArgumentNullException(nameof(runner));
            }

            ports = Array.Empty<UsbipPortBlock>();
            if (!runner(new[] { "port" }, out string output,
                out string commandError))
            {
                error = string.IsNullOrWhiteSpace(commandError) ?
                    "usbip.exe port failed without an error message." :
                    commandError.Trim();
                return false;
            }

            ports = ParseImportedPorts(output);
            error = string.Empty;
            return true;
        }

        internal static IReadOnlyList<UsbipPortBlock> ParseImportedPorts(
            string output)
        {
            List<UsbipPortBlock> ports = new List<UsbipPortBlock>();
            string[] lines = (output ?? string.Empty).Replace("\r\n", "\n").
                Split('\n');
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

        internal static bool IsDs4WindowsOwnedLocalPort(
            UsbipPortBlock port, string remoteBusId)
        {
            if (!TryParseRemoteLocation(port.Block, out string host,
                out int serverPort, out string parsedBusId) ||
                !IsLocalHost(host) || serverPort != ViiperUsbipServerPort)
            {
                return false;
            }

            // Protected Xbox imports belong to exact server lifetimes. Even
            // an unregistered/late-visible alias must never enter legacy
            // numeric port cleanup. Case variants fail closed as well.
            if (parsedBusId.StartsWith("x1-", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(remoteBusId) &&
                !string.Equals(parsedBusId, remoteBusId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // usbip-win2 0.9.7.7 has no attach-time serial override. Its
            // safe ownership identity is the exact VIIPER-only localhost
            // server port plus the remote bus/device tuple. If a newer port
            // listing does expose a serial, reject every foreign token.
            return !TryParseSerial(port.Block, out string serial) ||
                IsDs4WindowsOwnershipSerial(serial);
        }

        internal static bool IsDs4WindowsOwnershipSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial) ||
                serial.Length != OwnershipSerialLength ||
                !serial.StartsWith(OwnershipSerialPrefix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = OwnershipSerialPrefix.Length;
                index < serial.Length; index++)
            {
                char value = serial[index];
                if (!((value >= 'A' && value <= 'F') ||
                    (value >= 'a' && value <= 'f') ||
                    (value >= '0' && value <= '9')))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsTrustedCreateResponse(int port,
            string ownerSerial)
        {
            return port > 0 && (string.IsNullOrEmpty(ownerSerial) ||
                IsDs4WindowsOwnershipSerial(ownerSerial));
        }

        private static bool TryParseRemoteLocation(string block,
            out string host, out int serverPort, out string remoteBusId)
        {
            host = null;
            serverPort = -1;
            remoteBusId = null;
            foreach (string rawLine in SplitLines(block))
            {
                string line = rawLine.Trim();
                const string marker = "-> usbip://";
                if (!line.StartsWith(marker,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string location = line.Substring("-> ".Length);
                if (!Uri.TryCreate(location, UriKind.Absolute, out Uri uri) ||
                    !string.Equals(uri.Scheme, "usbip",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                host = uri.Host;
                serverPort = uri.Port;
                remoteBusId = uri.AbsolutePath.Trim('/');
                return !string.IsNullOrEmpty(host) &&
                    !string.IsNullOrEmpty(remoteBusId);
            }

            return false;
        }

        private static bool TryParseSerial(string block, out string serial)
        {
            serial = null;
            foreach (string rawLine in SplitLines(block))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("-> serial",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int openingQuote = line.IndexOf('\'');
                int closingQuote = openingQuote >= 0 ?
                    line.IndexOf('\'', openingQuote + 1) : -1;
                if (openingQuote < 0 || closingQuote <= openingQuote + 1)
                {
                    return false;
                }

                serial = line.Substring(openingQuote + 1,
                    closingQuote - openingQuote - 1);
                return true;
            }

            return false;
        }

        private static IEnumerable<string> SplitLines(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        }

        private static bool IsLocalHost(string host)
        {
            return string.Equals(host, "localhost",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "127.0.0.1",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
        }

        private static IOException CreatePortQueryException(string operation,
            string error)
        {
            string detail = string.IsNullOrWhiteSpace(error) ?
                "unknown usbip.exe failure" : error.Trim();
            AppLogger.LogToGui(
                $"VIIPER could not {operation}: {detail}", true);
            return new IOException(
                $"VIIPER could not {operation}: {detail}");
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
            return FindUsbipPath(
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("PATH"), File.Exists);
        }

        internal static string FindUsbipPath(string programW6432,
            string programFiles, string programFilesX86, string pathValue,
            Func<string, bool> fileExists)
        {
            if (fileExists == null)
            {
                throw new ArgumentNullException(nameof(fileExists));
            }

            HashSet<string> visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string root in new[]
            {
                programW6432,
                programFiles,
                programFilesX86,
            })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                string candidate = Path.Combine(root.Trim().Trim('"'), "USBip",
                    "usbip.exe");
                if (visited.Add(candidate) && fileExists(candidate))
                {
                    return candidate;
                }
            }

            foreach (string folder in (pathValue ?? string.Empty).
                Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    continue;
                }

                string candidate = Path.Combine(folder.Trim().Trim('"'),
                    "usbip.exe");
                if (visited.Add(candidate) && fileExists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        internal readonly struct UsbipPortBlock
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

    internal sealed class ViiperXboxOnePortLease : IDisposable
    {
        internal ViiperXboxOnePortLease(int port, string alias)
        {
            Port = port;
            Alias = alias;
        }
        internal int Port { get; }
        internal string Alias { get; }
        public void Dispose() => ViiperUsbipPortManager.UnregisterXboxOnePort(this);
    }

    internal sealed class ViiperVirtualDeviceLifetime : IDisposable
    {
        private readonly object lifecycleLock = new object();
        private readonly uint busId;
        private readonly string devId;
        private int usbipPort;
        private readonly Action<int, string> detachPort;
        private readonly Action<int> unregisterPort;
        private readonly Action<uint, string> removeDevice;
        private readonly Action detachStalePorts;
        private readonly Func<XboxOneAuthorizedRegistrationV1, bool> removeXboxOne;
        private readonly TaskCompletionSource<bool> xboxOneCleanupDone;
        private int xboxOneCleanupThread;
        private ViiperXboxOnePortLease xboxOnePort;
        private XboxOneActivationRequest xboxOneActivationRequest;
        private int disposed;

        internal ViiperVirtualDeviceLifetime(XboxOneAuthorizedRegistrationV1 registration,
            Func<XboxOneAuthorizedRegistrationV1, bool> removeXboxOne)
        {
            XboxOneRegistration = registration ?? throw new ArgumentNullException(nameof(registration));
            this.removeXboxOne = removeXboxOne ?? throw new ArgumentNullException(nameof(removeXboxOne));
            xboxOneCleanupDone = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            busId = registration.BusId;
            devId = registration.DevId;
            usbipPort = -1;
        }

        internal XboxOneAuthorizedRegistrationV1 XboxOneRegistration { get; }

        internal XboxOneActivationRequest BeginXboxOneActivationRequest()
        {
            lock (lifecycleLock)
            {
                if (disposed == 1)
                    throw new ObjectDisposedException(nameof(ViiperVirtualDeviceLifetime));
                if (XboxOneRegistration == null || usbipPort != -1 ||
                    xboxOneActivationRequest != null)
                    throw new InvalidOperationException("Xbox One activation already has an owner or completed.");
                return xboxOneActivationRequest = new XboxOneActivationRequest(this);
            }
        }

        internal void ReleaseXboxOneActivationRequest(XboxOneActivationRequest request)
        {
            lock (lifecycleLock)
            {
                if (ReferenceEquals(xboxOneActivationRequest, request))
                    xboxOneActivationRequest = null;
            }
        }

        internal void BindXboxOnePort(ViiperXboxOnePortLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            lock (lifecycleLock)
            {
                if (disposed == 1)
                    throw new ObjectDisposedException(nameof(ViiperVirtualDeviceLifetime));
                if (XboxOneRegistration == null ||
                    lease.Alias != XboxOneRegistration.UsbipBusId || usbipPort != -1)
                    throw new InvalidOperationException("Xbox One activation does not match this lifetime.");
                xboxOnePort = lease;
                usbipPort = lease.Port;
            }
        }

        internal ViiperVirtualDeviceLifetime(uint busId, string devId,
            int usbipPort, Action<uint, string> removeDevice,
            Action<int, string> detachPort = null,
            Action<int> unregisterPort = null,
            Action detachStalePorts = null)
        {
            this.busId = busId;
            this.devId = devId ?? throw new ArgumentNullException(nameof(devId));
            this.usbipPort = usbipPort;
            this.removeDevice = removeDevice;
            this.detachPort = detachPort ??
                ViiperUsbipPortManager.DetachRegisteredPort;
            this.unregisterPort = unregisterPort ??
                ViiperUsbipPortManager.UnregisterActivePort;
            this.detachStalePorts = detachStalePorts ??
                ViiperUsbipPortManager.DetachStaleLocalViiperPorts;
        }

        internal uint BusId => busId;

        internal string DevId => devId;

        internal int UsbipPort => Volatile.Read(ref usbipPort);

        internal void BindUsbipPort(int value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            lock (lifecycleLock)
            {
                if (disposed == 1)
                {
                    throw new ObjectDisposedException(
                        nameof(ViiperVirtualDeviceLifetime));
                }
                if (XboxOneRegistration != null)
                    throw new InvalidOperationException("Xbox One requires exact port-lease binding.");
                if (usbipPort != -1 && usbipPort != value)
                {
                    throw new InvalidOperationException(
                        "The VIIPER virtual-device lifetime already owns another usbip-win2 port.");
                }
                usbipPort = value;
            }
        }

        internal bool IsDisposed => Volatile.Read(ref disposed) == 1;

        public void Dispose()
        {
            int ownedPort;
            XboxOneActivationRequest pendingActivation;
            bool joinXboxOneCleanup = false;
            lock (lifecycleLock)
            {
                if (disposed == 1)
                {
                    if (XboxOneRegistration == null)
                        return;
                    if (xboxOneCleanupThread == Environment.CurrentManagedThreadId &&
                        !xboxOneCleanupDone.Task.IsCompleted)
                        throw new InvalidOperationException("Xbox One cleanup cannot reenter its own disposal.");
                    joinXboxOneCleanup = true;
                }
                else
                {
                    disposed = 1;
                    xboxOneCleanupThread = Environment.CurrentManagedThreadId;
                }
                ownedPort = usbipPort;
                pendingActivation = xboxOneActivationRequest;
            }
            if (joinXboxOneCleanup)
            {
                // A concurrent stream Dispose must not close the broker while
                // the first lifetime owner is still awaiting its Stop ACK.
                xboxOneCleanupDone.Task.GetAwaiter().GetResult();
                return;
            }
            if (XboxOneRegistration != null)
            {
                // Abort only the pending management socket. Keep the broker
                // reader alive for the following exact retained Stop/ACK.
                // No lifecycle lock is held across cancellation callbacks.
                try { pendingActivation?.Cancel(); }
                catch { /* Exact removal must still run if local abort fails. */ }
                try
                {
                    removeXboxOne(XboxOneRegistration);
                }
                catch
                {
                    // No numeric fallback on uncertain exact removal.
                }
                finally
                {
                    try { xboxOnePort?.Dispose(); }
                    finally { xboxOneCleanupDone.TrySetResult(true); }
                }
                return;
            }
            try
            {
                if (ownedPort > 0)
                {
                    detachPort?.Invoke(ownedPort,
                        "DS4Windows VIIPER device stopped");
                }
            }
            catch
            {
            }

            try
            {
                if (ownedPort > 0)
                {
                    unregisterPort?.Invoke(ownedPort);
                }
            }
            catch
            {
            }

            try
            {
                removeDevice?.Invoke(busId, devId);
            }
            catch
            {
            }

            try
            {
                detachStalePorts?.Invoke();
            }
            catch
            {
            }
        }

    }

    internal readonly struct ViiperFrameWriteTiming
    {
        internal ViiperFrameWriteTiming(long socketWriteStartedTimestamp,
            long socketWriteCompletedTimestamp,
            long acceptanceAcknowledgedTimestamp = 0,
            long waitCompletedTimestamp = 0)
        {
            SocketWriteStartedTimestamp = socketWriteStartedTimestamp;
            SocketWriteCompletedTimestamp = socketWriteCompletedTimestamp;
            AcceptanceAcknowledgedTimestamp =
                acceptanceAcknowledgedTimestamp;
            WaitCompletedTimestamp = waitCompletedTimestamp;
        }

        internal long SocketWriteStartedTimestamp { get; }
        internal long SocketWriteCompletedTimestamp { get; }
        internal long AcceptanceAcknowledgedTimestamp { get; }
        internal long WaitCompletedTimestamp { get; }
    }

    internal sealed class ViiperDeviceStream : IDisposable
    {
        private readonly IDisposable transport;
        private readonly Stream stream;
        private readonly ViiperVirtualDeviceLifetime deviceLifetime;
        private readonly object frameWriterOwnership = new object();
        private readonly object sendLock = new object();
        private readonly object xboxOneInputLock = new object();
        private readonly AutoResetEvent xboxOneInputAckSignal =
            new AutoResetEvent(false);
        private readonly byte[] incomingFrameHeader =
            new byte[FramedHeaderLength];
        private readonly byte[] xboxOneBrokerHeader =
            new byte[XboxOneBrokerHeaderLength];
        private readonly byte[] xboxOneBrokerSendBuffer =
            new byte[XboxOneBrokerHeaderLength +
                ControllerFeedbackFrame.SerializedLength];
        private readonly byte[] xboxOneBrokerAckPayload = new byte[1];
        // The production state writer is the sole owner of this buffer and
        // sequence. Compatibility callers take frameWriterOwnership before
        // entering the same core. Reusing the storage removes the per-frame
        // managed allocation which could otherwise create input-tail jitter.
        private byte[] outgoingFrameBuffer =
            new byte[FramedHeaderLength + 2048];
        private uint frameSequence;
        private uint incomingFrameSequence;
        private bool incomingFrameSequenceKnown;
        private ulong xboxOneInputRevision = 1;
        private long xboxOneAwaitingInputRevision;
        private long xboxOneInputAckReceivedTimestamp;
        private long xboxOneRejectedInputRevision;
        private int xboxOneInputAckResult;
        private bool xboxOneBrokerEnabled;
        private int transportClosed;
        private int streamDisposed;
        private const int FramedHeaderLength = 16;
        private const byte FrameMagic0 = (byte)'V';
        private const byte FrameMagic1 = (byte)'P';
        private const byte FrameMagic2 = (byte)'C';
        private const byte FrameMagic3 = (byte)'M';
        private const byte FrameVersionV3 = 0x03;
        private const byte FrameVersionV5 = 0x05;
        private const int XboxOneBrokerHeaderLength = 16;
        private const byte XboxOneBrokerVersion = 1;
        private const byte XboxOneBrokerConsumerReady = 0x01;
        private const byte XboxOneBrokerSemanticInput = 0x02;
        private const byte XboxOneBrokerCanonicalAck = 0x03;
        private const byte XboxOneBrokerConsumerReadyAck = 0x81;
        internal const byte XboxOneBrokerSemanticInputAck = 0x82;
        internal const byte XboxOneBrokerCanonicalFeedback = 0x83;
        private const byte XboxOneBrokerRejected = 0;
        private const byte XboxOneBrokerAccepted = 1;
        private const int XboxOneBrokerInputAckTimeoutMilliseconds = 250;
        private const int XboxOneInputAckPending = 0;
        private const int XboxOneInputAckAccepted = 1;
        private const int XboxOneInputAckRejected = 2;
        private const int XboxOneInputAckClosed = 3;
        private static readonly uint[] FramedCrcTable = BuildFramedCrcTable();

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
        }

        public uint BusId => deviceLifetime.BusId;

        public string DevId => deviceLifetime.DevId;

        public int UsbipPort => deviceLifetime.UsbipPort;

        internal ViiperVirtualDeviceLifetime DeviceLifetime => deviceLifetime;

        internal bool IsTransportClosed =>
            Volatile.Read(ref transportClosed) == 1;

        internal bool IsXboxOneBrokerEnabled => xboxOneBrokerEnabled;

        internal bool IsXboxOneInputRejected =>
            Interlocked.Read(ref xboxOneRejectedInputRevision) != 0;

        internal void BindUsbipPort(int port) =>
            deviceLifetime.BindUsbipPort(port);

        internal void EnableXboxOneBroker()
        {
            if (xboxOneBrokerEnabled ||
                Volatile.Read(ref transportClosed) == 1)
            {
                throw new InvalidOperationException(
                    "The VIIPER Xbox One broker cannot be enabled in this stream state.");
            }

            WriteXboxOneBrokerFrame(XboxOneBrokerConsumerReady, 0,
                Array.Empty<byte>(), 0);
            byte[] readyPayload = Array.Empty<byte>();
            int payloadLength = ReadXboxOneBrokerFrame(
                out byte type, out ulong correlation, readyPayload);
            if (type != XboxOneBrokerConsumerReadyAck || correlation != 0 ||
                payloadLength != 0)
            {
                throw new IOException(
                    "VIIPER returned an invalid Xbox One consumer-ready acknowledgement.");
            }
            xboxOneBrokerEnabled = true;
        }

        internal ViiperFrameWriteTiming WriteXboxOneInputAndWaitForAck(
            byte[] data, int length)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (!xboxOneBrokerEnabled || length != XboxOneEgressState.WireSize ||
                length > data.Length)
            {
                throw new IOException(
                    "The VIIPER Xbox One semantic-input broker is unavailable.");
            }

            lock (xboxOneInputLock)
            {
                if (Volatile.Read(ref transportClosed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ViiperDeviceStream));
                }
                ulong rejectedRevision = unchecked((ulong)Interlocked.Read(
                    ref xboxOneRejectedInputRevision));
                if (rejectedRevision != 0)
                {
                    throw new XboxOneSemanticInputRejectedException(rejectedRevision);
                }
                if (xboxOneInputRevision == ulong.MaxValue)
                {
                    throw new IOException(
                        "The VIIPER Xbox One semantic-input revision was exhausted.");
                }
                ulong revision = xboxOneInputRevision + 1;
                Interlocked.Exchange(ref xboxOneAwaitingInputRevision,
                    unchecked((long)revision));
                Interlocked.Exchange(ref xboxOneInputAckReceivedTimestamp, 0);
                Volatile.Write(ref xboxOneInputAckResult,
                    XboxOneInputAckPending);
                ViiperFrameWriteTiming timing;
                try
                {
                    timing = WriteXboxOneBrokerFrame(
                        XboxOneBrokerSemanticInput, revision, data, length);
                }
                catch
                {
                    Interlocked.Exchange(ref xboxOneAwaitingInputRevision, 0);
                    throw;
                }

                if (!xboxOneInputAckSignal.WaitOne(
                        XboxOneBrokerInputAckTimeoutMilliseconds))
                {
                    CloseTransport();
                    throw new IOException(
                        "VIIPER Xbox One semantic-input acceptance acknowledgement timed out; the one-shot persona was retired.");
                }
                int result = Volatile.Read(ref xboxOneInputAckResult);
                Interlocked.Exchange(ref xboxOneAwaitingInputRevision, 0);
                if (result != XboxOneInputAckAccepted)
                {
                    if (result == XboxOneInputAckRejected)
                    {
                        // Only an exact negative ACK permits this input-only
                        // fence. Keep canonical feedback/ACK alive so the owner
                        // can perform its bounded terminal Stop handshake.
                        throw new XboxOneSemanticInputRejectedException(revision);
                    }
                    CloseTransport();
                    throw new IOException(
                        "The VIIPER Xbox One broker closed before accepting semantic input.");
                }
                long waitCompletedTimestamp = Stopwatch.GetTimestamp();
                long acknowledgedTimestamp = Interlocked.Read(
                    ref xboxOneInputAckReceivedTimestamp);
                if (acknowledgedTimestamp <= 0 ||
                    acknowledgedTimestamp > waitCompletedTimestamp)
                {
                    acknowledgedTimestamp = waitCompletedTimestamp;
                }
                xboxOneInputRevision = revision;
                return new ViiperFrameWriteTiming(
                    timing.SocketWriteStartedTimestamp,
                    timing.SocketWriteCompletedTimestamp,
                    acknowledgedTimestamp, waitCompletedTimestamp);
            }
        }

        internal int ReadXboxOneBrokerFrame(out byte type,
            out ulong correlation, byte[] payloadBuffer)
        {
            ArgumentNullException.ThrowIfNull(payloadBuffer);
            byte[] header = xboxOneBrokerHeader;
            ReadExactly(header, 0, header.Length);
            if (header[0] != (byte)'X' || header[1] != (byte)'1' ||
                header[2] != (byte)'B' || header[3] != (byte)'R' ||
                header[4] != XboxOneBrokerVersion)
            {
                throw new IOException(
                    "VIIPER returned an invalid Xbox One broker frame header.");
            }
            type = header[5];
            int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(6, sizeof(ushort)));
            correlation = BinaryPrimitives.ReadUInt64LittleEndian(
                header.AsSpan(8, sizeof(ulong)));
            if (!XboxOneBrokerPayloadLengthValid(type, payloadLength) ||
                payloadLength > payloadBuffer.Length)
            {
                throw new IOException(
                    "VIIPER returned an invalid Xbox One broker payload length.");
            }
            ReadExactly(payloadBuffer, 0, payloadLength);
            return payloadLength;
        }

        internal void AcceptXboxOneInputAck(ulong correlation, byte status)
        {
            ulong awaiting = unchecked((ulong)Interlocked.Read(
                ref xboxOneAwaitingInputRevision));
            if (awaiting == 0 || correlation != awaiting ||
                (status != XboxOneBrokerAccepted &&
                    status != XboxOneBrokerRejected))
            {
                CloseTransport();
                throw new IOException(
                    "VIIPER returned an invalid Xbox One semantic-input acknowledgement.");
            }
            Interlocked.Exchange(ref xboxOneInputAckReceivedTimestamp,
                Stopwatch.GetTimestamp());
            int previous = Interlocked.CompareExchange(ref xboxOneInputAckResult,
                status == XboxOneBrokerAccepted ?
                    XboxOneInputAckAccepted : XboxOneInputAckRejected,
                XboxOneInputAckPending);
            if (previous != XboxOneInputAckPending)
            {
                CloseTransport();
                throw new IOException(
                    "VIIPER returned a duplicate or retired Xbox One semantic-input acknowledgement.");
            }
            if (status == XboxOneBrokerRejected)
            {
                // Set the permanent input fence before waking the writer. It
                // remains separate from transportClosed so feedback ACK writes
                // are still possible during this incarnation's retirement.
                // A malformed/duplicate acknowledgement cannot set this fence.
                Interlocked.CompareExchange(ref xboxOneRejectedInputRevision,
                    unchecked((long)correlation), 0);
            }
            xboxOneInputAckSignal.Set();
        }

        internal void AcknowledgeXboxOneFeedback(ulong correlation,
            bool accepted)
        {
            if (correlation == 0)
            {
                throw new IOException(
                    "VIIPER returned an invalid Xbox One feedback revision.");
            }
            byte[] status = xboxOneBrokerAckPayload;
            status[0] = accepted ? XboxOneBrokerAccepted :
                XboxOneBrokerRejected;
            WriteXboxOneBrokerFrame(XboxOneBrokerCanonicalAck,
                correlation, status, 1);
        }

        private ViiperFrameWriteTiming WriteXboxOneBrokerFrame(byte type,
            ulong correlation, byte[] payload, int payloadLength)
        {
            if (payload == null || payloadLength < 0 ||
                payloadLength > payload.Length ||
                !XboxOneBrokerPayloadLengthValid(type, payloadLength) ||
                Volatile.Read(ref transportClosed) == 1)
            {
                throw new IOException(
                    "The VIIPER Xbox One broker frame is invalid.");
            }
            lock (sendLock)
            {
                if (Volatile.Read(ref transportClosed) == 1)
                {
                    throw new ObjectDisposedException(
                        nameof(ViiperDeviceStream));
                }
                // Input and canonical-feedback ACKs are emitted by different
                // owners. Build under the same socket lock that owns the
                // reusable frame so their bytes cannot interleave or overwrite
                // one another before the contiguous write.
                byte[] frame = xboxOneBrokerSendBuffer;
                frame[0] = (byte)'X';
                frame[1] = (byte)'1';
                frame[2] = (byte)'B';
                frame[3] = (byte)'R';
                frame[4] = XboxOneBrokerVersion;
                frame[5] = type;
                BinaryPrimitives.WriteUInt16LittleEndian(
                    frame.AsSpan(6, sizeof(ushort)),
                    (ushort)payloadLength);
                BinaryPrimitives.WriteUInt64LittleEndian(
                    frame.AsSpan(8, sizeof(ulong)), correlation);
                if (payloadLength > 0)
                {
                    Buffer.BlockCopy(payload, 0, frame,
                        XboxOneBrokerHeaderLength, payloadLength);
                }
                long started = Stopwatch.GetTimestamp();
                stream.Write(frame, 0,
                    XboxOneBrokerHeaderLength + payloadLength);
                return new ViiperFrameWriteTiming(started,
                    Stopwatch.GetTimestamp());
            }
        }

        private static bool XboxOneBrokerPayloadLengthValid(byte type,
            int payloadLength)
        {
            return type switch
            {
                XboxOneBrokerConsumerReady or
                    XboxOneBrokerConsumerReadyAck => payloadLength == 0,
                XboxOneBrokerSemanticInput =>
                    payloadLength == XboxOneEgressState.WireSize,
                XboxOneBrokerCanonicalAck or
                    XboxOneBrokerSemanticInputAck => payloadLength == 1,
                XboxOneBrokerCanonicalFeedback =>
                    payloadLength == ControllerFeedbackFrame.SerializedLength,
                _ => false,
            };
        }

        public void Write(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            Write(data, data.Length);
        }

        internal void Write(byte[] data, int length)
        {
            ArgumentNullException.ThrowIfNull(data);
            if ((uint)length > (uint)data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            if (Volatile.Read(ref transportClosed) == 1)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            lock (sendLock)
            {
                if (Volatile.Read(ref transportClosed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ViiperDeviceStream));
                }

                stream.Write(data, 0, length);
            }
        }

        public void WriteFrame(byte version, byte frameType, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            WriteFrame(version, frameType, data, data.Length);
        }

        internal void WriteFrame(byte version, byte frameType, byte[] data,
            int payloadLength)
        {
            WriteFrameTimed(version, frameType, data, payloadLength);
        }

        internal ViiperFrameWriteTiming WriteFrameTimed(byte version,
            byte frameType, byte[] data, int payloadLength)
        {
            ValidateFrameArguments(version, data, payloadLength);

            // Public and non-hot compatibility callers retain serialized
            // frame ownership. The production V5 writer uses the owner-only
            // entry point below, so it never takes this broad compatibility
            // lock across framing, CRC, or socket I/O.
            lock (frameWriterOwnership)
            {
                return WriteFrameCore(version, frameType, data,
                    payloadLength);
            }
        }

        internal ViiperFrameWriteTiming WriteFrameFromOwnerTimed(byte version,
            byte frameType, byte[] data, int payloadLength)
        {
            ValidateFrameArguments(version, data, payloadLength);
            return WriteFrameCore(version, frameType, data, payloadLength);
        }

        private static void ValidateFrameArguments(byte version, byte[] data,
            int payloadLength)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if ((uint)payloadLength > (uint)data.Length ||
                payloadLength > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }
            if (version != FrameVersionV3 && version != FrameVersionV5)
            {
                throw new ArgumentOutOfRangeException(nameof(version));
            }
        }

        private ViiperFrameWriteTiming WriteFrameCore(byte version,
            byte frameType, byte[] data, int payloadLength)
        {
            // This method mutates outgoingFrameBuffer and frameSequence. It is
            // entered either by the sole production owner or by a compatibility
            // caller holding frameWriterOwnership; those ownership modes must
            // never be mixed concurrently for one stream generation.
            if (Volatile.Read(ref transportClosed) == 1)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            int frameLength = FramedHeaderLength + payloadLength;
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
            frame[6] = (byte)payloadLength;
            frame[7] = (byte)(payloadLength >> 8);
            uint sequence = frameSequence++;
            frame[8] = (byte)sequence;
            frame[9] = (byte)(sequence >> 8);
            frame[10] = (byte)(sequence >> 16);
            frame[11] = (byte)(sequence >> 24);
            Buffer.BlockCopy(data, 0, frame, FramedHeaderLength,
                payloadLength);
            uint crc = ComputeFramedCrc(frame, frameLength);
            frame[12] = (byte)crc;
            frame[13] = (byte)(crc >> 8);
            frame[14] = (byte)(crc >> 16);
            frame[15] = (byte)(crc >> 24);
            // Framing, sequence assignment and CRC are complete before
            // serialized socket ownership is acquired. Every logical frame is
            // emitted through one contiguous write call.
            lock (sendLock)
            {
                if (Volatile.Read(ref transportClosed) == 1)
                {
                    throw new ObjectDisposedException(
                        nameof(ViiperDeviceStream));
                }
                long socketWriteStarted = Stopwatch.GetTimestamp();
                stream.Write(frame, 0, frameLength);
                return new ViiperFrameWriteTiming(socketWriteStarted,
                    Stopwatch.GetTimestamp());
            }
        }

        public byte[] ReadFrame(byte expectedVersion, out byte frameType)
        {
            byte[] header = new byte[FramedHeaderLength];
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

        private static uint ComputeFramedCrc(byte[] frame)
        {
            return ComputeFramedCrc(frame, frame.Length);
        }

        internal static uint ComputeFramedCrc(byte[] frame, int frameLength)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 4; i < 12; i++)
            {
                crc = UpdateCrc32(crc, frame[i]);
            }
            for (int i = FramedHeaderLength; i < frameLength; i++)
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
            return FramedCrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        }

        private static uint[] BuildFramedCrcTable()
        {
            uint[] table = new uint[256];
            for (uint value = 0; value < table.Length; value++)
            {
                uint crc = value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^
                        ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
                table[value] = crc;
            }
            return table;
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

            Volatile.Write(ref xboxOneInputAckResult,
                XboxOneInputAckClosed);
            xboxOneInputAckSignal.Set();

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

        internal void DisposeDeviceLifetimeBeforeTransportClose()
        {
            // Retained Xbox One teardown needs the authenticated broker to
            // remain readable and writable while usbip disconnect publishes
            // and awaits acknowledgement for its terminal canonical Stop.
            // The lifetime is idempotent, so Dispose can safely repeat it.
            deviceLifetime.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref streamDisposed, 1) == 1)
            {
                return;
            }

            if (xboxOneBrokerEnabled &&
                Volatile.Read(ref transportClosed) == 0)
            {
                DisposeDeviceLifetimeBeforeTransportClose();
            }
            CloseTransport();
            deviceLifetime.Dispose();
            xboxOneInputAckSignal.Dispose();
        }
    }

    internal static class ViiperStatePacketBuilder
    {
        private const int X360PacketSize = 20;
        private const int XboxOnePacketSize = XboxOneEgressState.WireSize;
        private const int DS4PacketSize = 31;
        private const int DualSensePacketSize = 33;
        private const int DualSenseRawInputStatusPacketSize = 53;
        private const int DualSenseRawInputStatusFlagsOffset = 33;
        private const int DualSenseRawInputSensorTimestampOffset = 34;
        private const int DualSenseRawInputStatusOffset = 38;
        private const byte DualSenseRawInputStatusValidFlag = 0x01;
        private const byte DualSenseRawInputStatusEdgeLayoutFlag = 0x02;
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
                ViiperVirtualDeviceType.XboxOne => "xboxone",
                ViiperVirtualDeviceType.DualShock4 => "dualshock4",
                ViiperVirtualDeviceType.DualSense =>
                    "dualsensecombinedaudioduplexv5",
                ViiperVirtualDeviceType.DualSenseEdge =>
                    "dualsenseedgecombinedaudioduplexv5",
                ViiperVirtualDeviceType.Switch2Pro => "ns2pro",
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }

        public static int GetFeedbackLength(ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => 2,
                ViiperVirtualDeviceType.XboxOne =>
                    ControllerFeedbackFrame.SerializedLength,
                ViiperVirtualDeviceType.DualShock4 => 7,
                ViiperVirtualDeviceType.DualSense => DualSenseFeedbackPacketSize,
                ViiperVirtualDeviceType.DualSenseEdge => DualSenseFeedbackPacketSize,
                ViiperVirtualDeviceType.Switch2Pro => 34,
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }

        public static int GetPacketSize(ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => X360PacketSize,
                ViiperVirtualDeviceType.XboxOne => XboxOnePacketSize,
                ViiperVirtualDeviceType.DualShock4 => DS4PacketSize,
                ViiperVirtualDeviceType.DualSense => DualSensePacketSize,
                ViiperVirtualDeviceType.DualSenseEdge => DualSensePacketSize,
                ViiperVirtualDeviceType.Switch2Pro => Switch2PacketSize,
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }

        internal static int GetDualSenseInputPacketSize(
            bool includeRawInputStatus) => includeRawInputStatus ?
                DualSenseRawInputStatusPacketSize : DualSensePacketSize;

        public static byte[] Build(ViiperVirtualDeviceType type, DS4State state, int device)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => BuildXbox360(state, device),
                ViiperVirtualDeviceType.XboxOne => BuildXboxOne(state, device),
                ViiperVirtualDeviceType.DualShock4 => BuildDualShock4(state, device),
                ViiperVirtualDeviceType.DualSense => BuildDualSense(state, device),
                ViiperVirtualDeviceType.DualSenseEdge => BuildDualSense(state, device),
                ViiperVirtualDeviceType.Switch2Pro => BuildSwitch2Pro(state, device),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
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
            BuildXbox360State(state, device).BuildInto(packet);
            return packet;
        }

        private static byte[] BuildXboxOne(DS4State state, int device)
        {
            byte[] packet = new byte[XboxOnePacketSize];
            XboxOneEgressState.FromLegacyMappedState(state, device)
                .BuildInto(packet);
            return packet;
        }

        internal static Xbox360EgressState BuildXbox360State(DS4State state,
            int device)
        {
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
            short lx = ScaleMappedXboxAxis(state.LXAxis, false);
            short ly = ScaleMappedXboxAxis(state.LYAxis, true);
            short rx = ScaleMappedXboxAxis(state.RXAxis, false);
            short ry = ScaleMappedXboxAxis(state.RYAxis, true);

            ApplySteeringWheelX360(state, device, ref l2, ref r2, ref lx, ref ly, ref rx, ref ry);

            return new Xbox360EgressState(buttons, l2, r2, lx, ly, rx, ry);
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
            ViiperMappedInputState mapped = BuildMappedState(state, device);
            BuildInto(mapped, packet);
            return packet;
        }

        /// <summary>
        /// Constructs the exact final DualSense state used by both transition
        /// classification and serialization. No managed object is created.
        /// </summary>
        internal static ViiperMappedInputState BuildMappedState(DS4State state,
            int device)
        {
            ArgumentNullException.ThrowIfNull(state);

            byte lx = state.LX;
            byte ly = state.LY;
            byte rx = state.RX;
            byte ry = state.RY;
            byte l2 = state.L2;
            byte r2 = state.R2;
            ApplySteeringWheelByteAxes(state, device, ref l2, ref r2,
                ref lx, ref ly, ref rx, ref ry);

            // An explicitly mapped digital trigger is still a physical press.
            // Give it the smallest representable analog value so the wire's
            // analog and digital views can never contradict one another.
            if (state.L2Btn && l2 == 0)
            {
                l2 = 1;
            }
            if (state.R2Btn && r2 == 0)
            {
                r2 = 1;
            }

            uint buttons = BuildDualSenseButtons(state) &
                ~(ViiperMappedInputState.L2ButtonMask |
                    ViiperMappedInputState.R2ButtonMask);
            if (l2 != 0)
            {
                buttons |= ViiperMappedInputState.L2ButtonMask;
            }
            if (r2 != 0)
            {
                buttons |= ViiperMappedInputState.R2ButtonMask;
            }

            ViiperMappedInputState mapped = new()
            {
                LX = lx,
                LY = ly,
                RX = rx,
                RY = ry,
                Buttons = buttons,
                DPad = BuildDPadBits(state),
                L2 = l2,
                R2 = r2,
                Touch0 = BuildMappedTouch(state.TrackPadTouch0, 1920, 1080),
                Touch1 = BuildMappedTouch(state.TrackPadTouch1, 1920, 1080),
                RawInputStatus = state.DualSenseRawInputStatus,
            };

            SixAxis motion = state.Motion;
            if (motion == null)
            {
                mapped.AccelZ = DualSenseAccelRestZ;
                return mapped;
            }

            mapped.GyroX = ClampShort(SnapToZero(motion.gyroPitchFull,
                DualSenseGyroRestDeadband));
            mapped.GyroY = ClampShort(SnapToZero(-motion.gyroYawFull,
                DualSenseGyroRestDeadband));
            mapped.GyroZ = ClampShort(SnapToZero(-motion.gyroRollFull,
                DualSenseGyroRestDeadband));
            mapped.AccelX = ClampShort(-motion.accelXFull);
            mapped.AccelY = ClampShort(-motion.accelYFull);
            mapped.AccelZ = ClampShort(motion.accelZFull);
            if (mapped.AccelX == 0 && mapped.AccelY == 0 &&
                mapped.AccelZ == 0)
            {
                mapped.AccelZ = DualSenseAccelRestZ;
            }
            return mapped;
        }

        /// <summary>
        /// Serializes a mapped DualSense state into caller-owned storage.
        /// </summary>
        internal static void BuildInto(in ViiperMappedInputState mapped,
            Span<byte> destination)
        {
            BuildInto(mapped, destination, includeRawInputStatus: false);
        }

        /// <summary>
        /// Serializes the legacy mapped state and, for an explicitly
        /// negotiated V5-raw-input stream, the same-report physical sensor and
        /// status observation. Legacy peers continue to receive exactly 33
        /// bytes.
        /// </summary>
        internal static void BuildInto(in ViiperMappedInputState mapped,
            Span<byte> destination, bool includeRawInputStatus)
        {
            int packetSize = GetDualSenseInputPacketSize(
                includeRawInputStatus);
            if (destination.Length < packetSize)
            {
                throw new ArgumentException(
                    $"A DualSense input packet needs {packetSize} bytes.",
                    nameof(destination));
            }

            destination[0] = ToSignedAxisByte(mapped.LX);
            destination[1] = ToSignedAxisByte(mapped.LY);
            destination[2] = ToSignedAxisByte(mapped.RX);
            destination[3] = ToSignedAxisByte(mapped.RY);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4),
                mapped.Buttons);
            destination[8] = mapped.DPad;
            destination[9] = mapped.L2;
            destination[10] = mapped.R2;
            WriteMappedTouch(destination.Slice(11, 5), mapped.Touch0);
            WriteMappedTouch(destination.Slice(16, 5), mapped.Touch1);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(21, 2),
                mapped.GyroX);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(23, 2),
                mapped.GyroY);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(25, 2),
                mapped.GyroZ);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(27, 2),
                mapped.AccelX);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(29, 2),
                mapped.AccelY);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(31, 2),
                mapped.AccelZ);

            if (!includeRawInputStatus)
            {
                return;
            }

            Span<byte> extension = destination.Slice(
                DualSenseRawInputStatusFlagsOffset,
                DualSenseRawInputStatusPacketSize -
                    DualSenseRawInputStatusFlagsOffset);
            extension.Clear();
            if (!mapped.RawInputStatus.IsValid)
            {
                return;
            }

            destination[DualSenseRawInputStatusFlagsOffset] =
                (byte)(DualSenseRawInputStatusValidFlag |
                    (mapped.RawInputStatus.IsEdgeLayout ?
                        DualSenseRawInputStatusEdgeLayoutFlag : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(
                DualSenseRawInputSensorTimestampOffset, sizeof(uint)),
                mapped.RawInputStatus.SensorTimestamp);
            mapped.RawInputStatus.WriteStatusBytes(destination.Slice(
                DualSenseRawInputStatusOffset,
                DualSenseRawInputStatus.StatusByteCount));
        }

        private static ViiperMappedTouchState BuildMappedTouch(
            DS4State.TrackPadTouch touch, int maxX, int maxY)
        {
            return new ViiperMappedTouchState
            {
                X = (ushort)Math.Clamp(touch.X, 0, maxX),
                Y = (ushort)Math.Clamp(touch.Y, 0, maxY),
                TrackingId = (byte)(touch.RawTrackingNum & 0x7f),
                IsActive = touch.IsActive,
            };
        }

        private static void WriteMappedTouch(Span<byte> destination,
            in ViiperMappedTouchState touch)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(0, 2),
                touch.X);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2),
                touch.Y);
            destination[4] = touch.IsActive ?
                (byte)(touch.TrackingId & 0x7f) :
                (byte)(touch.TrackingId | 0x80);
        }

        private static byte[] BuildSwitch2Pro(DS4State state, int device)
        {
            byte[] packet = new byte[Switch2PacketSize];
            BuildSwitch2State(state, device).BuildInto(packet);
            return packet;
        }

        internal static Switch2EgressState BuildSwitch2State(DS4State state,
            int device)
        {
            ushort lx = ScaleMappedSwitchAxis(state.LXAxis);
            ushort ly = ScaleMappedSwitchAxis(state.LYAxis);
            ushort rx = ScaleMappedSwitchAxis(state.RXAxis);
            ushort ry = ScaleMappedSwitchAxis(state.RYAxis);
            ApplySteeringWheelSwitchAxes(state, device, ref lx, ref ly, ref rx, ref ry);

            return new Switch2EgressState(BuildSwitch2Buttons(state),
                lx, ly, rx, ry,
                ClampShort(state.Motion?.accelXFull ?? 0),
                ClampShort(state.Motion?.accelYFull ?? 0),
                ClampShort(state.Motion?.accelZFull ?? 0),
                ClampShort(state.Motion?.gyroYawFull ?? 0),
                ClampShort(state.Motion?.gyroPitchFull ?? 0),
                ClampShort(state.Motion?.gyroRollFull ?? 0));
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

        // Only final mapping-owned values cross this boundary. Legacy sources
        // retain their exact historical float/byte conversion; precise sources
        // quantize once to the target's wire domain. Steering overrides below
        // the call sites remain authoritative.
        private static short ScaleMappedXboxAxis(in DS4MappedStickAxis value, bool flip) =>
            value.IsHighResolution ? value.ToSigned16(flip) : AxisScaleX360(value.LegacyValue, flip);

        private static ushort ScaleMappedSwitchAxis(in DS4MappedStickAxis value) =>
            value.IsHighResolution ? value.ToUnsigned12() : ScaleSwitchAxis(value.LegacyValue);

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
            // Preserve all three exact anchors of the asymmetric byte range:
            // 0 -> 0, 128 -> the protocol's 0x0800 center, 255 -> 0x0fff.
            if (value <= 128)
            {
                return (ushort)(value * 16);
            }

            return (ushort)(0x0800 +
                ((value - 128) * 0x07ff + 63) / 127);
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
