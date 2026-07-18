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
using SBC;

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
        private const int DualSenseMicrophoneFramesPerPacket = 480;
        private const int DualSenseMicrophonePcmFrameLength = DualSenseMicrophoneFramesPerPacket * 2 * sizeof(short);
        private const int DualShock4MicrophoneMaximumUpsampledSamplesPerFrame =
            SbcFrame.MaxSamples * 3;
        private const int MaxPendingMicrophoneFrames = 4;
        private const byte ViiperStreamFrameInputState = 0x01;
        private const byte ViiperStreamFrameMicrophonePcm = 0x02;
        private const int MaxStreamRecoveryAttempts = 2;

        private readonly OutContType outputType;
        private readonly ViiperVirtualDeviceType viiperType;
        private readonly ViiperClient client;
        private readonly object pendingPacketLock = new object();
        private readonly object microphoneQueueLock = new object();
        private readonly object microphoneProcessingLock = new object();
        private readonly object writerThreadLock = new object();
        private readonly object microphoneWriterThreadLock = new object();
        private readonly object streamRecoveryLock = new object();
        private readonly object physicalDualSenseIdentityLock = new object();
        private readonly object microphoneSourceLock = new object();
        private readonly AutoResetEvent writerSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent microphoneWriterSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent microphoneInterfaceStopSignal = new ManualResetEvent(false);
        private readonly Queue<PendingMicrophoneFrame> pendingMicrophoneFrames =
            new Queue<PendingMicrophoneFrame>(MaxPendingMicrophoneFrames);
        private readonly short[] microphoneMonoPcm = new short[DualSenseMicrophoneFramesPerPacket];
        private readonly byte[] microphoneStereoPcm = new byte[DualSenseMicrophonePcmFrameLength];
        private readonly short[] dualShock4UpsampledPcm =
            new short[DualShock4MicrophoneMaximumUpsampledSamplesPerFrame];
        private readonly short[] dualShock4ResampleAccumulator =
            new short[DualSenseMicrophoneFramesPerPacket * 2];
        private readonly DualSenseMicrophoneProcessor microphoneProcessor = new DualSenseMicrophoneProcessor();
        private ViiperDeviceStream deviceStream;
        private Thread feedbackThread;
        private Thread stateWriterThread;
        private Thread microphoneWriterThread;
        private Thread microphoneInterfaceThread;
        private byte[] pendingStatePacket;
        private long pendingStatePacketQueuedTimestamp;
        private IOpusDecoder microphoneDecoder;
        private SbcDecoder microphoneSbcDecoder;
        private DS4Device microphoneSourceDevice;
        private int dualShock4ResampleAccumulatorCount;
        private volatile bool writerStopRequested;
        private bool activeStreamUsesFramedProtocol;
        private bool activeStreamSupportsMicrophone;
        private int microphoneVolume = 128;
        private int microphoneNoiseSuppression = (int)DualSenseMicrophoneNoiseSuppression.Balanced;
        private long lastMicrophoneFrameTimestamp;
        private long lastMicrophoneArmTimestamp;
        private long streamGeneration;
        private int streamRecoveryAttempts;
        private DateTime lastStreamRecoveryAttemptUtc = DateTime.MinValue;
        private long replacedPendingPacketCount;
        private long submittedPacketCount;
        private long writtenPacketCount;
        private long microphoneArmAttempts;
        private long microphoneArmFailures;
        private long microphoneOpusFramesReceived;
        private long microphoneSbcFramesReceived;
        private long microphoneFramesDecoded;
        private long microphoneFramesSubmitted;
        private long microphoneFramesDropped;
        private long microphoneDecodeFailures;
        private long lastStateQueuedTimestamp;
        private long lastStateWrittenTimestamp;
        private long maximumStateQueueGapTicks;
        private long maximumStatePacketAgeTicks;
        private long maximumStateWriteDurationTicks;
        private long maximumStateWriteGapTicks;
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
        private int edgePhysicalMismatchLogged;
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
            public PendingMicrophoneFrame(MicrophoneCodec codec, byte[] data)
            {
                Codec = codec;
                Data = data;
            }

            public MicrophoneCodec Codec { get; }
            public byte[] Data { get; }
        }

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
            Interlocked.Increment(ref streamGeneration);
            Volatile.Write(ref submitFailureLogged, 0);
            Volatile.Write(ref microphoneUnavailableLogged, 0);
            Volatile.Write(ref microphoneNoiseSuppressionUnavailableLogged, 0);
            Volatile.Write(ref microphoneProcessingFailureLogged, 0);
            Volatile.Write(ref microphoneMuted, 0);
            Volatile.Write(ref lastInputDeviceIndex, -1);
            Interlocked.Exchange(ref streamRecoveryAttempts, 0);
            lastStreamRecoveryAttemptUtc = DateTime.MinValue;
            Interlocked.Exchange(ref replacedPendingPacketCount, 0);
            Interlocked.Exchange(ref submittedPacketCount, 0);
            Interlocked.Exchange(ref writtenPacketCount, 0);
            Interlocked.Exchange(ref lastMicrophoneFrameTimestamp, 0);
            Interlocked.Exchange(ref lastMicrophoneArmTimestamp, 0);
            Interlocked.Exchange(ref microphoneArmAttempts, 0);
            Interlocked.Exchange(ref microphoneArmFailures, 0);
            Interlocked.Exchange(ref microphoneOpusFramesReceived, 0);
            Interlocked.Exchange(ref microphoneSbcFramesReceived, 0);
            Interlocked.Exchange(ref microphoneFramesDecoded, 0);
            Interlocked.Exchange(ref microphoneFramesSubmitted, 0);
            Interlocked.Exchange(ref microphoneFramesDropped, 0);
            Interlocked.Exchange(ref microphoneDecodeFailures, 0);
            Interlocked.Exchange(ref lastStateQueuedTimestamp, 0);
            Interlocked.Exchange(ref lastStateWrittenTimestamp, 0);
            Interlocked.Exchange(ref maximumStateQueueGapTicks, 0);
            Interlocked.Exchange(ref maximumStatePacketAgeTicks, 0);
            Interlocked.Exchange(ref maximumStateWriteDurationTicks, 0);
            Interlocked.Exchange(ref maximumStateWriteGapTicks, 0);
            Volatile.Write(ref edgePhysicalMismatchLogged, 0);
            lock (physicalDualSenseIdentityLock)
            {
                physicalDualSenseIdentityPath = null;
                physicalDualSenseIdentityVerified = false;
            }
            lastWriterHealthLogUtc = DateTime.MinValue;
            lastMicrophoneHealthLogUtc = DateTime.MinValue;
            Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);
            microphoneInterfaceStopSignal.Reset();
            writerStopRequested = false;
            connected = true;
            StartStateWriter();
            StartMicrophoneWriter();
            StartMicrophoneInterfaceMonitor();
            ResetState();
            StartFeedbackReader();
        }

        private ViiperDeviceStream CreateDeviceStream()
        {
            activeStreamUsesFramedProtocol = false;
            activeStreamSupportsMicrophone = false;
            Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
            Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);

            if (viiperType == ViiperVirtualDeviceType.DualSense)
            {
                try
                {
                    ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsensecombinedmicv2");
                    activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                    activeStreamUsesFramedProtocol = true;
                    activeStreamSupportsMicrophone = true;
                    return stream;
                }
                catch (IOException ex)
                {
                    AppLogger.LogToGui($"VIIPER DualSense microphone input unavailable, continuing without mic-in: {ex.Message}", false);
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
                    ViiperDeviceStream stream = client.CreateDeviceAndOpenStream("dualsenseedgecombinedmicv2");
                    activeFeedbackLength = DualSenseCombinedExtendedFeedbackLength;
                    activeStreamUsesFramedProtocol = true;
                    activeStreamSupportsMicrophone = true;
                    return stream;
                }
                catch (IOException ex)
                {
                    AppLogger.LogToGui($"VIIPER DualSense Edge microphone input unavailable, continuing without mic-in: {ex.Message}", false);
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
            writerSignal.Set();
            microphoneWriterSignal.Set();
            StopMicrophoneInterfaceMonitor();
            DetachBluetoothMicrophoneSource();
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

            if (stateWriterThread != null && stateWriterThread.IsAlive)
            {
                if (Thread.CurrentThread.ManagedThreadId != stateWriterThread.ManagedThreadId)
                {
                    stateWriterThread.Join(500);
                }
            }

            stateWriterThread = null;
            if (microphoneWriterThread != null && microphoneWriterThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != microphoneWriterThread.ManagedThreadId)
            {
                microphoneWriterThread.Join(500);
            }

            microphoneWriterThread = null;
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

        private void StartMicrophoneInterfaceMonitor()
        {
            if (!activeStreamSupportsMicrophone ||
                microphoneInterfaceThread != null && microphoneInterfaceThread.IsAlive)
            {
                return;
            }

            microphoneInterfaceThread = new Thread(MicrophoneInterfaceMonitorLoop)
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
        }

        private void MicrophoneInterfaceMonitorLoop()
        {
            bool lastActive = false;
            bool lastKnown = false;
            DateTime lastFailureLogUtc = DateTime.MinValue;

            while (connected && !microphoneInterfaceStopSignal.WaitOne(0))
            {
                ViiperDeviceStream stream = deviceStream;
                try
                {
                    bool active = stream != null &&
                        client.GetMicrophoneInterfaceActive(stream.BusId, stream.DevId);
                    Volatile.Write(ref virtualMicrophoneInterfaceActive, active ? 1 : 0);
                    Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 1);

                    if (Global.VerboseStartupLogging && (!lastKnown || active != lastActive))
                    {
                        AppLogger.LogToGui(
                            $"VIIPER {viiperType} microphone capture interface active={active}.",
                            false);
                    }

                    lastKnown = true;
                    lastActive = active;
                }
                catch (Exception ex) when (ex is IOException ||
                    ex is SocketException || ex is JsonException)
                {
                    Volatile.Write(ref virtualMicrophoneInterfaceActive, 0);
                    Volatile.Write(ref virtualMicrophoneInterfaceStateKnown, 0);
                    lastKnown = false;

                    if (Global.VerboseStartupLogging &&
                        DateTime.UtcNow - lastFailureLogUtc >= TimeSpan.FromSeconds(5))
                    {
                        lastFailureLogUtc = DateTime.UtcNow;
                        AppLogger.LogToGui(
                            $"VIIPER {viiperType} microphone interface query failed: {ex.Message}",
                            true);
                    }
                }

                UpdateBluetoothMicrophoneSource(Volatile.Read(ref lastInputDeviceIndex));

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
                    Priority = ThreadPriority.AboveNormal,
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
                    long queuedAt;
                    lock (pendingPacketLock)
                    {
                        packet = pendingStatePacket;
                        pendingStatePacket = null;
                        queuedAt = pendingStatePacketQueuedTimestamp;
                        pendingStatePacketQueuedTimestamp = 0;
                    }

                    if (packet == null)
                    {
                        break;
                    }

                    long writeStreamGeneration = Volatile.Read(ref streamGeneration);
                    try
                    {
                        long writeStartedAt = Stopwatch.GetTimestamp();
                        if (queuedAt > 0)
                        {
                            RecordMaximum(ref maximumStatePacketAgeTicks,
                                writeStartedAt - queuedAt);
                        }

                        WriteState(packet);
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
                        if (TryRecoverStream(ex.Message, writeStreamGeneration, packet))
                        {
                            continue;
                        }

                        LogSubmitFailure(ex.Message);
                        return;
                    }
                    catch (SocketException ex)
                    {
                        if (TryRecoverStream(ex.Message, writeStreamGeneration, packet))
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
                            if (TryRecoverStream(ex.Message, writeStreamGeneration, packet))
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

        private void EnsureMicrophoneWriterAlive()
        {
            if (!connected || writerStopRequested || !activeStreamSupportsMicrophone)
            {
                return;
            }

            if (microphoneWriterThread == null || !microphoneWriterThread.IsAlive)
            {
                StartMicrophoneWriter();
            }
        }

        private void StartMicrophoneWriter()
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

                microphoneWriterThread = new Thread(MicrophoneWriteLoop)
                {
                    IsBackground = true,
                    Name = $"VIIPER {viiperType} microphone writer",
                    Priority = ThreadPriority.Normal,
                };
                microphoneWriterThread.Start();
            }
        }

        private void MicrophoneWriteLoop()
        {
            while (!writerStopRequested)
            {
                microphoneWriterSignal.WaitOne();
                if (writerStopRequested)
                {
                    return;
                }

                while (!writerStopRequested)
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

                    long writeStreamGeneration = Volatile.Read(ref streamGeneration);
                    try
                    {
                        WriteMicrophoneFrame(microphoneFrame.Value);
                        Interlocked.Exchange(ref streamRecoveryAttempts, 0);
                    }
                    catch (IOException ex)
                    {
                        if (TryRecoverStream(ex.Message, writeStreamGeneration))
                        {
                            continue;
                        }

                        LogSubmitFailure(ex.Message);
                        return;
                    }
                    catch (SocketException ex)
                    {
                        if (TryRecoverStream(ex.Message, writeStreamGeneration))
                        {
                            continue;
                        }

                        LogSubmitFailure(ex.Message);
                        return;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        if (!writerStopRequested &&
                            TryRecoverStream(ex.Message, writeStreamGeneration))
                        {
                            continue;
                        }

                        return;
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
                    }
                }
            }
        }

        private bool TryRecoverStream(string reason, long failedStreamGeneration,
            byte[] packetToRetry = null)
        {
            if (writerStopRequested || !connected)
            {
                return false;
            }

            if (Volatile.Read(ref streamGeneration) != failedStreamGeneration)
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

                if (Volatile.Read(ref streamGeneration) != failedStreamGeneration)
                {
                    QueueRetryStatePacket(packetToRetry);
                    return true;
                }

                DateTime now = DateTime.UtcNow;
                if (now - lastStreamRecoveryAttemptUtc < TimeSpan.FromSeconds(2))
                {
                    return false;
                }

                if (Volatile.Read(ref streamRecoveryAttempts) >= MaxStreamRecoveryAttempts)
                {
                    return false;
                }

                int recoveryAttempt = Interlocked.Increment(ref streamRecoveryAttempts);
                lastStreamRecoveryAttemptUtc = now;
                AppLogger.LogToGui(
                    $"VIIPER {viiperType} stream interrupted; attempting recovery {recoveryAttempt}/{MaxStreamRecoveryAttempts}: {reason}",
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
                    Interlocked.Increment(ref streamGeneration);
                    oldStream?.Dispose();
                    StopFeedbackReader();

                    deviceStream = CreateDeviceStreamWithServerFallback();
                    Interlocked.Increment(ref streamGeneration);
                    StartFeedbackReader();

                    QueueRetryStatePacket(packetToRetry);

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
                stream.WriteFrameV2(ViiperStreamFrameInputState, data);
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
                    WriteMicrophoneSbcFrame(frame.Data);
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
                int frames = DualSenseMicrophoneFramesPerPacket;
                if (!muted)
                {
                    IOpusDecoder decoder = microphoneDecoder;
                    if (decoder == null)
                    {
                        decoder = OpusCodecFactory.CreateDecoder(48000, 1);
                        microphoneDecoder = decoder;
                    }

                    int decodedSamples = decoder.Decode(opusFrame.AsSpan(),
                        microphoneMonoPcm.AsSpan(), DualSenseMicrophoneFramesPerPacket, false);
                    if (decodedSamples <= 0)
                    {
                        Interlocked.Increment(ref microphoneDecodeFailures);
                        return;
                    }

                    Interlocked.Increment(ref microphoneFramesDecoded);

                    frames = Math.Min(decodedSamples, DualSenseMicrophoneFramesPerPacket);
                }

                SubmitMicrophonePcm(frames, muted);
            }
        }

        private void WriteMicrophoneSbcFrame(byte[] sbcFrame)
        {
            if (!activeStreamSupportsMicrophone ||
                !activeStreamUsesFramedProtocol ||
                sbcFrame == null || sbcFrame.Length < SbcFrame.HeaderSize)
            {
                return;
            }

            lock (microphoneProcessingLock)
            {
                SbcDecoder decoder = microphoneSbcDecoder;
                if (decoder == null)
                {
                    decoder = new SbcDecoder();
                    microphoneSbcDecoder = decoder;
                }

                if (!decoder.Decode(sbcFrame, out short[] decoded,
                    out short[] ignoredRight, out SbcFrame decodedFrame) ||
                    decoded == null || decoded.Length == 0 ||
                    decodedFrame.Mode != SbcMode.Mono ||
                    decodedFrame.GetFrequencyHz() <= 0)
                {
                    Interlocked.Increment(ref microphoneDecodeFailures);
                    return;
                }

                Interlocked.Increment(ref microphoneFramesDecoded);
                int upsampledSamples = (int)Math.Round(decoded.Length * 48000.0 /
                    decodedFrame.GetFrequencyHz());
                if (upsampledSamples <= 0 ||
                    upsampledSamples > dualShock4UpsampledPcm.Length)
                {
                    Interlocked.Increment(ref microphoneDecodeFailures);
                    return;
                }

                UpsampleDualShock4Microphone(decoded, dualShock4UpsampledPcm,
                    upsampledSamples);
                int available = dualShock4ResampleAccumulator.Length -
                    dualShock4ResampleAccumulatorCount;
                int appended = Math.Min(available, upsampledSamples);
                Array.Copy(dualShock4UpsampledPcm, 0, dualShock4ResampleAccumulator,
                    dualShock4ResampleAccumulatorCount, appended);
                dualShock4ResampleAccumulatorCount += appended;
                if (dualShock4ResampleAccumulatorCount <
                    DualSenseMicrophoneFramesPerPacket)
                {
                    return;
                }

                Array.Copy(dualShock4ResampleAccumulator, 0, microphoneMonoPcm, 0,
                    DualSenseMicrophoneFramesPerPacket);
                int remaining = dualShock4ResampleAccumulatorCount -
                    DualSenseMicrophoneFramesPerPacket;
                if (remaining > 0)
                {
                    Array.Copy(dualShock4ResampleAccumulator,
                        DualSenseMicrophoneFramesPerPacket,
                        dualShock4ResampleAccumulator, 0, remaining);
                }
                dualShock4ResampleAccumulatorCount = remaining;
                SubmitMicrophonePcm(DualSenseMicrophoneFramesPerPacket,
                    Volatile.Read(ref microphoneMuted) == 1);
            }
        }

        private static void UpsampleDualShock4Microphone(short[] source,
            short[] destination, int destinationCount)
        {
            for (int index = 0; index < destinationCount; index++)
            {
                double position = index * source.Length / (double)destinationCount;
                int first = Math.Min((int)position, source.Length - 1);
                int second = Math.Min(first + 1, source.Length - 1);
                double blend = position - first;
                destination[index] = (short)Math.Clamp((int)Math.Round(
                    source[first] * (1.0 - blend) + source[second] * blend),
                    short.MinValue, short.MaxValue);
            }
        }

        private void SubmitMicrophonePcm(int frames, bool muted)
        {
            ViiperDeviceStream stream = deviceStream;
            if (stream == null)
            {
                throw new ObjectDisposedException(nameof(ViiperDeviceStream));
            }

            if (!muted)
            {
                DualSenseMicrophoneNoiseSuppression suppression =
                    (DualSenseMicrophoneNoiseSuppression)Math.Clamp(
                        Volatile.Read(ref microphoneNoiseSuppression),
                        (int)DualSenseMicrophoneNoiseSuppression.Off,
                        (int)DualSenseMicrophoneNoiseSuppression.Strong);
                microphoneProcessor.Process(microphoneMonoPcm, frames,
                    (byte)Math.Clamp(Volatile.Read(ref microphoneVolume), 0,
                        byte.MaxValue), suppression);
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
            }

            Array.Clear(microphoneStereoPcm, 0, microphoneStereoPcm.Length);
            if (!muted)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    short sample = microphoneMonoPcm[frame];
                    int offset = frame * 4;
                    microphoneStereoPcm[offset] = (byte)sample;
                    microphoneStereoPcm[offset + 1] = (byte)(sample >> 8);
                    microphoneStereoPcm[offset + 2] = (byte)sample;
                    microphoneStereoPcm[offset + 3] = (byte)(sample >> 8);
                }
            }

            stream.WriteFrameV2(ViiperStreamFrameMicrophonePcm, microphoneStereoPcm);
            Interlocked.Increment(ref microphoneFramesSubmitted);
        }

        private void LogWriterHealthIfNeeded()
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now - lastWriterHealthLogUtc < TimeSpan.FromSeconds(5))
            {
                return;
            }

            lastWriterHealthLogUtc = now;
            long maximumQueueGap = Interlocked.Exchange(ref maximumStateQueueGapTicks, 0);
            long maximumPacketAge = Interlocked.Exchange(ref maximumStatePacketAgeTicks, 0);
            long maximumWriteDuration = Interlocked.Exchange(ref maximumStateWriteDurationTicks, 0);
            long maximumWriteGap = Interlocked.Exchange(ref maximumStateWriteGapTicks, 0);
            AppLogger.LogToGui(
                $"VIIPER {viiperType} writer stats: " +
                $"submitted={Interlocked.Read(ref submittedPacketCount)} " +
                $"written={Interlocked.Read(ref writtenPacketCount)} " +
                $"coalesced={Interlocked.Read(ref replacedPendingPacketCount)} " +
                $"queueGapMaxMs={StopwatchTicksToMilliseconds(maximumQueueGap):F2} " +
                $"packetAgeMaxMs={StopwatchTicksToMilliseconds(maximumPacketAge):F2} " +
                $"writeMaxMs={StopwatchTicksToMilliseconds(maximumWriteDuration):F2} " +
                $"writeGapMaxMs={StopwatchTicksToMilliseconds(maximumWriteGap):F2}",
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
            long readStreamGeneration = Volatile.Read(ref streamGeneration);
            try
            {
                while (connected)
                {
                    ViiperDeviceStream stream = deviceStream;
                    if (stream == null)
                    {
                        return;
                    }

                    readStreamGeneration = Volatile.Read(ref streamGeneration);
                    stream.ReadExactly(buffer, 0, feedbackLength);
                    ApplyFeedback(buffer, feedbackLength);
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
                            TryApplyBluetoothHapticsOutputReport(device, feedback, feedbackLength))
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

                        Program.rootHub.SetDevRumble(device, lightFast, heavySlow,
                            deviceIndex);
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
            bool profileRequested = connected &&
                IsDualSenseType() &&
                deviceIndex >= 0 &&
                deviceIndex < Global.DualSenseEnableMicrophonePassthrough.Length &&
                Global.DualSenseEnableMicrophonePassthrough[deviceIndex];
            bool requested = profileRequested &&
                Volatile.Read(ref virtualMicrophoneInterfaceActive) == 1;

            if (!requested || !activeStreamSupportsMicrophone)
            {
                if (profileRequested && !activeStreamSupportsMicrophone &&
                    Interlocked.Exchange(ref microphoneUnavailableLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "VIIPER DualSense microphone input requires the microphone-rebuild VIIPER backend.",
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

            if (Program.rootHub == null ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length)
            {
                DetachBluetoothMicrophoneSource();
                return;
            }

            DS4Device source = Program.rootHub.DS4Controllers[deviceIndex];
            DualSenseDevice dualSenseSource = source as DualSenseDevice;
            bool validDualSense = dualSenseSource != null &&
                dualSenseSource.ConnectionType == ConnectionType.BT &&
                IsCurrentPhysicalSonyDualSense(dualSenseSource);
            bool validDualShock4 = source != null &&
                source.DeviceType == InputDeviceType.DS4 &&
                source.ConnectionType == ConnectionType.BT &&
                IsCurrentPhysicalSonyDualShock4(source);
            if (!validDualSense && !validDualShock4)
            {
                DetachBluetoothMicrophoneSource();
                return;
            }

            Volatile.Write(ref microphoneMuted,
                dualSenseSource?.IsProfileMicrophoneMuted == true ? 1 : 0);

            bool sourceAlreadyAttached;
            lock (microphoneSourceLock)
            {
                sourceAlreadyAttached = ReferenceEquals(microphoneSourceDevice, source);
            }
            if (sourceAlreadyAttached)
            {
                MaintainBluetoothMicrophoneStreaming(source);
                return;
            }

            DetachBluetoothMicrophoneSource();
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

            Interlocked.Exchange(ref lastMicrophoneFrameTimestamp, 0);
            Interlocked.Exchange(ref lastMicrophoneArmTimestamp, 0);
            MaintainBluetoothMicrophoneStreaming(source);
        }

        private void MaintainBluetoothMicrophoneStreaming(DS4Device source)
        {
            long now = Stopwatch.GetTimestamp();
            long lastFrame = Interlocked.Read(ref lastMicrophoneFrameTimestamp);
            long lastArm = Interlocked.Read(ref lastMicrophoneArmTimestamp);
            long oneSecond = Stopwatch.Frequency;
            long retryPeriod = Stopwatch.Frequency * 2;
            LogMicrophoneHealthIfNeeded(source, now, lastFrame);
            if (lastFrame != 0 && now - lastFrame < oneSecond)
            {
                return;
            }
            if (lastArm != 0 && now - lastArm < retryPeriod)
            {
                return;
            }

            Interlocked.Exchange(ref lastMicrophoneArmTimestamp, now);
            Interlocked.Increment(ref microphoneArmAttempts);
            bool armed = source is DualSenseDevice dualSenseSource ?
                dualSenseSource.SetBluetoothMicrophoneStreaming(true) :
                source.SetDualShock4BluetoothMicrophoneStreaming(true);
            if (!armed)
            {
                Interlocked.Increment(ref microphoneArmFailures);
            }
        }

        private void LogMicrophoneHealthIfNeeded(DS4Device source, long now,
            long lastFrame)
        {
            if (!Global.VerboseStartupLogging ||
                DateTime.UtcNow - lastMicrophoneHealthLogUtc < TimeSpan.FromSeconds(5))
            {
                return;
            }

            lastMicrophoneHealthLogUtc = DateTime.UtcNow;
            string frameAge = lastFrame == 0 ? "never" :
                $"{Math.Max(0, (now - lastFrame) * 1000 / Stopwatch.Frequency)}ms";
            DualSenseDevice dualSenseSource = source as DualSenseDevice;
            int rejectedTag = dualSenseSource?.BluetoothLastRejectedInputTag ?? -1;
            string rejectedTagText = rejectedTag < 0 ? "none" : $"0x{rejectedTag:X2}";
            long physicalFrames = dualSenseSource?.BluetoothMicrophoneFramesReceived ??
                source.DualShock4BluetoothMicrophoneFramesReceived;
            long rejectedInputs = dualSenseSource?.BluetoothRejectedInputFrames ?? 0;
            string armStatus = dualSenseSource?.LastBluetoothMicrophoneWriteStatus ??
                source.LastBluetoothAudioWriteStatus;
            AppLogger.LogToGui(
                $"VIIPER {viiperType} microphone stats: streamV2={activeStreamUsesFramedProtocol} " +
                $"interfaceKnown={Volatile.Read(ref virtualMicrophoneInterfaceStateKnown) == 1} " +
                $"interfaceActive={Volatile.Read(ref virtualMicrophoneInterfaceActive) == 1} " +
                $"armAttempts={Interlocked.Read(ref microphoneArmAttempts)} " +
                $"armFailures={Interlocked.Read(ref microphoneArmFailures)} " +
                $"physicalFrames={physicalFrames} " +
                $"opusFrames={Interlocked.Read(ref microphoneOpusFramesReceived)} " +
                $"sbcFrames={Interlocked.Read(ref microphoneSbcFramesReceived)} " +
                $"decodedFrames={Interlocked.Read(ref microphoneFramesDecoded)} " +
                $"submittedFrames={Interlocked.Read(ref microphoneFramesSubmitted)} " +
                $"queueDrops={Interlocked.Read(ref microphoneFramesDropped)} " +
                $"decodeFailures={Interlocked.Read(ref microphoneDecodeFailures)} " +
                $"rejectedInputs={rejectedInputs} " +
                $"lastRejectedTag={rejectedTagText} lastFrameAge={frameAge} " +
                $"armStatus=\"{armStatus}\"",
                false);
        }

        private void DetachBluetoothMicrophoneSource()
        {
            DS4Device source = null;
            bool resetProcessor = false;
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

            lock (microphoneQueueLock)
            {
                resetProcessor |= pendingMicrophoneFrames.Count > 0;
                pendingMicrophoneFrames.Clear();
            }
            lock (microphoneProcessingLock)
            {
                resetProcessor |= microphoneDecoder != null ||
                    microphoneSbcDecoder != null ||
                    dualShock4ResampleAccumulatorCount > 0;
                microphoneDecoder = null;
                microphoneSbcDecoder = null;
                dualShock4ResampleAccumulatorCount = 0;
                Array.Clear(dualShock4ResampleAccumulator, 0,
                    dualShock4ResampleAccumulator.Length);
                if (resetProcessor)
                {
                    microphoneProcessor.Reset();
                }
            }
            Interlocked.Exchange(ref lastMicrophoneFrameTimestamp, 0);
            Interlocked.Exchange(ref lastMicrophoneArmTimestamp, 0);
            Volatile.Write(ref microphoneMuted, 0);

            if (source != null)
            {
                try
                {
                    if (source is DualSenseDevice dualSenseSource)
                    {
                        dualSenseSource.SetBluetoothMicrophoneStreaming(false);
                    }
                    else
                    {
                        source.SetDualShock4BluetoothMicrophoneStreaming(false);
                    }
                }
                catch
                {
                }
            }
        }

        private void BluetoothMicrophoneOpusFrameReceived(DualSenseDevice source,
            byte[] opusFrame)
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

            Interlocked.Increment(ref microphoneOpusFramesReceived);
            Interlocked.Exchange(ref lastMicrophoneFrameTimestamp,
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
            }

            EnsureMicrophoneWriterAlive();
            microphoneWriterSignal.Set();
        }

        private void BluetoothMicrophoneSbcFrameReceived(DS4Device source,
            byte[] sbcFrame)
        {
            lock (microphoneSourceLock)
            {
                if (!connected || !ReferenceEquals(source, microphoneSourceDevice))
                {
                    return;
                }
            }

            if (sbcFrame == null || sbcFrame.Length < SbcFrame.HeaderSize)
            {
                return;
            }

            Interlocked.Increment(ref microphoneSbcFramesReceived);
            Interlocked.Exchange(ref lastMicrophoneFrameTimestamp,
                Stopwatch.GetTimestamp());
            byte[] copy = new byte[sbcFrame.Length];
            Buffer.BlockCopy(sbcFrame, 0, copy, 0, copy.Length);
            lock (microphoneQueueLock)
            {
                while (pendingMicrophoneFrames.Count >= MaxPendingMicrophoneFrames)
                {
                    pendingMicrophoneFrames.Dequeue();
                    Interlocked.Increment(ref microphoneFramesDropped);
                }
                pendingMicrophoneFrames.Enqueue(new PendingMicrophoneFrame(
                    MicrophoneCodec.Sbc, copy));
            }

            EnsureMicrophoneWriterAlive();
            microphoneWriterSignal.Set();
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

        private static bool TryApplyBluetoothHapticsOutputReport(DS4Device device, byte[] feedback, int feedbackLength)
        {
            if (feedbackLength < DualSenseExtendedFeedbackLength ||
                device is not DualSenseDevice dualSenseDevice ||
                feedback[DualSenseBluetoothHapticsReportOffset] != 0x32)
            {
                return false;
            }

            return dualSenseDevice.WriteBluetoothHapticsOutputReport(feedback,
                DualSenseBluetoothHapticsReportOffset,
                DualSenseBluetoothHapticsReportLength);
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
                isPhysical = !Global.CheckIfVirtualDevice(devicePath);
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

        public bool GetMicrophoneInterfaceActive(uint busId, string devId)
        {
            ViiperBusDevicesResponse response =
                SendRequest<ViiperBusDevicesResponse>($"bus/{busId}/list");
            if (response?.Devices == null)
            {
                return false;
            }

            foreach (ViiperListedDevice device in response.Devices)
            {
                if (!string.Equals(device.DevId, devId, StringComparison.Ordinal) ||
                    device.DeviceSpecific.ValueKind != JsonValueKind.Object ||
                    !device.DeviceSpecific.TryGetProperty("microphoneInterfaceActive",
                        out JsonElement active))
                {
                    continue;
                }

                return active.ValueKind == JsonValueKind.True ||
                    active.ValueKind == JsonValueKind.String &&
                    bool.TryParse(active.GetString(), out bool parsed) && parsed;
            }

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
            [JsonPropertyName("devId")]
            public string DevId { get; set; }
        }

        private sealed class ViiperDeviceCreateRequest
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }
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
        private uint frameSequence;
        private int disposed;
        private const int FrameV2HeaderLength = 16;
        private const byte FrameMagic0 = (byte)'V';
        private const byte FrameMagic1 = (byte)'P';
        private const byte FrameMagic2 = (byte)'C';
        private const byte FrameMagic3 = (byte)'M';
        private const byte FrameVersionV2 = 0x02;

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

        public void WriteFrameV2(byte frameType, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (data.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(data));
            }

            lock (writeLock)
            {
                if (Volatile.Read(ref disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ViiperDeviceStream));
                }

                byte[] frame = new byte[FrameV2HeaderLength + data.Length];
                frame[0] = FrameMagic0;
                frame[1] = FrameMagic1;
                frame[2] = FrameMagic2;
                frame[3] = FrameMagic3;
                frame[4] = FrameVersionV2;
                frame[5] = frameType;
                frame[6] = (byte)data.Length;
                frame[7] = (byte)(data.Length >> 8);
                uint sequence = frameSequence++;
                frame[8] = (byte)sequence;
                frame[9] = (byte)(sequence >> 8);
                frame[10] = (byte)(sequence >> 16);
                frame[11] = (byte)(sequence >> 24);
                Buffer.BlockCopy(data, 0, frame, FrameV2HeaderLength, data.Length);
                uint crc = ComputeFrameV2Crc(frame);
                frame[12] = (byte)crc;
                frame[13] = (byte)(crc >> 8);
                frame[14] = (byte)(crc >> 16);
                frame[15] = (byte)(crc >> 24);
                stream.Write(frame, 0, frame.Length);
            }
        }

        private static uint ComputeFrameV2Crc(byte[] frame)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 4; i < 12; i++)
            {
                crc = UpdateCrc32(crc, frame[i]);
            }
            for (int i = FrameV2HeaderLength; i < frame.Length; i++)
            {
                crc = UpdateCrc32(crc, frame[i]);
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
        private const float X360RecipInputPosResolution = 1 / 127f;
        private const float X360RecipInputNegResolution = 1 / 128f;
        private const int X360OutputResolution = 32767 - (-32768);

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
