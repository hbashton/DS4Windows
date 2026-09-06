using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2PcmCorrelationTests
{
    public TestContext TestContext { get; set; }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ShortPeakIsNotReducedToSliceAverage(bool combined)
    {
        var packet = Packet(i => i == 15 ? 96 : 0, _ => 0, combined);
        var (left, right) = Translate(packet);
        ushort peakCode = Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(49151);
        TestContext.WriteLine($"Peak code: HF={left.Second.Oscillator0AmplitudeCode}, LF={left.Second.Oscillator1AmplitudeCode}, authored peak reference={peakCode}.");
        Assert.IsTrue(left.Second.Oscillator0AmplitudeCode >= peakCode * 0.8,
            $"The brief authored peak was flattened: {left.Second.Oscillator0AmplitudeCode}/{peakCode}.");
        Assert.IsFalse(left.First.HasNonzeroAmplitude, "No pre-rumble in a silent slice.");
        Assert.IsFalse(left.Third.HasNonzeroAmplitude, "No invented release tail.");
        Assert.IsFalse(right.First.HasNonzeroAmplitude);
        Assert.IsFalse(right.Second.HasNonzeroAmplitude);
        Assert.IsFalse(right.Third.HasNonzeroAmplitude);
    }

    [TestMethod]
    public void LowAndHighEnvelopesFollowTheirOwnTimeSlices()
    {
        var (left, _) = Translate(Packet(i => i < 10 ? 80 : i < 21 ?
            ((i & 1) == 0 ? 80 : -80) : 0, _ => 0));
        Assert.IsTrue(left.First.Oscillator1AmplitudeCode > left.First.Oscillator0AmplitudeCode * 2,
            "The low-frequency opening must not borrow the later high-frequency balance.");
        Assert.IsTrue(left.Second.Oscillator0AmplitudeCode > left.Second.Oscillator1AmplitudeCode * 2,
            "The high-frequency middle must not inherit the opening's low-frequency envelope.");
        Assert.IsFalse(left.Third.HasNonzeroAmplitude);
    }

    [TestMethod]
    public void LouderPeaksKeepHeadroomInsteadOfFlatteningAtLegacyGainCeiling()
    {
        ushort previous = 0;
        foreach (int peak in new[] { 88, 96, 112, 127 })
        {
            var (left, _) = Translate(Packet(_ => peak, _ => 0));
            Assert.IsTrue(left.First.Oscillator1AmplitudeCode > previous,
                $"Peak {peak} collapsed onto the preceding amplitude {previous}.");
            previous = left.First.Oscillator1AmplitudeCode;
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void StereoBandsKeepTheirSideThroughBothWireFormats(bool combined, bool bluetooth)
    {
        int Low(int i) => Tone(i, 1, 80);
        int High(int i) => Tone(i, 6, 60);
        var (left, right) = Translate(Packet(Low, High, combined));
        var (swappedLeft, swappedRight) = Translate(Packet(High, Low, combined));
        Assert.AreEqual(left, swappedRight);
        Assert.AreEqual(right, swappedLeft);
        byte[] wire = new byte[bluetooth ? Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength :
            Switch2UsbHdRumbleCodec.ReportLength];
        Assert.IsTrue(bluetooth ? Switch2BluetoothHdRumbleCodec.TryEncodeProController(3, left, right, wire) :
            Switch2UsbHdRumbleCodec.TryEncodeProController(3, left, right, wire));
        for (int slice = 0; slice < 3; slice++)
        {
            // Independent bit extraction: envelope byte, group header, then 5 bytes per slice.
            var leftFields = ReadAmplitudes(wire, 2 + slice * 5);
            var rightFields = ReadAmplitudes(wire, 18 + slice * 5);
            Assert.IsTrue(leftFields.Low > leftFields.High * 10);
            Assert.IsTrue(rightFields.High > rightFields.Low * 10);
        }
    }

    [TestMethod]
    public void WeakCompatibilityRumbleCannotOverwriteStrongerStereoPcmCarriers()
    {
        byte[] packet = Packet(i => Tone(i, 1, 70), i => Tone(i, 6, 70));
        var (pcmLeft, pcmRight) = Translate(packet);
        packet[0] = packet[1] = 1;
        Assert.IsTrue(ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(packet, packet.Length, 76,
            false, false, out var left, out var right, out _));
        Assert.AreEqual(pcmLeft.First.Oscillator1ControlCode, left.First.Oscillator1ControlCode);
        Assert.AreEqual(pcmRight.First.Oscillator0ControlCode, right.First.Oscillator0ControlCode);
        Assert.IsTrue(left.First.Oscillator1AmplitudeCode >= pcmLeft.First.Oscillator1AmplitudeCode);
        Assert.IsTrue(right.First.Oscillator0AmplitudeCode >= pcmRight.First.Oscillator0AmplitudeCode);
    }

    [TestMethod]
    public void StrongCompatibilityRumbleRetainsItsCarrierOverQuietPcm()
    {
        byte[] packet = Packet(i => Tone(i, 1, 2), i => Tone(i, 6, 2));
        packet[0] = packet[1] = 180;
        Assert.IsTrue(ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(packet, packet.Length, 76,
            false, false, out var left, out var right, out _));
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.SdlLowControlCode, left.First.Oscillator1ControlCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.SdlHighControlCode, right.First.Oscillator0ControlCode);
    }

    [TestMethod]
    public void FrequencyBinAndPhaseSweepRetainsBandOwnership()
    {
        for (int bin = 1; bin <= 16; bin++)
            for (int phase = 0; phase < 8; phase++)
            {
                byte[] packet = Packet(i => (int)Math.Round(80 *
                    Math.Cos(2 * Math.PI * bin * i / 32 + phase * Math.PI / 4)), _ => 0);
                var (left, right) = Translate(packet);
                foreach (var slice in new[] { left.First, left.Second, left.Third })
                {
                    ushort opposite = bin <= 2 ? slice.Oscillator0AmplitudeCode : slice.Oscillator1AmplitudeCode;
                    Assert.IsTrue(opposite <= 2, $"Bin={bin}, phase={phase}, leakage={opposite}.");
                }
                Assert.IsFalse(right.First.HasNonzeroAmplitude);
                Assert.IsFalse(right.Second.HasNonzeroAmplitude);
                Assert.IsFalse(right.Third.HasNonzeroAmplitude);
            }
    }

    [TestMethod]
    public void EveryPeakPositionRetainsSliceTimingAndSignedRange()
    {
        for (int position = 0; position < 32; position++)
            foreach (int peak in new[] { -128, -32, 32, 127 })
            {
                var (left, _) = Translate(Packet(i => i == position ? peak : 0, _ => 0));
                var slices = new[] { left.First, left.Second, left.Third };
                int active = position < 10 ? 0 : position < 21 ? 1 : 2;
                for (int slice = 0; slice < 3; slice++)
                    Assert.AreEqual(slice == active, slices[slice].HasNonzeroAmplitude,
                        $"Position={position}, peak={peak}, slice={slice}.");
            }
    }

    [TestMethod]
    public void ExactGainScalingAndChannelSwapRemainIndependentAcrossCorpus()
    {
        var random = new Random(4917);
        for (int trial = 0; trial < 100; trial++)
        {
            int[] sourceLeft = Enumerable.Range(0, 32).Select(_ => random.Next(-60, 61)).ToArray();
            int[] sourceRight = Enumerable.Range(0, 32).Select(_ => random.Next(-60, 61)).ToArray();
            var normal = Translate(Packet(i => sourceLeft[i], i => sourceRight[i]));
            var louder = Translate(Packet(i => sourceLeft[i] * 2, i => sourceRight[i] * 2));
            var swapped = Translate(Packet(i => sourceRight[i], i => sourceLeft[i]));
            Assert.AreEqual(normal.Left, swapped.Right);
            Assert.AreEqual(normal.Right, swapped.Left);
            var a = new[] { normal.Left.First, normal.Left.Second, normal.Left.Third,
                normal.Right.First, normal.Right.Second, normal.Right.Third };
            var b = new[] { louder.Left.First, louder.Left.Second, louder.Left.Third,
                louder.Right.First, louder.Right.Second, louder.Right.Third };
            for (int slice = 0; slice < 6; slice++)
            {
                // Canonical rounding plus the SDL integer wire quantizer can
                // straddle a threshold; allow two codes, not a nonlinear gain.
                Assert.AreEqual(a[slice].Oscillator0AmplitudeCode * 2,
                    b[slice].Oscillator0AmplitudeCode, 2.0);
                Assert.AreEqual(a[slice].Oscillator1AmplitudeCode * 2,
                    b[slice].Oscillator1AmplitudeCode, 2.0);
            }
        }
    }

    private static byte[] Packet(Func<int, int> left, Func<int, int> right, bool combined = false)
    {
        byte[] packet = new byte[76 + (combined ? 398 : 141)];
        packet[76] = combined ? (byte)0x36 : (byte)0x32;
        int offset = 76 + (combined ? 78 : 13);
        for (int i = 0; i < 32; i++)
        {
            packet[offset + i * 2] = unchecked((byte)(sbyte)left(i));
            packet[offset + i * 2 + 1] = unchecked((byte)(sbyte)right(i));
        }
        return packet;
    }

    private static (Switch2HdRumbleGroup Left, Switch2HdRumbleGroup Right) Translate(byte[] packet)
    {
        Assert.IsTrue(DualSenseHapticsTranslator.TryTranslateToSwitch2Groups(packet, packet.Length, 76,
            out var left, out var right));
        return (left, right);
    }

    private static int Tone(int sample, int bin, int peak) =>
        (int)Math.Round(peak * Math.Sin(2 * Math.PI * bin * sample / 32));

    private static (int Low, int High) ReadAmplitudes(byte[] wire, int offset)
    {
        ulong bits = 0;
        for (int i = 0; i < 5; i++) bits |= (ulong)wire[offset + i] << (8 * i);
        return ((int)((bits >> 30) & 1023), (int)((bits >> 10) & 1023));
    }
}
