using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MotionSoftDeadzoneTests
{
    [TestMethod]
    public void DisabledDeadzonePreservesEveryAxisExactly()
    {
        Vector3 input = new(12.5f, -34.5f, 56.5f);

        Vector3 output = Switch2MotionSoftDeadzone.Apply(input, 0.0,
            horizontal: false);

        Assert.AreEqual(input, output);
    }

    [TestMethod]
    public void VerticalModeSoftThresholdsPitchAndYawOnly()
    {
        Vector3 output = Switch2MotionSoftDeadzone.Apply(
            new Vector3(12.0f, 7.0f, -4.0f), 5.0,
            horizontal: false);

        Assert.AreEqual(new Vector3(7.0f, 7.0f, 0.0f), output);
    }

    [TestMethod]
    public void HorizontalModeSoftThresholdsAlternatePitchAndYawAxes()
    {
        Vector3 output = Switch2MotionSoftDeadzone.Apply(
            new Vector3(12.0f, -8.0f, 5.0f), 5.0,
            horizontal: true);

        Assert.AreEqual(new Vector3(12.0f, -3.0f, 0.0f), output);
    }

    [TestMethod]
    public void InvalidDeadzoneFailsToDisabledDefault()
    {
        Vector3 input = new(12.0f, -8.0f, 5.0f);

        Assert.AreEqual(input, Switch2MotionSoftDeadzone.Apply(input,
            double.NaN, horizontal: false));
        Assert.AreEqual(input, Switch2MotionSoftDeadzone.Apply(input,
            100.5, horizontal: true));
    }
}
