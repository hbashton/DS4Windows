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
    }
}
