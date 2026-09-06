using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MappedStickMousePresentationTests
{
    [TestMethod]
    public void CapturesSignedCanonicalDeltaAsVelocityPerAxis()
    {
        Switch2MappedStickMousePresentationFrame frame = default;

        Assert.IsTrue(frame.TryCapture(DS4Controls.LXNeg,
            signedDelta: -2.5, reportIntervalMilliseconds: 5.0));
        Assert.IsTrue(frame.TryCapture(DS4Controls.RYPos,
            signedDelta: 3.0, reportIntervalMilliseconds: 10.0));

        Assert.IsTrue(frame.HasHorizontalMapping);
        Assert.IsTrue(frame.HasVerticalMapping);
        Assert.AreEqual(-2.5, frame.DeltaX, 0.000001);
        Assert.AreEqual(3.0, frame.DeltaY, 0.000001);
        Assert.AreEqual(-500.0, frame.VelocityX, 0.000001);
        Assert.AreEqual(300.0, frame.VelocityY, 0.000001);
        Assert.IsTrue(frame.Active);
    }

    [TestMethod]
    public void CenteredMappedAxisIsOwnedButInactive()
    {
        Switch2MappedStickMousePresentationFrame frame = default;

        Assert.IsTrue(frame.TryCapture(DS4Controls.LYNeg, 0.0, 2.0));
        Assert.IsTrue(frame.HasVerticalMapping);
        Assert.AreEqual(0.0, frame.VelocityY);
        Assert.IsFalse(frame.Active);
    }

    [TestMethod]
    public void RejectsNonStickInvalidAndStaleReportIntervals()
    {
        Switch2MappedStickMousePresentationFrame frame = default;

        Assert.IsFalse(frame.TryCapture(DS4Controls.Cross, 1.0, 1.0));
        Assert.IsFalse(frame.TryCapture(DS4Controls.LXPos,
            double.NaN, 1.0));
        Assert.IsFalse(frame.TryCapture(DS4Controls.LXPos, 1.0, 0.0));
        Assert.IsFalse(frame.TryCapture(DS4Controls.LXPos, 1.0,
            Switch2MappedStickMousePresentationFrame.
                MaximumReportIntervalMilliseconds + 0.001));
        Assert.IsFalse(frame.Active);
    }

    [TestMethod]
    public void TransferRemovesOnlyAdmittedMappedDeltaFromCanonicalReport()
    {
        Switch2MappedStickMousePresentationFrame frame = default;
        frame.TryCapture(DS4Controls.LXPos, 2.5, 5.0);
        frame.TryCapture(DS4Controls.RYNeg, -1.5, 5.0);
        double reportX = 12.5;
        double reportY = 8.5;

        Assert.IsTrue(Mapping.TransferSwitch2MappedStickMouseToHighRate(
            in frame, active: true, admitted: true, ref reportX,
            ref reportY));
        Assert.AreEqual(10.0, reportX, 0.000001);
        Assert.AreEqual(10.0, reportY, 0.000001);
    }

    [TestMethod]
    public void RejectedAdmissionPreservesPerReportFallbackExactly()
    {
        Switch2MappedStickMousePresentationFrame frame = default;
        frame.TryCapture(DS4Controls.LXPos, 2.5, 5.0);
        double reportX = 2.5;
        double reportY = 0.0;

        Assert.IsFalse(Mapping.TransferSwitch2MappedStickMouseToHighRate(
            in frame, active: true, admitted: false, ref reportX,
            ref reportY));
        Assert.AreEqual(2.5, reportX, 0.000001);
        Assert.AreEqual(0.0, reportY, 0.000001);
    }

    [TestMethod]
    public void WarmCapturePathDoesNotAllocate()
    {
        Switch2MappedStickMousePresentationFrame frame = default;
        frame.TryCapture(DS4Controls.LXPos, 1.0, 2.0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 20_000; index++)
        {
            frame = default;
            succeeded &= frame.TryCapture(DS4Controls.LXPos,
                index % 2 == 0 ? 1.25 : -1.25, 2.0);
            succeeded &= frame.TryCapture(DS4Controls.RYNeg,
                index % 2 == 0 ? -0.75 : 0.75, 2.0);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, after - before);
    }
}
