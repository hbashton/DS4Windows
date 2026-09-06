using DS4Windows;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerAudioEndpointTests
    {
        [DataTestMethod]
        [DataRow(@"USB\VID_054C&PID_09CC&MI_01", (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow(@"USB\VID_054C&PID_05C4&MI_01", (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow(@"USB\VID_054C&PID_0CE6&MI_01", (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow(@"USB\VID_054C&PID_0DF2&MI_01", (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow("DualSense Wireless Controller", (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow("DualShock 4 Controller", (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow("Wireless Controller", (int)ControllerAudioEndpointKind.Any)]
        public void ClassifiesControllerAudioEndpointIdentity(string identity, int expected)
        {
            Assert.AreEqual((ControllerAudioEndpointKind)expected,
                DualSenseAudioPassthrough.ClassifyEndpointIdentity(identity));
        }

        [DataTestMethod]
        // Exact MMDevice property projection observed on the affected Sonar
        // endpoint: InstanceId is absent, while ControllerDeviceId and
        // InterfaceKey both project the ROOT\MEDIA adapter.
        [DataRow("SteelSeries Sonar - Gaming SteelSeries Sonar Virtual Audio Device",
            "", @"{1}.ROOT\MEDIA\0000",
            @"{2}.\\?\root#media#0000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\game_wave_speaker",
            (int)DualShock4EndpointCaptureBackend.
                SoftwareRouterPollingLoopback)]
        [DataRow("SteelSeries Sonar Virtual Audio Device",
            @"ROOT\MEDIA\0000", "", "",
            (int)DualShock4EndpointCaptureBackend.
                SoftwareRouterPollingLoopback)]
        [DataRow("SteelSeries Sonar Virtual Audio Device", "",
            @"{1}.ROOT\MEDIA\0000", "",
            (int)DualShock4EndpointCaptureBackend.
                SoftwareRouterPollingLoopback)]
        [DataRow("SteelSeries Sonar - Gaming", @"SWD\MMDEVAPI\{sonar}",
            "", "", (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        [DataRow("SteelSeries Sonar - Gaming", "", "", "",
            (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        [DataRow("Sonar Virtual Audio Device", @"SWD\MMDEVAPI\{sonar}",
            "", "", (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        [DataRow("SteelSeries Gaming Audio", @"SWD\MMDEVAPI\{steelseries}",
            "", "", (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        [DataRow("VB-Audio Virtual Cable", @"SWD\MMDEVAPI\{virtual}",
            "", "", (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        [DataRow("SteelSeries Sonar - Gaming", @"SWD\MMDEVAPI\{sonar}",
            @"HDAUDIO\FUNC_01&VEN_10EC", "",
            (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        [DataRow("SteelSeries Sonar - Gaming", @"SWD\MMDEVAPI\{sonar}",
            @"USB\VID_1038&PID_12E0", "",
            (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        [DataRow("SteelSeries Sonar - Gaming", @"SWD\MMDEVAPI\{sonar}",
            @"BTHENUM\DEV_001122334455", "",
            (int)DualShock4EndpointCaptureBackend.StandardLoopback)]
        public void SonarPollingCaptureRequiresProductAndSoftwareDeviceEvidence(
            string productIdentity, string instanceId,
            string controllerDeviceId, string interfaceKey, int expected)
        {
            Assert.AreEqual((DualShock4EndpointCaptureBackend)expected,
                DualShock4EndpointCapturePolicy.SelectBackend(productIdentity,
                    instanceId, controllerDeviceId, interfaceKey));
        }

        [TestMethod]
        public void SonarCaptureUsesPollingFlagsAndRequestedBuffer()
        {
            Assert.AreEqual(4,
                DualShock4SoftwareRouterLoopbackCapture.
                    RequestedBufferMilliseconds);
            NAudio.CoreAudioApi.AudioClientStreamFlags flags =
                DualShock4SoftwareRouterLoopbackCapture.CaptureStreamFlags;
            Assert.IsTrue((flags & NAudio.CoreAudioApi.
                AudioClientStreamFlags.Loopback) != 0);
            Assert.IsTrue((flags & NAudio.CoreAudioApi.
                AudioClientStreamFlags.AutoConvertPcm) != 0);
            Assert.IsTrue((flags & NAudio.CoreAudioApi.
                AudioClientStreamFlags.SrcDefaultQuality) != 0);
            Assert.IsFalse((flags & NAudio.CoreAudioApi.
                AudioClientStreamFlags.EventCallback) != 0,
                "Software-router loopback must retain the proven polling path.");
        }

        [TestMethod]
        public void SonarExtensibleFloatFormatIsExposedAsIeeeFloat()
        {
            var extensible = new NAudio.Wave.WaveFormatExtensible(48000,
                32, 2);

            NAudio.Wave.WaveFormat normalized =
                DualShock4EndpointCapturePolicy.NormalizeCaptureWaveFormat(
                    extensible);

            Assert.AreEqual(NAudio.Wave.WaveFormatEncoding.IeeeFloat,
                normalized.Encoding);
            Assert.AreEqual(48000, normalized.SampleRate);
            Assert.AreEqual(2, normalized.Channels);
            Assert.AreEqual(32, normalized.BitsPerSample);
            Assert.AreEqual(extensible.BlockAlign, normalized.BlockAlign);
        }

        [DataTestMethod]
        [DataRow(OutContType.ViiperDS4, (int)ControllerAudioEndpointKind.DualShock4)]
        [DataRow(OutContType.ViiperDualSense, (int)ControllerAudioEndpointKind.DualSense)]
        [DataRow(OutContType.ViiperDualSenseEdge, (int)ControllerAudioEndpointKind.DualSense)]
        // Legacy profiles that serialized "DS4" are migrated to the VIIPER
        // DualShock 4 backend before audio endpoint selection.
        [DataRow(OutContType.DS4, (int)ControllerAudioEndpointKind.DualShock4)]
        public void MapsVirtualOutputToPreferredAudioEndpoint(OutContType output, int expected)
        {
            Assert.AreEqual((ControllerAudioEndpointKind)expected,
                DualSenseAudioPassthrough.GetEndpointKind(output));
        }

        [DataTestMethod]
        [DataRow("", false, true)]
        [DataRow(DualSenseAudioPassthrough.AutoDetectGameAudioEndpointId,
            false, true)]
        [DataRow(DualSenseAudioPassthrough.DefaultSystemAudioEndpointId,
            true, false)]
        [DataRow("{0.0.0.00000000}.{virtual-controller}", true, true)]
        [DataRow("{0.0.0.00000000}.{other-endpoint}", false, false)]
        [DataRow("DS4Windows:AudioHapticsAuto:0", true, false)]
        public void DirectSpeakerRouteHonorsExplicitEndpointOwnership(
            string endpointId, bool endpointOwnedByDirectSource, bool expected)
        {
            Assert.AreEqual(expected,
                DualSenseAudioPassthrough.IsDirectSpeakerRequest(endpointId,
                    endpointOwnedByDirectSource));
        }

        [DataTestMethod]
        [DataRow("", true, false,
            (int)DirectSpeakerEndpointOwnership.Unresolved,
            (int)DirectSpeakerRouteDecision.Pending)]
        [DataRow("", true, true,
            (int)DirectSpeakerEndpointOwnership.Unresolved,
            (int)DirectSpeakerRouteDecision.Direct)]
        [DataRow(DualSenseAudioPassthrough.DefaultSystemAudioEndpointId,
            true, true, (int)DirectSpeakerEndpointOwnership.Owned,
            (int)DirectSpeakerRouteDecision.Loopback)]
        [DataRow("explicit", true, true,
            (int)DirectSpeakerEndpointOwnership.Unresolved,
            (int)DirectSpeakerRouteDecision.Pending)]
        [DataRow("explicit", true, true,
            (int)DirectSpeakerEndpointOwnership.Owned,
            (int)DirectSpeakerRouteDecision.Direct)]
        [DataRow("explicit", true, false,
            (int)DirectSpeakerEndpointOwnership.Owned,
            (int)DirectSpeakerRouteDecision.Pending)]
        [DataRow("explicit", true, true,
            (int)DirectSpeakerEndpointOwnership.Unowned,
            (int)DirectSpeakerRouteDecision.Loopback)]
        [DataRow("explicit", true, true,
            (int)DirectSpeakerEndpointOwnership.Missing,
            (int)DirectSpeakerRouteDecision.Direct)]
        [DataRow("explicit", true, false,
            (int)DirectSpeakerEndpointOwnership.Missing,
            (int)DirectSpeakerRouteDecision.Pending)]
        [DataRow("explicit", false, false,
            (int)DirectSpeakerEndpointOwnership.Unresolved,
            (int)DirectSpeakerRouteDecision.Loopback)]
        [DataRow("DS4Windows:AudioHapticsAuto:0", true, true,
            (int)DirectSpeakerEndpointOwnership.Owned,
            (int)DirectSpeakerRouteDecision.Loopback)]
        public void DirectSpeakerRouteRetriesTransientEnumerationAndRecovery(
            string endpointId, bool capable, bool active, int ownership,
            int expected)
        {
            Assert.AreEqual((DirectSpeakerRouteDecision)expected,
                DualSenseAudioPassthrough.DecideDirectSpeakerRoute(endpointId,
                    capable, active,
                    (DirectSpeakerEndpointOwnership)ownership));
        }

        [DataTestMethod]
        [DataRow(true, true, true, true, true, 1, 1,
            (int)DirectSpeakerEndpointOwnership.Owned)]
        [DataRow(true, true, true, true, true, 2, 1,
            (int)DirectSpeakerEndpointOwnership.Unowned)]
        [DataRow(true, true, true, true, false, -1, 1,
            (int)DirectSpeakerEndpointOwnership.Unowned)]
        [DataRow(true, true, false, false, false, -1, 1,
            (int)DirectSpeakerEndpointOwnership.Unresolved)]
        [DataRow(true, true, true, false, false, -1, 1,
            (int)DirectSpeakerEndpointOwnership.Unresolved)]
        [DataRow(true, true, true, true, true, -1, 1,
            (int)DirectSpeakerEndpointOwnership.Unresolved)]
        [DataRow(false, true, true, true, true, 1, 1,
            (int)DirectSpeakerEndpointOwnership.Unresolved)]
        [DataRow(true, false, true, true, true, 1, 1,
            (int)DirectSpeakerEndpointOwnership.Unowned)]
        public void DirectSpeakerOwnershipRequiresExactActiveUsbipPort(
            bool endpointActive, bool identityMatches,
            bool interfacePathAvailable, bool usbIpQueryResolved,
            bool usbIpAncestor, int endpointPort, int sourcePort, int expected)
        {
            Assert.AreEqual((DirectSpeakerEndpointOwnership)expected,
                DualSenseAudioPassthrough.
                    ClassifyDirectSpeakerEndpointOwnership(endpointActive,
                        identityMatches, interfacePathAvailable,
                        usbIpQueryResolved, usbIpAncestor, endpointPort,
                        sourcePort));
        }

        [TestMethod]
        public void ViiperUsbipOwnershipTracksOnlyRegisteredActivePort()
        {
            const int ownedPort = 7331;
            const int unrelatedPort = 7332;
            ViiperUsbipPortManager.UnregisterActivePort(ownedPort);
            ViiperUsbipPortManager.UnregisterActivePort(unrelatedPort);

            try
            {
                Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(ownedPort));
                ViiperUsbipPortManager.RegisterActivePort(ownedPort);
                Assert.IsTrue(ViiperUsbipPortManager.IsActivePort(ownedPort));
                Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(
                    unrelatedPort));
            }
            finally
            {
                ViiperUsbipPortManager.UnregisterActivePort(ownedPort);
                ViiperUsbipPortManager.UnregisterActivePort(unrelatedPort);
            }

            Assert.IsFalse(ViiperUsbipPortManager.IsActivePort(ownedPort));
        }

        [DataTestMethod]
        [DataRow(0x05C4, true)]
        [DataRow(0x09CC, true)]
        [DataRow(0x0CE6, true)]
        [DataRow(0x0DF2, true)]
        [DataRow(0x0268, false)]
        public void ViiperSonyOutputIdentityIncludesEverySupportedPersona(
            int productId, bool expected)
        {
            Assert.AreEqual(expected,
                DS4Devices.IsViiperSonyProductId(productId));
        }

        [TestMethod]
        public void MissingDevicePathIsNeverTreatedAsOwnedViiperOutput()
        {
            Assert.IsFalse(DS4Devices.IsOwnVirtualDevice(null));
            Assert.IsFalse(DS4Devices.IsOwnVirtualDevice(string.Empty));
        }

        [DataTestMethod]
        [DataRow((int)ControllerAudioEndpointKind.DualShock4,
            (int)ControllerAudioEndpointKind.DualSense, true)]
        [DataRow((int)ControllerAudioEndpointKind.DualSense,
            (int)ControllerAudioEndpointKind.DualShock4, true)]
        [DataRow((int)ControllerAudioEndpointKind.DualSense,
            (int)ControllerAudioEndpointKind.DualSense, true)]
        [DataRow((int)ControllerAudioEndpointKind.DualShock4,
            (int)ControllerAudioEndpointKind.DualShock4, true)]
        [DataRow((int)ControllerAudioEndpointKind.Any,
            (int)ControllerAudioEndpointKind.DualSense, false)]
        [DataRow((int)ControllerAudioEndpointKind.DualSense,
            (int)ControllerAudioEndpointKind.Any, false)]
        public void EverySonyControllerEndpointFollowsTheCurrentVirtualPersona(
            int savedEndpointKind, int currentOutputKind, bool expected)
        {
            Assert.AreEqual(expected,
                DualSenseAudioPassthrough.IsControllerEndpointSelection(
                    (ControllerAudioEndpointKind)savedEndpointKind,
                    (ControllerAudioEndpointKind)currentOutputKind));
        }
    }

    [TestClass]
    public class ControllerMicrophoneRoutePolicyTests
    {
        [DataTestMethod]
        [DataRow(OutContType.DS4, true)]
        [DataRow(OutContType.ViiperDS4, true)]
        [DataRow(OutContType.ViiperDualSense, true)]
        [DataRow(OutContType.ViiperDualSenseEdge, true)]
        [DataRow(OutContType.X360, true)]
        [DataRow(OutContType.ViiperX360, true)]
        [DataRow(OutContType.ViiperSwitch2Pro, true)]
        public void DirectRouteSupportsPrimaryOrAudioOnlyFeatureOutput(
            OutContType outputType, bool expected)
        {
            Assert.AreEqual(expected,
                ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                    profileEnabled: true,
                    eligibleBluetoothSource: true,
                    outputType: outputType,
                    activeStreamSupportsMicrophone: true));
        }

        [TestMethod]
        public void DirectRouteRequiresProfileSourceAndActiveConsumer()
        {
            Assert.IsFalse(
                ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                    profileEnabled: false, eligibleBluetoothSource: true,
                    outputType: OutContType.ViiperDS4,
                    activeStreamSupportsMicrophone: true));
            Assert.IsFalse(
                ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                    profileEnabled: true, eligibleBluetoothSource: false,
                    outputType: OutContType.ViiperDS4,
                    activeStreamSupportsMicrophone: true));
            Assert.IsFalse(
                ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                    profileEnabled: true, eligibleBluetoothSource: true,
                    outputType: OutContType.ViiperDS4,
                    activeStreamSupportsMicrophone: false));
        }

        [TestMethod]
        public void PhysicalMicrophoneArmsOnlyForAnOpenVirtualCaptureInterface()
        {
            Assert.IsFalse(
                ControllerMicrophoneRoutePolicy.ShouldArmPhysicalBluetoothMicrophone(
                    profileEnabled: true, eligibleBluetoothSource: true,
                    outputType: OutContType.ViiperDualSense,
                    activeStreamSupportsMicrophone: true,
                    virtualMicrophoneInterfaceActive: false));
            Assert.IsTrue(
                ControllerMicrophoneRoutePolicy.ShouldArmPhysicalBluetoothMicrophone(
                    profileEnabled: true, eligibleBluetoothSource: true,
                    outputType: OutContType.ViiperDualSense,
                    activeStreamSupportsMicrophone: true,
                    virtualMicrophoneInterfaceActive: true));
            Assert.IsFalse(
                ControllerMicrophoneRoutePolicy.ShouldArmPhysicalBluetoothMicrophone(
                    profileEnabled: true, eligibleBluetoothSource: true,
                    outputType: OutContType.ViiperSwitch2Pro,
                    activeStreamSupportsMicrophone: true,
                    virtualMicrophoneInterfaceActive: false));
            Assert.IsTrue(
                ControllerMicrophoneRoutePolicy.ShouldArmPhysicalBluetoothMicrophone(
                    profileEnabled: true, eligibleBluetoothSource: true,
                    outputType: OutContType.ViiperSwitch2Pro,
                    activeStreamSupportsMicrophone: true,
                    virtualMicrophoneInterfaceActive: true));
        }
    }

    [TestClass]
    public class ViiperMicrophoneFormatTests
    {
        [TestMethod]
        public void DualShock4MicrophoneDownsamples48kMonoTo16kMono()
        {
            short[] source = new short[480];
            for (int frame = 0; frame < 160; frame++)
            {
                source[frame * 3] = (short)(frame - 80);
                source[frame * 3 + 1] = (short)(frame + 20);
                source[frame * 3 + 2] = (short)(frame + 120);
            }

            byte[] destination = new byte[320];
            int frames = ViiperOutDevice.ConvertMicrophoneMono48kToDualShock4Pcm(
                source, source.Length, destination);

            Assert.AreEqual(160, frames);
            for (int frame = 0; frame < frames; frame++)
            {
                short actual = (short)(destination[frame * 2] |
                    destination[frame * 2 + 1] << 8);
                Assert.AreEqual((short)(frame + 20), actual);
            }
        }

        [TestMethod]
        public void DualShock4MicrophonePadsPartialAndMutedFramesWithSilence()
        {
            short[] source = new short[480];
            Array.Fill(source, (short)900);
            byte[] destination = new byte[320];
            Array.Fill(destination, (byte)0xFF);

            int frames = ViiperOutDevice.ConvertMicrophoneMono48kToDualShock4Pcm(
                source, 6, destination);

            Assert.AreEqual(2, frames);
            Assert.AreEqual(900, (short)(destination[0] | destination[1] << 8));
            Assert.AreEqual(900, (short)(destination[2] | destination[3] << 8));
            for (int index = 4; index < destination.Length; index++)
            {
                Assert.AreEqual(0, destination[index]);
            }

            frames = ViiperOutDevice.ConvertMicrophoneMono48kToDualShock4Pcm(
                source, 0, destination);
            Assert.AreEqual(0, frames);
            CollectionAssert.AreEqual(new byte[destination.Length], destination);
        }
    }

    [TestClass]
    public class ViiperMicrophoneInterfaceActivityTrackerTests
    {
        [TestMethod]
        public void QueryFailurePreservesLastKnownActiveState()
        {
            var tracker = new MicrophoneInterfaceActivityTracker();

            Assert.IsTrue(tracker.RecordObservation(active: true, timestamp: 0));
            tracker.RecordQueryFailure();

            Assert.IsTrue(tracker.StateKnown);
            Assert.IsTrue(tracker.IsActive);
        }

        [TestMethod]
        public void InactiveStateRequiresConsecutiveObservationsAndGrace()
        {
            var tracker = new MicrophoneInterfaceActivityTracker();
            long grace = MicrophoneInterfaceActivityTracker.InactiveGraceTicks;

            tracker.RecordObservation(active: true, timestamp: 0);
            Assert.IsFalse(tracker.RecordObservation(active: false, timestamp: 1));
            Assert.IsFalse(tracker.RecordObservation(active: false,
                timestamp: grace));
            Assert.IsTrue(tracker.IsActive);

            Assert.IsTrue(tracker.RecordObservation(active: false,
                timestamp: grace + 1));
            Assert.IsTrue(tracker.StateKnown);
            Assert.IsFalse(tracker.IsActive);
        }

        [TestMethod]
        public void QueryFailureBreaksPendingInactiveRun()
        {
            var tracker = new MicrophoneInterfaceActivityTracker();
            long grace = MicrophoneInterfaceActivityTracker.InactiveGraceTicks;

            tracker.RecordObservation(active: true, timestamp: 0);
            tracker.RecordObservation(active: false, timestamp: 1);
            tracker.RecordObservation(active: false, timestamp: grace + 1);
            tracker.RecordQueryFailure();

            Assert.IsFalse(tracker.RecordObservation(active: false,
                timestamp: grace * 2));
            Assert.IsFalse(tracker.RecordObservation(active: false,
                timestamp: grace * 3));
            Assert.IsTrue(tracker.IsActive);

            Assert.IsTrue(tracker.RecordObservation(active: false,
                timestamp: grace * 3 + 1));
            Assert.IsFalse(tracker.IsActive);
        }

        [TestMethod]
        public void ActiveObservationCancelsPendingInactiveRunImmediately()
        {
            var tracker = new MicrophoneInterfaceActivityTracker();
            long grace = MicrophoneInterfaceActivityTracker.InactiveGraceTicks;

            tracker.RecordObservation(active: true, timestamp: 0);
            tracker.RecordObservation(active: false, timestamp: 1);
            tracker.RecordObservation(active: false, timestamp: grace + 1);
            Assert.IsFalse(tracker.RecordObservation(active: true,
                timestamp: grace + 2));

            Assert.IsFalse(tracker.RecordObservation(active: false,
                timestamp: grace * 2));
            Assert.IsTrue(tracker.IsActive);
        }
    }

    [TestClass]
    public class ViiperMicrophoneBufferSnapshotTests
    {
        [TestMethod]
        public void ParsesCurrentViiperMicrophoneBufferTelemetry()
        {
            using JsonDocument document = JsonDocument.Parse("""
                {
                  "queuedMicrophoneBytes": 960,
                  "microphoneQueueTargetBytes": "1920",
                  "microphoneFilteredQueueBytes": 1280,
                  "microphoneQueuePrimed": true,
                  "microphoneUnderruns": 2,
                  "microphoneReprimes": "3",
                  "microphoneDroppedBytes": 4,
                  "microphoneZeroPackets": 5,
                  "microphoneOverflowEvents": 6,
                  "microphoneLowWaterBytes": -1,
                  "microphoneHighWaterBytes": 2240,
                  "microphoneQueueFrames": 7,
                  "microphoneQueueMinGapUS": 7300,
                  "microphoneQueueMaxGapUS": 12100,
                  "microphoneReadMinGapUS": 900,
                  "microphoneReadMaxGapUS": 1350
                }
                """);

            ViiperMicrophoneBufferSnapshot snapshot =
                ViiperMicrophoneBufferSnapshot.Parse(document.RootElement);

            Assert.AreEqual(960L, snapshot.QueuedBytes);
            Assert.AreEqual(1920L, snapshot.TargetBytes);
            Assert.AreEqual(1280L, snapshot.FilteredBytes);
            Assert.AreEqual(true, snapshot.Primed);
            Assert.AreEqual(2UL, snapshot.Underruns);
            Assert.AreEqual(3UL, snapshot.Reprimes);
            Assert.AreEqual(4UL, snapshot.DroppedBytes);
            Assert.AreEqual(5UL, snapshot.ZeroPackets);
            Assert.AreEqual(6UL, snapshot.OverflowEvents);
            Assert.AreEqual(-1L, snapshot.LowWaterBytes);
            Assert.AreEqual(2240L, snapshot.HighWaterBytes);
            Assert.AreEqual(7UL, snapshot.QueueFrames);
            Assert.AreEqual(7300L, snapshot.QueueMinGapMicroseconds);
            Assert.AreEqual(12100L, snapshot.QueueMaxGapMicroseconds);
            Assert.AreEqual(900L, snapshot.ReadMinGapMicroseconds);
            Assert.AreEqual(1350L, snapshot.ReadMaxGapMicroseconds);
        }

        [TestMethod]
        public void MissingOrMalformedTelemetryIsUnavailableWithoutThrowing()
        {
            using JsonDocument document = JsonDocument.Parse("""
                {
                  "queuedMicrophoneBytes": {},
                  "microphoneQueuePrimed": "not-a-boolean",
                  "microphoneUnderruns": -1,
                  "microphoneReprimes": 18446744073709551615,
                  "MICROPHONEZEROPACKETS": "8"
                }
                """);

            ViiperMicrophoneBufferSnapshot snapshot =
                ViiperMicrophoneBufferSnapshot.Parse(document.RootElement);

            Assert.IsNull(snapshot.QueuedBytes);
            Assert.IsNull(snapshot.TargetBytes);
            Assert.IsNull(snapshot.Primed);
            Assert.IsNull(snapshot.Underruns);
            Assert.AreEqual(ulong.MaxValue, snapshot.Reprimes);
            Assert.AreEqual(8UL, snapshot.ZeroPackets);
            StringAssert.Contains(snapshot.ToLogFields(),
                "virtualMicQueuedBytes=n/a");
        }
    }

    [TestClass]
    public class ViiperMicrophonePipelineHealthTests
    {
        private const long Timeout = 1_000;
        private const long Now = 10_000;

        [TestMethod]
        public void UnarmedPipelineWithNoActivityIsStarting()
        {
            Assert.AreEqual(MicrophonePipelineHealthStage.Starting,
                Classify(0, 0, 0, hasArmedSource: false));
        }

        [TestMethod]
        public void ArmedPipelineWithoutPhysicalFramesReportsReceiveStall()
        {
            Assert.AreEqual(
                MicrophonePipelineHealthStage.PhysicalReceiveStalled,
                Classify(0, 0, 0, hasArmedSource: true));
        }

        [TestMethod]
        public void FreshCompressedFramesCannotHideDecodeProcessStall()
        {
            Assert.AreEqual(
                MicrophonePipelineHealthStage.DecodeOrProcessStalled,
                Classify(Now - 1, 0, 0));
        }

        [TestMethod]
        public void FreshProcessedPcmCannotHideVirtualSubmissionStall()
        {
            Assert.AreEqual(
                MicrophonePipelineHealthStage.VirtualSubmissionStalled,
                Classify(Now - 2, Now - 1, 0));
        }

        [TestMethod]
        public void OnlyFreshSuccessfulVirtualSubmissionIsHealthy()
        {
            Assert.AreEqual(MicrophonePipelineHealthStage.Healthy,
                Classify(Now - 3, Now - 2, Now - 1));

            Assert.AreEqual(
                MicrophonePipelineHealthStage.PhysicalReceiveStalled,
                Classify(Now - Timeout, Now - Timeout,
                    Now - Timeout));
        }

        [TestMethod]
        public void SuccessfulSubmissionRestoresHealthAfterVirtualTransportStall()
        {
            long freshCompressedRx = Now - 3;
            long freshProcessedPcm = Now - 2;

            Assert.AreEqual(
                MicrophonePipelineHealthStage.VirtualSubmissionStalled,
                Classify(freshCompressedRx, freshProcessedPcm,
                    Now - Timeout));

            // A recovered transport is not considered healthy merely because
            // it reopened. The first PCM frame must successfully reach the
            // virtual endpoint before the pipeline returns to green.
            Assert.AreEqual(MicrophonePipelineHealthStage.Healthy,
                Classify(freshCompressedRx, freshProcessedPcm, Now - 1));
        }

        [TestMethod]
        public void FailedStageNamesAreExplicitForTelemetry()
        {
            Assert.AreEqual("physical-rx-stalled",
                MicrophonePipelineHealth.GetDisplayName(
                    MicrophonePipelineHealthStage.PhysicalReceiveStalled));
            Assert.AreEqual("decode-process-stalled",
                MicrophonePipelineHealth.GetDisplayName(
                    MicrophonePipelineHealthStage.DecodeOrProcessStalled));
            Assert.AreEqual("virtual-submit-stalled",
                MicrophonePipelineHealth.GetDisplayName(
                    MicrophonePipelineHealthStage.VirtualSubmissionStalled));
        }

        private static MicrophonePipelineHealthStage Classify(
            long lastCompressedRx, long lastProcessed, long lastSubmitted,
            bool hasArmedSource = true)
        {
            return MicrophonePipelineHealth.Classify(Now, Timeout,
                lastCompressedRx, lastProcessed, lastSubmitted,
                hasArmedSource);
        }
    }

    [TestClass]
    public class ViiperMicrophoneTelemetryTests
    {
        [TestMethod]
        public void SubmitGapMaximumIsMonotonicAndRejectsBadTimestamps()
        {
            var telemetry = new ViiperMicrophoneTelemetry();

            telemetry.RecordSuccessfulSubmission(100);
            telemetry.RecordSuccessfulSubmission(110);
            telemetry.RecordSuccessfulSubmission(105);
            telemetry.RecordSuccessfulSubmission(140);
            telemetry.RecordSuccessfulSubmission(0);

            Assert.AreEqual(2L, telemetry.ObservedSubmissionGaps);
            Assert.AreEqual(30L, telemetry.LastSubmissionGapTicks);
            Assert.AreEqual(30L, telemetry.MaximumSubmissionGapTicks);

            telemetry.RecordSuccessfulSubmission(145);

            Assert.AreEqual(3L, telemetry.ObservedSubmissionGaps);
            Assert.AreEqual(5L, telemetry.LastSubmissionGapTicks);
            Assert.AreEqual(30L, telemetry.MaximumSubmissionGapTicks);
        }

        [TestMethod]
        public void CaptureIntervalResetPreservesCumulativeTelemetry()
        {
            var telemetry = new ViiperMicrophoneTelemetry();
            telemetry.RecordSuccessfulSubmission(100);
            telemetry.RecordSuccessfulSubmission(120);

            telemetry.ResetSubmissionBaseline();
            telemetry.RecordSuccessfulSubmission(10_000);

            Assert.AreEqual(1L, telemetry.ObservedSubmissionGaps);
            Assert.AreEqual(0L, telemetry.LastSubmissionGapTicks);
            Assert.AreEqual(20L, telemetry.MaximumSubmissionGapTicks);
        }

        [TestMethod]
        public void ZeroFrameCountersDistinguishProcessingAndExpectedMute()
        {
            var telemetry = new ViiperMicrophoneTelemetry();
            short[] silence = new short[480];
            short[] signal = new short[480];
            signal[200] = -1234;

            telemetry.ObservePreProcessorFrame(silence, silence.Length);
            telemetry.ObservePreProcessorFrame(signal, signal.Length);
            telemetry.ObservePostProcessorFrame(silence, silence.Length,
                muted: false);
            telemetry.ObservePostProcessorFrame(silence, silence.Length,
                muted: true);
            telemetry.ObservePostProcessorFrame(signal, signal.Length,
                muted: false);
            telemetry.ObservePreProcessorFrame(null, 480);
            telemetry.ObservePostProcessorFrame(silence, 0, muted: false);

            Assert.AreEqual(1L, telemetry.PreProcessorAllZeroFrames);
            Assert.AreEqual(2L, telemetry.PostProcessorAllZeroFrames);
            Assert.AreEqual(1L,
                telemetry.PostProcessorAllZeroUnmutedFrames);
            Assert.AreEqual(1234L, telemetry.PreProcessorPeak);
            Assert.AreEqual(1234L, telemetry.PostProcessorPeak);

            telemetry.Reset();
            Assert.AreEqual(0L, telemetry.PreProcessorPeak);
            Assert.AreEqual(0L, telemetry.PostProcessorPeak);
        }

        [TestMethod]
        public void QueueHighWaterAndCountersCannotMoveBackwardOrOverflow()
        {
            var telemetry = new ViiperMicrophoneTelemetry();
            telemetry.ObserveCompressedQueueDepth(3);
            telemetry.ObserveCompressedQueueDepth(1);
            telemetry.ObserveCompressedQueueDepth(-1);
            telemetry.ObserveCompressedQueueDepth(8);

            Assert.AreEqual(8L, telemetry.CompressedQueueHighWaterMark);

            long counter = long.MaxValue - 1;
            ViiperMicrophoneTelemetry.IncrementSaturating(ref counter);
            ViiperMicrophoneTelemetry.IncrementSaturating(ref counter);
            Assert.AreEqual(long.MaxValue, counter);
        }
    }

    [TestClass]
    public class ViiperDeviceStreamRecoveryTests
    {
        [TestMethod]
        public void TransportClosePreservesVirtualDeviceForSameIdentity()
        {
            var cleanup = new CleanupCounters();
            var lifetime = cleanup.CreateLifetime(27, "4", 11);
            var oldPayload = new CountingMemoryStream();
            var oldTransport = new CountingDisposable();
            var oldStream = new ViiperDeviceStream(oldPayload, oldTransport,
                lifetime);

            oldStream.CloseTransport();

            Assert.IsTrue(oldStream.IsTransportClosed);
            Assert.AreEqual(1, oldPayload.DisposeCount);
            Assert.AreEqual(1, oldTransport.DisposeCount);
            cleanup.AssertNotCleaned();

            var replacementPayload = new CountingMemoryStream();
            var replacementTransport = new CountingDisposable();
            var replacement = new ViiperDeviceStream(replacementPayload,
                replacementTransport, lifetime);

            Assert.AreEqual(oldStream.BusId, replacement.BusId);
            Assert.AreEqual(oldStream.DevId, replacement.DevId);
            Assert.AreEqual(oldStream.UsbipPort, replacement.UsbipPort);
            Assert.AreSame(oldStream.DeviceLifetime,
                replacement.DeviceLifetime);

            replacement.Dispose();
            oldStream.Dispose();

            Assert.AreEqual(1, replacementPayload.DisposeCount);
            Assert.AreEqual(1, replacementTransport.DisposeCount);
            cleanup.AssertCleanedExactlyOnce(27, "4", 11);
        }

        [DataTestMethod]
        [DataRow(320, DisplayName = "DualShock 4 microphone payload")]
        [DataRow(1920, DisplayName = "DualSense microphone payload")]
        public void CloseFirstRecoveryCanImmediatelySubmitMicrophonePayload(
            int payloadLength)
        {
            var cleanup = new CleanupCounters();
            var lifetime = cleanup.CreateLifetime(73, "5", 13);
            var oldPayload = new CountingMemoryStream();
            var oldTransport = new CountingDisposable();
            var oldStream = new ViiperDeviceStream(oldPayload, oldTransport,
                lifetime);
            byte[] warmup = Enumerable.Range(0, 32)
                .Select(index => (byte)index)
                .ToArray();
            oldStream.WriteFrame(0x03, 0x02, warmup);

            oldStream.CloseTransport();
            cleanup.AssertNotCleaned();
            Assert.ThrowsException<ObjectDisposedException>(() =>
                oldStream.WriteFrame(0x03, 0x02, warmup));

            var replacementPayload = new CountingMemoryStream();
            var replacementTransport = new CountingDisposable();
            var replacement = new ViiperDeviceStream(replacementPayload,
                replacementTransport, lifetime);
            byte[] microphonePcm = Enumerable.Range(0, payloadLength)
                .Select(index => (byte)(index * 37 + 11))
                .ToArray();

            replacement.WriteFrame(0x03, 0x02, microphonePcm);

            byte[] framed = replacementPayload.ToArray();
            Assert.AreEqual(16 + payloadLength, framed.Length);
            CollectionAssert.AreEqual(new byte[]
            {
                (byte)'V', (byte)'P', (byte)'C', (byte)'M', 0x03, 0x02,
            }, framed.Take(6).ToArray());
            Assert.AreEqual(payloadLength, framed[6] | framed[7] << 8);
            Assert.AreEqual(0u, ReadUInt32(framed, 8),
                "A replacement transport begins a new framed-stream generation.");
            CollectionAssert.AreEqual(microphonePcm,
                framed.Skip(16).ToArray());
            cleanup.AssertNotCleaned();

            replacement.Dispose();
            oldStream.Dispose();

            Assert.AreEqual(1, oldPayload.DisposeCount);
            Assert.AreEqual(1, oldTransport.DisposeCount);
            Assert.AreEqual(1, replacementPayload.DisposeCount);
            Assert.AreEqual(1, replacementTransport.DisposeCount);
            cleanup.AssertCleanedExactlyOnce(73, "5", 13);
        }

        [TestMethod]
        public void FramedWriterReusesStorageAcrossChangingPayloadSizes()
        {
            var cleanup = new CleanupCounters();
            var lifetime = cleanup.CreateLifetime(81, "8", 14);
            var payloadStream = new CountingMemoryStream();
            var transport = new CountingDisposable();
            var stream = new ViiperDeviceStream(payloadStream, transport,
                lifetime);
            byte[] large = Enumerable.Range(0, 1920)
                .Select(index => (byte)(index * 17 + 3)).ToArray();
            byte[] small = Enumerable.Range(0, 32)
                .Select(index => (byte)(index * 29 + 7)).ToArray();

            stream.WriteFrame(0x03, 0x02, large);
            stream.WriteFrame(0x03, 0x01, small);

            byte[] framed = payloadStream.ToArray();
            int secondOffset = 16 + large.Length;
            Assert.AreEqual(secondOffset + 16 + small.Length,
                framed.Length,
                "The reusable writer must not transmit stale bytes from its prior larger payload.");
            Assert.AreEqual(0u, ReadUInt32(framed, 8));
            Assert.AreEqual(1u, ReadUInt32(framed, secondOffset + 8));
            Assert.AreEqual(0x03, framed[secondOffset + 4]);
            Assert.AreEqual(0x01, framed[secondOffset + 5]);
            Assert.AreEqual(small.Length, framed[secondOffset + 6] |
                framed[secondOffset + 7] << 8);
            CollectionAssert.AreEqual(small, framed.Skip(secondOffset + 16)
                .ToArray());

            stream.Dispose();
            Assert.AreEqual(1, payloadStream.DisposeCount);
            Assert.AreEqual(1, transport.DisposeCount);
            cleanup.AssertCleanedExactlyOnce(81, "8", 14);
        }

        [TestMethod]
        public void FramedWriterRejectsRemovedAtomicV4Generation()
        {
            var cleanup = new CleanupCounters();
            var lifetime = cleanup.CreateLifetime(82, "9", 15);
            var payloadStream = new CountingMemoryStream();
            var transport = new CountingDisposable();
            var stream = new ViiperDeviceStream(payloadStream, transport,
                lifetime);
            byte[] generation = Enumerable.Range(0, 2524)
                .Select(index => (byte)(index * 23 + 5)).ToArray();

            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                stream.WriteFrame(0x04, 0x83, generation));
            Assert.AreEqual(0, payloadStream.Length,
                "Removed V4 frames must never reach the backend transport.");

            stream.Dispose();
            Assert.AreEqual(1, payloadStream.DisposeCount);
            Assert.AreEqual(1, transport.DisposeCount);
            cleanup.AssertCleanedExactlyOnce(82, "9", 15);
        }

        [TestMethod]
        public void ConcurrentFinalDisposalCleansVirtualDeviceExactlyOnce()
        {
            var cleanup = new CleanupCounters();
            var lifetime = cleanup.CreateLifetime(91, "12", 7);
            ViiperDeviceStream[] generations = Enumerable.Range(0, 16)
                .Select(_ => new ViiperDeviceStream(
                    new CountingMemoryStream(), new CountingDisposable(),
                    lifetime))
                .ToArray();

            Parallel.ForEach(generations, stream => stream.Dispose());
            Parallel.ForEach(generations, stream => stream.Dispose());

            Assert.IsTrue(lifetime.IsDisposed);
            cleanup.AssertCleanedExactlyOnce(91, "12", 7);
            foreach (ViiperDeviceStream generation in generations)
            {
                Assert.IsTrue(generation.IsTransportClosed);
            }
        }

        [TestMethod]
        public async Task OpenExistingDeviceStreamTargetsOriginalBusAndDevice()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var cleanup = new CleanupCounters();
            var lifetime = cleanup.CreateLifetime(42, "9", 6);
            ViiperDeviceStream opened = null;

            try
            {
                Task<TcpClient> accept = listener.AcceptTcpClientAsync();
                var client = new ViiperClient("127.0.0.1", port,
                    stream => stream);
                opened = client.OpenExistingDeviceStream(42, "9", 6,
                    lifetime);
                using TcpClient accepted = await accept.WaitAsync(
                    TimeSpan.FromSeconds(2));
                NetworkStream serverStream = accepted.GetStream();
                byte[] expected = Encoding.UTF8.GetBytes("bus/42/9\0");
                byte[] received = new byte[expected.Length];
                int total = 0;
                while (total < received.Length)
                {
                    int read = await serverStream.ReadAsync(
                        received.AsMemory(total, received.Length - total));
                    Assert.IsTrue(read > 0,
                        "VIIPER stream handshake closed early.");
                    total += read;
                }

                CollectionAssert.AreEqual(expected, received);
                Assert.AreEqual((uint)42, opened.BusId);
                Assert.AreEqual("9", opened.DevId);
                Assert.AreEqual(6, opened.UsbipPort);
                cleanup.AssertNotCleaned();
            }
            finally
            {
                opened?.Dispose();
                listener.Stop();
            }

            cleanup.AssertCleanedExactlyOnce(42, "9", 6);
        }

        [TestMethod]
        public async Task XboxOneBrokerCommitsInputOnlyAfterExactAckAndCarriesFeedbackAck()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var cleanup = new CleanupCounters();
            var lifetime = cleanup.CreateLifetime(43, "10", 7);
            ViiperDeviceStream broker = null;
            using var client = new TcpClient();
            try
            {
                Task<TcpClient> accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using TcpClient server = await accept.WaitAsync(
                    TimeSpan.FromSeconds(2));
                NetworkStream serverStream = server.GetStream();
                broker = new ViiperDeviceStream(client.GetStream(), client,
                    lifetime);

                Task enable = Task.Run(broker.EnableXboxOneBroker);
                byte[] ready = await ReadExactlyAsync(serverStream, 16);
                AssertXboxBrokerHeader(ready, 0x01, 0, 0);
                await serverStream.WriteAsync(BuildXboxBrokerFrame(
                    0x81, 0, Array.Empty<byte>()));
                await enable.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.IsTrue(broker.IsXboxOneBrokerEnabled);

                byte[] semantic = Enumerable.Range(0,
                        XboxOneEgressState.WireSize)
                    .Select(index => (byte)(index * 11 + 3)).ToArray();
                Task<ViiperFrameWriteTiming> input = Task.Run(() =>
                    broker.WriteXboxOneInputAndWaitForAck(semantic,
                        semantic.Length));
                byte[] inputFrame = await ReadExactlyAsync(serverStream,
                    16 + semantic.Length);
                AssertXboxBrokerHeader(inputFrame, 0x02, 2,
                    semantic.Length);
                CollectionAssert.AreEqual(semantic,
                    inputFrame.Skip(16).ToArray());
                Assert.IsFalse(input.IsCompleted,
                    "Socket acceptance must not commit semantic input.");

                await serverStream.WriteAsync(BuildXboxBrokerFrame(
                    ViiperDeviceStream.XboxOneBrokerSemanticInputAck, 2,
                    new byte[] { 1 }));
                byte[] inputAckPayload = new byte[1];
                Assert.AreEqual(1, broker.ReadXboxOneBrokerFrame(
                    out byte inputAckType, out ulong inputAckRevision,
                    inputAckPayload));
                Assert.AreEqual(ViiperDeviceStream.
                    XboxOneBrokerSemanticInputAck, inputAckType);
                broker.AcceptXboxOneInputAck(inputAckRevision,
                    inputAckPayload[0]);
                ViiperFrameWriteTiming inputTiming = await input.WaitAsync(
                    TimeSpan.FromSeconds(2));
                Assert.IsTrue(inputTiming.SocketWriteStartedTimestamp > 0);
                Assert.IsTrue(inputTiming.SocketWriteCompletedTimestamp >=
                    inputTiming.SocketWriteStartedTimestamp);
                Assert.IsTrue(inputTiming.AcceptanceAcknowledgedTimestamp >=
                    inputTiming.SocketWriteStartedTimestamp);
                Assert.IsTrue(inputTiming.WaitCompletedTimestamp >=
                    inputTiming.AcceptanceAcknowledgedTimestamp);

                byte[] feedback = Enumerable.Range(0,
                        ControllerFeedbackFrame.SerializedLength)
                    .Select(index => (byte)(index * 7 + 5)).ToArray();
                await serverStream.WriteAsync(BuildXboxBrokerFrame(
                    ViiperDeviceStream.XboxOneBrokerCanonicalFeedback, 9,
                    feedback));
                byte[] feedbackPayload = new byte[feedback.Length];
                Assert.AreEqual(feedback.Length,
                    broker.ReadXboxOneBrokerFrame(out byte feedbackType,
                        out ulong feedbackRevision, feedbackPayload));
                Assert.AreEqual(ViiperDeviceStream.
                    XboxOneBrokerCanonicalFeedback, feedbackType);
                Assert.AreEqual(9ul, feedbackRevision);
                CollectionAssert.AreEqual(feedback, feedbackPayload);

                broker.AcknowledgeXboxOneFeedback(feedbackRevision, true);
                byte[] feedbackAck = await ReadExactlyAsync(serverStream, 17);
                AssertXboxBrokerHeader(feedbackAck, 0x03, 9, 1);
                Assert.AreEqual(1, feedbackAck[16]);
            }
            finally
            {
                broker?.Dispose();
                listener.Stop();
            }
            cleanup.AssertCleanedExactlyOnce(43, "10", 7);
        }

        [TestMethod]
        public void XboxOneAcknowledgementReaderUsesRealtimePriority()
        {
            Assert.AreEqual(ThreadPriority.Highest,
                ViiperOutDevice.GetFeedbackReaderThreadPriority(
                    ViiperVirtualDeviceType.XboxOne,
                    supportsDirectSpeaker: false));
            Assert.AreEqual(ThreadPriority.Highest,
                ViiperOutDevice.GetFeedbackReaderThreadPriority(
                    ViiperVirtualDeviceType.DualSense,
                    supportsDirectSpeaker: true));
            Assert.AreEqual(ThreadPriority.AboveNormal,
                ViiperOutDevice.GetFeedbackReaderThreadPriority(
                    ViiperVirtualDeviceType.Xbox360,
                    supportsDirectSpeaker: false));
        }

        [TestMethod]
        public void RecoveryBackoffIsImmediateExponentialAndBounded()
        {
            CollectionAssert.AreEqual(new[]
            {
                0, 50, 100, 200, 400, 800, 1000, 1000,
            }, Enumerable.Range(1, 8)
                .Select(ViiperOutDevice.GetStreamRecoveryBackoffMilliseconds)
                .ToArray());
            Assert.AreEqual(1000,
                ViiperOutDevice.GetStreamRecoveryBackoffMilliseconds(100));
        }

        private static byte[] BuildXboxBrokerFrame(byte type,
            ulong correlation, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            byte[] frame = new byte[16 + payload.Length];
            frame[0] = (byte)'X';
            frame[1] = (byte)'1';
            frame[2] = (byte)'B';
            frame[3] = (byte)'R';
            frame[4] = 1;
            frame[5] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2),
                (ushort)payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8, 8),
                correlation);
            payload.CopyTo(frame, 16);
            return frame;
        }

        private static void AssertXboxBrokerHeader(byte[] frame, byte type,
            ulong correlation, int payloadLength)
        {
            Assert.IsTrue(frame.Length >= 16);
            CollectionAssert.AreEqual(new byte[]
            {
                (byte)'X', (byte)'1', (byte)'B', (byte)'R',
            }, frame.Take(4).ToArray());
            Assert.AreEqual(1, frame[4]);
            Assert.AreEqual(type, frame[5]);
            Assert.AreEqual(payloadLength,
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)));
            Assert.AreEqual(correlation,
                BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(8, 8)));
        }

        private static async Task<byte[]> ReadExactlyAsync(Stream stream,
            int length)
        {
            byte[] result = new byte[length];
            int total = 0;
            while (total < result.Length)
            {
                int read = await stream.ReadAsync(
                    result.AsMemory(total, result.Length - total));
                Assert.IsTrue(read > 0, "Broker stream closed early.");
                total += read;
            }
            return result;
        }

        [TestMethod]
        public void AuthorizedXboxOnePersonaCannotReopenInPlace()
        {
            Assert.IsFalse(ViiperOutDevice.SupportsInPlaceStreamRecovery(
                ViiperVirtualDeviceType.XboxOne));
            Assert.IsTrue(ViiperOutDevice.SupportsInPlaceStreamRecovery(
                ViiperVirtualDeviceType.Xbox360));
            Assert.IsTrue(ViiperOutDevice.SupportsInPlaceStreamRecovery(
                ViiperVirtualDeviceType.Switch2Pro));
        }

        [TestMethod]
        public void DormantXboxLifetimeBindsOnePortAndNeverDetachesPlaceholder()
        {
            var activatedCleanup = new CleanupCounters();
            ViiperVirtualDeviceLifetime activated = activatedCleanup.
                CreateLifetime(51, "4", -1);
            Assert.AreEqual(-1, activated.UsbipPort);
            activated.BindUsbipPort(12);
            activated.BindUsbipPort(12);
            Assert.AreEqual(12, activated.UsbipPort);
            Assert.ThrowsException<InvalidOperationException>(() =>
                activated.BindUsbipPort(13));
            activated.Dispose();
            activatedCleanup.AssertCleanedExactlyOnce(51, "4", 12);

            var dormantCleanup = new CleanupCounters();
            ViiperVirtualDeviceLifetime dormant = dormantCleanup.
                CreateLifetime(52, "5", -1);
            dormant.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() =>
                dormant.BindUsbipPort(14));
            dormantCleanup.AssertRemovedWithoutOwnedPort(52, "5");
        }

        [TestMethod]
        public void XboxOneProfileRebuildDisposesLifetimeWhileBrokerTransportIsOpen()
        {
            var payload = new CountingMemoryStream();
            var transport = new CountingDisposable();
            int detachCount = 0;
            int removeCount = 0;
            var lifetime = new ViiperVirtualDeviceLifetime(53, "6", 15,
                (_, _) =>
                {
                    Assert.AreEqual(0, payload.DisposeCount,
                        "Device removal must precede broker-stream disposal.");
                    Assert.AreEqual(0, transport.DisposeCount,
                        "Device removal must precede transport disposal.");
                    Interlocked.Increment(ref removeCount);
                },
                (_, _) =>
                {
                    Assert.AreEqual(0, payload.DisposeCount,
                        "usbip detach must retain the broker reader.");
                    Assert.AreEqual(0, transport.DisposeCount,
                        "usbip detach must retain the broker writer.");
                    Interlocked.Increment(ref detachCount);
                },
                _ => { },
                () => { });
            var stream = new ViiperDeviceStream(payload, transport,
                lifetime);

            stream.DisposeDeviceLifetimeBeforeTransportClose();

            Assert.AreEqual(1, Volatile.Read(ref detachCount));
            Assert.AreEqual(1, Volatile.Read(ref removeCount));
            Assert.AreEqual(0, payload.DisposeCount);
            Assert.AreEqual(0, transport.DisposeCount);

            stream.Dispose();
            Assert.AreEqual(1, payload.DisposeCount);
            Assert.AreEqual(1, transport.DisposeCount);
            Assert.AreEqual(1, Volatile.Read(ref detachCount),
                "Repeated stream cleanup must not detach twice.");
            Assert.AreEqual(1, Volatile.Read(ref removeCount),
                "Repeated stream cleanup must not remove twice.");
        }

        [TestMethod]
        public void XboxOneProfileRebuildWaitsOnlyForAcknowledgedTerminalStop()
        {
            Assert.IsTrue(ControllerFeedbackClock.
                TryGetTimestampMicroseconds(out ulong now));
            Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
                ControllerFeedbackSource.XboxOneVirtualDevice,
                ControllerFeedbackCommand.Stop,
                ControllerFeedbackActuators.All,
                0, 0, 0, 0, 1, 2, 3, 4, now, 250_000,
                out ControllerFeedbackFrame stop));
            byte[] payload = new byte[ControllerFeedbackFrame.SerializedLength];
            Assert.IsTrue(stop.TryWriteTo(payload));

            Assert.IsTrue(ViiperOutDevice.
                IsAcknowledgedXboxOneTerminalFeedback(payload,
                    payload.Length, 7, delivered: true,
                    acknowledged: true));
            Assert.IsFalse(ViiperOutDevice.
                IsAcknowledgedXboxOneTerminalFeedback(payload,
                    payload.Length, 7, delivered: true,
                    acknowledged: false));
            Assert.IsFalse(ViiperOutDevice.
                IsAcknowledgedXboxOneTerminalFeedback(payload,
                    payload.Length, 0, delivered: true,
                    acknowledged: true));

            payload[9] = (byte)ControllerFeedbackCommand.Neutral;
            Assert.IsFalse(ViiperOutDevice.
                IsAcknowledgedXboxOneTerminalFeedback(payload,
                    payload.Length, 7, delivered: true,
                    acknowledged: true));
        }

        [TestMethod]
        public void XboxOneProfileRebuildAcceptsOnlyExactFreshStopAfterPhysicalRetirement()
        {
            Assert.IsTrue(ControllerFeedbackClock.
                TryGetTimestampMicroseconds(out ulong now));
            var binding = new XboxOneAuthorizedFeedbackBinding
            {
                Source = (byte)ControllerFeedbackSource.
                    XboxOneVirtualDevice,
                PersonaGeneration = 5,
                DeviceGeneration = 11,
                TransportGeneration = 12,
                OwnershipEpoch = 13,
                TimeToLiveMicroseconds = 250_000,
            };
            Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
                ControllerFeedbackSource.XboxOneVirtualDevice,
                ControllerFeedbackCommand.Stop,
                ControllerFeedbackActuators.All,
                0, 0, 0, 0, 17, 11, 12, 13, now, 250_000,
                out ControllerFeedbackFrame stop));

            Assert.IsTrue(ViiperOutDevice.
                IsAuthorizedXboxOneTerminalStopAfterPhysicalRetirement(
                    stop, binding, 16));
            Assert.IsFalse(ViiperOutDevice.
                IsAuthorizedXboxOneTerminalStopAfterPhysicalRetirement(
                    stop, binding, 17));

            Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
                ControllerFeedbackSource.XboxOneVirtualDevice,
                ControllerFeedbackCommand.Neutral,
                ControllerFeedbackActuators.All,
                0, 0, 0, 0, 18, 11, 12, 13, now, 250_000,
                out ControllerFeedbackFrame neutral));
            Assert.IsFalse(ViiperOutDevice.
                IsAuthorizedXboxOneTerminalStopAfterPhysicalRetirement(
                    neutral, binding, 17));

            binding.TransportGeneration++;
            Assert.IsFalse(ViiperOutDevice.
                IsAuthorizedXboxOneTerminalStopAfterPhysicalRetirement(
                    stop, binding, 16));
        }

        [DataTestMethod]
        [DataRow(null, 0)]
        [DataRow("", 0)]
        [DataRow("immediate", 0)]
        [DataRow("29", 0)]
        [DataRow("200", 200)]
        [DataRow(" 250 ", 250)]
        [DataRow("1001", 0)]
        public void ViiperStateWriteRateEnvironmentIsStrictAndBounded(
            string value, int expected)
        {
            Assert.AreEqual(expected,
                ViiperStateWriteRateSettings.Parse(value));
        }

        [TestMethod]
        public void DualSenseV5UsesViipersAuthoritativePresentationClock()
        {
            const int defaultRate =
                ViiperStateWriteRateSettings.DefaultControllerRateHz;
            Assert.AreEqual(1000, ViiperStateWriteRateSettings.GetDefaultRateHz(
                ViiperVirtualDeviceType.DualShock4));
            Assert.AreEqual(0, ViiperStateWriteRateSettings.GetDefaultRateHz(
                ViiperVirtualDeviceType.DualSense));
            Assert.AreEqual(0, ViiperStateWriteRateSettings.GetDefaultRateHz(
                ViiperVirtualDeviceType.DualSenseEdge));
            Assert.AreEqual(1000, ViiperStateWriteRateSettings.GetDefaultRateHz(
                ViiperVirtualDeviceType.Xbox360));
            Assert.AreEqual(1000, ViiperStateWriteRateSettings.GetDefaultRateHz(
                ViiperVirtualDeviceType.Switch2Pro));
            Assert.AreEqual(1000,
                ViiperStateWriteRateSettings.Parse(null, defaultRate));
            Assert.AreEqual(1000,
                ViiperStateWriteRateSettings.Parse("unknown", defaultRate));
            Assert.AreEqual(250,
                ViiperStateWriteRateSettings.Parse("250", defaultRate));
            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.Parse("off", defaultRate));
            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.Parse("0", defaultRate));
        }

        [TestMethod]
        public void ControllerStateRateAllowsOnlyBoundedNumericOverrides()
        {
            Assert.AreEqual(200,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.DualSense, "200"));
            Assert.AreEqual(250,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.DualSenseEdge, "250"));
            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.DualSense, "immediate"));
            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.DualSense, "off"));
            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.DualSenseEdge, "0"));
            Assert.AreEqual(100,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.DualShock4, "100"));
            Assert.AreEqual(1000,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.Xbox360, null));
            Assert.AreEqual(1000,
                ViiperStateWriteRateSettings.ResolveConfiguredRateHz(
                    ViiperVirtualDeviceType.Switch2Pro, null));
        }

        [TestMethod]
        public void DualSensePhysicalCadenceIsNotDelayedByOneKilohertzCeiling()
        {
            long intervalTicks =
                ViiperStateWriteRateSettings.GetMinimumIntervalTicks(1000);
            long previousWriteTicks = 10 * Stopwatch.Frequency;
            long nextPhysicalReportTicks = previousWriteTicks +
                (Stopwatch.Frequency * 5 / 4 / 1000);

            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.GetRemainingTicks(
                    nextPhysicalReportTicks, previousWriteTicks,
                    intervalTicks));
        }

        [TestMethod]
        public void NativeGameOwnerFilteringRejectsShellAndInfrastructureOnly()
        {
            Assert.IsTrue(ViiperOutDevice.IsExcludedNativeGameOwner(
                "DS4Windows"));
            Assert.IsTrue(ViiperOutDevice.IsExcludedNativeGameOwner(
                "viiper"));
            Assert.IsTrue(ViiperOutDevice.IsExcludedNativeGameOwner(
                "explorer"));
            Assert.IsTrue(ViiperOutDevice.IsExcludedNativeGameOwner(
                "steam"));
            Assert.IsTrue(ViiperOutDevice.IsExcludedNativeGameOwner(
                "ChatGPT"));
            Assert.IsTrue(ViiperOutDevice.IsExcludedNativeGameOwner(
                "WindowsTerminal"));
            Assert.IsFalse(ViiperOutDevice.IsExcludedNativeGameOwner(
                "SlayTheSpire2"));
            Assert.IsFalse(ViiperOutDevice.IsExcludedNativeGameOwner(
                "GhostOfTsushima"));
        }

        [TestMethod]
        public void NeutralNativeInitializationCannotClaimGameOwnership()
        {
            byte[] report = new byte[48];
            report[0] = 0x02;

            Assert.IsFalse(ViiperOutDevice.HasMeaningfulNativeGameOutput(
                report, 0));

            report[1] = 0x04;
            Assert.IsTrue(ViiperOutDevice.HasMeaningfulNativeGameOutput(
                report, 0));
        }

        [TestMethod]
        public void ViiperStateWriteRateWindowStartsImmediatelyAndNeverCatchesUp()
        {
            long interval =
                ViiperStateWriteRateSettings.GetMinimumIntervalTicks(200);
            Assert.IsTrue(interval >= Stopwatch.Frequency / 200);
            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.GetRemainingTicks(
                    now: 1000, previousWriteStart: 0,
                    minimumIntervalTicks: interval));

            long firstWrite = 10 * Stopwatch.Frequency;
            Assert.AreEqual(interval,
                ViiperStateWriteRateSettings.GetRemainingTicks(
                    now: firstWrite, previousWriteStart: firstWrite,
                    minimumIntervalTicks: interval));
            Assert.AreEqual(0,
                ViiperStateWriteRateSettings.GetRemainingTicks(
                    now: firstWrite + interval,
                    previousWriteStart: firstWrite,
                    minimumIntervalTicks: interval));

            long lateWrite = firstWrite + interval * 3;
            Assert.AreEqual(interval,
                ViiperStateWriteRateSettings.GetRemainingTicks(
                    now: lateWrite, previousWriteStart: lateWrite,
                    minimumIntervalTicks: interval),
                "A late write must rebase the next window instead of creating a catch-up burst.");
        }

        [TestMethod]
        public void AbsoluteRateCursorPreservesPhaseAndReanchorsOneIntervalLate()
        {
            const long interval = 1_000;
            const long initialNow = 10_000;
            long deadline = ViiperStateWriteRateSettings.
                AdvanceAbsoluteDeadline(0, initialNow, interval);
            Assert.AreEqual(11_000, deadline);

            deadline = ViiperStateWriteRateSettings.
                AdvanceAbsoluteDeadline(deadline, 11_125, interval);
            Assert.AreEqual(12_000, deadline,
                "Sub-interval jitter must preserve the planned phase.");

            deadline = ViiperStateWriteRateSettings.
                AdvanceAbsoluteDeadline(deadline, 13_000, interval);
            Assert.AreEqual(14_000, deadline,
                "A complete expired interval must re-anchor without catch-up.");
        }

        private sealed class CleanupCounters
        {
            private int detachCount;
            private int unregisterCount;
            private int removeCount;
            private int staleCount;
            private int detachedPort = int.MinValue;
            private int unregisteredPort = int.MinValue;
            private uint removedBus;
            private string removedDevice;

            public ViiperVirtualDeviceLifetime CreateLifetime(uint busId,
                string devId, int usbipPort)
            {
                return new ViiperVirtualDeviceLifetime(busId, devId,
                    usbipPort,
                    (bus, device) =>
                    {
                        removedBus = bus;
                        removedDevice = device;
                        Interlocked.Increment(ref removeCount);
                    },
                    (port, _) =>
                    {
                        detachedPort = port;
                        Interlocked.Increment(ref detachCount);
                    },
                    port =>
                    {
                        unregisteredPort = port;
                        Interlocked.Increment(ref unregisterCount);
                    },
                    () => Interlocked.Increment(ref staleCount));
            }

            public void AssertNotCleaned()
            {
                Assert.AreEqual(0, Volatile.Read(ref detachCount));
                Assert.AreEqual(0, Volatile.Read(ref unregisterCount));
                Assert.AreEqual(0, Volatile.Read(ref removeCount));
                Assert.AreEqual(0, Volatile.Read(ref staleCount));
            }

            public void AssertCleanedExactlyOnce(uint busId, string devId,
                int usbipPort)
            {
                Assert.AreEqual(1, Volatile.Read(ref detachCount));
                Assert.AreEqual(1, Volatile.Read(ref unregisterCount));
                Assert.AreEqual(1, Volatile.Read(ref removeCount));
                Assert.AreEqual(1, Volatile.Read(ref staleCount));
                Assert.AreEqual(usbipPort, detachedPort);
                Assert.AreEqual(usbipPort, unregisteredPort);
                Assert.AreEqual(busId, removedBus);
                Assert.AreEqual(devId, removedDevice);
            }

            public void AssertRemovedWithoutOwnedPort(uint busId,
                string devId)
            {
                Assert.AreEqual(0, Volatile.Read(ref detachCount));
                Assert.AreEqual(0, Volatile.Read(ref unregisterCount));
                Assert.AreEqual(1, Volatile.Read(ref removeCount));
                Assert.AreEqual(1, Volatile.Read(ref staleCount));
                Assert.AreEqual(busId, removedBus);
                Assert.AreEqual(devId, removedDevice);
            }
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | data[offset + 1] << 8 |
                data[offset + 2] << 16 | data[offset + 3] << 24);
        }

        private sealed class CountingDisposable : IDisposable
        {
            private int disposeCount;

            public int DisposeCount => Volatile.Read(ref disposeCount);

            public void Dispose()
            {
                Interlocked.Increment(ref disposeCount);
            }
        }

        private sealed class CountingMemoryStream : MemoryStream
        {
            private int disposeCount;

            public int DisposeCount => Volatile.Read(ref disposeCount);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Interlocked.Increment(ref disposeCount);
                }
                base.Dispose(disposing);
            }
        }
    }
}
