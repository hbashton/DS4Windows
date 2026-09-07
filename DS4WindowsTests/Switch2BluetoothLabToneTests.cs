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
        Assert.AreEqual(interval, tone.IntervalMilliseconds);
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
}
