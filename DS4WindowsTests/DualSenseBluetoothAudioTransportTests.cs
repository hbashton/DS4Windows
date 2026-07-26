using DS4Windows;
using DS4Windows.InputDevices;
using System.Collections;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseBluetoothAudioTransportTests
    {
        [TestMethod]
        public void RealtimeWriterBoundsNormalFragmentedAudioToTwoIrps()
        {
            Assert.IsFalse(DualSenseBluetoothRealtimeWriter
                .ShouldThrottleFragmentedAudioWrites(0));
            Assert.IsFalse(DualSenseBluetoothRealtimeWriter
                .ShouldThrottleFragmentedAudioWrites(1));
            Assert.IsTrue(DualSenseBluetoothRealtimeWriter
                .ShouldThrottleFragmentedAudioWrites(2));
            Assert.IsTrue(DualSenseBluetoothRealtimeWriter
                .ShouldThrottleFragmentedAudioWrites(3));
        }

        private static readonly FieldInfo ConnectionTypeField =
            typeof(DS4Device).GetField("conType",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo EventQueueField =
            typeof(DS4Device).GetField("eventQueue",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo HasInputEventsField =
            typeof(DS4Device).GetField("hasInputEvts",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OutputTransportStoppingField =
            typeof(DualSenseDevice).GetField(
                "bluetoothOutputTransportStopping",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MicrophoneStreamingRequestedField =
            typeof(DualSenseDevice).GetField(
                "bluetoothMicrophoneStreamingRequested",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MicrophoneControlPendingField =
            typeof(DualSenseDevice).GetField(
                "bluetoothMicrophoneControlUpdatePending",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerClockActiveClaimField =
            typeof(DualSenseDevice).GetField(
                "bluetoothSpeakerClockActiveClaim",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerClockLeaseExpiryField =
            typeof(DualSenseDevice).GetField(
                "bluetoothSpeakerClockLeaseExpiryTimestamp",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerReportSequenceField =
            typeof(DualSenseDevice).GetField(
                "bluetoothCombinedSpeakerReportSequence",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerPacketSequenceField =
            typeof(DualSenseDevice).GetField(
                "bluetoothCombinedSpeakerPacketSequence",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CachedCombinedReportField =
            typeof(DualSenseDevice).GetField(
                "latestBluetoothCombinedSpeakerReport",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NativeStateTimestampField =
            typeof(DualSenseDevice).GetField(
                "latestBluetoothCombinedNativeStateTimestamp",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo BuildCombinedControlReportMethod =
            typeof(DualSenseDevice).GetMethod(
                "BuildBluetoothCombinedControlReport",
                BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo DrainQueuedInputEventsMethod =
            typeof(DualSenseDevice).GetMethod(
                "DrainQueuedInputEvents",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RecordBluetoothMicrophoneFrameMethod =
            typeof(DualSenseDevice).GetMethod(
                "RecordBluetoothMicrophoneFrame",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo
            ApplyBluetoothMicrophoneStreamingRequestMethod =
                typeof(DualSenseDevice).GetMethod(
                    "ApplyBluetoothMicrophoneStreamingRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, types: new[] { typeof(byte[]) },
                    modifiers: null);
        private static readonly MethodInfo
            RequiresCompletionAwareBluetoothControlWriteMethod =
                typeof(DualSenseDevice).GetMethod(
                    "RequiresCompletionAwareBluetoothControlWrite",
                    BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo ClaimBluetoothSpeakerClockMethod =
            typeof(DualSenseDevice).GetMethod(
                "ClaimBluetoothSpeakerClock",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo
            PacerReferenceRetainsBluetoothTransportOwnershipMethod =
                typeof(DualSenseDevice).GetMethod(
                    "PacerReferenceRetainsBluetoothTransportOwnership",
                    BindingFlags.Static | BindingFlags.NonPublic);

        [TestMethod]
        public void DiagnosticPcmTraceHasRecoverableStreamingHeaderImmediately()
        {
            string path = Path.Combine(Path.GetTempPath(),
                $"ds4windows-dualsense-trace-{Guid.NewGuid():N}.wav");
            try
            {
                using Pcm16WaveTraceWriter writer =
                    Pcm16WaveTraceWriter.TryCreate(path, 48000, 2);
                Assert.IsNotNull(writer);

                byte[] header = new byte[44];
                using (var stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                {
                    Assert.AreEqual(header.Length, stream.Read(header, 0,
                        header.Length));
                }

                CollectionAssert.AreEqual("RIFF"u8.ToArray(),
                    header.AsSpan(0, 4).ToArray());
                CollectionAssert.AreEqual("WAVE"u8.ToArray(),
                    header.AsSpan(8, 4).ToArray());
                CollectionAssert.AreEqual("data"u8.ToArray(),
                    header.AsSpan(36, 4).ToArray());
                Assert.AreEqual(uint.MaxValue,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        header.AsSpan(40, 4)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void DiagnosticPcmTraceDrainsAndFinalizesExactWaveLengths()
        {
            string path = Path.Combine(Path.GetTempPath(),
                $"ds4windows-dualsense-trace-{Guid.NewGuid():N}.wav");
            byte[] pcm = Enumerable.Range(0, 4096)
                .Select(index => (byte)(index * 31)).ToArray();
            try
            {
                using (Pcm16WaveTraceWriter writer =
                    Pcm16WaveTraceWriter.TryCreate(path, 32000, 2))
                {
                    Assert.IsNotNull(writer);
                    writer.Write(pcm, 0, pcm.Length);
                }

                byte[] wave = File.ReadAllBytes(path);
                Assert.AreEqual(44 + pcm.Length, wave.Length);
                Assert.AreEqual((uint)(36 + pcm.Length),
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        wave.AsSpan(4, 4)));
                Assert.AreEqual((uint)pcm.Length,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        wave.AsSpan(40, 4)));
                CollectionAssert.AreEqual(pcm,
                    wave.AsSpan(44).ToArray());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void MicrophoneControlWriteBypassesInputEventQueue()
        {
            DualSenseDevice device = CreateBluetoothDevice();

            try
            {
                // A hardware-less device may reject a direct HID write. The
                // regression contract is that microphone control must never be
                // serialized behind input reports, so only queue state matters.
                device.SetBluetoothMicrophoneStreaming(true);
            }
            catch
            {
            }

            Assert.AreEqual(0, GetEventQueue(device).Count,
                "Mic control entered the input event queue and can starve when mic input stalls.");
            Assert.IsFalse(GetFieldValue<bool>(HasInputEventsField, device),
                "Mic control marked an input-thread event pending.");
        }

        [TestMethod]
        public void MicrophoneOnlyInputCanDrainOrdinaryControllerEvents()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            int invoked = 0;
            device.queueEvent(() => invoked++);

            Assert.IsNotNull(DrainQueuedInputEventsMethod);
            DrainQueuedInputEventsMethod.Invoke(device, null);

            Assert.AreEqual(1, invoked);
            Assert.AreEqual(0, GetEventQueue(device).Count);
            Assert.IsFalse(GetFieldValue<bool>(HasInputEventsField, device));
        }

        [TestMethod]
        public void ShutdownOwnershipGateRejectsLateSpeakerFrameBeforeHandoff()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(OutputTransportStoppingField, device, 1);

            bool accepted = device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200);

            Assert.IsFalse(accepted,
                "A late producer callback restarted Bluetooth output during shutdown.");
            Assert.IsFalse(device.BluetoothCombinedOutputTransportEnabled,
                "The rejected callback initialized a new combined transport.");
            Assert.AreEqual(1L, device.BluetoothSpeakerFramesDropped);
        }

        [TestMethod]
        public void FaultedPacerReferenceStillRetainsTransportOwnership()
        {
            Assert.IsNotNull(
                PacerReferenceRetainsBluetoothTransportOwnershipMethod);

            Assert.IsTrue((bool)
                PacerReferenceRetainsBluetoothTransportOwnershipMethod.Invoke(
                    null, new object[] { true }),
                "A retained faulted/stopping helper reference allowed a competing direct HID writer.");
            Assert.IsFalse((bool)
                PacerReferenceRetainsBluetoothTransportOwnershipMethod.Invoke(
                    null, new object[] { false }));
        }

        [TestMethod]
        public void FailedFirstSpeakerSubmissionDoesNotClaimActiveClock()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;

            bool accepted = device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200);

            Assert.IsFalse(accepted,
                "The hardware-less speaker submission unexpectedly succeeded.");
            Assert.AreEqual(0L, GetFieldValue<long>(
                SpeakerClockActiveClaimField, device),
                "A failed first frame left a false active speaker clock.");
            Assert.AreEqual(0L, GetFieldValue<long>(
                SpeakerClockLeaseExpiryField, device));
        }

        [TestMethod]
        public void RejectedSpeakerSubmissionDoesNotConsumeSequence()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;
            SetFieldValue(SpeakerReportSequenceField, device, (byte)9);
            SetFieldValue(SpeakerPacketSequenceField, device, (byte)41);

            Assert.IsFalse(device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200));
            Assert.AreEqual((byte)9, GetFieldValue<byte>(
                SpeakerReportSequenceField, device));
            Assert.AreEqual((byte)41, GetFieldValue<byte>(
                SpeakerPacketSequenceField, device));
        }

        [TestMethod]
        public void FailedLaterSpeakerSubmissionPreservesPriorAcceptedLease()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;
            Assert.IsNotNull(ClaimBluetoothSpeakerClockMethod);
            long existingClaim = (long)ClaimBluetoothSpeakerClockMethod.Invoke(
                device, new object[] { 3000 });
            long existingExpiry = GetFieldValue<long>(
                SpeakerClockLeaseExpiryField, device);

            bool accepted = device.SetBluetoothSpeakerAudioFrame(
                new byte[200], 200);

            Assert.IsFalse(accepted,
                "The hardware-less speaker submission unexpectedly succeeded.");
            Assert.AreEqual(existingClaim, GetFieldValue<long>(
                SpeakerClockActiveClaimField, device),
                "A failed later frame cleared the lease earned by queued audio.");
            Assert.AreEqual(existingExpiry, GetFieldValue<long>(
                SpeakerClockLeaseExpiryField, device),
                "A failed later frame falsely extended the active clock lease.");
        }

        [TestMethod]
        public void LegacySpeakerCompatibilityPropagatesSubmissionFailure()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.EnableSpeakerOutput = true;
            byte[] report = new byte[334];
            report[0] = 0x35;
            report[11] = 0x93;
            report[12] = 200;

            Assert.IsFalse(device.WriteBluetoothSpeakerAudioOutputReport(
                report, 0, report.Length),
                "The compatibility API hid the physical speaker submission failure.");
        }

        [TestMethod]
        public void AudioOnlyCombinedCarrierPreservesProfileLightbar()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.LightBarColor = new DS4Color(12, 34, 56);
            byte[] report = BuildCombinedControlReport(0, 0, false);

            device.WriteBluetoothCombinedHapticsAudioOutputReport(report, 0,
                report.Length, hasNativeGameState: false);

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            Assert.AreEqual((byte)12, cached[13 + 44]);
            Assert.AreEqual((byte)34, cached[13 + 45]);
            Assert.AreEqual((byte)56, cached[13 + 46]);
            Assert.AreEqual(0L, GetFieldValue<long>(
                NativeStateTimestampField, device));
        }

        [TestMethod]
        public void NativeGameCombinedCarrierRemainsAuthoritative()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.LightBarColor = new DS4Color(12, 34, 56);
            byte[] report = BuildCombinedControlReport(0, 0, false);
            report[13 + 44] = 90;
            report[13 + 45] = 91;
            report[13 + 46] = 92;

            device.WriteBluetoothCombinedHapticsAudioOutputReport(report, 0,
                report.Length, hasNativeGameState: true);

            byte[] cached = GetFieldValue<byte[]>(CachedCombinedReportField,
                device);
            Assert.AreEqual((byte)90, cached[13 + 44]);
            Assert.AreEqual((byte)91, cached[13 + 45]);
            Assert.AreEqual((byte)92, cached[13 + 46]);
            Assert.IsTrue(GetFieldValue<long>(NativeStateTimestampField,
                device) > 0);
        }

        [TestMethod]
        public void ShutdownOwnershipGateRejectsMicrophoneRearmWithoutMutation()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(OutputTransportStoppingField, device, 1);

            bool accepted = device.SetBluetoothMicrophoneStreaming(true);

            Assert.IsFalse(accepted,
                "A late VIIPER microphone re-arm was accepted during shutdown.");
            Assert.AreEqual(0, GetFieldValue<int>(
                MicrophoneStreamingRequestedField, device),
                "The rejected re-arm changed requested microphone state.");
            Assert.AreEqual(0, GetFieldValue<int>(
                MicrophoneControlPendingField, device),
                "The rejected re-arm created a pending control transition.");
            Assert.IsFalse(device.BluetoothCombinedOutputTransportEnabled,
                "The rejected re-arm initialized a new combined transport.");
        }

        [DataTestMethod]
        [DataRow(true, true, false, true)]
        [DataRow(true, false, false, true)]
        [DataRow(false, false, true, false)]
        [DataRow(false, false, false, false)]
        [DataRow(false, true, true, false)]
        [DataRow(false, true, false, false)]
        public void OnlyExplicitControlBarrierWaitsForPhysicalCompletion(
            bool completionRequested, bool speakerClockActive,
            bool pacerOwnsTransport, bool expected)
        {
            Assert.IsNotNull(
                RequiresCompletionAwareBluetoothControlWriteMethod);
            bool actual = (bool)
                RequiresCompletionAwareBluetoothControlWriteMethod.Invoke(null,
                    new object[] { completionRequested, speakerClockActive,
                        pacerOwnsTransport });

            Assert.AreEqual(expected, actual,
                "Ordinary idle helper control must queue physically without blocking the caller for completion.");
        }

        [TestMethod]
        public void PhysicalMicrophoneFrameCommitsPendingEnable()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(MicrophoneStreamingRequestedField, device, 1);
            SetFieldValue(MicrophoneControlPendingField, device, 1);
            byte[] microphoneReport = new byte[78];
            microphoneReport[0] = 0x31;
            microphoneReport[1] = 0x02;

            Assert.IsNotNull(RecordBluetoothMicrophoneFrameMethod);
            RecordBluetoothMicrophoneFrameMethod.Invoke(device,
                new object[] { microphoneReport });

            Assert.AreEqual(0, GetFieldValue<int>(
                MicrophoneControlPendingField, device),
                "Physical input did not commit the pending microphone enable.");
            Assert.AreEqual(1L, device.BluetoothMicrophoneFramesReceived);
        }

        [TestMethod]
        public void LateMicrophoneFrameCannotCommitPendingDisable()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            SetFieldValue(MicrophoneStreamingRequestedField, device, 0);
            SetFieldValue(MicrophoneControlPendingField, device, 1);
            byte[] microphoneReport = new byte[78];
            microphoneReport[0] = 0x31;
            microphoneReport[1] = 0x02;

            Assert.IsNotNull(RecordBluetoothMicrophoneFrameMethod);
            RecordBluetoothMicrophoneFrameMethod.Invoke(device,
                new object[] { microphoneReport });

            Assert.AreEqual(1, GetFieldValue<int>(
                MicrophoneControlPendingField, device),
                "An in-flight microphone packet falsely committed disable.");
            Assert.AreEqual(0L, device.BluetoothMicrophoneFramesReceived);
        }

        [TestMethod]
        public void PendingMicrophoneEnableMapsProfileMaximumToPhysicalAdcCeiling()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.MicrophoneVolume = byte.MaxValue;
            SetFieldValue(MicrophoneStreamingRequestedField, device, 1);
            SetFieldValue(MicrophoneControlPendingField, device, 1);
            byte[] report = BuildCombinedControlReport(
                sequence: 0, packetSequence: 0, microphoneEnabled: false);
            report[13] &= unchecked((byte)~0x40);
            report[19] = 0;

            Assert.IsNotNull(
                ApplyBluetoothMicrophoneStreamingRequestMethod);
            ApplyBluetoothMicrophoneStreamingRequestMethod.Invoke(device,
                new object[] { report });

            Assert.AreNotEqual(0, report[4] & 0x01,
                "The physical microphone stream-enable bit was not set.");
            Assert.AreNotEqual(0, report[13] & 0x40,
                "The controller was not told that microphone volume is valid.");
            Assert.AreEqual((byte)0x40, report[19],
                "The combined transport must not overdrive the physical DualSense ADC.");
        }

        [TestMethod]
        public void CommittedMicrophoneEnableDoesNotReplayAdcControl()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            device.MicrophoneVolume = byte.MaxValue;
            SetFieldValue(MicrophoneStreamingRequestedField, device, 1);
            SetFieldValue(MicrophoneControlPendingField, device, 0);
            byte[] report = BuildCombinedControlReport(
                sequence: 0, packetSequence: 0, microphoneEnabled: false);
            report[13] &= unchecked((byte)~0x40);
            report[19] = 0;

            Assert.IsNotNull(
                ApplyBluetoothMicrophoneStreamingRequestMethod);
            ApplyBluetoothMicrophoneStreamingRequestMethod.Invoke(device,
                new object[] { report });

            Assert.AreNotEqual(0, report[4] & 0x01);
            Assert.AreEqual(0, report[13] & 0x40,
                "Steady-state audio frames must not replay the one-shot ADC control bit.");
            Assert.AreEqual((byte)0, report[19]);
        }

        [DataTestMethod]
        [DataRow(false, (byte)0xFE)]
        [DataRow(true, (byte)0xFF)]
        public void CombinedControlReportMatchesKnownGoodVdsLayout(
            bool microphoneEnabled, byte expectedAudioControl)
        {
            byte[] report = BuildCombinedControlReport(
                sequence: 0x0A,
                packetSequence: 0x37,
                microphoneEnabled);

            Assert.AreEqual(398, report.Length);
            Assert.AreEqual((byte)0x36, report[0]);
            Assert.AreEqual((byte)0xA0, report[1]);
            Assert.AreEqual((byte)0x91, report[2]);
            Assert.AreEqual((byte)0x07, report[3]);
            Assert.AreEqual(expectedAudioControl, report[4]);
            for (int index = 5; index <= 9; index++)
            {
                // VIIPER's canonical control/state baseline requests the
                // minimum documented queue. A report carrying a real speaker
                // frame separately raises these fields to 0x40.
                Assert.AreEqual((byte)0x10, report[index],
                    $"Unexpected packet 0x11 buffer depth at byte {index}.");
            }
            Assert.AreEqual((byte)0x37, report[10]);

            Assert.AreEqual((byte)0x90, report[11]);
            Assert.AreEqual((byte)63, report[12]);
            CollectionAssert.AreEqual(BuildExpectedDefaultState(),
                CopyRange(report, 13, 63),
                "Packet 0x10 did not contain the known-good vDS default state.");

            Assert.AreEqual((byte)0x92, report[76]);
            Assert.AreEqual((byte)64, report[77]);
            AssertRangeIsZero(report, 78, 64,
                "The control report's haptics lane was not silent.");

            AssertRangeIsZero(report, 142, report.Length - 4 - 142,
                "The control report unexpectedly included a speaker TLV or Opus data.");
            AssertCrcIsValid(report);
        }

        private static byte[] BuildCombinedControlReport(byte sequence,
            byte packetSequence, bool microphoneEnabled)
        {
            Assert.IsNotNull(BuildCombinedControlReportMethod);
            return (byte[])BuildCombinedControlReportMethod.Invoke(null,
                new object[] { sequence, packetSequence, microphoneEnabled });
        }

        private static byte[] BuildExpectedDefaultState()
        {
            byte[] state = new byte[63];
            byte[] knownState =
            {
                0xFD, 0xF7, 0x00, 0x00, 0x7F, 0x64, 0xFF, 0x09,
                0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x07, 0x00,
                0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00,
            };
            Array.Copy(knownState, state, knownState.Length);
            return state;
        }

        private static byte[] CopyRange(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        private static void AssertRangeIsZero(byte[] report, int offset,
            int length, string message)
        {
            for (int index = offset; index < offset + length; index++)
            {
                Assert.AreEqual((byte)0, report[index],
                    $"{message} Unexpected byte at offset {index}.");
            }
        }

        private static DualSenseDevice CreateBluetoothDevice()
        {
            var hidDevice = (HidDevice)RuntimeHelpers.GetUninitializedObject(
                typeof(HidDevice));
            var device = new DualSenseDevice(hidDevice, "Bluetooth transport test");
            SetFieldValue(ConnectionTypeField, device, ConnectionType.BT);
            return device;
        }

        private static ICollection GetEventQueue(DualSenseDevice device)
        {
            return GetFieldValue<ICollection>(EventQueueField, device);
        }

        private static T GetFieldValue<T>(FieldInfo field, object instance)
        {
            Assert.IsNotNull(field);
            return (T)field.GetValue(instance);
        }

        private static void SetFieldValue(FieldInfo field, object instance,
            object value)
        {
            Assert.IsNotNull(field);
            field.SetValue(instance, value);
        }

        private static void AssertCrcIsValid(byte[] report)
        {
            uint expected = ComputeCrc(report, report.Length - sizeof(uint));
            uint actual = (uint)(report[^4] |
                (report[^3] << 8) |
                (report[^2] << 16) |
                (report[^1] << 24));
            Assert.AreEqual(expected, actual);
        }

        private static uint ComputeCrc(byte[] data, int length)
        {
            uint crc = ~0xEADA2D49u;
            for (int index = 0; index < length; index++)
            {
                crc ^= data[index];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^
                        ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
            }

            return ~crc;
        }
    }
}
