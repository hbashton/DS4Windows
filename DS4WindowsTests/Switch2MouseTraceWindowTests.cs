using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MouseTraceWindowTests
{
    private static Switch2JoyConRawInputStatus Input() => new()
    {
        IsValid = true, QpcFrequency = 1_000,
        LeftPresent = true, RightPresent = true,
        LeftHasCommonMotion = true, RightHasCommonMotion = true,
        LeftDeviceGeneration = 1, RightDeviceGeneration = 2,
        LeftTransportGeneration = 3, RightTransportGeneration = 4,
        LeftIrX = 100, RightIrX = 200, LeftIrDistance = 500,
    };

    [TestMethod]
    public void CountsActualSideChangesAndBoundsLogWindows()
    {
        var window = new Switch2MouseTraceWindow();
        var raw = Input();
        Assert.IsFalse(window.TrySample(raw, out _));
        raw.CompletionTimestampQpc = 100;
        raw.LeftIrX++;
        Assert.IsFalse(window.TrySample(raw, out _));
        raw.CompletionTimestampQpc = 2_000;
        Assert.IsTrue(window.TrySample(raw, out var sample));
        Assert.AreEqual(1, sample.LeftChanges);
        Assert.AreEqual(0, sample.RightChanges);
        Assert.AreEqual(3, sample.Reports);
        Assert.AreEqual((ushort)500, sample.LeftIrDistance);
        for (int i = 1; i < Switch2MouseTraceWindow.MaximumWindows; i++)
        {
            raw.CompletionTimestampQpc += 2_000;
            Assert.IsTrue(window.TrySample(raw, out sample));
            Assert.AreEqual(0, sample.LeftChanges);
        }
        raw.CompletionTimestampQpc += 2_000;
        Assert.IsFalse(window.TrySample(raw, out _));
    }

    [TestMethod]
    public void ReplacementCounterDoesNotMasqueradeAsMovement()
    {
        var window = new Switch2MouseTraceWindow();
        var raw = Input();
        window.TrySample(raw, out _);
        raw.LeftTransportGeneration++;
        raw.LeftIrX = 1_000;
        raw.CompletionTimestampQpc = 2_000;
        Assert.IsTrue(window.TrySample(raw, out var sample));
        Assert.AreEqual(0, sample.LeftChanges);
        raw.CompletionTimestampQpc = 1; // Clock regression starts a fresh baseline.
        raw.LeftIrX = 2_000;
        Assert.IsFalse(window.TrySample(raw, out _));
        raw.CompletionTimestampQpc = 2_001;
        Assert.IsTrue(window.TrySample(raw, out sample));
        Assert.AreEqual(0, sample.LeftChanges);
    }

    [TestMethod]
    public void SamplingIsAllocationFree()
    {
        var raw = Input();
        var window = new Switch2MouseTraceWindow();
        for (int i = 0; i < 100; i++) window.TrySample(raw, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            raw.CompletionTimestampQpc++;
            raw.LeftIrX++;
            window.TrySample(raw, out _);
        }
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [TestMethod]
    public void MotionPeakCapturesBriefMovementAndSignedMinimumWithoutOverflow()
    {
        var raw = Input();
        var window = new Switch2MouseTraceWindow();
        raw.LeftGyroscope = new Switch2Vector3Raw(short.MinValue, 1, 2);
        window.TrySample(raw, out _);
        raw.LeftGyroscope = default;
        raw.RightGyroscope = new Switch2Vector3Raw(3, -400, 5);
        raw.CompletionTimestampQpc = 2_000;
        Assert.IsTrue(window.TrySample(raw, out var sample));
        Assert.AreEqual(32_768, sample.LeftGyroPeak);
        Assert.AreEqual(400, sample.RightGyroPeak);
    }
}
