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
