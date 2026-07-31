using Concentus;
using Concentus.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseOpusEncoderTests
    {
        [TestMethod]
        public void SpeakerEncoderMatchesBluetoothTransportProfileAndFixedPayload()
        {
            using IOpusEncoder encoder =
                DualSenseBluetoothSpeakerPassthrough.CreateSpeakerOpusEncoder();

            Assert.AreEqual(0, encoder.Complexity,
                "The DualSense Bluetooth speaker encoder should match the low-latency CombinedReportReference transport profile.");
            Assert.AreEqual(OpusFramesize.OPUS_FRAMESIZE_ARG,
                encoder.ExpertFrameDuration,
                "The 480-sample input should select the natural 10 ms frame.");
            Assert.IsFalse(encoder.UseVBR);
            Assert.AreEqual(160_000, encoder.Bitrate);

            float[] frame = new float[480 * 2];
            byte[] packet = new byte[200];
            for (int block = 0; block < 32; block++)
            {
                for (int sample = 0; sample < 480; sample++)
                {
                    int absoluteSample = block * 480 + sample;
                    frame[sample * 2] = (float)(0.45 * Math.Sin(
                        2.0 * Math.PI * 523.25 * absoluteSample / 48_000.0) +
                        0.18 * Math.Sin(2.0 * Math.PI * 5_017.0 *
                            absoluteSample / 48_000.0));
                    frame[sample * 2 + 1] = (float)(0.38 * Math.Sin(
                        2.0 * Math.PI * 787.0 * absoluteSample / 48_000.0) +
                        (sample == 0 && block % 7 == 0 ? 0.35 : 0.0));
                }

                int encoded = encoder.Encode(frame.AsSpan(), 480,
                    packet.AsSpan(), packet.Length);
                Assert.AreEqual(packet.Length, encoded,
                    $"CBR payload length changed on block {block}.");
            }
        }

        [TestMethod]
        public void KnownBadLevelRemainsSpectrallyCleanThroughOpusRoundTrip()
        {
            const int sampleRate = 48_000;
            const int channels = 2;
            const int framesPerPacket = 480;
            const int packetCount = 400;
            const int warmupPackets = 40;
            const double frequency = 523.25;
            const double amplitude = 0.12;

            using IOpusEncoder encoder =
                DualSenseBluetoothSpeakerPassthrough.CreateSpeakerOpusEncoder();
            using IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(
                sampleRate, channels);
            float[] input = new float[framesPerPacket * channels];
            byte[] packet = new byte[200];
            float[] decoded = new float[framesPerPacket * channels];
            float[] steady = new float[
                (packetCount - warmupPackets) * framesPerPacket];
            int steadyPosition = 0;

            for (int packetIndex = 0; packetIndex < packetCount; packetIndex++)
            {
                long packetStart = (long)packetIndex * framesPerPacket;
                for (int frame = 0; frame < framesPerPacket; frame++)
                {
                    float sample = (float)(amplitude * Math.Sin(
                        2.0 * Math.PI * frequency *
                        (packetStart + frame) / sampleRate));
                    input[frame * channels] = sample;
                    input[frame * channels + 1] = sample;
                }

                int encoded = encoder.Encode(input.AsSpan(), framesPerPacket,
                    packet.AsSpan(), packet.Length);
                Assert.AreEqual(packet.Length, encoded);
                int decodedFrames = decoder.Decode(packet.AsSpan(),
                    decoded.AsSpan(), framesPerPacket, false);
                Assert.AreEqual(framesPerPacket, decodedFrames);

                if (packetIndex < warmupPackets)
                {
                    continue;
                }

                for (int frame = 0; frame < framesPerPacket; frame++)
                {
                    steady[steadyPosition++] = decoded[frame * channels];
                }
            }

            double fundamental = ProjectAmplitude(steady, sampleRate,
                frequency);
            double secondHarmonic = ProjectAmplitude(steady, sampleRate,
                frequency * 2.0);
            double thirdHarmonic = ProjectAmplitude(steady, sampleRate,
                frequency * 3.0);

            Assert.AreEqual(amplitude, fundamental, 0.002,
                "Opus changed the known-bad tone level unexpectedly.");
            Assert.IsTrue(secondHarmonic / fundamental < 0.001,
                $"Opus introduced excessive H2: " +
                $"{20.0 * Math.Log10(secondHarmonic / fundamental):F2} dBc.");
            Assert.IsTrue(thirdHarmonic / fundamental < 0.001,
                $"Opus introduced excessive H3: " +
                $"{20.0 * Math.Log10(thirdHarmonic / fundamental):F2} dBc.");
        }

        private static double ProjectAmplitude(float[] samples,
            int sampleRate, double frequency)
        {
            double cosine = 0.0;
            double sine = 0.0;
            for (int index = 0; index < samples.Length; index++)
            {
                double phase = 2.0 * Math.PI * frequency * index / sampleRate;
                cosine += samples[index] * Math.Cos(phase);
                sine += samples[index] * Math.Sin(phase);
            }

            return 2.0 * Math.Sqrt(cosine * cosine + sine * sine) /
                samples.Length;
        }
    }
}
