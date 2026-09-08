using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2HwOpusPlanTests
{
    [DataTestMethod]
    [DataRow("t")]
    [DataRow("a")]
    public void HeaderMatchesActualEncoderRangeAndPreservesQuietStereoSource(string mode)
    {
        var plan = HwOpusPlanFactory.Create(mode, 2);
        var raw = Switch2BluetoothLabTone.Create("t:opus:2:20:80:raw");
        var sourcePcm = Switch2BluetoothLabTone.Create("t:pcm:2:2.5:0:raw");
        Assert.AreEqual((ulong)2, plan.Generation);
        Assert.AreEqual(mode == "a", plan.HeadsetNotifications);
        Assert.AreEqual(30, plan.Packets.Length);

        using var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = 80000;
        encoder.UseVBR = false;
        encoder.Complexity = 5;
        encoder.ForceChannels = 2;
        encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
        using var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        short[] pcm = new short[1920], output = new short[1920];
        byte[] independentlyEncoded = new byte[512];
        var channels = new[] { new List<short>(), new List<short>() };
        bool observedNonzeroRange = false;
        for (int frame = 0; frame < plan.Packets.Length; frame++)
        {
            for (int sample = 0; sample < 960; sample++)
            {
                int position = frame * 960 + sample;
                var pair = sourcePcm.Packets[position / 120].AsSpan((position % 120) * 4, 4);
                pcm[sample * 2] = BinaryPrimitives.ReadInt16LittleEndian(pair);
                pcm[sample * 2 + 1] = BinaryPrimitives.ReadInt16LittleEndian(pair[2..]);
            }
            int encodedLength = encoder.Encode(pcm.AsSpan(), 960, independentlyEncoded.AsSpan(), independentlyEncoded.Length);
            Assert.AreEqual(200, encodedLength);

            byte[] value = plan.Packets[frame].Payload;
            Assert.AreEqual(frame * 20000, plan.Packets[frame].OffsetMicroseconds);
            Assert.AreEqual(208, value.Length);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 200 }, value.Take(4).ToArray());
            Assert.AreEqual(encoder.FinalRange, BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(4)));
            observedNonzeroRange |= encoder.FinalRange != 0;
            CollectionAssert.AreEqual(raw.Packets[frame], value.Skip(8).ToArray());
            CollectionAssert.AreEqual(independentlyEncoded.Take(encodedLength).ToArray(), value.Skip(8).ToArray());
            Assert.AreEqual(960, decoder.Decode(value.AsSpan(8), output.AsSpan(), 960, false));
            Assert.AreEqual(encoder.FinalRange, decoder.FinalRange);
            for (int sample = 0; sample < 960; sample++)
                for (int side = 0; side < 2; side++)
                    channels[side].Add(output[sample * 2 + side]);
        }
        Assert.IsTrue(observedNonzeroRange, "A placeholder zero range is not this hypothesis.");
        for (int side = 0; side < 2; side++)
        {
            var samples = channels[side];
            Assert.AreEqual(28800, samples.Count);
            Assert.IsTrue(samples.All(s => Math.Abs((int)s) < 500), "Quiet peaks including encoder overshoot.");
            Assert.IsTrue(samples.Skip(26400).All(s => Math.Abs((int)s) < 10), "The encoded tail decays to silence.");
            double Amplitude(int frequency)
            {
                double real = 0, imaginary = 0;
                for (int i = 4800; i < 19200; i++)
                {
                    double phase = 2 * Math.PI * frequency * i / 48000;
                    real += samples[i] * Math.Cos(phase);
                    imaginary += samples[i] * Math.Sin(phase);
                }
                return Math.Sqrt(real * real + imaginary * imaginary);
            }
            Assert.IsTrue(Amplitude(side == 0 ? 440 : 660) > 10 * Amplitude(side == 0 ? 660 : 440));
        }
    }

    [TestMethod]
    public void FactoryHasOnlyTwoFixedHypothesesAndIndependentPayloads()
    {
        Assert.ThrowsException<ArgumentException>(() => HwOpusPlanFactory.Create("other", 2));
        Assert.ThrowsException<ArgumentException>(() => HwOpusPlanFactory.Create("t", 0));
        var first = HwOpusPlanFactory.Create("t", 2);
        var second = HwOpusPlanFactory.Create("t", 2);
        CollectionAssert.AreEqual(first.Packets[0].Payload, second.Packets[0].Payload);
        first.Packets[0].Payload[8] ^= 0xFF;
        Assert.AreNotEqual(first.Packets[0].Payload[8], second.Packets[0].Payload[8]);
        Assert.AreNotSame(first.Packets[0].Payload, first.Packets[1].Payload);
    }
}
