/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Concentus;
using DS4Windows.InputDevices;
using NLog;

namespace DS4Windows
{
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
        private static readonly Logger viiperMicDiagLogger = LogManager.GetLogger("ViiperMicDiag");

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
        private const int DualSenseMicrophoneOpusFrameLength = 71;
        private const int DualSenseMicrophoneSampleRate = 48000;
        private const int DualSenseMicrophoneChannels = 2;
        private const int DualSenseMicrophoneFramesPerPacket = 480;
        private const int DualSenseMicrophonePcmFrameLength = DualSenseMicrophoneFramesPerPacket *
            DualSenseMicrophoneChannels * sizeof(short);
        private const int MaxPendingMicrophoneFrames = 8;
        private const int MaxMicrophoneDiagnosticFrames = 40000;
        private const int MaxStreamRecoveryAttempts = 2;
        private static readonly TimeSpan MicrophoneRearmCheckInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan MicrophoneStallRearmThreshold = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MicrophoneRearmAttemptInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MicrophoneRearmLogInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MicrophoneHealthLogInterval = TimeSpan.FromSeconds(2);
        private const byte ViiperStreamFrameInputState = 0x01;
        private const byte ViiperStreamFrameMicrophonePcm = 0x02;

        private readonly OutContType outputType;
        private readonly ViiperVirtualDeviceType viiperType;
        private readonly ViiperClient client;
        private readonly object pendingPacketLock = new object();
        private readonly object writerThreadLock = new object();
        private readonly object streamRecoveryLock = new object();
        private readonly object physicalDualSenseIdentityLock = new object();
        private readonly object microphoneSourceLock = new object();
        private readonly AutoResetEvent writerSignal = new AutoResetEvent(false);
        private readonly Queue<byte[]> pendingMicrophoneOpusFrames = new Queue<byte[]>(MaxPendingMicrophoneFrames);
        private readonly short[] microphoneMonoPcm = new short[DualSenseMicrophoneFramesPerPacket];
        private readonly byte[] microphoneStereoPcm = new byte[DualSenseMicrophonePcmFrameLength];
        private ViiperDeviceStream deviceStream;
        private Thread feedbackThread;
        private Thread stateWriterThread;
        private Thread microphoneRearmThread;
        private IOpusDecoder microphoneDecoder;
        private DualSenseDevice microphoneSourceDevice;
        private byte[] pendingStatePacket;
        private volatile bool writerStopRequested;
        private int streamRecoveryAttempts;
        private DateTime lastStreamRecoveryAttemptUtc = DateTime.MinValue;
        private long replacedPendingPacketCount;
        private long submittedPacketCount;
        private long writtenPacketCount;
        private DateTime lastWriterHealthLogUtc = DateTime.MinValue;
        private int lastInputDeviceIndex = -1;
        private int submitFailureLogged;
        private int edgePhysicalMismatchLogged;
        private int activeFeedbackLength;
        private bool activeStreamUsesFramedProtocol;
        private bool activeStreamSupportsMicrophone;
        private int microphoneUnavailableLogged;
        private int microphoneVolume = 128;
        private string physicalDualSenseIdentityPath;
        private bool physicalDualSenseIdentityVerified;
        private volatile bool microphoneRearmStopRequested;
        private int microphoneEndpointActive;
        private int microphonePhysicalStreamingEnabled;
        private int microphoneVerboseGateOverrideLogged;
        private long lastMicrophoneOpusFrameUtcTicks;
        private long lastMicrophoneRearmAttemptUtcTicks;
        private DateTime lastMicrophoneRearmLogUtc = DateTime.MinValue;
        private DateTime lastMicrophoneHealthLogUtc = DateTime.MinValue;
        private long queuedMicrophoneFrameCount;
        private long droppedMicrophoneFrameCount;
        private long writtenMicrophoneFrameCount;
        private long microphoneRearmAttemptCount;
        private long microphoneDecodeFailureCount;
        private long inputPacketDiagnosticCount;
        private long microphonePacketDiagnosticCount;
        private readonly byte[] lastR2TriggerFeedback = new byte[DualSenseTriggerEffectLength];
        private readonly byte[] lastL2TriggerFeedback = new byte[DualSenseTriggerEffectLength];

        public ViiperOutDevice(OutContType outputType, ViiperVirtualDeviceType viiperType)
        {
            this.outputType = outputType;
            this.viiperType = viiperType;
            client = new ViiperClient(DefaultHost, DefaultPort);
        }

        public override void Connect()
        {
            Disconnect();

            ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
            if (!status.Ready)
            {
                throw new IOException(
                    $"{status.DisplayText}. Use Settings > VIIPER Virtual Controller Support to install or repair VIIPER and usbip-win2.");
            }

            deviceStream = CreateDeviceStreamWithServerFallback();
            Volatile.Write(ref submitFailureLogged, 0);
            Volatile.Write(ref lastInputDeviceIndex, -1);
            streamRecoveryAttempts = 0;
            lastStreamRecoveryAttemptUtc = DateTime.MinValue;
            Interlocked.Exchange(ref replacedPendingPacketCount, 0);
            Interlocked.Exchange(ref submittedPacketCount, 0);
            Interlocked.Exchange(ref writtenPacketCount, 0);
            Interlocked.Exchange(ref queuedMicrophoneFrameCount, 0);
            Interlocked.Exchange(ref droppedMicrophoneFrameCount, 0);
            Interlocked.Exchange(ref writtenMicrophoneFrameCount, 0);
            Interlocked.Exchange(ref microphoneDecodeFailureCount, 0);
            Interlocked.Exchange(ref lastMicrophoneOpusFrameUtcTicks, 0);
            Interlocked.Exchange(ref lastMicrophoneRearmAttemptUtcTicks, 0);
            Interlocked.Exchange(ref microphoneRearmAttemptCount, 0);
            Interlocked.Exchange(ref microphoneEndpointActive, 0);
            Interlocked.Exchange(ref microphonePhysicalStreamingEnabled, 0);
            Interlocked.Exchange(ref microphoneVerboseGateOverrideLogged, 0);
            Volatile.Write(ref edgePhysicalMismatchLogged, 0);
            Volatile.Write(ref microphoneUnavailableLogged, 0);
            lock (physicalDualSenseIdentityLock)
            {
                physicalDualSenseIdentityPath = null;
                physicalDualSenseIdentityVerified = false;
            }
            lastWriterHealthLogUtc = DateTime.MinValue;
            lastMicrophoneRearmLogUtc = DateTime.MinValue;
            lastMicrophoneHealthLogUtc = DateTime.MinValue;
            microphoneRearmStopRequested = false;
            writerStopRequested = false;
            connected = true;
            StartStateWriter();
            ResetState();
            StartFeedbackReader();
        }

        private ViiperDeviceStream CreateDeviceStream()
        {
            activeStreamUsesFramedProtocol = false;
            activeStreamSupportsMicrophone = false;

            if (viiperType == ViiperVirtualDeviceType.DualSense)
            {
                try
                {
                    ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsensecombinedmicext");
                    activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                    activeStreamUsesFramedProtocol = true;
                    activeStreamSupportsMicrophone = true;
                    return stream;
                }
                catch (IOException ex)
                {
                    AppLogger.LogToGui($"VIIPER DualSense microphone endpoint unavailable, using combined feedback without mic-in: {ex.Message}", false);
                }

                try
                {
                    ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsensecombinedext");
                    activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                    return stream;
                }
                catch (IOException ex)
                {
                    try
                    {
                        AppLogger.LogToGui($"VIIPER DualSense combined haptics feedback unavailable, using legacy extended feedback: {ex.Message}", false);
                        ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsenseext");
                        activeFeedbackLength = DualSenseExtendedFeedbackLength;
                        return stream;
                    }
                    catch (IOException legacyEx)
                    {
                        AppLogger.LogToGui($"VIIPER DualSense adaptive trigger feedback unavailable, falling back to base DualSense output: {legacyEx.Message}", false);
                        activeFeedbackLength = DualSenseBaseFeedbackLength;
                        return client.CreateDeviceAndOpenStream("dualsense");
                    }
                }
            }

            if (viiperType == ViiperVirtualDeviceType.DualSenseEdge)
            {
                try
                {
                    ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsenseedgecombinedmicext");
                    activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                    activeStreamUsesFramedProtocol = true;
                    activeStreamSupportsMicrophone = true;
                    return stream;
                }
                catch (IOException ex)
                {
                    AppLogger.LogToGui($"VIIPER DualSense Edge microphone endpoint unavailable, using combined feedback without mic-in: {ex.Message}", false);
                }

                try
                {
                    ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsenseedgecombinedext");
                    activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                    return stream;
                }
                catch (IOException ex)
                {
                    try
                    {
                        AppLogger.LogToGui($"VIIPER DualSense Edge combined haptics feedback unavailable, using legacy extended feedback: {ex.Message}", false);
                        ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsenseedgeext");
                        activeFeedbackLength = DualSenseExtendedFeedbackLength;
                        return stream;
                    }
                    catch (IOException legacyEx)
                    {
                        AppLogger.LogToGui($"VIIPER DualSense Edge adaptive trigger feedback unavailable, falling back to base DualSense Edge output: {legacyEx.Message}", false);
                        activeFeedbackLength = DualSenseBaseFeedbackLength;
                        return client.CreateDeviceAndOpenStream("dualsenseedge");
                    }
                }
            }

            activeFeedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(viiperType);
            return client.CreateDeviceAndOpenStream(viiperType);
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
            connected = false;
            writerStopRequested = true;
            microphoneRearmStopRequested = true;
            writerSignal.Set();
            DetachBluetoothMicrophoneSource();
            lock (pendingPacketLock)
            {
                pendingStatePacket = null;
                pendingMicrophoneOpusFrames.Clear();
            }

            lock (streamRecoveryLock)
            {
                ViiperDeviceStream stream = Interlocked.Exchange(ref deviceStream, null);
                stream?.Dispose();
            }

            if (stateWriterThread != null && stateWriterThread.IsAlive)
            {
                if (Thread.CurrentThread.ManagedThreadId != stateWriterThread.ManagedThreadId)
                {
                    stateWriterThread.Join(500);
                }
            }

            stateWriterThread = null;
            StopMicrophoneRearmThread();
            StopFeedbackReader();
            ViiperUsbipPortManager.DetachStaleLocalViiperPorts();
        }

        private void StopFeedbackReader()
        {
            if (feedbackThread != null && feedbackThread.IsAlive)
            {
                if (Thread.CurrentThread.ManagedThreadId != feedbackThread.ManagedThreadId)
                {
                    feedbackThread.Join(500);
                }
            }

            feedbackThread = null;
        }

        public override void ConvertandSendReport(DS4State state, int device)
        {
            Volatile.Write(ref lastInputDeviceIndex, device);
            UpdateBluetoothMicrophoneSource(device);
            if (!connected)
            {
                return;
            }

            try
            {
                byte[] packet = ViiperStatePacketBuilder.Build(viiperType, state, device);
                LogInputPacketDiagnostic(state, packet, device);
                QueueStatePacket(packet);
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
                QueueStatePacket(ViiperStatePacketBuilder.Build(viiperType, new DS4State(), -1));
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

        private void QueueStatePacket(byte[] data)
        {
            lock (pendingPacketLock)
            {
                if (pendingStatePacket != null)
                {
                    Interlocked.Increment(ref replacedPendingPacketCount);
                }

                pendingStatePacket = data;
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

            if (stateWriterThread == null || !stateWriterThread.IsAlive)
            {
                StartStateWriter();
            }
        }

        private void StartStateWriter()
        {
            lock (writerThreadLock)
            {
                if (stateWriterThread != null && stateWriterThread.IsAlive)
                {
                    return;
                }

                stateWriterThread = new Thread(StateWriteLoop)
                {
                    IsBackground = true,
                    Name = $"VIIPER {viiperType} writer",
                };
                stateWriterThread.Start();
            }
        }

        private void StateWriteLoop()
        {
            while (!writerStopRequested)
            {
                writerSignal.WaitOne();
                if (writerStopRequested)
                {
                    return;
                }

                while (!writerStopRequested)
                {
                    byte[] packet;
                    byte[] microphoneOpusFrame;
                    lock (pendingPacketLock)
                    {
                        packet = pendingStatePacket;
                        pendingStatePacket = null;
                        microphoneOpusFrame = pendingMicrophoneOpusFrames.Count > 0 ?
                            pendingMicrophoneOpusFrames.Dequeue() : null;
                    }

                    if (packet == null && microphoneOpusFrame == null)
                    {
                        break;
                    }

                    try
                    {
                        if (packet != null)
                        {
                            WriteState(packet);
                            Interlocked.Increment(ref writtenPacketCount);
                        }

                        if (microphoneOpusFrame != null)
                        {
                            WriteMicrophoneOpusFrame(microphoneOpusFrame);
                        }

                        streamRecoveryAttempts = 0;
                        LogWriterHealthIfNeeded();
                    }
                    catch (IOException ex)
                    {
                        if (TryRecoverStream(ex.Message, packet))
                        {
                            continue;
                        }

                        LogSubmitFailure(ex.Message);
                        return;
                    }
                    catch (SocketException ex)
                    {
                        if (TryRecoverStream(ex.Message, packet))
                        {
                            continue;
                        }

                        LogSubmitFailure(ex.Message);
                        return;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        if (!writerStopRequested)
                        {
                            if (TryRecoverStream(ex.Message, packet))
                            {
                                continue;
                            }

                            LogSubmitFailure(ex.Message);
                        }

                        return;
                    }
                }
            }
        }

        private bool TryRecoverStream(string reason, byte[] packetToRetry = null)
        {
            if (writerStopRequested || !connected)
            {
                return false;
            }

            lock (streamRecoveryLock)
            {
                if (writerStopRequested || !connected)
                {
                    return false;
                }

                DateTime now = DateTime.UtcNow;
                if (now - lastStreamRecoveryAttemptUtc < TimeSpan.FromSeconds(2))
                {
                    return false;
                }

                if (streamRecoveryAttempts >= MaxStreamRecoveryAttempts)
                {
                    return false;
                }

                streamRecoveryAttempts++;
                lastStreamRecoveryAttemptUtc = now;
                AppLogger.LogToGui(
                    $"VIIPER {viiperType} stream interrupted; attempting recovery {streamRecoveryAttempts}/{MaxStreamRecoveryAttempts}: {reason}",
                    true);

                try
                {
                    ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
                    if (!status.Ready)
                    {
                        AppLogger.LogToGui($"VIIPER {viiperType} recovery failed: {status.DisplayText}", true);
                        return false;
                    }

                    ViiperDeviceStream oldStream = Interlocked.Exchange(ref deviceStream, null);
                    oldStream?.Dispose();
                    StopFeedbackReader();

                    deviceStream = CreateDeviceStreamWithServerFallback();
                    StartFeedbackReader();

                    if (packetToRetry != null)
                    {
                        lock (pendingPacketLock)
                        {
                            pendingStatePacket = packetToRetry;
                        }

                        writerSignal.Set();
                    }

                    AppLogger.LogToGui($"VIIPER {viiperType} stream recovered.", false);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.LogToGui($"VIIPER {viiperType} recovery failed: {ex.Message}", true);
                    return false;
                }
            }
        }

        private void WriteState(byte[] data)
        {
            ViiperDeviceStream stream = deviceStream;
            if (stream == null)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            if (activeStreamUsesFramedProtocol)
            {
                stream.WriteFrame(ViiperStreamFrameInputState, data);
            }
            else
            {
                stream.Write(data);
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

            ViiperDeviceStream stream = deviceStream;
            if (stream == null)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            microphoneDecoder ??= OpusCodecFactory.CreateDecoder(DualSenseMicrophoneSampleRate, 1);
            int decodedSamples;
            try
            {
                decodedSamples = microphoneDecoder.Decode(opusFrame.AsSpan(),
                    microphoneMonoPcm.AsSpan(), DualSenseMicrophoneFramesPerPacket, false);
            }
            catch
            {
                Interlocked.Increment(ref microphoneDecodeFailureCount);
                throw;
            }

            if (decodedSamples <= 0)
            {
                Interlocked.Increment(ref microphoneDecodeFailureCount);
                return;
            }

            Array.Clear(microphoneStereoPcm, 0, microphoneStereoPcm.Length);
            int frames = Math.Min(decodedSamples, DualSenseMicrophoneFramesPerPacket);
            float gain = Math.Clamp(Volatile.Read(ref microphoneVolume) / 255.0f, 0.0f, 1.0f);
            for (int frame = 0; frame < frames; frame++)
            {
                short sample = (short)Math.Clamp(microphoneMonoPcm[frame] * gain,
                    (float)short.MinValue, short.MaxValue);
                int offset = frame * DualSenseMicrophoneChannels * sizeof(short);
                microphoneStereoPcm[offset] = (byte)sample;
                microphoneStereoPcm[offset + 1] = (byte)(sample >> 8);
                microphoneStereoPcm[offset + 2] = (byte)sample;
                microphoneStereoPcm[offset + 3] = (byte)(sample >> 8);
            }

            LogMicrophonePacketDiagnostic(opusFrame, decodedSamples, frames);
            long writeStartTicks = Stopwatch.GetTimestamp();
            try
            {
                stream.WriteFrame(ViiperStreamFrameMicrophonePcm, microphoneStereoPcm);
                Interlocked.Increment(ref writtenMicrophoneFrameCount);
                LogMicrophonePcmWriteDiagnostic(writeStartTicks, null);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                LogMicrophonePcmWriteDiagnostic(writeStartTicks, ex);
                throw;
            }
        }

        private void LogMicrophonePacketDiagnostic(byte[] opusFrame, int decodedSamples, int pcmFrames)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            long count = Interlocked.Increment(ref microphonePacketDiagnosticCount);
            if (count > MaxMicrophoneDiagnosticFrames)
            {
                return;
            }

            viiperMicDiagLogger.Info(
                $"VIIPER_MIC_DIAG type={viiperType} utc={DateTime.UtcNow:O} ticks={Stopwatch.GetTimestamp()} count={count} opusLen={opusFrame?.Length ?? 0} decodedSamples={decodedSamples} pcmFrames={pcmFrames} pcmBytes={DualSenseMicrophonePcmFrameLength} queued={Interlocked.Read(ref queuedMicrophoneFrameCount)} written={Interlocked.Read(ref writtenMicrophoneFrameCount)} dropped={Interlocked.Read(ref droppedMicrophoneFrameCount)} decodeFailures={Interlocked.Read(ref microphoneDecodeFailureCount)} pcmStats={SummarizePcm16Stereo(microphoneStereoPcm, pcmFrames)} opus={FormatBytes(opusFrame, 71)} pcmFirst64={FormatBytes(microphoneStereoPcm, 64)}");
        }

        private void LogMicrophonePcmWriteDiagnostic(long writeStartTicks, Exception exception)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            long written = Interlocked.Read(ref writtenMicrophoneFrameCount);
            if (written > MaxMicrophoneDiagnosticFrames)
            {
                return;
            }

            double elapsedMs = (Stopwatch.GetTimestamp() - writeStartTicks) * 1000.0 / Stopwatch.Frequency;
            viiperMicDiagLogger.Info(
                $"VIIPER_MIC_PCM_WRITE type={viiperType} utc={DateTime.UtcNow:O} ticks={Stopwatch.GetTimestamp()} frameType=0x{ViiperStreamFrameMicrophonePcm:X2} payloadBytes={microphoneStereoPcm.Length} headerBytes=8 queued={Interlocked.Read(ref queuedMicrophoneFrameCount)} written={written} dropped={Interlocked.Read(ref droppedMicrophoneFrameCount)} pending={GetPendingMicrophoneFrameCount()} elapsedMs={elapsedMs:F3} success={exception == null} exception={exception?.GetType().Name}:{exception?.Message} pcmStats={SummarizePcm16Stereo(microphoneStereoPcm, DualSenseMicrophoneFramesPerPacket)}");
        }

        private void LogWriterHealthIfNeeded()
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            long replaced = Interlocked.Read(ref replacedPendingPacketCount);
            if (replaced == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now - lastWriterHealthLogUtc < TimeSpan.FromSeconds(5))
            {
                return;
            }

            lastWriterHealthLogUtc = now;
            AppLogger.LogToGui(
                $"VIIPER {viiperType} writer stats: submitted={Interlocked.Read(ref submittedPacketCount)} written={Interlocked.Read(ref writtenPacketCount)} coalesced={replaced} micQueued={Interlocked.Read(ref queuedMicrophoneFrameCount)} micWritten={Interlocked.Read(ref writtenMicrophoneFrameCount)} micDropped={Interlocked.Read(ref droppedMicrophoneFrameCount)} micDecodeFailures={Interlocked.Read(ref microphoneDecodeFailureCount)}",
                false);
        }

        private void StartFeedbackReader()
        {
            int length = activeFeedbackLength > 0 ? activeFeedbackLength : ViiperStatePacketBuilder.GetFeedbackLength(viiperType);
            if (length <= 0)
            {
                return;
            }

            feedbackThread = new Thread(() => FeedbackReadLoop(length))
            {
                IsBackground = true,
                Name = $"VIIPER {viiperType} feedback",
            };
            feedbackThread.Start();
        }

        private void FeedbackReadLoop(int feedbackLength)
        {
            int bufferLength = IsDualSenseType() ? Math.Max(feedbackLength, DualSenseCombinedExtendedFeedbackLength) : feedbackLength;
            byte[] buffer = new byte[bufferLength];
            try
            {
                while (connected)
                {
                    ViiperDeviceStream stream = deviceStream;
                    if (stream == null)
                    {
                        return;
                    }

                    stream.ReadExactly(buffer, 0, feedbackLength);
                    ApplyFeedback(buffer, feedbackLength);
                }
            }
            catch (IOException)
            {
                if (connected && !TryRecoverStream("feedback reader stopped"))
                {
                    AppLogger.LogToGui($"VIIPER {viiperType} feedback reader stopped.", true);
                }
            }
            catch (SocketException)
            {
                if (connected && !TryRecoverStream("feedback reader stopped due to socket error"))
                {
                    AppLogger.LogToGui($"VIIPER {viiperType} feedback reader stopped due to socket error.", true);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ApplyFeedback(byte[] feedback, int feedbackLength)
        {
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if (deviceIndex < 0 ||
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

            switch (viiperType)
            {
                case ViiperVirtualDeviceType.Xbox360:
                    if (feedbackLength >= 2)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[0], feedback[1], deviceIndex);
                    }
                    break;

                case ViiperVirtualDeviceType.DualShock4:
                    if (feedbackLength >= 7)
                    {
                        Program.rootHub.SetDevRumble(device, feedback[1], feedback[0], deviceIndex);
                        ApplyLightbar(device, feedback[2], feedback[3], feedback[4], feedback[5], feedback[6]);
                    }
                    break;

                case ViiperVirtualDeviceType.DualSense:
                case ViiperVirtualDeviceType.DualSenseEdge:
                    if (feedbackLength >= DualSenseBaseFeedbackLength)
                    {
                        bool nativeForwardingAllowed = IsNativeDualSenseFeedbackCompatible(device);
                        if (nativeForwardingAllowed &&
                            TryApplyBluetoothCombinedHapticsOutputReport(device, feedback, feedbackLength))
                        {
                            break;
                        }

                        if (nativeForwardingAllowed &&
                            TryApplyBluetoothHapticsOutputReport(device, feedback, feedbackLength,
                                waitForWrite: false))
                        {
                            break;
                        }

                        if (nativeForwardingAllowed &&
                            TryApplyNativeDualSenseOutputReport(device, deviceIndex, feedback, feedbackLength))
                        {
                            break;
                        }

                        Program.rootHub.SetDevRumble(device, feedback[1], feedback[0], deviceIndex);
                        ApplyLightbar(device, feedback[2], feedback[3], feedback[4], 0, 0);
                        ApplyDualSenseTriggerFeedback(device, feedback, feedbackLength);
                    }
                    break;

                case ViiperVirtualDeviceType.Switch2Pro:
                    if (feedbackLength >= 34)
                    {
                        byte left = MaxByte(feedback, 0, 16);
                        byte right = MaxByte(feedback, 16, 16);
                        Program.rootHub.SetDevRumble(device, left, right, deviceIndex);
                    }
                    break;
            }
        }

        private bool IsDualSenseType()
        {
            return viiperType == ViiperVirtualDeviceType.DualSense ||
                viiperType == ViiperVirtualDeviceType.DualSenseEdge;
        }

        private void UpdateBluetoothMicrophoneSource(int deviceIndex)
        {
            if (!connected)
            {
                DetachBluetoothMicrophoneSource();
                return;
            }

            bool microphoneRequested = deviceIndex >= 0 &&
                deviceIndex < Global.DualSenseEnableMicrophonePassthrough.Length &&
                Global.DualSenseEnableMicrophonePassthrough[deviceIndex];

            if (IsDualSenseType() &&
                microphoneRequested &&
                !DualSenseDevice.BluetoothMicrophoneInputTransportAvailable)
            {
                if (Interlocked.Exchange(ref microphoneUnavailableLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "VIIPER DualSense mic-in is not available in this build.",
                        true);
                }

                DetachBluetoothMicrophoneSource();
                return;
            }

            if (!IsDualSenseType() || !activeStreamSupportsMicrophone)
            {
                if (IsDualSenseType() &&
                    microphoneRequested &&
                    Interlocked.Exchange(ref microphoneUnavailableLogged, 1) == 0)
                {
                    AppLogger.LogToGui("VIIPER DualSense mic-in needs a newer VIIPER build with dualsensecombinedmicext support.", true);
                }

                DetachBluetoothMicrophoneSource();
                return;
            }

            if (!microphoneRequested)
            {
                DetachBluetoothMicrophoneSource();
                return;
            }

            Volatile.Write(ref microphoneVolume,
                deviceIndex < Global.DualSenseMicrophoneVolume.Length ?
                    Global.DualSenseMicrophoneVolume[deviceIndex] : 128);

            if (Program.rootHub == null ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length ||
                Program.rootHub.DS4Controllers[deviceIndex] is not DualSenseDevice dualSenseDevice ||
                dualSenseDevice.ConnectionType != ConnectionType.BT ||
                !IsCurrentPhysicalSonyDualSense(dualSenseDevice))
            {
                DetachBluetoothMicrophoneSource();
                return;
            }

            lock (microphoneSourceLock)
            {
                if (ReferenceEquals(microphoneSourceDevice, dualSenseDevice))
                {
                    return;
                }
            }

            DetachBluetoothMicrophoneSource();
            lock (microphoneSourceLock)
            {
                microphoneSourceDevice = dualSenseDevice;
                microphoneSourceDevice.BluetoothMicrophoneOpusFrameReceived += BluetoothMicrophoneOpusFrameReceived;
            }

            dualSenseDevice.ResetBluetoothMicrophoneProbeStatistics();
            Interlocked.Exchange(ref lastMicrophoneOpusFrameUtcTicks, 0);
            Interlocked.Exchange(ref lastMicrophoneRearmAttemptUtcTicks, 0);
            Interlocked.Exchange(ref microphoneRearmAttemptCount, 0);
            Interlocked.Exchange(ref microphoneEndpointActive, 0);
            Interlocked.Exchange(ref microphonePhysicalStreamingEnabled, 0);
            try
            {
                dualSenseDevice.SetBluetoothMicrophoneStreaming(false);
            }
            catch { }

            StartMicrophoneRearmThread();
            if (Global.VerboseStartupLogging)
            {
                AppLogger.LogToGui(
                    $"VIIPER DualSense mic-in armed on controller {deviceIndex + 1}; waiting for Windows to open the virtual microphone endpoint.",
                    false);
            }
        }

        private void DetachBluetoothMicrophoneSource()
        {
            DualSenseDevice oldSource = null;
            lock (microphoneSourceLock)
            {
                if (microphoneSourceDevice != null)
                {
                    oldSource = microphoneSourceDevice;
                    microphoneSourceDevice.BluetoothMicrophoneOpusFrameReceived -= BluetoothMicrophoneOpusFrameReceived;
                    microphoneSourceDevice = null;
                }
            }

            lock (pendingPacketLock)
            {
                pendingMicrophoneOpusFrames.Clear();
            }

            microphoneDecoder = null;
            Interlocked.Exchange(ref lastMicrophoneOpusFrameUtcTicks, 0);
            Interlocked.Exchange(ref lastMicrophoneRearmAttemptUtcTicks, 0);
            Interlocked.Exchange(ref microphoneRearmAttemptCount, 0);
            Interlocked.Exchange(ref microphoneEndpointActive, 0);
            Interlocked.Exchange(ref microphonePhysicalStreamingEnabled, 0);
            Interlocked.Exchange(ref microphoneVerboseGateOverrideLogged, 0);
            StopMicrophoneRearmThread();

            if (oldSource != null)
            {
                try
                {
                    oldSource.SetBluetoothMicrophoneStreaming(false);
                    if (Global.VerboseStartupLogging)
                    {
                        AppLogger.LogToGui(
                            $"VIIPER DualSense mic-in disabled: status={oldSource.LastBluetoothMicrophoneWriteStatus}",
                            false);
                    }
                }
                catch { }
            }
        }

        private void BluetoothMicrophoneOpusFrameReceived(DualSenseDevice source, byte[] opusFrame)
        {
            lock (microphoneSourceLock)
            {
                if (!connected || !ReferenceEquals(source, microphoneSourceDevice))
                {
                    return;
                }
            }

            if (opusFrame == null || opusFrame.Length != DualSenseMicrophoneOpusFrameLength)
            {
                return;
            }
            if (Volatile.Read(ref microphoneEndpointActive) == 0 &&
                !IsVerboseMicrophoneGateOverrideEnabled())
            {
                return;
            }

            Interlocked.Exchange(ref lastMicrophoneOpusFrameUtcTicks, DateTime.UtcNow.Ticks);
            byte[] copy = new byte[DualSenseMicrophoneOpusFrameLength];
            Buffer.BlockCopy(opusFrame, 0, copy, 0, copy.Length);
            lock (pendingPacketLock)
            {
                while (pendingMicrophoneOpusFrames.Count >= MaxPendingMicrophoneFrames)
                {
                    pendingMicrophoneOpusFrames.Dequeue();
                    Interlocked.Increment(ref droppedMicrophoneFrameCount);
                }

                pendingMicrophoneOpusFrames.Enqueue(copy);
            }

            Interlocked.Increment(ref queuedMicrophoneFrameCount);
            LogMicrophoneQueueDiagnostic(source, copy);
            EnsureStateWriterAlive();
            writerSignal.Set();
        }

        private void StartMicrophoneRearmThread()
        {
            lock (microphoneSourceLock)
            {
                if (microphoneRearmThread != null && microphoneRearmThread.IsAlive)
                {
                    return;
                }

                microphoneRearmStopRequested = false;
                microphoneRearmThread = new Thread(MicrophoneRearmLoop)
                {
                    IsBackground = true,
                    Name = $"VIIPER {viiperType} mic rearm",
                };
                microphoneRearmThread.Start();
            }
        }

        private void StopMicrophoneRearmThread()
        {
            Thread thread = null;
            lock (microphoneSourceLock)
            {
                microphoneRearmStopRequested = true;
                thread = microphoneRearmThread;
                microphoneRearmThread = null;
            }

            if (thread != null &&
                thread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != thread.ManagedThreadId)
            {
                thread.Join(500);
            }
        }

        private void MicrophoneRearmLoop()
        {
            while (!microphoneRearmStopRequested && connected)
            {
                Thread.Sleep(MicrophoneRearmCheckInterval);
                if (microphoneRearmStopRequested || !connected)
                {
                    return;
                }

                DualSenseDevice source;
                lock (microphoneSourceLock)
                {
                    source = microphoneSourceDevice;
                }

                if (source == null)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                bool endpointStateKnown = TryGetVirtualMicrophoneInterfaceActive(out bool endpointActive, out string endpointStatus);
                bool verboseGateOverride = IsVerboseMicrophoneGateOverrideEnabled();
                bool endpointGateOpen = endpointStateKnown && endpointActive;
                if (verboseGateOverride && !endpointGateOpen &&
                    Interlocked.Exchange(ref microphoneVerboseGateOverrideLogged, 1) == 0)
                {
                    viiperMicDiagLogger.Info(
                        $"VIIPER_MIC_VERBOSE_GATE_OVERRIDE type={viiperType} utc={now:O} actualActive={endpointActive} actualKnown={endpointStateKnown} status={endpointStatus} note=temporary_testing_override_remove_with_verbose_mic_logging");
                }
                else if (!verboseGateOverride)
                {
                    Interlocked.Exchange(ref microphoneVerboseGateOverrideLogged, 0);
                }

                if (!endpointGateOpen && !verboseGateOverride)
                {
                    bool wasEndpointActive = Interlocked.Exchange(ref microphoneEndpointActive, 0) == 1;
                    bool wasStreaming = Interlocked.Exchange(ref microphonePhysicalStreamingEnabled, 0) == 1;
                    Interlocked.Exchange(ref lastMicrophoneRearmAttemptUtcTicks, 0);
                    ClearPendingMicrophoneFrames();
                    if (wasStreaming)
                    {
                        try
                        {
                            source.SetBluetoothMicrophoneStreaming(false);
                        }
                        catch { }
                    }

                    LogMicrophoneHealthIfNeeded(now, DateTime.MinValue);
                    if (Global.VerboseStartupLogging &&
                        (wasEndpointActive || wasStreaming || !endpointStateKnown) &&
                        now - lastMicrophoneRearmLogUtc >= MicrophoneRearmLogInterval)
                    {
                        lastMicrophoneRearmLogUtc = now;
                        viiperMicDiagLogger.Info(
                            $"VIIPER_MIC_ENDPOINT type={viiperType} utc={now:O} active={endpointActive} known={endpointStateKnown} physicalStreaming=False status={endpointStatus}");
                    }

                    continue;
                }

                if (Interlocked.Exchange(ref microphoneEndpointActive, 1) == 0 &&
                    Global.VerboseStartupLogging)
                {
                    viiperMicDiagLogger.Info(
                        $"VIIPER_MIC_ENDPOINT type={viiperType} utc={now:O} active={endpointGateOpen} known={endpointStateKnown} verboseGateOverride={verboseGateOverride} physicalStreaming={Volatile.Read(ref microphonePhysicalStreamingEnabled) == 1} status={endpointStatus}");
                }

                if (Volatile.Read(ref microphonePhysicalStreamingEnabled) == 0)
                {
                    long previousAttemptTicks = Interlocked.Read(ref lastMicrophoneRearmAttemptUtcTicks);
                    DateTime previousAttemptUtc = previousAttemptTicks > 0 ?
                        new DateTime(previousAttemptTicks, DateTimeKind.Utc) : DateTime.MinValue;
                    if (previousAttemptUtc != DateTime.MinValue &&
                        now - previousAttemptUtc < MicrophoneRearmAttemptInterval)
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref lastMicrophoneRearmAttemptUtcTicks, now.Ticks);
                    long activationAttempt = Interlocked.Increment(ref microphoneRearmAttemptCount);
                    bool activationAccepted = false;
                    string activationStatus = string.Empty;
                    try
                    {
                        activationAccepted = source.SetBluetoothMicrophoneStreaming(true);
                        activationStatus = source.LastBluetoothMicrophoneWriteStatus;
                    }
                    catch (Exception ex)
                    {
                        activationStatus = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    if (activationAccepted)
                    {
                        Interlocked.Exchange(ref microphonePhysicalStreamingEnabled, 1);
                    }

                    if (Global.VerboseStartupLogging)
                    {
                        viiperMicDiagLogger.Info(
                            $"VIIPER_MIC_ACTIVATE type={viiperType} utc={now:O} attempt={activationAttempt} endpointActive={endpointGateOpen} verboseGateOverride={verboseGateOverride} accepted={activationAccepted} status={activationStatus}");
                    }

                    continue;
                }

                long lastFrameTicks = Interlocked.Read(ref lastMicrophoneOpusFrameUtcTicks);
                DateTime lastFrameUtc = lastFrameTicks > 0 ?
                    new DateTime(lastFrameTicks, DateTimeKind.Utc) : DateTime.MinValue;
                LogMicrophoneHealthIfNeeded(now, lastFrameUtc);
                if (lastFrameUtc != DateTime.MinValue &&
                    now - lastFrameUtc < MicrophoneStallRearmThreshold)
                {
                    continue;
                }

                long lastAttemptTicks = Interlocked.Read(ref lastMicrophoneRearmAttemptUtcTicks);
                DateTime lastAttemptUtc = lastAttemptTicks > 0 ?
                    new DateTime(lastAttemptTicks, DateTimeKind.Utc) : DateTime.MinValue;
                if (lastAttemptUtc != DateTime.MinValue &&
                    now - lastAttemptUtc < MicrophoneRearmAttemptInterval)
                {
                    continue;
                }

                Interlocked.Exchange(ref lastMicrophoneRearmAttemptUtcTicks, now.Ticks);
                long attempt = Interlocked.Increment(ref microphoneRearmAttemptCount);
                bool accepted = false;
                string status = string.Empty;
                try
                {
                    accepted = source.SetBluetoothMicrophoneStreaming(true);
                    status = source.LastBluetoothMicrophoneWriteStatus;
                }
                catch (Exception ex)
                {
                    status = $"{ex.GetType().Name}: {ex.Message}";
                }

                if (Global.VerboseStartupLogging &&
                    now - lastMicrophoneRearmLogUtc >= MicrophoneRearmLogInterval)
                {
                    lastMicrophoneRearmLogUtc = now;
                    viiperMicDiagLogger.Info(
                        $"VIIPER_MIC_REARM type={viiperType} utc={now:O} attempt={attempt} accepted={accepted} lastFrameUtc={(lastFrameUtc == DateTime.MinValue ? "<none>" : lastFrameUtc.ToString("O"))} queued={Interlocked.Read(ref queuedMicrophoneFrameCount)} written={Interlocked.Read(ref writtenMicrophoneFrameCount)} dropped={Interlocked.Read(ref droppedMicrophoneFrameCount)} pending={GetPendingMicrophoneFrameCount()} status={status}");
                }
            }
        }

        private bool TryGetVirtualMicrophoneInterfaceActive(out bool active, out string status)
        {
            active = false;
            status = string.Empty;

            ViiperDeviceStream stream = deviceStream;
            if (stream == null)
            {
                status = "no active VIIPER stream";
                return false;
            }

            try
            {
                return client.TryGetDualSenseMicrophoneInterfaceActive(stream.BusId, stream.DevId, out active, out status);
            }
            catch (Exception ex)
            {
                status = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool IsVerboseMicrophoneGateOverrideEnabled()
        {
            return Global.VerboseStartupLogging;
        }

        private void LogMicrophoneHealthIfNeeded(DateTime now, DateTime lastFrameUtc)
        {
            if (!Global.VerboseStartupLogging ||
                now - lastMicrophoneHealthLogUtc < MicrophoneHealthLogInterval)
            {
                return;
            }

            lastMicrophoneHealthLogUtc = now;
            double lastFrameAgeMs = lastFrameUtc == DateTime.MinValue ?
                -1.0 : (now - lastFrameUtc).TotalMilliseconds;
            DateTime lastAttemptUtc = DateTime.MinValue;
            long lastAttemptTicks = Interlocked.Read(ref lastMicrophoneRearmAttemptUtcTicks);
            if (lastAttemptTicks > 0)
            {
                lastAttemptUtc = new DateTime(lastAttemptTicks, DateTimeKind.Utc);
            }

            double nextRearmEligibleMs = lastAttemptUtc == DateTime.MinValue ?
                0.0 : Math.Max(0.0, (MicrophoneRearmAttemptInterval - (now - lastAttemptUtc)).TotalMilliseconds);
            viiperMicDiagLogger.Info(
                $"VIIPER_MIC_HEALTH type={viiperType} utc={now:O} endpointActive={Volatile.Read(ref microphoneEndpointActive) == 1} verboseGateOverride={IsVerboseMicrophoneGateOverrideEnabled()} physicalStreaming={Volatile.Read(ref microphonePhysicalStreamingEnabled) == 1} lastFrameAgeMs={lastFrameAgeMs:F0} queued={Interlocked.Read(ref queuedMicrophoneFrameCount)} written={Interlocked.Read(ref writtenMicrophoneFrameCount)} dropped={Interlocked.Read(ref droppedMicrophoneFrameCount)} pending={GetPendingMicrophoneFrameCount()} rearmAttempts={Interlocked.Read(ref microphoneRearmAttemptCount)} nextRearmEligibleMs={nextRearmEligibleMs:F0}");
        }

        private void LogMicrophoneQueueDiagnostic(DualSenseDevice source, byte[] opusFrame)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            long queued = Interlocked.Read(ref queuedMicrophoneFrameCount);
            if (queued > MaxMicrophoneDiagnosticFrames)
            {
                return;
            }

            int pending;
            lock (pendingPacketLock)
            {
                pending = pendingMicrophoneOpusFrames.Count;
            }

            viiperMicDiagLogger.Info(
                $"VIIPER_MIC_QUEUE_DIAG type={viiperType} utc={DateTime.UtcNow:O} ticks={Stopwatch.GetTimestamp()} source={source?.MacAddress} queued={queued} written={Interlocked.Read(ref writtenMicrophoneFrameCount)} dropped={Interlocked.Read(ref droppedMicrophoneFrameCount)} pending={pending} opusLen={opusFrame?.Length ?? 0} opus={FormatBytes(opusFrame, 71)}");
        }

        private int GetPendingMicrophoneFrameCount()
        {
            lock (pendingPacketLock)
            {
                return pendingMicrophoneOpusFrames.Count;
            }
        }

        private void ClearPendingMicrophoneFrames()
        {
            lock (pendingPacketLock)
            {
                pendingMicrophoneOpusFrames.Clear();
            }
        }

        private void LogInputPacketDiagnostic(DS4State state, byte[] packet, int device)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            long count = Interlocked.Increment(ref inputPacketDiagnosticCount);
            bool sonyPacket = viiperType == ViiperVirtualDeviceType.DualSense ||
                viiperType == ViiperVirtualDeviceType.DualSenseEdge ||
                viiperType == ViiperVirtualDeviceType.DualShock4;
            bool nonNeutral = IsNonNeutralState(state, packet);
            if (count > 512 && !nonNeutral)
            {
                return;
            }

            AppLogger.LogToGui(
                $"VIIPER_INPUT_DIAG type={viiperType} device={device + 1} count={count} nonNeutral={nonNeutral} stateButtons={SummarizeDs4State(state)} packet={SummarizeViiperPacket(packet)} first40={FormatBytes(packet, 40)} sonyPacket={sonyPacket}",
                false);
        }

        private static bool IsNonNeutralState(DS4State state, byte[] packet)
        {
            if (state == null)
            {
                return packet != null && packet.Length > 0;
            }

            return state.Cross || state.Circle || state.Square || state.Triangle ||
                state.DpadUp || state.DpadDown || state.DpadLeft || state.DpadRight ||
                state.L1 || state.R1 || state.L2Btn || state.R2Btn ||
                state.Share || state.Options || state.PS || state.Mute ||
                state.TouchButton || state.OutputTouchButton ||
                state.L3 || state.R3 ||
                state.L2 != 0 || state.R2 != 0 ||
                Math.Abs(state.LX - 128) > 4 || Math.Abs(state.LY - 128) > 4 ||
                Math.Abs(state.RX - 128) > 4 || Math.Abs(state.RY - 128) > 4 ||
                state.TrackPadTouch0.IsActive ||
                state.TrackPadTouch1.IsActive;
        }

        private static string SummarizeDs4State(DS4State state)
        {
            if (state == null)
            {
                return "<null>";
            }

            return $"lx={state.LX} ly={state.LY} rx={state.RX} ry={state.RY} l2={state.L2}/{state.L2Btn} r2={state.R2}/{state.R2Btn} dpad=U{state.DpadUp}D{state.DpadDown}L{state.DpadLeft}R{state.DpadRight} face=X{state.Cross}O{state.Circle}Sq{state.Square}Tr{state.Triangle} shoulders=L1{state.L1}R1{state.R1} sys=Sh{state.Share}Op{state.Options}PS{state.PS}Mute{state.Mute} touchBtn={state.TouchButton}/{state.OutputTouchButton} t0={state.TrackPadTouch0.IsActive}:{state.TrackPadTouch0.X},{state.TrackPadTouch0.Y} t1={state.TrackPadTouch1.IsActive}:{state.TrackPadTouch1.X},{state.TrackPadTouch1.Y}";
        }

        private string SummarizeViiperPacket(byte[] packet)
        {
            if (packet == null)
            {
                return "<null>";
            }

            if ((viiperType == ViiperVirtualDeviceType.DualSense ||
                    viiperType == ViiperVirtualDeviceType.DualSenseEdge) &&
                packet.Length >= 33)
            {
                uint buttons = BitConverter.ToUInt32(packet, 4);
                return $"ds lx={packet[0]} ly={packet[1]} rx={packet[2]} ry={packet[3]} buttons=0x{buttons:X8} dpad=0x{packet[8]:X2} l2={packet[9]} r2={packet[10]} touch0Status=0x{packet[15]:X2} touch1Status=0x{packet[20]:X2} gyro={BitConverter.ToInt16(packet, 21)},{BitConverter.ToInt16(packet, 23)},{BitConverter.ToInt16(packet, 25)} accel={BitConverter.ToInt16(packet, 27)},{BitConverter.ToInt16(packet, 29)},{BitConverter.ToInt16(packet, 31)}";
            }

            if (viiperType == ViiperVirtualDeviceType.DualShock4 && packet.Length >= 25)
            {
                ushort buttons = BitConverter.ToUInt16(packet, 4);
                return $"ds4 lx={packet[0]} ly={packet[1]} rx={packet[2]} ry={packet[3]} buttons=0x{buttons:X4} dpad=0x{packet[6]:X2} l2={packet[7]} r2={packet[8]}";
            }

            return $"len={packet.Length}";
        }

        private static string SummarizePcm16Stereo(byte[] pcmBytes, int pcmFrames)
        {
            if (pcmBytes == null || pcmFrames <= 0)
            {
                return "frames=0";
            }

            int frames = Math.Min(pcmFrames, pcmBytes.Length / (DualSenseMicrophoneChannels * sizeof(short)));
            long sumLeft = 0;
            long sumRight = 0;
            long sumSqLeft = 0;
            long sumSqRight = 0;
            int peakLeft = 0;
            int peakRight = 0;
            int nonZeroFrames = 0;
            int clippedSamples = 0;

            for (int frame = 0; frame < frames; frame++)
            {
                int offset = frame * DualSenseMicrophoneChannels * sizeof(short);
                short left = BitConverter.ToInt16(pcmBytes, offset);
                short right = BitConverter.ToInt16(pcmBytes, offset + sizeof(short));
                int absLeft = Math.Abs((int)left);
                int absRight = Math.Abs((int)right);
                peakLeft = Math.Max(peakLeft, absLeft);
                peakRight = Math.Max(peakRight, absRight);
                sumLeft += left;
                sumRight += right;
                sumSqLeft += (long)left * left;
                sumSqRight += (long)right * right;

                if (left != 0 || right != 0)
                {
                    nonZeroFrames++;
                }

                if (left == short.MinValue || left == short.MaxValue ||
                    right == short.MinValue || right == short.MaxValue)
                {
                    clippedSamples++;
                }
            }

            double invFrames = frames > 0 ? 1.0 / frames : 0.0;
            double dcLeft = sumLeft * invFrames;
            double dcRight = sumRight * invFrames;
            double rmsLeft = Math.Sqrt(sumSqLeft * invFrames);
            double rmsRight = Math.Sqrt(sumSqRight * invFrames);
            return $"frames={frames} nonZero={nonZeroFrames} peakL={peakLeft} peakR={peakRight} rmsL={rmsLeft:F1} rmsR={rmsRight:F1} dcL={dcLeft:F1} dcR={dcRight:F1} clipped={clippedSamples}";
        }

        private static string FormatBytes(byte[] bytes, int count)
        {
            if (bytes == null)
            {
                return "<null>";
            }

            int length = Math.Min(bytes.Length, count);
            if (length <= 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(length * 3);
            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(bytes[i].ToString("X2"));
            }

            return builder.ToString();
        }

        private void ApplyDualSenseTriggerFeedback(DS4Device device, byte[] feedback, int feedbackLength)
        {
            if (feedbackLength < DualSenseExtendedFeedbackLength ||
                device is not DualSenseDevice dualSenseDevice ||
                !IsCurrentPhysicalSonyDualSense(dualSenseDevice))
            {
                return;
            }

            int r2Offset = DualSenseTriggerFeedbackOffset;
            int l2Offset = DualSenseTriggerFeedbackOffset + DualSenseTriggerEffectLength;
            bool r2Changed = !TriggerFeedbackEquals(feedback, r2Offset, lastR2TriggerFeedback);
            bool l2Changed = !TriggerFeedbackEquals(feedback, l2Offset, lastL2TriggerFeedback);

            if (r2Changed)
            {
                CopyTriggerFeedback(feedback, r2Offset, lastR2TriggerFeedback);
                ApplyRawTriggerEffect(dualSenseDevice, TriggerId.RightTrigger, feedback, r2Offset);
            }

            if (l2Changed)
            {
                CopyTriggerFeedback(feedback, l2Offset, lastL2TriggerFeedback);
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

            byte[] report = PrepareNativeDualSenseOutputReportForProfile(feedback);
            return dualSenseDevice.WriteRawOutputReportFromGame(report,
                0,
                DualSenseNativeOutputReportLength);
        }

        private static byte[] PrepareNativeDualSenseOutputReportForProfile(byte[] feedback)
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

            return report;
        }

        private static bool TryApplyBluetoothHapticsOutputReport(DS4Device device, byte[] feedback, int feedbackLength,
            bool waitForWrite)
        {
            if (feedbackLength < DualSenseExtendedFeedbackLength ||
                device is not DualSenseDevice dualSenseDevice ||
                feedback[DualSenseBluetoothHapticsReportOffset] != 0x32)
            {
                return false;
            }

            return dualSenseDevice.WriteBluetoothHapticsOutputReport(feedback,
                DualSenseBluetoothHapticsReportOffset,
                DualSenseBluetoothHapticsReportLength,
                waitForWrite);
        }

        private static bool TryApplyBluetoothCombinedHapticsOutputReport(DS4Device device, byte[] feedback, int feedbackLength)
        {
            if (feedbackLength < DualSenseCombinedExtendedFeedbackLength ||
                device is not DualSenseDevice dualSenseDevice ||
                feedback[DualSenseCombinedBluetoothReportOffset] != 0x36)
            {
                return false;
            }

            return dualSenseDevice.WriteBluetoothCombinedHapticsAudioOutputReport(feedback,
                DualSenseCombinedBluetoothReportOffset,
                DualSenseCombinedBluetoothReportLength);
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
                isPhysical = !Global.CheckIfVirtualDevice(devicePath);
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

    internal sealed class ViiperClient
    {
        private const int ApiReceiveTimeoutMs = 5000;
        private const int StreamReceiveTimeoutMs = 0;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly string host;
        private readonly int port;

        public ViiperClient(string host, int port)
        {
            this.host = host;
            this.port = port;
        }

        public ViiperDeviceStream CreateDeviceAndOpenStream(ViiperVirtualDeviceType deviceType)
        {
            return CreateDeviceAndOpenStream(ViiperStatePacketBuilder.GetViiperDeviceName(deviceType));
        }

        public ViiperDeviceStream CreateDeviceAndOpenStream(string deviceName)
        {
            ViiperUsbipPortManager.DetachStaleLocalViiperPorts();

            ViiperBusCreateResponse bus = SendRequest<ViiperBusCreateResponse>("bus/create", "0");
            ViiperDeviceResponse device = null;
            int usbipPort = -1;
            try
            {
                string payload = JsonSerializer.Serialize(new ViiperDeviceCreateRequest
                {
                    Type = deviceName,
                }, JsonOptions);

                device = SendRequest<ViiperDeviceResponse>($"bus/{bus.BusId}/add", payload);
                usbipPort = ViiperUsbipPortManager.FindLocalViiperPort(bus.BusId, device.DevId);
                ViiperUsbipPortManager.RegisterActivePort(usbipPort);
                ViiperUsbipPortManager.DetachDuplicateLocalViiperPorts(bus.BusId, device.DevId, usbipPort);
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

        public bool TryGetDualSenseMicrophoneInterfaceActive(uint busId, string devId, out bool active, out string status)
        {
            active = false;
            status = string.Empty;

            ViiperDevicesListResponse response = SendRequest<ViiperDevicesListResponse>($"bus/{busId}/list");
            if (response?.Devices == null)
            {
                status = "VIIPER returned no devices.";
                return false;
            }

            foreach (ViiperDeviceResponse device in response.Devices)
            {
                if (!string.Equals(device.DevId, devId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (device.DeviceSpecific.ValueKind == JsonValueKind.Object &&
                    device.DeviceSpecific.TryGetProperty("microphoneInterfaceActive", out JsonElement value) &&
                    (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
                {
                    active = value.GetBoolean();
                    status = "ok";
                    return true;
                }

                status = "VIIPER device did not expose microphoneInterfaceActive.";
                return false;
            }

            status = $"VIIPER device {devId} was not found on bus {busId}.";
            return false;
        }

        private ViiperDeviceStream OpenStream(uint busId, string devId, int usbipPort)
        {
            TcpClient tcp = Connect(StreamReceiveTimeoutMs);
            try
            {
                NetworkStream stream = tcp.GetStream();
                byte[] request = Encoding.UTF8.GetBytes($"bus/{busId}/{devId}\0");
                stream.Write(request, 0, request.Length);
                return new ViiperDeviceStream(tcp, busId, devId, usbipPort, RemoveDevice);
            }
            catch
            {
                tcp.Dispose();
                throw;
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

        private T SendRequest<T>(string path, string payload = null)
        {
            string raw = SendRequestRaw(path, payload);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new IOException("VIIPER returned an empty response.");
            }

            ViiperApiError apiError = JsonSerializer.Deserialize<ViiperApiError>(raw, JsonOptions);
            if (apiError != null && (apiError.Status != 0 || !string.IsNullOrEmpty(apiError.Title)))
            {
                throw new IOException($"VIIPER API error {apiError.Status} {apiError.Title}: {apiError.Detail}");
            }

            return JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }

        private string SendRequestRaw(string path, string payload = null)
        {
            using TcpClient tcp = Connect(ApiReceiveTimeoutMs);
            NetworkStream stream = tcp.GetStream();
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
            [JsonPropertyName("busId")]
            public uint BusId { get; set; }

            [JsonPropertyName("devId")]
            public string DevId { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("deviceSpecific")]
            public JsonElement DeviceSpecific { get; set; }
        }

        private sealed class ViiperDevicesListResponse
        {
            [JsonPropertyName("devices")]
            public List<ViiperDeviceResponse> Devices { get; set; }
        }

        private sealed class ViiperDeviceCreateRequest
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }
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
    }

    internal static class ViiperUsbipPortManager
    {
        private static readonly string[] KnownViiperDeviceIds =
        {
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

            foreach (UsbipPortBlock port in GetImportedPorts())
            {
                if (!activePorts.Contains(port.Port) && IsLocalViiperPort(port, null))
                {
                    DetachPort(port.Port, "stale local VIIPER controller import");
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

    internal sealed class ViiperDeviceStream : IDisposable
    {
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly uint busId;
        private readonly string devId;
        private readonly int usbipPort;
        private readonly Action<uint, string> removeDevice;
        private readonly object writeLock = new object();
        private int disposed;
        // Mic-capable VIIPER streams need a real packet boundary so PCM can never be parsed as controller input.
        private const int FrameHeaderLength = 8;
        private const byte FrameMagic0 = (byte)'V';
        private const byte FrameMagic1 = (byte)'P';
        private const byte FrameMagic2 = (byte)'C';
        private const byte FrameMagic3 = (byte)'M';
        private const byte FrameVersion = 0x01;

        public ViiperDeviceStream(TcpClient tcp, uint busId, string devId, int usbipPort, Action<uint, string> removeDevice)
        {
            this.tcp = tcp;
            this.stream = tcp.GetStream();
            this.busId = busId;
            this.devId = devId;
            this.usbipPort = usbipPort;
            this.removeDevice = removeDevice;
        }

        public uint BusId => busId;

        public string DevId => devId;

        public void Write(byte[] data)
        {
            if (Volatile.Read(ref disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            lock (writeLock)
            {
                if (Volatile.Read(ref disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ViiperDeviceStream));
                }

                stream.Write(data, 0, data.Length);
            }
        }

        public void WriteFrame(byte frameType, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "VIIPER framed stream payload is too large.");
            }

            byte[] frame = new byte[data.Length + FrameHeaderLength];
            frame[0] = FrameMagic0;
            frame[1] = FrameMagic1;
            frame[2] = FrameMagic2;
            frame[3] = FrameMagic3;
            frame[4] = FrameVersion;
            frame[5] = frameType;
            frame[6] = (byte)(data.Length & 0xFF);
            frame[7] = (byte)((data.Length >> 8) & 0xFF);
            Buffer.BlockCopy(data, 0, frame, FrameHeaderLength, data.Length);
            Write(frame);
        }

        public void ReadExactly(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                if (Volatile.Read(ref disposed) == 1)
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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
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
                tcp.Dispose();
            }
            catch
            {
            }

            ViiperUsbipPortManager.DetachPort(usbipPort, "DS4Windows VIIPER device stopped");
            ViiperUsbipPortManager.UnregisterActivePort(usbipPort);

            removeDevice?.Invoke(busId, devId);
            ViiperUsbipPortManager.DetachStaleLocalViiperPorts();
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
        private const int DualSenseMotionOffset = 21;
        private const int ViiperStreamFrameHeaderLength = 8;
        private const byte ViiperStreamMagic0 = 0x56;
        private const byte ViiperStreamMagic1 = 0x50;
        private const byte ViiperStreamMagic2 = 0x43;
        private const byte ViiperStreamMagic3 = 0x4D;
        private const byte ViiperStreamVersion = 0x01;
        private const byte ViiperInputStateFrameType = 0x01;
        private const byte ViiperMicrophonePcmFrameType = 0x02;
        private const float X360RecipInputPosResolution = 1 / 127f;
        private const float X360RecipInputNegResolution = 1 / 128f;
        private const int X360OutputResolution = 32767 - (-32768);
        private static long dualSenseTransportSignatureDropCount;

        public static string GetViiperDeviceName(ViiperVirtualDeviceType type)
        {
            return type switch
            {
                ViiperVirtualDeviceType.Xbox360 => "xbox360",
                ViiperVirtualDeviceType.DualShock4 => "dualshock4",
                ViiperVirtualDeviceType.DualSense => "dualsenseext",
                ViiperVirtualDeviceType.DualSenseEdge => "dualsenseedgeext",
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
            WriteSonyMotion(packet, DualSenseMotionOffset, state, DualSenseGyroRestDeadband, DualSenseAccelRestZ);
            return SanitizeDualSenseTransportSignature(packet);
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
                WriteNeutralSonyMotion(packet, offset, restAccelZ);
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

        private static void WriteNeutralSonyMotion(byte[] packet, int offset, int restAccelZ)
        {
            WriteInt16(packet, offset, 0);
            WriteInt16(packet, offset + 2, 0);
            WriteInt16(packet, offset + 4, 0);
            WriteInt16(packet, offset + 6, 0);
            WriteInt16(packet, offset + 8, 0);
            WriteInt16(packet, offset + 10, ClampShort(restAccelZ));
        }

        private static byte[] SanitizeDualSenseTransportSignature(byte[] packet)
        {
            if (!ContainsViiperStreamFrameHeader(packet, 0, packet.Length))
            {
                return packet;
            }

            string before = Global.VerboseStartupLogging ?
                FormatPacketBytes(packet, 0, packet.Length) :
                string.Empty;
            byte[] neutral = BuildNeutralDualSensePacket();

            long count = Interlocked.Increment(ref dualSenseTransportSignatureDropCount);
            if (Global.VerboseStartupLogging && (count <= 128 || IsPowerOfTwo(count)))
            {
                AppLogger.LogToGui(
                    $"VIIPER_INPUT_CORRUPT_PACKET_DROPPED count={count} reason=transport_magic_in_dualsense_input before={before} after={FormatPacketBytes(neutral, 0, neutral.Length)}",
                    false);
            }

            return neutral;
        }

        private static byte[] BuildNeutralDualSensePacket()
        {
            byte[] packet = new byte[DualSensePacketSize];
            packet[15] = 0x80;
            packet[20] = 0x80;
            WriteNeutralSonyMotion(packet, DualSenseMotionOffset, DualSenseAccelRestZ);
            return packet;
        }

        private static bool ContainsViiperStreamFrameHeader(byte[] packet, int offset, int length)
        {
            if (packet == null || length < ViiperStreamFrameHeaderLength)
            {
                return false;
            }

            int start = Math.Max(0, offset);
            int end = Math.Min(packet.Length, offset + length);
            for (int i = start; i + ViiperStreamFrameHeaderLength <= end; i++)
            {
                if (packet[i] == ViiperStreamMagic0 &&
                    packet[i + 1] == ViiperStreamMagic1 &&
                    packet[i + 2] == ViiperStreamMagic2 &&
                    packet[i + 3] == ViiperStreamMagic3 &&
                    packet[i + 4] == ViiperStreamVersion &&
                    IsKnownViiperStreamFrame(packet[i + 5], packet[i + 6] | (packet[i + 7] << 8)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsKnownViiperStreamFrame(byte frameType, int payloadLength)
        {
            return (frameType == ViiperInputStateFrameType && payloadLength == DualSensePacketSize) ||
                (frameType == ViiperMicrophonePcmFrameType && payloadLength == DualSenseMicrophonePcmFrameLength);
        }

        private static bool IsPowerOfTwo(long value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        private static string FormatPacketBytes(byte[] data, int offset, int length)
        {
            if (data == null)
            {
                return "<null>";
            }

            int start = Math.Max(0, offset);
            int end = Math.Min(data.Length, offset + length);
            StringBuilder builder = new StringBuilder(Math.Max(0, end - start) * 3);
            for (int i = start; i < end; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(data[i].ToString("X2"));
            }

            return builder.ToString();
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
