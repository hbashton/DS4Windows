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
            StringAssert.Contains(readLoop, "RequestPhysicalRemoval(");
            Assert.IsTrue(readLoop.LastIndexOf("Report?.Invoke",
                    System.StringComparison.Ordinal) <
                readLoop.LastIndexOf("QueuePhysicalOutputUpdate()",
                    System.StringComparison.Ordinal),
                "Virtual mapping/publication must precede physical output signaling.");
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
