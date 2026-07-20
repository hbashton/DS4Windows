using SBC;

namespace DS4WindowsTests
{
    [TestClass]
    public class SbcDecoderRealtimeTests
    {
        // One standard-SBC microphone frame captured from a genuine CUH-ZCT2
        // Bluetooth 0x13/A1 input report: 16 kHz, mono, 16 blocks, 8
        // subbands, bitpool 29, 128 decoded samples.
        private const string GenuineDualShock4MicrophoneFrameHex =
            "9C311D94E4454554D7C25376BA5ADC75CDB6E3AC68BA8D64A948EA936DA7521" +
            "331BA82EB70D3BACB869936D334AD371BA429A4CD1B89B668928DA74112CE39F" +
            "0D274";

        [TestMethod]
        public void DecodeIntoMatchesLegacyDecodeForGenuineDualShock4Mono()
        {
            byte[] encoded = Convert.FromHexString(
                GenuineDualShock4MicrophoneFrameHex);
            var legacyDecoder = new SbcDecoder();
            var realtimeDecoder = new SbcDecoder();
            var realtimeFrame = new SbcFrame();
            var realtimePcm = new short[SbcFrame.MaxSamples];

            // Exercise synthesis history as well as the first frame. Both
            // decoder instances must evolve identically over a stream.
            for (int iteration = 0; iteration < 32; iteration++)
            {
                Assert.IsTrue(legacyDecoder.Decode(encoded,
                    out short[] legacyPcm, out short[] legacyRight,
                    out SbcFrame legacyFrame));
                Assert.IsTrue(realtimeDecoder.DecodeInto(encoded,
                    realtimePcm, null, realtimeFrame,
                    out int realtimeSamples));

                Assert.IsNull(legacyRight);
                Assert.AreEqual(legacyPcm.Length, realtimeSamples);
                Assert.AreEqual(legacyFrame.Frequency,
                    realtimeFrame.Frequency);
                Assert.AreEqual(legacyFrame.Mode, realtimeFrame.Mode);
                Assert.AreEqual(legacyFrame.Blocks, realtimeFrame.Blocks);
                Assert.AreEqual(legacyFrame.Subbands,
                    realtimeFrame.Subbands);
                Assert.AreEqual(legacyFrame.Bitpool,
                    realtimeFrame.Bitpool);

                for (int sample = 0; sample < realtimeSamples; sample++)
                {
                    Assert.AreEqual(legacyPcm[sample], realtimePcm[sample],
                        $"PCM differed at iteration {iteration}, sample " +
                        $"{sample}.");
                }
            }
        }

        [TestMethod]
        public void DecodeIntoDoesNotAllocatePerDualShock4MicrophoneFrame()
        {
            byte[] encoded = Convert.FromHexString(
                GenuineDualShock4MicrophoneFrameHex);
            var decoder = new SbcDecoder();
            var frame = new SbcFrame();
            var pcm = new short[SbcFrame.MaxSamples];

            bool decoded = true;
            int sampleCount = 0;
            for (int iteration = 0; iteration < 64; iteration++)
            {
                decoded &= decoder.DecodeInto(encoded, pcm, null, frame,
                    out sampleCount);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 10_000; iteration++)
            {
                decoded &= decoder.DecodeInto(encoded, pcm, null, frame,
                    out sampleCount);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(decoded);
            Assert.AreEqual(128, sampleCount);
            Assert.AreEqual(SbcFrequency.Freq16K, frame.Frequency);
            Assert.AreEqual(SbcMode.Mono, frame.Mode);
            Assert.IsTrue(allocated < 1024,
                $"Realtime SBC decoding allocated {allocated} bytes over " +
                "10,000 frames.");
        }
    }
}
