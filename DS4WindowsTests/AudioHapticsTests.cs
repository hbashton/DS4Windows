using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;
using DS4Windows;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests
{
    [TestClass]
    public class AudioHapticsTests
    {
        [TestMethod]
        public void SustainedAudioProducesBoundedHapticOutput()
        {
            AudioHapticsProcessor processor = new AudioHapticsProcessor(
                new AudioHapticsProfileSettings
                {
                    GainPercent = 100,
                    BassFocus = AudioHapticsBassFocus.Balanced,
                    Response = AudioHapticsResponse.Balanced,
                    Attack = AudioHapticsAttack.Balanced,
                    Release = AudioHapticsRelease.Balanced,
                }, 48000);

            float maximum = 0;
            for (int sample = 0; sample < 48000; sample++)
            {
                float value = (float)Math.Sin(2.0 * Math.PI * 110.0 *
                    sample / 48000.0) * 0.45f;
                processor.Process(value, -value, out float left,
                    out float right);
                maximum = Math.Max(maximum, Math.Max(Math.Abs(left),
                    Math.Abs(right)));
                Assert.IsTrue(left >= -1.0f && left <= 1.0f);
                Assert.IsTrue(right >= -1.0f && right <= 1.0f);
            }

            Assert.IsTrue(maximum > 0.05f,
                "An audible low-frequency source should open the haptics gate.");
        }

        [TestMethod]
        public void MixSigned8SoftClipsInsteadOfWrapping()
        {
            byte positive = unchecked((byte)(sbyte)110);
            byte mixed = AudioHapticsProcessor.MixSigned8(positive, positive);
            int signed = unchecked((sbyte)mixed);

            Assert.IsTrue(signed > 110);
            Assert.IsTrue(signed <= 127);
        }

        [TestMethod]
        public void AudioHapticsStateRoundTripsInsideProfileXml()
        {
            ProfileDTO original = new ProfileDTO
            {
                AudioHapticsSettings = new AudioHapticsProfileSettings
                {
                    Enabled = true,
                    StreamAppAudioToController = true,
                    StreamAppAudioToHeadsetOnly = true,
                    AutomaticGameDetection = true,
                    Source = AudioHapticsSourceKind.AppSession,
                    Mode = AudioHapticsMode.Replace,
                    GainPercent = 145,
                    BassFocus = AudioHapticsBassFocus.Wide,
                    Response = AudioHapticsResponse.Strong,
                    Attack = AudioHapticsAttack.Fast,
                    Release = AudioHapticsRelease.Long,
                    ProcessId = 4242,
                    DisplayName = "Game",
                    ExecutableName = "game",
                    ProcessPath = @"C:\Games\game.exe",
                    SessionIdentifier = "session",
                    SessionInstanceIdentifier = "instance",
                },
            };
            XmlSerializer serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());

            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, original);
                xml = writer.ToString();
            }

            ProfileDTO restored;
            using (StringReader reader = new StringReader(xml))
            {
                restored = (ProfileDTO)serializer.Deserialize(reader);
            }

            Assert.IsTrue(xml.Contains("<AudioHaptics>"));
            Assert.IsTrue(restored.AudioHapticsSettings.Enabled);
            Assert.IsTrue(restored.AudioHapticsSettings
                .StreamAppAudioToHeadsetOnly);
            Assert.IsTrue(restored.AudioHapticsSettings
                .AutomaticGameDetection);
            Assert.AreEqual(AudioHapticsSourceKind.AppSession,
                restored.AudioHapticsSettings.Source);
            Assert.AreEqual(AudioHapticsMode.Replace,
                restored.AudioHapticsSettings.Mode);
            Assert.AreEqual(145,
                restored.AudioHapticsSettings.GainPercent);
            Assert.AreEqual("instance",
                restored.AudioHapticsSettings.SessionInstanceIdentifier);
        }

        [TestMethod]
        public void DefaultAudioHapticsSettingsAreNotSerialized()
        {
            ProfileDTO profile = new ProfileDTO();
            XmlSerializer serializer = new XmlSerializer(typeof(ProfileDTO),
                ProfileDTO.GetAttributeOverrides());

            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, profile);
                xml = writer.ToString();
            }

            Assert.IsFalse(xml.Contains("<AudioHaptics>"));
        }

        [TestMethod]
        public void AppSpeakerOverridePersistsOnlyForAppSources()
        {
            var settings = new AudioHapticsProfileSettings
            {
                Enabled = true,
                Source = AudioHapticsSourceKind.AppSession,
                ProcessId = 1234,
                StreamAppAudioToController = true,
                StreamAppAudioToHeadsetOnly = true,
            }.Normalize();
            Assert.IsTrue(settings.StreamAppAudioToController);
            Assert.IsTrue(settings.Clone().StreamAppAudioToController);
            Assert.IsTrue(settings.Clone().StreamAppAudioToHeadsetOnly);

            settings.Source = AudioHapticsSourceKind.SystemAudio;
            settings.Normalize();
            Assert.IsFalse(settings.StreamAppAudioToController);
            Assert.IsFalse(settings.StreamAppAudioToHeadsetOnly);
        }

        [TestMethod]
        public void AutomaticGameDetectionForcesAnAppSessionSource()
        {
            AudioHapticsProfileSettings settings =
                new AudioHapticsProfileSettings
                {
                    AutomaticGameDetection = true,
                    Source = AudioHapticsSourceKind.SystemAudio,
                }.Normalize();

            Assert.AreEqual(AudioHapticsSourceKind.AppSession,
                settings.Source);
            Assert.IsTrue(settings.Clone().AutomaticGameDetection);
            Assert.IsFalse(settings.IsDefaultConfiguration());
        }

        [TestMethod]
        public void ManifestGameBeatsAnUnclassifiedAudioApp()
        {
            GameAudioCandidate game = AutomaticGameAudioDetector.ScoreCandidate(
                42, "game.exe", @"C:\Games\Example\game.exe", "Example",
                hasActiveAudio: true, isForeground: false,
                fullscreenDirect3D: false,
                GameDetectionEvidence.InstalledGameManifest, "Example Game");
            GameAudioCandidate app = AutomaticGameAudioDetector.ScoreCandidate(
                43, "music.exe", @"C:\Apps\music.exe", "Music",
                hasActiveAudio: true, isForeground: true,
                fullscreenDirect3D: false, GameDetectionEvidence.None,
                string.Empty);

            Assert.IsNotNull(game);
            Assert.AreEqual("Example Game", game.DisplayName);
            Assert.IsNull(app,
                "Ordinary foreground audio must never be guessed to be a game.");
        }

        [TestMethod]
        public void FullscreenDirect3DForegroundAppIsAWindowsGameCandidate()
        {
            GameAudioCandidate candidate =
                AutomaticGameAudioDetector.ScoreCandidate(42, "unknown.exe",
                    @"C:\Games\Unknown\unknown.exe", "Unknown Game",
                    hasActiveAudio: true, isForeground: true,
                    fullscreenDirect3D: true, GameDetectionEvidence.None,
                    string.Empty);

            Assert.IsNotNull(candidate);
            Assert.AreEqual(GameDetectionEvidence.FullscreenDirect3D,
                candidate.Evidence);
        }

        [TestMethod]
        public void LauncherProcessesAreNeverAutomaticGameCandidates()
        {
            GameAudioCandidate candidate =
                AutomaticGameAudioDetector.ScoreCandidate(42,
                    "EpicGamesLauncher.exe",
                    @"C:\Apps\EpicGamesLauncher.exe", "Epic",
                    hasActiveAudio: true, isForeground: true,
                    fullscreenDirect3D: true,
                    GameDetectionEvidence.WindowsGameRecord, "Epic");

            Assert.IsNull(candidate);
        }

        [TestMethod]
        public void AutomaticProcessEndpointRoundTripsControllerSlot()
        {
            string endpoint = ProcessLoopbackWaveCapture
                .BuildAutomaticEndpointId(2);

            Assert.IsTrue(ProcessLoopbackWaveCapture
                .TryParseAutomaticEndpointId(endpoint, out int slot));
            Assert.AreEqual(2, slot);
            Assert.IsFalse(ProcessLoopbackWaveCapture.TryParseEndpointId(
                endpoint, out _));
        }

        [TestMethod]
        public void InstalledCatalogMatchesExactWindowsRecordsAndGameRoots()
        {
            InstalledGameCatalog catalog = InstalledGameCatalog.FromEntries(
                new[]
                {
                    (@"C:\Recorded\game.exe", "Recorded Game",
                        GameDetectionEvidence.WindowsGameRecord),
                    (@"C:\Steam\common\Example", "Manifest Game",
                        GameDetectionEvidence.InstalledGameManifest),
                });

            Assert.AreEqual(GameDetectionEvidence.WindowsGameRecord,
                catalog.Match(@"C:\Recorded\game.exe", "game", string.Empty,
                    out string recordedName));
            Assert.AreEqual("Recorded Game", recordedName);
            Assert.AreEqual(GameDetectionEvidence.WindowsGameRecord,
                catalog.Match(string.Empty, "game.exe", string.Empty,
                    out string executableName));
            Assert.AreEqual("Recorded Game", executableName);
            Assert.AreEqual(GameDetectionEvidence.InstalledGameManifest,
                catalog.Match(@"C:\Steam\common\Example\bin\game.exe",
                    "game", string.Empty, out string manifestName));
            Assert.AreEqual("Manifest Game", manifestName);
            Assert.AreEqual(GameDetectionEvidence.InstalledGameManifest,
                catalog.Match(string.Empty, "unknown.exe",
                    "Manifest Game - DirectX 12", out string windowName));
            Assert.AreEqual("Manifest Game", windowName);
        }

        [TestMethod]
        public void Ds5BridgeAudioHapticsConfigurationMatrixClonesWithoutLoss()
        {
            foreach (AudioHapticsSourceKind source in
                Enum.GetValues<AudioHapticsSourceKind>())
            foreach (AudioHapticsMode mode in Enum.GetValues<AudioHapticsMode>())
            foreach (AudioHapticsBassFocus bass in
                Enum.GetValues<AudioHapticsBassFocus>())
            foreach (AudioHapticsResponse response in
                Enum.GetValues<AudioHapticsResponse>())
            foreach (AudioHapticsAttack attack in
                Enum.GetValues<AudioHapticsAttack>())
            foreach (AudioHapticsRelease release in
                Enum.GetValues<AudioHapticsRelease>())
            {
                AudioHapticsProfileSettings clone =
                    new AudioHapticsProfileSettings
                    {
                        Enabled = true,
                        Source = source,
                        Mode = mode,
                        GainPercent = 150,
                        BassFocus = bass,
                        Response = response,
                        Attack = attack,
                        Release = release,
                    }.Clone();

                Assert.AreEqual(source, clone.Source);
                Assert.AreEqual(mode, clone.Mode);
                Assert.AreEqual(bass, clone.BassFocus);
                Assert.AreEqual(response, clone.Response);
                Assert.AreEqual(attack, clone.Attack);
                Assert.AreEqual(release, clone.Release);
                Assert.AreEqual(150, clone.GainPercent);
            }
        }

        [TestMethod]
        public void LiveAudioHapticsDoesNotWaitForAPlaybackReservoir()
        {
            Assert.AreEqual(1,
                AudioHapticsService.SlotRuntime.WriterPrebufferFrames,
                "Live audio haptics must start with the first complete packet.");
            Assert.IsTrue(
                AudioHapticsService.SlotRuntime.CaptureBufferMilliseconds <= 5,
                "Loopback capture should request a sub-10 ms period.");
            Assert.IsTrue(
                AudioHapticsService.SlotRuntime
                    .UsbOutputLatencyMilliseconds <= 10,
                "USB haptics must not use a media-playback latency buffer.");
        }

        [TestMethod]
        public void StaleAudioHapticsPacketIsNeverReplayed()
        {
            long now = Stopwatch.GetTimestamp();
            long stale = now - Stopwatch.Frequency *
                (AudioHapticsService.SlotRuntime
                    .MaximumLivePacketAgeMilliseconds + 1) / 1000;

            Assert.IsTrue(AudioHapticsService.SlotRuntime
                .IsLivePacketExpired(stale, now));
            Assert.IsFalse(AudioHapticsService.SlotRuntime
                .IsLivePacketExpired(now, now));
        }

        [TestMethod]
        public void MissingReplaceFrameLeavesNativeGameHapticsIntact()
        {
            byte[] carrier = Enumerable.Repeat((byte)37,
                AudioHapticsService.SlotRuntime.FrameBytes).ToArray();
            byte[] derived = Enumerable.Repeat((byte)99,
                AudioHapticsService.SlotRuntime.FrameBytes).ToArray();

            bool applied = AudioHapticsService.SlotRuntime.ApplyLiveFrame(
                AudioHapticsMode.Replace, derived,
                liveFrameAvailable: false, carrier, 0);

            Assert.IsFalse(applied,
                "A missing Audio Haptics frame must not claim game-carrier cadence.");
            CollectionAssert.AreEqual(
                Enumerable.Repeat((byte)37,
                    AudioHapticsService.SlotRuntime.FrameBytes).ToArray(),
                carrier,
                "Replace mode must not erase native game haptics while capture is stale.");
        }

        [TestMethod]
        public void LiveReplaceFrameClaimsCarrierAndReplacesSamples()
        {
            byte[] carrier = new byte[
                AudioHapticsService.SlotRuntime.FrameBytes];
            byte[] derived = Enumerable.Range(0,
                    AudioHapticsService.SlotRuntime.FrameBytes)
                .Select(value => (byte)value).ToArray();

            bool applied = AudioHapticsService.SlotRuntime.ApplyLiveFrame(
                AudioHapticsMode.Replace, derived,
                liveFrameAvailable: true, carrier, 0);

            Assert.IsTrue(applied);
            CollectionAssert.AreEqual(derived, carrier);
        }

        [TestMethod]
        public void SilentStandaloneFramesDoNotCreateACompetingCadence()
        {
            Assert.IsFalse(
                AudioHapticsService.SlotRuntime.ShouldPublishStandaloneFrame(
                    hasFrame: false, maximumMagnitude: 0,
                    hapticsActive: false));
            Assert.IsFalse(
                AudioHapticsService.SlotRuntime.ShouldPublishStandaloneFrame(
                    hasFrame: true, maximumMagnitude: 0,
                    hapticsActive: false));
            Assert.IsTrue(
                AudioHapticsService.SlotRuntime.ShouldPublishStandaloneFrame(
                    hasFrame: true, maximumMagnitude: 24,
                    hapticsActive: false));
        }

        [TestMethod]
        public void OneSilentStandaloneFrameReleasesAnActiveEffect()
        {
            Assert.IsTrue(
                AudioHapticsService.SlotRuntime.ShouldPublishStandaloneFrame(
                    hasFrame: true, maximumMagnitude: 0,
                    hapticsActive: true));
        }

        [TestMethod]
        public void MissingCaptureFrameReleasesAnActiveEffect()
        {
            Assert.IsTrue(
                AudioHapticsService.SlotRuntime.ShouldPublishStandaloneFrame(
                    hasFrame: false, maximumMagnitude: 0,
                    hapticsActive: true),
                "A capture restart must publish one zero frame so the previous effect cannot stick.");
        }

        [TestMethod]
        public void AppCaptureRequestsWindowsFormatConversion()
        {
            NAudio.CoreAudioApi.AudioClientStreamFlags flags =
                ProcessLoopbackWaveCapture.CaptureStreamFlags;

            Assert.IsTrue((flags &
                NAudio.CoreAudioApi.AudioClientStreamFlags.Loopback) != 0);
            Assert.IsTrue((flags &
                NAudio.CoreAudioApi.AudioClientStreamFlags.EventCallback) != 0);
            Assert.IsTrue((flags &
                NAudio.CoreAudioApi.AudioClientStreamFlags.AutoConvertPcm) != 0);
            Assert.IsTrue((flags &
                NAudio.CoreAudioApi.AudioClientStreamFlags.SrcDefaultQuality) != 0);
        }
    }
}
