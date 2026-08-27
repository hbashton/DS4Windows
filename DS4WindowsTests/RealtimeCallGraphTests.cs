using System.IO;

namespace DS4WindowsTests
{
    [TestClass]
    public class RealtimeCallGraphTests
    {
        [TestMethod]
        public void PhysicalReadLoopContainsNoOutputIoOrMicrophoneCallback()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Library", "InputDevices",
                "DualSenseDevice.cs"));
            string readLoop = Extract(source,
                "private unsafe void ReadInput()",
                "private static bool IsBluetoothMicrophoneFrame");

            AssertDoesNotContain(readLoop, "PrepareOutReport(");
            AssertDoesNotContain(readLoop,
                "FlushPreparedOutputReport(");
            AssertDoesNotContain(readLoop, "WriteReport(");
            AssertDoesNotContain(readLoop,
                "BluetoothMicrophoneOpusFrameReceived?.Invoke");
            AssertDoesNotContain(readLoop,
                "TryUpdateBluetoothAudioPacer");
            AssertDoesNotContain(readLoop, "StopOutputUpdate(");
            AssertDoesNotContain(readLoop, "RunRemoval(");
            AssertDoesNotContain(readLoop, "AppLogger.LogToGui(");
            AssertDoesNotContain(readLoop, "ChargingChanged?.Invoke");
            AssertDoesNotContain(readLoop, "BatteryChanged?.Invoke");
            AssertDoesNotContain(readLoop, "DisconnectBT(");
            StringAssert.Contains(readLoop,
                "CreatePipelinedInputReportReader(inputReport)");
            StringAssert.Contains(readLoop, "inputReader.ReadNext(");
            AssertDoesNotContain(readLoop, "hDevice.ReadFile(");
            AssertDoesNotContain(readLoop, "QueuePhysicalOutputUpdate();");
            StringAssert.Contains(readLoop, "RequestPhysicalRemoval(");
            Assert.IsTrue(readLoop.LastIndexOf("inputReader.ReadNext(",
                    System.StringComparison.Ordinal) <
                readLoop.LastIndexOf("Report?.Invoke",
                    System.StringComparison.Ordinal),
                "The alternate HID read must be armed before mapping and publication.");
            Assert.IsTrue(readLoop.LastIndexOf("Report?.Invoke",
                    System.StringComparison.Ordinal) <
                readLoop.LastIndexOf("QueuePhysicalOutputKeepaliveIfDue()",
                    System.StringComparison.Ordinal),
                "Virtual mapping/publication must precede physical output maintenance signaling.");
            Assert.IsTrue(readLoop.LastIndexOf("Report?.Invoke",
                    System.StringComparison.Ordinal) <
                readLoop.LastIndexOf("DrainQueuedInputEvents();",
                    System.StringComparison.Ordinal),
                "Status and configuration callbacks must be admitted only after virtual publication.");
            Assert.IsTrue(readLoop.LastIndexOf("Report?.Invoke",
                    System.StringComparison.Ordinal) <
                readLoop.LastIndexOf("RequestPhysicalIdleDisconnect();",
                    System.StringComparison.Ordinal),
                "Idle disconnect must be handed to lifecycle only after virtual publication.");
        }

        [TestMethod]
        public void UsbReportIdGuardPrecedesPhysicalStatusAndControlParsing()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Library", "InputDevices",
                "DualSenseDevice.cs"));
            string readLoop = Extract(source,
                "private unsafe void ReadInput()",
                "internal static bool TryExtractPhysicalInputStatus");
            int guard = readLoop.IndexOf(
                "if (!TryAcceptUsbNormalInputFrame(inputReport))",
                System.StringComparison.Ordinal);
            int status = readLoop.IndexOf(
                "TryExtractPhysicalInputStatus(inputReport, reportOffset",
                System.StringComparison.Ordinal);
            Assert.IsTrue(guard >= 0 && status > guard,
                "USB report ID 0x01 must be accepted before parsing raw status or controls.");

            string rejection = Extract(source,
                "internal bool TryAcceptUsbNormalInputFrame(",
                "internal static bool IsUsbNormalInputFrame(");
            StringAssert.Contains(rejection,
                "Interlocked.Increment(ref usbRejectedInputFrames)");
            AssertDoesNotContain(rejection, "Report?.Invoke");
            AssertDoesNotContain(rejection, "AppLogger.");
        }

        [TestMethod]
        public void PhysicalHidTransfersReuseCompletionEvents()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "HidLibrary", "HidDevice.cs"));
            string read = Extract(source,
                "public unsafe ReadStatus ReadFile(",
                "private EventWaitHandle GetOrCreateReadCompletionEvent");
            string write = Extract(source,
                "public unsafe bool WriteOutputReportViaInterrupt(byte[] outputBuffer,",
                "private EventWaitHandle GetOrCreateInterruptWriteCompletionEvent");

            AssertDoesNotContain(read, "new AutoResetEvent");
            AssertDoesNotContain(read, "new EventWaitHandle");
            StringAssert.Contains(read, "GetOrCreateReadCompletionEvent");
            StringAssert.Contains(read, "AcquireTransferHandle");
            StringAssert.Contains(read, "IsTransferEpochCurrent");
            StringAssert.Contains(read, "transferHandle.IsClosed");
            StringAssert.Contains(read, "CancelIoEx");
            StringAssert.Contains(read, "GetOverlappedResultPinned");
            AssertDoesNotContain(write, "new AutoResetEvent");
            AssertDoesNotContain(write, "new EventWaitHandle");
            StringAssert.Contains(write,
                "GetOrCreateInterruptWriteCompletionEvent");
            StringAssert.Contains(write, "AcquireTransferHandle");
            StringAssert.Contains(write, "IsTransferEpochCurrent");
            StringAssert.Contains(write, "transferHandle.IsClosed");
            StringAssert.Contains(write, "CancelIoEx");
            StringAssert.Contains(write, "GetOverlappedResultPinned");
        }

        [TestMethod]
        public void DualShock4AudioUsesBlockingCadenceAndDefinitiveRetirement()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control",
                "DualShock4BluetoothSpeakerPassthrough.cs")).
                Replace("\r\n", "\n");
            string wait = Extract(source,
                "private void WaitUntil(",
                "private static IntPtr RegisterMultimediaScheduler");
            string dispose = Extract(source,
                "public void Dispose()",
                "private void QueueDeferredResourceRetirement(");

            AssertDoesNotContain(source, "ThreadPriority.Highest");
            AssertDoesNotContain(source, "Thread.SpinWait");
            AssertDoesNotContain(source, "timeBeginPeriod");
            AssertDoesNotContain(source, "timeEndPeriod");
            StringAssert.Contains(wait, "SetWaitableTimer(");
            StringAssert.Contains(wait, "stoppingSignal.WaitOne(");
            AssertDoesNotContain(wait, "CreateHighResolutionTimer(");
            AssertDoesNotContain(wait, "new ");
            StringAssert.Contains(source,
                "AvSetMmThreadCharacteristicsW(\"Audio\"");
            StringAssert.Contains(source,
                "AvSetMmThreadPriority(handle, AvrtPriority.Normal)");
            StringAssert.Contains(source,
                "new Thread(RunDirectStreamWorker)");
            StringAssert.Contains(source,
                "new Thread(RunCaptureStreamWorker)");
            string workerOwner = Extract(source,
                "private void RunDirectStreamWorker()",
                "private void DirectStreamLoop()");
            StringAssert.Contains(workerOwner,
                "MustRetireAfterWorkerExit(stopping,");
            StringAssert.Contains(workerOwner, "RequestStop();");
            StringAssert.Contains(workerOwner,
                "unexpectedWorkerExit(this);");
            string captureStopped = Extract(source,
                "private void Capture_RecordingStopped(",
                "private void StreamLoop()");
            StringAssert.Contains(captureStopped,
                "retireWhenWorkerExits = true;");
            StringAssert.Contains(captureStopped, "RequestStop();");

            string productionSubmit = Extract(source,
                "private ProductionReplaySubmissionResult\n            SubmitProductionReplayFrame(",
                "private FifoBufferedSubmissionResult SubmitFifoBufferedSteadyFrame(");
            StringAssert.Contains(productionSubmit,
                "device.AcquireDualShock4BluetoothAudioMode()");
            StringAssert.Contains(productionSubmit,
                "preparePooledReport");
            AssertDoesNotContain(productionSubmit,
                "ReadDualShock4BluetoothAudioModeSynchronized");
            AssertDoesNotContain(productionSubmit, "() =>");

            string recurringSubmits = string.Concat(
                Extract(source, "private void SubmitEncodedFrames(",
                    "/// <summary>\n        /// Presents one complete reference-transport report"),
                Extract(source, "private bool SubmitEncodedFramesAndWait(",
                    "private MeasuredTransportAsyncSubmissionResult"),
                Extract(source,
                    "private MeasuredTransportAsyncSubmissionResult\n            SubmitEncodedFramesMeasuredTransportAsync(",
                    "private ProductionReplaySubmissionResult"),
                Extract(source,
                    "private FifoBufferedSubmissionResult\n            SubmitFifoBufferedPrimeReport()",
                    "private CreditBufferedSubmissionResult"),
                Extract(source,
                    "private CreditBufferedSubmissionResult\n            SubmitCreditBufferedReport(",
                    "internal static void ApplyProductionReplayAudioMode"));
            AssertDoesNotContain(recurringSubmits,
                "ReadDualShock4BluetoothAudioModeSynchronized");
            AssertDoesNotContain(recurringSubmits, "() =>");
            string referenceSubmit = Extract(source,
                "private bool SubmitEncodedFramesAndWait(",
                "private MeasuredTransportAsyncSubmissionResult");
            StringAssert.Contains(referenceSubmit,
                "speakerWritePool.SendAndWait(report,");
            AssertDoesNotContain(referenceSubmit,
                "WriteOutputReportViaInterrupt(");

            string controlSubmit = Extract(source,
                "public bool TrySendControl(byte[] report, out string error)",
                "public bool TryDrainOutstanding(");
            StringAssert.Contains(controlSubmit, "buffers[slot]");
            StringAssert.Contains(controlSubmit, "outstanding[slot] = true;");
            AssertDoesNotContain(controlSubmit, "new byte[");
            AssertDoesNotContain(controlSubmit, "GCHandle.Alloc(");
            AssertDoesNotContain(controlSubmit, "CreateEventW(");
            AssertDoesNotContain(controlSubmit, "Marshal.AllocHGlobal(");

            int captureDispose = dispose.IndexOf("oldCapture.Dispose()",
                System.StringComparison.Ordinal);
            int workerJoin = dispose.IndexOf("retiringWorker.Join(",
                System.StringComparison.Ordinal);
            int transportDisable = dispose.IndexOf(
                "DisableSpeakerTransport()",
                System.StringComparison.Ordinal);
            Assert.IsTrue(captureDispose >= 0 && workerJoin > captureDispose &&
                transportDisable > workerJoin,
                "WASAPI must close before bounded HID and final mode retirement.");
            StringAssert.Contains(dispose,
                "retiringPool?.CancelPendingWrites()");
            AssertDoesNotContain(dispose, "retiringWorker.Join();");
            StringAssert.Contains(dispose,
                "CancelledWorkerStopMilliseconds");
            StringAssert.Contains(dispose,
                "RetireDualShock4BluetoothSpeakerStreaming(");

            string release = Extract(source,
                "private void ReleaseRetiredResources(",
                "private void ReleaseMeasuredTransportReferenceInputIntervalOverride");
            StringAssert.Contains(release, "captureAvailable.Dispose();");
            StringAssert.Contains(release, "stoppingSignal.Dispose();");

            string barrier = Extract(source,
                "private bool WriteBluetoothAudioControlBarrier(",
                "private bool TrySendBluetoothAudioControl(");
            int staleGuard = barrier.IndexOf(
                "if (!speakerTransportEnabled)",
                System.StringComparison.Ordinal);
            int physicalWrite = barrier.IndexOf(
                "TrySendBluetoothAudioControl(report, out _)",
                System.StringComparison.Ordinal);
            Assert.IsTrue(staleGuard >= 0 && physicalWrite > staleGuard,
                "A callback captured before unregister must be rejected after final disable.");
            AssertDoesNotContain(barrier, "stopping ||");

            string effectPublish = Extract(source,
                "private bool PublishBluetoothAudioEffect(",
                "private bool TrySendBluetoothAudioControl(");
            StringAssert.Contains(effectPublish,
                "pool.TryPublishLatestEffect(report)");
            AssertDoesNotContain(effectPublish, "TrySendControl(");
            AssertDoesNotContain(effectPublish, "WaitForSingleObject(");
            AssertDoesNotContain(effectPublish, "DrainOutstanding");

            string mailboxSubmit = Extract(source,
                "private bool TrySubmitPendingEffectNoLock(",
                "public bool TrySendControl(");
            StringAssert.Contains(mailboxSubmit,
                "effectMailbox.TryClaim(");
            StringAssert.Contains(mailboxSubmit,
                "effectMailbox.Reject(effectVersion)");
            StringAssert.Contains(mailboxSubmit,
                "effectVersions[slot] = effectVersion");
            AssertDoesNotContain(mailboxSubmit, "WaitForSingleObject(");
            string completionReap = Extract(source,
                "private void ReapCompletedNoLock()",
                "public void CancelPendingWrites()");
            StringAssert.Contains(completionReap,
                "effectMailbox.Reject(effectVersion)");
            StringAssert.Contains(completionReap,
                "effectMailbox.Acknowledge(effectVersion)");

            string deviceSource = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Library", "DS4Device.cs"));
            string inputLoop = Extract(deviceSource,
                "protected unsafe void performDs4Input()",
                "protected Debouncer SetupDebouncer()");
            string sendOutput = Extract(deviceSource,
                "private bool sendOutputReport(",
                "// Perform outReportBuffer copy on a separate thread");
            StringAssert.Contains(inputLoop,
                "if (sendOutputReport(syncWriteReport, forceWrite))");
            Assert.IsTrue(inputLoop.IndexOf(
                    "if (sendOutputReport(syncWriteReport, forceWrite))",
                    System.StringComparison.Ordinal) <
                inputLoop.IndexOf("forceWrite = false;",
                    inputLoop.IndexOf(
                        "if (sendOutputReport(syncWriteReport, forceWrite))",
                        System.StringComparison.Ordinal),
                    System.StringComparison.Ordinal),
                "A nonblocking mode-transition deferral must retain forced physical output.");
            StringAssert.Contains(sendOutput,
                "bluetoothAudioState.TryAcquireRead(");
            AssertDoesNotContain(sendOutput,
                "bluetoothAudioState.TryReadSynchronized(");
            AssertDoesNotContain(sendOutput, "=>");
            StringAssert.Contains(sendOutput,
                "audioEffectDeferred = true;");
            StringAssert.Contains(sendOutput,
                "if (!outputWritten && !audioEffectDeferred)");
            StringAssert.Contains(sendOutput,
                "return !audioEffectDeferred;");
            Assert.IsTrue(sendOutput.IndexOf(
                    "if (outputWritten)",
                    System.StringComparison.Ordinal) <
                sendOutput.IndexOf(
                    "outReportBuffer.CopyTo(outputReport, 0);",
                    sendOutput.IndexOf("if (outputWritten)",
                        System.StringComparison.Ordinal),
                    System.StringComparison.Ordinal),
                "The last-sent effect state must advance only after mailbox admission.");

            string ownerSource = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control",
                "DualShock4AudioPassthrough.cs"));
            StringAssert.Contains(ownerSource,
                "EnqueueWhileHolding(syncRoot");
            AssertDoesNotContain(ownerSource,
                "slotWorkQueues[slot].Enqueue(");
            AssertDoesNotContain(ownerSource, "slotWorkerLocks");
            AssertDoesNotContain(ownerSource, "StartBackgroundThread");
            AssertDoesNotContain(ownerSource, "TaskScheduler.Default");
            StringAssert.Contains(ownerSource,
                "Priority = ThreadPriority.BelowNormal");
            StringAssert.Contains(ownerSource, "previous?.RequestStop();");
            StringAssert.Contains(ownerSource, "playback?.RequestStop();");
            StringAssert.Contains(ownerSource,
                "slots[slot]?.IsOperational == true");
            StringAssert.Contains(ownerSource,
                "HandleUnexpectedWorkerExit(slot, generation, owner)");
            StringAssert.Contains(ownerSource,
                "TryEnqueueUnexpectedRetirementWhileHolding(");
            StringAssert.Contains(ownerSource,
                "queue.EnqueueWhileHolding(ownerGate, retirement)");
        }

        [TestMethod]
        public void RemovalCallbackRunsAfterLifecycleOwnedPhysicalRetirement()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Library", "InputDevices",
                "DualSenseDevice.cs"));
            string lifecycle = Extract(source,
                "private void PhysicalLifecycleLoop()",
                "private void RequestPhysicalLifecycleShutdown");

            int stop = lifecycle.IndexOf("StopPhysicalWorkersCore();",
                System.StringComparison.Ordinal);
            int finalOutput = lifecycle.IndexOf("FinalizePhysicalOutput();",
                System.StringComparison.Ordinal);
            int diagnostics = lifecycle.IndexOf(
                "ReportPhysicalInputFailure();",
                System.StringComparison.Ordinal);
            int removal = lifecycle.IndexOf("RunRemoval();",
                System.StringComparison.Ordinal);
            Assert.IsTrue(stop >= 0 && finalOutput > stop &&
                diagnostics > finalOutput && removal > diagnostics,
                "Removal notification must follow worker retirement and final physical output on the lifecycle owner.");
        }

        [TestMethod]
        public void MicrophoneCompatibilityMonitorNeverUsesBroadStatusQuery()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));
            string compatibility = Extract(source,
                "private void MicrophoneInterfaceMonitorLoop",
                "private void MicrophoneInterfaceEventLoop");
            string eventPath = Extract(source,
                "private void MicrophoneInterfaceEventLoop",
                "internal static bool TryParseMicrophoneInterfaceStateEvent");

            AssertDoesNotContain(compatibility,
                "GetMicrophoneInterfaceStatus(");
            StringAssert.Contains(compatibility,
                "GetNarrowMicrophoneInterfaceStatus(");
            AssertDoesNotContain(eventPath, "client.");
        }

        [TestMethod]
        public void MappedAndMicrophoneProducersCannotStartWorkers()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));
            string mappedProducer = Extract(source,
                "public override void ConvertandSendReport",
                "public override void ResetState");
            string opusProducer = Extract(source,
                "private void BluetoothMicrophoneOpusFrameReceived",
                "private void BluetoothMicrophoneSbcFrameReceived");
            string sbcProducer = Extract(source,
                "private void BluetoothMicrophoneSbcFrameReceived",
                "private void ApplyDualSenseTriggerFeedback");

            AssertDoesNotContain(mappedProducer, "EnsureStateWriterAlive");
            AssertDoesNotContain(mappedProducer, "StartStateWriter");
            AssertDoesNotContain(mappedProducer, "new Thread");
            AssertDoesNotContain(mappedProducer, "WriteFrame");
            AssertDoesNotContain(mappedProducer, "deviceStream");
            AssertDoesNotContain(mappedProducer, "TryRecoverStream");
            AssertDoesNotContain(mappedProducer, "streamRecoveryGate");
            StringAssert.Contains(mappedProducer, "inputScheduler.Publish");
            StringAssert.Contains(mappedProducer, "writerSignal.Set");
            AssertDoesNotContain(opusProducer,
                "EnsureMicrophoneWriterAlive");
            AssertDoesNotContain(sbcProducer,
                "EnsureMicrophoneWriterAlive");
            AssertDoesNotContain(opusProducer, "WriteFrame");
            AssertDoesNotContain(sbcProducer, "WriteFrame");
            AssertDoesNotContain(opusProducer, "deviceStream");
            AssertDoesNotContain(sbcProducer, "deviceStream");
            StringAssert.Contains(opusProducer,
                "TryEnqueuePendingMicrophoneFrame");
            StringAssert.Contains(opusProducer,
                "microphoneWriterSignal.Set");
            StringAssert.Contains(sbcProducer,
                "TryEnqueuePendingMicrophoneFrame");
            StringAssert.Contains(sbcProducer,
                "microphoneWriterSignal.Set");
        }

        [TestMethod]
        public void ProductionFramedWriterUsesOnlyOwnerEntryPoint()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));
            string microphoneOwner = Extract(source,
                "private bool TryWritePreparedMicrophoneFromWriter",
                "private void EnsureMicrophoneWriterAlive");
            string inputOwner = Extract(source,
                "private ViiperFrameWriteTiming WriteState",
                "private void WriteMicrophoneFrame");
            string compatibilityEntry = Extract(source,
                "internal ViiperFrameWriteTiming WriteFrameTimed",
                "internal ViiperFrameWriteTiming WriteFrameFromOwnerTimed");
            string ownerEntry = Extract(source,
                "internal ViiperFrameWriteTiming WriteFrameFromOwnerTimed",
                "private static void ValidateFrameArguments");
            string frameCore = Extract(source,
                "private ViiperFrameWriteTiming WriteFrameCore",
                "public byte[] ReadFrame");

            StringAssert.Contains(microphoneOwner,
                "WriteFrameFromOwnerTimed");
            StringAssert.Contains(inputOwner,
                "WriteFrameFromOwnerTimed");
            AssertDoesNotContain(microphoneOwner, ".WriteFrame(");
            AssertDoesNotContain(inputOwner, ".WriteFrameTimed(");
            StringAssert.Contains(compatibilityEntry,
                "lock (frameWriterOwnership)");
            AssertDoesNotContain(ownerEntry, "frameWriterOwnership");

            int crc = frameCore.IndexOf("ComputeFramedCrc(",
                System.StringComparison.Ordinal);
            int sendOwnership = frameCore.IndexOf("lock (sendLock)",
                System.StringComparison.Ordinal);
            int write = frameCore.IndexOf("stream.Write(frame, 0, frameLength)",
                System.StringComparison.Ordinal);
            Assert.IsTrue(crc >= 0 && sendOwnership > crc &&
                write > sendOwnership,
                "The owner must finish framing/CRC before one contiguous write under narrow send ownership.");
        }

        [TestMethod]
        public void StreamRecoveryUsesAtomicElectionWithoutSlowMonitorLease()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));
            string election = Extract(source,
                "private bool TryRecoverStream",
                "private bool RecoverStreamAsOwner");
            string owner = Extract(source,
                "private bool RecoverStreamAsOwner",
                "internal static int GetStreamRecoveryBackoffMilliseconds");

            StringAssert.Contains(election,
                "streamRecoveryGate.ExecuteOrWait");
            AssertDoesNotContain(election, "lock (");
            AssertDoesNotContain(owner, "streamRecoveryLock");
            AssertDoesNotContain(source,
                "lock (streamRecoveryLock)");
            AssertDoesNotContain(owner,
                "feedbackDispatchGenerationBarrier");

            int open = owner.IndexOf("client.OpenExistingDeviceStream(",
                System.StringComparison.Ordinal);
            int generationLease = owner.IndexOf(
                "lock (feedbackCallbackAdmissionLock)",
                System.StringComparison.Ordinal);
            int generationPublication = owner.IndexOf(
                "Volatile.Write(ref deviceStream, replacement);",
                System.StringComparison.Ordinal);
            int callbackRetirement = owner.IndexOf(
                "WaitForFeedbackDispatchCallbacks();",
                System.StringComparison.Ordinal);
            int clearPending = owner.IndexOf(
                "feedbackDispatchBuffer.ClearPending();",
                System.StringComparison.Ordinal);
            int startReader = owner.IndexOf("StartFeedbackReader();",
                System.StringComparison.Ordinal);
            Assert.IsTrue(open >= 0 && generationLease > open &&
                generationPublication > generationLease &&
                callbackRetirement > generationPublication &&
                clearPending > callbackRetirement &&
                startReader > clearPending,
                "Network/controller creation, queue reset, and reader startup must remain outside the short generation lease.");
        }

        [TestMethod]
        public void FeedbackAndMicrophoneControlNeverHoldGenerationLocksAcrossCallbacksOrIo()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));

            AssertDoesNotContain(source,
                "feedbackDispatchGenerationBarrier");
            AssertDoesNotContain(source,
                "microphoneControlTransitionLock");

            string callbackAdmission = Extract(source,
                "private bool TryBeginFeedbackDispatchCallback",
                "private bool TryBeginFeedbackReaderCallback");
            string callbackRelease = Extract(source,
                "private void EndFeedbackCallback()",
                "private bool IsFeedbackDispatchGenerationActive");
            AssertDoesNotContain(callbackAdmission, "ApplyFeedback(");
            AssertDoesNotContain(callbackAdmission, "subscriber(");
            AssertDoesNotContain(callbackAdmission, "AppLogger.");
            AssertDoesNotContain(callbackRelease, "ApplyFeedback(");
            AssertDoesNotContain(callbackRelease, "AppLogger.");

            string physicalMicrophone = Extract(source,
                "private bool ApplyPhysicalBluetoothMicrophoneState",
                "private static bool SetPhysicalBluetoothMicrophoneStreaming");
            int physicalCall = physicalMicrophone.IndexOf(
                "SetPhysicalBluetoothMicrophoneStreaming(source,",
                System.StringComparison.Ordinal);
            int sourceSnapshot = physicalMicrophone.IndexOf(
                "lock (microphoneSourceLock)",
                System.StringComparison.Ordinal);
            Assert.IsTrue(physicalCall >= 0 && sourceSnapshot > physicalCall,
                "Physical microphone I/O must complete before the short source-state snapshot lock is acquired.");
        }

        [TestMethod]
        public void NativeOutputTraceFormatsLogsAndQueriesProcessesOutsideTraceLock()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));
            string trace = Extract(source,
                "private void TraceNativeGameOutput(byte[] feedback",
                "internal static bool HasMeaningfulNativeGameOutput");
            string idle = Extract(source,
                "private void TraceNativeGameOutputIdleBoundary()",
                "private void CaptureForegroundNativeGameOutputOwner()");
            string capture = Extract(source,
                "private void CaptureForegroundNativeGameOutputOwner()",
                "private void ClearNativeGameOutputProcessLease()");

            Assert.IsTrue(trace.IndexOf("AppLogger.LogToGui(",
                    System.StringComparison.Ordinal) >
                trace.IndexOf("if (sessionStarted)",
                    System.StringComparison.Ordinal));
            Assert.IsTrue(idle.IndexOf("AppLogger.LogToGui(",
                    System.StringComparison.Ordinal) >
                idle.IndexOf("if (logIdleBoundary)",
                    System.StringComparison.Ordinal));
            Assert.IsTrue(capture.IndexOf("IsProcessAlive(observedOwner)",
                    System.StringComparison.Ordinal) <
                capture.LastIndexOf("lock (nativeGameOutputTraceLock)",
                    System.StringComparison.Ordinal));
            Assert.IsTrue(capture.IndexOf("candidate.Dispose();",
                    System.StringComparison.Ordinal) <
                capture.IndexOf("lock (nativeGameOutputTraceLock)",
                    System.StringComparison.Ordinal) ||
                capture.LastIndexOf("candidate.Dispose();",
                    System.StringComparison.Ordinal) >
                capture.LastIndexOf("lock (nativeGameOutputTraceLock)",
                    System.StringComparison.Ordinal));
        }

        [TestMethod]
        public void NativeOutputDispatchOwnsOnePersistentBuildIntoScratch()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperOutDevice.cs"));
            string controlOwner = Extract(source,
                "private void FeedbackControlDispatchLoop",
                "private void DispatchFeedbackControl");
            string nativeApply = Extract(source,
                "private bool TryApplyNativeDualSenseOutputReport",
                "internal static void PrepareNativeDualSenseOutputReportForProfileInto");
            string buildInto = Extract(source,
                "internal static void PrepareNativeDualSenseOutputReportForProfileInto",
                "internal static void CopyPreparedNativeDualSenseStateIntoCombinedCarrier");

            int scratch = controlOwner.IndexOf(
                "byte[] nativeOutputScratch =",
                System.StringComparison.Ordinal);
            int loop = controlOwner.IndexOf("while (",
                System.StringComparison.Ordinal);
            Assert.IsTrue(scratch >= 0 && loop > scratch,
                "The dispatch owner must allocate its fixed native scratch before entering the work loop.");
            AssertDoesNotContain(nativeApply, "new byte[");
            AssertDoesNotContain(buildInto, "new byte[");
            StringAssert.Contains(nativeApply,
                "PrepareNativeDualSenseOutputReportForProfileInto");
        }

        [TestMethod]
        public void PhysicalWorkerStartUsesAtomicElectionWithoutLifecycleMonitor()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Library", "InputDevices",
                "DualSenseDevice.cs"));
            string start = Extract(source,
                "private void StartPhysicalWorkers()",
                "private void StopPhysicalWorkersCore()");
            string request = Extract(source,
                "private void RequestPhysicalLifecycleShutdown",
                "private void RequestPhysicalRemoval()");

            AssertDoesNotContain(source, "physicalWorkerLifecycleLock");
            StringAssert.Contains(start,
                "Interlocked.CompareExchange(");
            StringAssert.Contains(start,
                "physicalWorkerStartCompleted.Reset();");
            StringAssert.Contains(start,
                "physicalLifecycleExternalRequestVersion");
            StringAssert.Contains(start,
                "DrainPhysicalLifecycleSignal();");
            Assert.IsTrue(start.IndexOf("StopPhysicalWorkersCore();",
                    System.StringComparison.Ordinal) <
                start.IndexOf("new Thread(",
                    System.StringComparison.Ordinal));
            StringAssert.Contains(request,
                "physicalWorkerStartCompleted.WaitOne(10);");
            AssertDoesNotContain(request, "lock (");
        }

        [TestMethod]
        public void BluetoothRecoveryRetryIsGenerationOwnedAndRetiredBeforeRestart()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Library", "InputDevices",
                "DualSenseDevice.cs"));
            string recovery = Extract(source,
                "private void RequestUnifiedBluetoothOutputTransportRecovery()",
                "private void StopBluetoothAudioPacerLocked()");
            string start = Extract(source,
                "private void StartPhysicalWorkers()",
                "private void StopPhysicalWorkersCore()");
            string stop = Extract(source,
                "private void StopPhysicalWorkersCore()",
                "private static void JoinWorker");

            StringAssert.Contains(recovery, "physicalOutputGeneration");
            StringAssert.Contains(recovery,
                "IsBluetoothOutputRecoveryGenerationActive");
            StringAssert.Contains(recovery,
                "bluetoothAudioRecoveryWorkerIdle");
            StringAssert.Contains(recovery,
                "bluetoothAudioRecoveryWake.WaitOne(");
            AssertDoesNotContain(recovery, "Thread.Sleep(");
            StringAssert.Contains(start,
                "RetireBluetoothOutputRecoveryWorker();");
            StringAssert.Contains(stop,
                "RetireBluetoothOutputRecoveryWorker();");
        }

        [TestMethod]
        public void CombinedControlAndSpeakerShareAdmissionBeforeCompletionWait()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Library", "InputDevices",
                "DualSenseDevice.cs")).Replace("\r\n", "\n");
            string queue = Extract(source,
                "private bool TryQueueBluetoothControlThroughAudioPacer",
                "private bool WaitForBluetoothControlThroughAudioPacer");
            string wait = Extract(source,
                "private bool WaitForBluetoothControlThroughAudioPacer",
                "private bool RefreshBluetoothAudioPacerTemplateFromCache");
            string control = Extract(source,
                "in DualSensePhysicalOutputSnapshot outputState)\n        {",
                "private bool TryWriteCachedBluetoothCombinedSpeakerReport(");
            string speaker = Extract(source,
                "private bool TryWriteCachedBluetoothCombinedSpeakerReportCore",
                "private static void ApplyBluetoothCombinedCrc");

            AssertDoesNotContain(queue, "WaitForControlReport(");
            AssertDoesNotContain(wait,
                "bluetoothCombinedTransportWriteLock");
            int admissionLock = control.IndexOf(
                "lock (bluetoothCombinedTransportWriteLock)",
                System.StringComparison.Ordinal);
            int queueAdmission = control.IndexOf(
                "TryQueueBluetoothControlThroughAudioPacer(",
                System.StringComparison.Ordinal);
            int completionWait = control.IndexOf(
                "WaitForBluetoothControlThroughAudioPacer(",
                System.StringComparison.Ordinal);
            Assert.IsTrue(admissionLock >= 0 &&
                queueAdmission > admissionLock &&
                completionWait > queueAdmission,
                "Sequence reservation must enqueue control under the shared admission boundary before any completion wait.");
            StringAssert.Contains(speaker,
                "TryQueueBluetoothAudioPacerReport(combined,");
        }

        [TestMethod]
        public void ControlServiceShutdownRetiresAsyncMonitoringOwners()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "ControlService.cs"));
            string shutdown = Extract(source, "private void ShutDownCore()",
                "private void DS4Devices_RequestElevation");
            Assert.IsTrue(shutdown.Contains("DisposeRealtimeWorkers();",
                System.StringComparison.Ordinal));
            Assert.IsTrue(shutdown.Contains(
                "oscMonitoringWorker.Dispose();",
                System.StringComparison.Ordinal));
            Assert.IsTrue(shutdown.Contains(
                "reportDiagnosticsWorker.Dispose();",
                System.StringComparison.Ordinal));
            Assert.IsTrue(shutdown.Contains(
                "Interlocked.Exchange(ref realtimeWorkersDisposed, 1)",
                System.StringComparison.Ordinal));
        }

        [TestMethod]
        public void ExplicitRateWaiterDoesNotUseUnmeasuredBusySpin()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                "DS4Windows", "DS4Control", "Viiper",
                "ViiperHighResolutionWaiter.cs"));
            AssertDoesNotContain(source, "SpinWait");
        }

        private static void AssertDoesNotContain(string source, string value)
        {
            Assert.IsFalse(source.Contains(value,
                System.StringComparison.Ordinal), value);
        }

        private static string Extract(string source, string startMarker,
            string endMarker)
        {
            int start = source.IndexOf(startMarker,
                System.StringComparison.Ordinal);
            int end = source.IndexOf(endMarker, start + startMarker.Length,
                System.StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, startMarker);
            Assert.IsTrue(end > start, endMarker);
            return source.Substring(start, end - start);
        }

        private static string FindRepositoryFile(params string[] parts)
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(new[] { directory.FullName }
                    .Concat(parts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
            Assert.Fail("Unable to locate repository source file: " +
                Path.Combine(parts));
            return null;
        }
    }
}
