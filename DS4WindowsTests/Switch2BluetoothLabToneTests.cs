using Concentus;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothLabToneTests
{
    [DataTestMethod]
    [DataRow("tone-opus5", 5, 0)]
    [DataRow("tone-opus20", 20, 0)]
    [DataRow("tone-rumble-opus5", 5, 32)]
    [DataRow("tone-rumble-opus20", 20, 32)]
    [DataRow("tone-length-opus5", 5, 1)]
    [DataRow("tone-length-opus20", 20, 1)]
    [DataRow("tone-rumble-length-opus5", 5, 33)]
    [DataRow("tone-rumble-length-opus20", 20, 33)]
    [DataRow("active-opus5", 5, 0)]
    [DataRow("active-opus20", 20, 0)]
    [DataRow("active-length-opus5", 5, 1)]
    [DataRow("active-rumble-length-opus20", 20, 33)]
    public void HypothesesAreFiniteBoundedDecodableAndChannelSpecific(string command, int interval, int prefix)
    {
        var tone = Switch2BluetoothLabTone.Create(command);
        Assert.AreEqual((double)interval, tone.IntervalMilliseconds);
        Assert.AreEqual(prefix, tone.PrefixBytes);
        Assert.AreEqual(600 / interval, tone.Packets.Length);
        using var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        var output = new short[960 * 2];
        var pcm = new List<short>();
        foreach (var packet in tone.Packets)
        {
            Assert.AreEqual(prefix + interval * 10, packet.Length);
            Assert.IsTrue(packet.Length <= 509);
            for (int p = 0; p < prefix; p++)
                Assert.AreEqual(command.Contains("-length-") && p == prefix - 1 ? interval * 10 : 0, (int)packet[p]);
            int samples = decoder.Decode(packet.AsSpan(prefix), output.AsSpan(), 960, false);
            Assert.AreEqual(48 * interval, samples);
            pcm.AddRange(output.Take(samples * 2));
        }
        Assert.IsTrue(pcm.Max(x => Math.Abs((int)x)) < 500, "Decoded signal must remain quiet, including codec overshoot.");
        double Amplitude(int channel, double hz, int start, int count)
        {
            double real = 0, imaginary = 0;
            for (int i = 0; i < count; i++)
            {
                double sample = pcm[(start + i) * 2 + channel];
                real += sample * Math.Cos(2 * Math.PI * hz * i / 48000);
                imaginary += sample * Math.Sin(2 * Math.PI * hz * i / 48000);
            }
            return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
        }
        Assert.IsTrue(Amplitude(0, 440, 4800, 14400) > 10 * Amplitude(0, 660, 4800, 14400));
        Assert.IsTrue(Amplitude(1, 660, 4800, 14400) > 10 * Amplitude(1, 440, 4800, 14400));
        Assert.IsTrue(pcm.Skip(48000 * 55 / 100 * 2).All(x => Math.Abs((int)x) < 10), "Finite trailing silence must decay.");
    }

    [TestMethod]
    public async Task UndersizedAttCapacityAndPreCancellationNeverWrite()
    {
        var tone = Switch2BluetoothLabTone.Create("tone-opus5");
        int writes = 0;
        Task<bool> Write(byte[] _) { writes++; return Task.FromResult(true); }
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => tone.SendAsync(49, Write, CancellationToken.None));
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => tone.SendAsync(509, Write, stopping.Token));
        Assert.AreEqual(0, writes);
    }

    [TestMethod]
    public async Task CancellationRetainsRealWriteCompletionAndNeverReplays()
    {
        var tone = Switch2BluetoothLabTone.Create("tone-opus5");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        int writes = 0;
        Task<object> send = tone.SendAsync(509, _ => { writes++; entered.TrySetResult(); return finish.Task; }, stopping.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopping.Cancel();
        Assert.IsFalse(send.IsCompleted, "Do not release the service under a still-owned WinRT write.");
        finish.SetResult(true);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => send);
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public async Task RejectedWriteIsNotReplayed()
    {
        int writes = 0;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Switch2BluetoothLabTone.Create("tone-opus20")
            .SendAsync(509, _ => { writes++; return Task.FromResult(false); }, CancellationToken.None));
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public void HeadsetParserDoesNotConfuseMotionWithAudioOrOtherReportTypes()
    {
        byte[] value = new byte[112];
        value[13] = 5; value[14] = 50; value[15] = 0xf8; value[16] = 0xff; value[17] = 0xfe;
        value[65] = 40;
        value.AsSpan(66).Fill(0xa5);
        Assert.IsTrue(Switch2BluetoothLabHeadsetHeader.TryRead(value, out byte jack, out byte length, out bool idle));
        Assert.AreEqual(5, (int)jack); Assert.AreEqual(50, (int)length); Assert.IsTrue(idle);
        value[25] = 1;
        Switch2BluetoothLabHeadsetHeader.TryRead(value, out _, out _, out idle);
        Assert.IsFalse(idle);
        Assert.IsFalse(Switch2BluetoothLabHeadsetHeader.TryRead(value.AsSpan(0, 64), out _, out _, out _));
        value[14] = 200;
        Assert.IsFalse(Switch2BluetoothLabHeadsetHeader.TryRead(value, out _, out _, out _));
    }

    [DataTestMethod]
    [DataRow("t:opus:1:20:20:raw", 1, 20.0, 50)]
    [DataRow("a:opus:1:20:20:len", 1, 20.0, 51)]
    [DataRow("t:opus:2:20:160:raw", 2, 20.0, 400)]
    [DataRow("a:opus:2:2.5:80:rumlen", 2, 2.5, 58)]
    [DataRow("t:pcm:1:5:0:raw", 1, 5.0, 480)]
    [DataRow("t:pcm:2:2.5:0:raw", 2, 2.5, 480)]
    [DataRow("a:pcm:1:2.5:0:rumlen", 1, 2.5, 273)]
    public void ParameterizedCandidatesAreQuietFiniteAndSingleAtt(string command, int channels, double interval, int bytes)
    {
        Assert.IsTrue(Switch2BluetoothLabCandidate.TryParse(command, out var candidate));
        var tone = Switch2BluetoothLabTone.Create(command);
        Assert.AreEqual(channels, tone.Channels);
        Assert.AreEqual(interval, tone.IntervalMilliseconds);
        Assert.AreEqual((int)(600 / interval), tone.Packets.Length);
        using var decoder = candidate.Pcm ? null : OpusCodecFactory.CreateDecoder(48000, channels);
        var output = new short[960 * channels];
        var pcm = new List<short>();
        foreach (byte[] packet in tone.Packets)
        {
            Assert.AreEqual(bytes, packet.Length);
            Assert.IsTrue(packet.Length <= 509);
            if (candidate.LengthPrefix) Assert.AreEqual(packet.Length - candidate.PrefixBytes, (int)packet[candidate.PrefixBytes - 1]);
            var payload = packet.AsSpan(candidate.PrefixBytes);
            if (candidate.Pcm)
                for (int i = 0; i < payload.Length; i += 2) pcm.Add(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(payload[i..]));
            else
            {
                int samples = decoder.Decode(payload, output.AsSpan(), 960, false);
                Assert.AreEqual(candidate.Samples, samples);
                pcm.AddRange(output.Take(samples * channels));
                Assert.AreEqual(channels, Concentus.Structs.OpusPacketInfo.GetNumEncodedChannels(payload));
            }
        }
        Assert.IsTrue(pcm.Max(x => Math.Abs((int)x)) < 500);
        Assert.IsTrue(pcm.Skip(26400 * channels).All(x => Math.Abs((int)x) < 10));
        double Amplitude(int ch, int hz)
        {
            double real = 0, imaginary = 0;
            for (int i = 4800; i < 19200; i++)
            {
                double phase = 2 * Math.PI * hz * i / 48000;
                real += pcm[i * channels + ch] * Math.Cos(phase);
                imaginary += pcm[i * channels + ch] * Math.Sin(phase);
            }
            return Math.Sqrt(real * real + imaginary * imaginary);
        }
        Assert.IsTrue(Amplitude(0, 440) > 10 * Amplitude(0, 660));
        if (channels == 2) Assert.IsTrue(Amplitude(1, 660) > 10 * Amplitude(1, 440));
    }

    [TestMethod]
    public void CandidateGrammarRejectsArbitraryPayloadsAndImpossibleFraming()
    {
        foreach (string value in new[] { "", "t:opus:0:5:80:raw", "t:opus:2:3:80:raw", "t:opus:2:5:0:raw",
            "t:opus:1:2.5:20:raw", "t:opus:2:20:160:len", "t:pcm:2:5:0:raw", "t:pcm:1:5:0:rum",
            "t:pcm:2:2.5:0:len", "t:pcm:2:2.5:80:raw", "t:pcm:2:2.5:0:raw:gain=1", "t:lc3:2:5:80:raw",
            "t:opus:2:5:80:raw ", "T:opus:2:5:80:raw", "t:opus:2:5:80:00FF", new string('x', 10000) })
        {
            Assert.IsFalse(Switch2BluetoothLabCandidate.TryParse(value, out _), value);
            Assert.IsFalse(Switch2BluetoothLabAudioProtocol.IsAllowed(value), value);
        }
        Assert.IsFalse(Switch2BluetoothLabCandidate.TryParse(null, out _));
    }

    [TestMethod]
    public void MonoTwentyMillisecondCandidateMatchesIdlePacketStructureNotPlayback()
    {
        var tone = Switch2BluetoothLabTone.Create("t:opus:1:20:20:raw");
        Assert.IsTrue(tone.Packets.All(p => p.Length == 50 && p[0] == 0xf8), "Fullband mono 20 ms TOC is the tested hypothesis.");
    }

    [TestMethod]
    public void HeadsetControlsRetainDocumentedFieldsWithoutReadingAudioAsMotion()
    {
        byte[] body = new byte[112];
        body[0] = 42; body[1] = 0x24; body[2] = 0x55; body[3] = 0xAA; body[4] = 0x12;
        body[5] = 0x34; body[6] = 0xA2; body[7] = 0xBC;
        body[8] = 0x78; body[9] = 0xF6; body[10] = 0xDE;
        body[13] = 5; body[14] = 50; body.AsSpan(15, 50).Fill(0xA5); body[65] = 30;
        Assert.IsTrue(Switch2InputCodec.TryDecodeHeadsetControls(body, out var controls));
        Assert.AreEqual(0x12AA55u, controls.Buttons);
        Assert.AreEqual(Switch2InputCodec.DecodePackedStick(body.AsSpan(5, 3)), controls.PrimaryStick);
        Assert.AreEqual(Switch2InputCodec.DecodePackedStick(body.AsSpan(8, 3)), controls.SecondaryStick);
        Assert.AreEqual(66, controls.Motion.BodyOffset);
        Assert.AreEqual(30, controls.Motion.DeclaredLength);
        body[14] = 0;
        Assert.IsTrue(Switch2InputCodec.TryDecodeHeadsetControls(body, out _));
        Assert.IsTrue(Switch2BluetoothLabHeadsetHeader.TryRead(body, out _, out byte length, out bool idle));
        Assert.AreEqual(0, (int)length); Assert.IsFalse(idle);
        for (int size = 0; size < 112; size++) Assert.IsFalse(Switch2InputCodec.TryDecodeHeadsetControls(body.AsSpan(0, size), out _));
        body[65] = 41; Assert.IsFalse(Switch2InputCodec.TryDecodeHeadsetControls(body, out _));
        body[65] = 0; body[14] = 49; Assert.IsFalse(Switch2InputCodec.TryDecodeHeadsetControls(body, out _));
    }

    [TestMethod]
    public void HeadsetControlsDecoderAllocatesNothing()
    {
        byte[] body = new byte[112]; body[14] = 50;
        for (int i = 0; i < 100; i++) Switch2InputCodec.TryDecodeHeadsetControls(body, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Switch2InputCodec.TryDecodeHeadsetControls(body, out _);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
