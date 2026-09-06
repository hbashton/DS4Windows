using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2FeedbackDetailTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void MixerIsBoundedMonotonicAndNeutralTransparentAcrossAllWireCodes()
    {
        for (ushort a = 0; a <= 1023; a++)
        {
            ushort previous = 0;
            for (ushort b = 0; b <= 1023; b++)
            {
                ushort mixed = Switch2HdRumbleFeedbackTranslator.MixPackedAmplitudesWithHeadroom(a, b);
                Assert.IsTrue(mixed >= Math.Max(a, b) && mixed <= 1023 && mixed >= previous);
                Assert.AreEqual(mixed, Switch2HdRumbleFeedbackTranslator.MixPackedAmplitudesWithHeadroom(b, a));
                if (b == 0) Assert.AreEqual(a, mixed);
                previous = mixed;
            }
        }
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(16)]
    public void PacketBandEnergyAgreesWithIndependentDirectFourierOracle(int dominantBin)
    {
        byte[] samples = new byte[64];
        for (int i = 0; i < 32; i++)
            samples[2 * i] = unchecked((byte)(sbyte)Math.Round(60 *
                Math.Cos(2 * Math.PI * dominantBin * i / 32 + 0.37) +
                5 * Math.Sin(2 * Math.PI * 2 * i / 32)));
        double low = 0, high = 0;
        for (int bin = 0; bin <= 16; bin++)
        {
            double real = 0, imaginary = 0;
            for (int i = 0; i < 32; i++)
            {
                double sample = unchecked((sbyte)samples[i * 2]);
                real += sample * Math.Cos(2 * Math.PI * bin * i / 32);
                imaginary -= sample * Math.Sin(2 * Math.PI * bin * i / 32);
            }
            double power = (real * real + imaginary * imaginary) * (bin is 0 or 16 ? 1 : 2);
            if (bin <= 2) low += power; else high += power;
        }
        var result = Switch2PcmBandAnalyzer.Analyze(samples, 0);
        Assert.AreEqual(low / (low + high), result.LowScale * result.LowScale, 1e-9);
        Assert.AreEqual(high / (low + high), result.HighScale * result.HighScale, 1e-9);
        Assert.AreEqual(1.0, result.LowScale * result.LowScale + result.HighScale * result.HighScale, 1e-9);
    }

    [TestMethod]
    public void SilenceAfterLoudPcmDoesNotRetainAnyPriorEffect()
    {
        _ = TranslateTone(3, 120);
        var silent = TranslateTone(3, 0);
        Assert.IsFalse(silent.Left.First.HasNonzeroAmplitude);
        Assert.IsFalse(silent.Left.Second.HasNonzeroAmplitude);
        Assert.IsFalse(silent.Left.Third.HasNonzeroAmplitude);
        Assert.AreEqual(silent.Left, silent.Right);
    }

    [TestMethod]
    public void PcmConversionHasNoWarmAllocationsAndReportsOfflineCpuCost()
    {
        byte[] packet = new byte[426];
        packet[28] = 0x32;
        for (int i = 0; i < 64; i++) packet[41 + i] = unchecked((byte)(sbyte)(i % 31 - 15));
        for (int i = 0; i < 2000; i++)
            DualSenseHapticsTranslator.TryTranslateToSwitch2Groups(packet, packet.Length, 28, out _, out _);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = true;
        for (int i = 0; i < 20_000; i++)
            accepted &= DualSenseHapticsTranslator.TryTranslateToSwitch2Groups(packet, packet.Length, 28, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double microseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds / 20_000;
        Assert.IsTrue(accepted);
        Assert.AreEqual(0L, allocated);
        TestContext.WriteLine($"Offline warmed stereo PCM converter mean: {microseconds:F3} us/report; not hardware latency.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QuietPcmSurvivesHdConversionWithoutWakingTheSilentSide(bool combined)
    {
        var (left, right) = TranslateTone(2, 2, combined);
        Assert.IsTrue(left.First.HasNonzeroAmplitude);
        Assert.IsTrue(left.Second.HasNonzeroAmplitude);
        Assert.IsTrue(left.Third.HasNonzeroAmplitude);
        Assert.IsFalse(right.First.HasNonzeroAmplitude);
        Assert.IsFalse(right.Second.HasNonzeroAmplitude);
        Assert.IsFalse(right.Third.HasNonzeroAmplitude);
    }

    [DataTestMethod]
    [DataRow(1, 2, false)]
    [DataRow(3, 6, true)]
    public void EqualStrengthTonesRetainDistinctBandFrequencies(int firstBin, int secondBin, bool high)
    {
        var first = TranslateTone(firstBin, 70).Left.First;
        var second = TranslateTone(secondBin, 70).Left.First;
        Assert.IsTrue(high ? second.Oscillator0ControlCode > first.Oscillator0ControlCode :
            second.Oscillator1ControlCode > first.Oscillator1ControlCode);
    }

    [TestMethod]
    public void HighToneDoesNotBecomeAnEquallyStrongLowBandBuzz()
    {
        var high = TranslateTone(4, 70).Left.First;
        Assert.IsTrue(high.Oscillator0AmplitudeCode > 0);
        Assert.IsTrue(high.Oscillator1AmplitudeCode < high.Oscillator0AmplitudeCode / 10);
    }

    [TestMethod]
    public void TriggerFrequencySurvivesAnOccupiedBodyCarrier()
    {
        var body = new Switch2HdRumbleSubframe(391, 1, 274, 200);
        var trigger = new Switch2HdRumbleSubframe(320, 300, 225, 0);
        var mixed = DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(body, trigger);
        Assert.AreEqual((ushort)320, mixed.Oscillator0ControlCode);
        Assert.AreEqual(body.Oscillator1ControlCode, mixed.Oscillator1ControlCode);
        Assert.AreEqual(body.Oscillator1AmplitudeCode, mixed.Oscillator1AmplitudeCode);
        Assert.IsTrue(mixed.Oscillator0AmplitudeCode >= trigger.Oscillator0AmplitudeCode);
    }

    [TestMethod]
    public void OverlapRetainsHeadroomAndRespondsToIncreasingEffects()
    {
        var body = new Switch2HdRumbleSubframe(391, 900, 274, 0);
        var medium = DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(body,
            new Switch2HdRumbleSubframe(320, 300, 0, 0));
        var strong = DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(body,
            new Switch2HdRumbleSubframe(320, 600, 0, 0));
        Assert.IsTrue(medium.Oscillator0AmplitudeCode > body.Oscillator0AmplitudeCode);
        Assert.IsTrue(strong.Oscillator0AmplitudeCode > medium.Oscillator0AmplitudeCode);
        Assert.IsTrue(strong.Oscillator0AmplitudeCode < 1023);
        var neutral = DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(body, default(Switch2HdRumbleSubframe));
        Assert.AreEqual(body, neutral);
    }

    [TestMethod]
    public void XboxQuantizedSilentImpulseCannotRetuneBodyRumble()
    {
        var body = TranslateXbox(0);
        var imperceptible = TranslateXbox(1);
        Assert.AreEqual(body.Left, imperceptible.Left);
        Assert.AreEqual(body.Right, imperceptible.Right);
    }

    [TestMethod]
    public void XboxBoostedBodyStillLeavesImpulseDynamicRange()
    {
        var medium = TranslateXbox(30_000);
        var strong = TranslateXbox(60_000);
        Assert.IsTrue(strong.Left.First.Oscillator0AmplitudeCode >
            medium.Left.First.Oscillator0AmplitudeCode);
        Assert.IsTrue(strong.Left.First.Oscillator0AmplitudeCode < 1023);
        Assert.AreEqual(medium.Right, strong.Right, "Left impulse must remain side-local.");
    }

    internal static (Switch2HdRumbleGroup Left, Switch2HdRumbleGroup Right) TranslateTone(
        int bin, int peak, bool combined = false)
    {
        var packet = new byte[426];
        packet[28] = combined ? (byte)0x36 : (byte)0x32;
        int offset = 28 + (combined ? 78 : 13);
        for (int i = 0; i < 32; i++)
            packet[offset + i * 2] = unchecked((byte)(sbyte)Math.Round(
                peak * Math.Sin(2 * Math.PI * bin * i / 32)));
        Assert.IsTrue(DualSenseHapticsTranslator.TryTranslateToSwitch2Groups(packet,
            packet.Length, 28, out var left, out var right));
        return (left, right);
    }

    private static Switch2HdRumbleFeedbackSynthesis TranslateXbox(ushort trigger)
    {
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxSeriesVirtualDevice, ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators.All, 0, ushort.MaxValue, trigger, 0,
            sequence: 1, deviceGeneration: 1, transportGeneration: 1, ownershipEpoch: 1,
            timestampMicroseconds: 100, timeToLiveMicroseconds: 10_000, out var frame));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(200, out var body));
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(true, 10, 10, out var impulse));
        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.TryTranslate(frame, 100,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating, impulse, body,
            out var result));
        return result;
    }
}
