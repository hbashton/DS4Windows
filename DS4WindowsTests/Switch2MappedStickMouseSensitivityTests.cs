using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MappedStickMouseSensitivityTests
{
    [TestMethod]
    public void ValidSwitch2SourceAppliesIndependentLogicalStickGain()
    {
        Switch2RawInputStatus pro = Pro();

        Assert.AreEqual(0.5, Resolve(pro, default, DS4Controls.LXNeg,
            left: 2.5, right: 7.5), 0.000001);
        Assert.AreEqual(1.5, Resolve(pro, default, DS4Controls.RYPos,
            left: 2.5, right: 7.5), 0.000001);
        Assert.AreEqual(1.0, Resolve(pro, default, DS4Controls.Cross,
            left: 2.5, right: 7.5), 0.000001);
        Assert.AreEqual(0.0, Resolve(pro, default, DS4Controls.LYNeg,
            left: 0.0, right: 5.0), 0.000001);
    }

    [TestMethod]
    public void JoyConLogicalAxesUseTheSameIndependentProfileControls()
    {
        Switch2JoyConRawInputStatus joyCon = JoyCon();

        Assert.AreEqual(0.4, Resolve(default, joyCon, DS4Controls.LXPos,
            left: 2.0, right: 9.0), 0.000001);
        Assert.AreEqual(1.8, Resolve(default, joyCon, DS4Controls.RXNeg,
            left: 2.0, right: 9.0), 0.000001);
    }

    [TestMethod]
    public void InvalidOrAmbiguousSourcePreservesCanonicalMouseSpeed()
    {
        Assert.AreEqual(1.0, Resolve(default, default, DS4Controls.LXPos,
            left: 1.0, right: 10.0), 0.000001);
        Assert.AreEqual(1.0, Resolve(Pro(), JoyCon(), DS4Controls.RXPos,
            left: 1.0, right: 10.0), 0.000001);

        Switch2RawInputStatus stale = Pro();
        stale.TransportGeneration = 0;
        Assert.AreEqual(1.0, Resolve(stale, default, DS4Controls.LXPos,
            left: 1.0, right: 10.0), 0.000001);
    }

    [TestMethod]
    public void InvalidSensitivityNormalizesToIdentityDefault()
    {
        Assert.AreEqual(Switch2MappedStickMouseSensitivity.Default,
            Switch2MappedStickMouseSensitivity.Normalize(double.NaN));
        Assert.AreEqual(Switch2MappedStickMouseSensitivity.Default,
            Switch2MappedStickMouseSensitivity.Normalize(-0.1));
        Assert.AreEqual(Switch2MappedStickMouseSensitivity.Default,
            Switch2MappedStickMouseSensitivity.Normalize(10.1));
        Assert.AreEqual(10.0,
            Switch2MappedStickMouseSensitivity.Normalize(10.0));
    }

    [TestMethod]
    public void WarmPathDoesNotAllocate()
    {
        Switch2RawInputStatus pro = Pro();
        Resolve(pro, default, DS4Controls.LXPos, 2.5, 7.5);

        long before = GC.GetAllocatedBytesForCurrentThread();
        double sum = 0.0;
        for (int index = 0; index < 20_000; index++)
        {
            sum += Resolve(pro, default,
                index % 2 == 0 ? DS4Controls.LXPos : DS4Controls.RYNeg,
                2.5, 7.5);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.IsTrue(sum > 0.0);
        Assert.AreEqual(0L, after - before);
    }

    private static double Resolve(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon, DS4Controls control,
        double left, double right) =>
        Switch2MappedStickMouseSensitivity.ResolveGain(pro, joyCon, control,
            left, right);

    private static Switch2RawInputStatus Pro() => new()
    {
        IsValid = true,
        ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
        DeviceGeneration = 11,
        TransportGeneration = 12,
        CompletionTimestampQpc = 1_000,
        QpcFrequency = 1_000,
    };

    private static Switch2JoyConRawInputStatus JoyCon() => new()
    {
        IsValid = true,
        ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
        Mode = Switch2JoyConProfileMode.Joined,
        PairEpoch = 7,
        CompletionTimestampQpc = 1_000,
        QpcFrequency = 1_000,
        LeftPresent = true,
        LeftDeviceGeneration = 21,
        LeftTransportGeneration = 22,
        RightPresent = true,
        RightDeviceGeneration = 31,
        RightTransportGeneration = 32,
    };
}
