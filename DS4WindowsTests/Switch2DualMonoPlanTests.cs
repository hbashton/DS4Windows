using Concentus;

namespace DS4WindowsTests;

[TestClass]
public class Switch2DualMonoPlanTests
{
    [DataTestMethod]
    [DataRow(5, "raw")]
    [DataRow(5, "len8")]
    [DataRow(5, "lengths")]
    [DataRow(5, "id0-len8")]
    [DataRow(20, "raw")]
    [DataRow(20, "len8")]
    [DataRow(20, "lengths")]
    [DataRow(20, "id0-len8")]
    public void IndependentChannelsDecodeQuietlyWithCorrectFrequencyAndSilence(int milliseconds, string framing)
    {
        foreach (string mode in new[] { "t", "a" })
        {
            var plan = DualMonoPlanFactory.Create(mode, milliseconds, 2, framing);
            Assert.AreEqual(mode == "a", plan.HeadsetNotifications);
            Assert.AreEqual(600 / milliseconds, plan.Packets.Length);
            using var leftDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
            using var rightDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
            var decoded = new[] { new List<short>(), new List<short>() };
            for (int frame = 0; frame < plan.Packets.Length; frame++)
            {
                byte[] payload = plan.Packets[frame].Payload;
                Assert.AreEqual(frame * milliseconds * 1000, plan.Packets[frame].OffsetMicroseconds);
                int prefix = framing == "id0-len8" ? 1 : 0;
                int leftOffset = framing switch { "raw" => 0, "lengths" => 2, _ => prefix + 1 };
                int rightOffset = framing == "raw" ? 50 : prefix + 52;
                Assert.AreEqual(framing == "raw" ? 100 : 102 + prefix, payload.Length);
                if (prefix == 1) Assert.AreEqual((byte)0, payload[0]);
                if (framing != "raw")
                {
                    Assert.AreEqual((byte)50, payload[prefix]);
                    Assert.AreEqual((byte)50, payload[framing == "lengths" ? 1 : prefix + 51]);
                }
                for (int side = 0; side < 2; side++)
                {
                    var codec = payload.AsSpan(side == 0 ? leftOffset : rightOffset, 50);
                    Assert.AreEqual(1, Concentus.Structs.OpusPacketInfo.GetNumEncodedChannels(codec));
                    var output = new short[960];
                    int count = (side == 0 ? leftDecoder : rightDecoder).Decode(codec, output.AsSpan(), 960, false);
                    Assert.AreEqual(milliseconds * 48, count);
                    decoded[side].AddRange(output.Take(count));
                }
            }
            AssertQuietChannels(decoded);
        }
    }

    [DataTestMethod]
    [DataRow(5, "t")]
    [DataRow(5, "a")]
    [DataRow(20, "t")]
    [DataRow(20, "a")]
    public void SelfDelimitedPacketsDecodeAsTwoRealOpusStreams(int milliseconds, string mode)
    {
        var plan = DualMonoPlanFactory.Create(mode, milliseconds, 2, "self-delimited");
        var raw = DualMonoPlanFactory.Create(mode, milliseconds, 2, "raw");
        Assert.AreEqual(mode == "a", plan.HeadsetNotifications);
        Assert.AreEqual((ulong)2, plan.Generation);
        Assert.AreEqual(600 / milliseconds, plan.Packets.Length);
        // Exercise the actual multistream parser, not a test-only unpacker that
        // could silently agree with an incorrect handcrafted envelope.
        using var decoder = OpusCodecFactory.CreateMultiStreamDecoder(48000, 2, 2, 0, new byte[] { 0, 1 });
        using var leftDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
        using var rightDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
        var decoded = new[] { new List<short>(), new List<short>() };
        for (int frame = 0; frame < plan.Packets.Length; frame++)
        {
            byte[] payload = plan.Packets[frame].Payload;
            byte[] source = raw.Packets[frame].Payload;
            Assert.AreEqual(101, payload.Length);
            Assert.AreEqual(source[0], payload[0]);
            Assert.AreEqual((byte)49, payload[1]);
            Assert.AreEqual(frame * milliseconds * 1000, plan.Packets[frame].OffsetMicroseconds);
            CollectionAssert.AreEqual(source.Skip(1).Take(49).ToArray(), payload.Skip(2).Take(49).ToArray());
            CollectionAssert.AreEqual(source.Skip(50).ToArray(), payload.Skip(51).ToArray());
            var stereo = new short[1920];
            int count = decoder.DecodeMultistream(payload, stereo.AsSpan(), 960, false);
            Assert.AreEqual(milliseconds * 48, count);
            for (int side = 0; side < 2; side++)
            {
                var expected = new short[960];
                int expectedCount = (side == 0 ? leftDecoder : rightDecoder).Decode(
                    source.AsSpan(side * 50, 50), expected.AsSpan(), 960, false);
                Assert.AreEqual(count, expectedCount);
                for (int sample = 0; sample < count; sample++)
                {
                    Assert.AreEqual(expected[sample], stereo[sample * 2 + side], "Framing preserves decoded channel samples.");
                    decoded[side].Add(stereo[sample * 2 + side]);
                }
            }
        }
        AssertQuietChannels(decoded);
    }

    [TestMethod]
    public void SelfDelimitedPackingRejectsUnsupportedCodecShapesAndDoesNotAliasInput()
    {
        byte[] left = new byte[50], right = new byte[50];
        left[0] = right[0] = 0xF8; // Mono, fullband, 20 ms, one frame.
        byte[] packed = DualMonoPlanFactory.PackSelfDelimitedDualMono(left, right);
        left[1] = right[1] = 0xFF;
        Assert.AreEqual((byte)0, packed[2]);
        Assert.AreEqual((byte)0, packed[52]);
        Assert.ThrowsException<InvalidDataException>(() => DualMonoPlanFactory.PackSelfDelimitedDualMono(new byte[49], right));
        Assert.ThrowsException<InvalidDataException>(() => DualMonoPlanFactory.PackSelfDelimitedDualMono(left, new byte[51]));
        left[0] = right[0] = 0xFC; // A coupled stereo stream is not dual mono.
        Assert.ThrowsException<InvalidDataException>(() => DualMonoPlanFactory.PackSelfDelimitedDualMono(left, right));
        left[0] = right[0] = 0xF9; // Code 1 requires a different frame-length calculation.
        Assert.ThrowsException<InvalidDataException>(() => DualMonoPlanFactory.PackSelfDelimitedDualMono(left, right));
        left[0] = 0xF8; right[0] = 0xE8; // Different stream durations are not this hypothesis.
        Assert.ThrowsException<InvalidDataException>(() => DualMonoPlanFactory.PackSelfDelimitedDualMono(left, right));
    }

    private static void AssertQuietChannels(List<short>[] decoded)
    {
        for (int side = 0; side < 2; side++)
        {
            var pcm = decoded[side];
            Assert.AreEqual(28800, pcm.Count);
            Assert.IsTrue(pcm.All(s => Math.Abs((int)s) < 500), "Quiet level including encoder overshoot.");
            Assert.IsTrue(pcm.Skip(26400).All(s => Math.Abs((int)s) < 10), "Trailing silence decays.");
            double Amplitude(int hz)
            {
                double real = 0, imaginary = 0;
                for (int i = 4800; i < 19200; i++)
                {
                    double phase = 2 * Math.PI * hz * i / 48000;
                    real += pcm[i] * Math.Cos(phase); imaginary += pcm[i] * Math.Sin(phase);
                }
                return Math.Sqrt(real * real + imaginary * imaginary);
            }
            Assert.IsTrue(Amplitude(side == 0 ? 440 : 660) > 10 * Amplitude(side == 0 ? 660 : 440));
        }
    }

    [TestMethod]
    public void InvalidDualMonoHypothesesAreRejectedOffline()
    {
        Assert.ThrowsException<ArgumentException>(() => DualMonoPlanFactory.Create("other", 5, 2, "raw"));
        Assert.ThrowsException<ArgumentException>(() => DualMonoPlanFactory.Create("t", 10, 2, "raw"));
        Assert.ThrowsException<ArgumentException>(() => DualMonoPlanFactory.Create("t", 5, 0, "raw"));
        Assert.ThrowsException<ArgumentException>(() => DualMonoPlanFactory.Create("t", 5, 2, "unknown"));
    }
}
