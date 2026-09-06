using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class MappedStickTransformTests
{
    [TestMethod]
    public void SensitivityPreservesEveryLegacyResultAndFractionalValues()
    {
        foreach (double sensitivity in new[] { 0.1, 0.5, 1, 1.35, 5 })
        {
            for (int value = 0; value < 256; value++)
            {
                var axis = DS4MappedStickAxis.FromLegacy((byte)value);
                byte expected = (byte)Global.Clamp(0, sensitivity * (value - 128.0) + 128.0, 255);
                Mapping.ApplyStickSensitivity(ref axis, sensitivity);
                Assert.AreEqual(expected, axis.LegacyValue);
                Assert.IsFalse(axis.IsHighResolution);
            }
            for (int value = short.MinValue; value <= short.MaxValue; value++)
            {
                var axis = DS4MappedStickAxis.FromSigned((short)value);
                double expected = Math.Clamp(sensitivity * (axis.ProfileCoordinate - 128) + 128, 0, 255);
                Mapping.ApplyStickSensitivity(ref axis, sensitivity);
                Assert.AreEqual(expected, axis.ProfileCoordinate, 1e-12);
                Assert.IsTrue(axis.IsHighResolution);
            }
        }
    }

    [DataTestMethod]
    [DataRow(0.0)]
    [DataRow(1.0)]
    [DataRow(5.0)]
    public void SquareStickPreservesAllLegacyCoordinates(double roundness)
    {
        for (int rawX = 0; rawX < 256; rawX++)
        for (int rawY = 0; rawY < 256; rawY++)
        {
            var x = DS4MappedStickAxis.FromLegacy((byte)rawX);
            var y = DS4MappedStickAxis.FromLegacy((byte)rawY);
            var expected = OriginalSquare(rawX, rawY, roundness);
            Mapping.ApplySquareStickCoordinates(Global.TEST_PROFILE_INDEX, ref x, ref y, roundness);
            Assert.AreEqual((byte)expected.X, x.LegacyValue);
            Assert.AreEqual((byte)expected.Y, y.LegacyValue);
            Assert.IsFalse(x.IsHighResolution || y.IsHighResolution);
        }
    }

    [TestMethod]
    public void SquareStickKeepsSubByteMotionAndPromotesCoupledPrecision()
    {
        foreach (double roundness in new[] { 0.0, 1.0, 5.0 })
        foreach (double rawX in new[] { 127.99, 128.01, 128.25, 201.123 })
        foreach (double rawY in new[] { 127.87, 128.0, 128.12, 91.456 })
        {
            Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(rawX, out var x));
            Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(rawY, out var y));
            var expected = OriginalSquare(x.ProfileCoordinate, y.ProfileCoordinate, roundness);
            Mapping.ApplySquareStickCoordinates(Global.TEST_PROFILE_INDEX, ref x, ref y, roundness);
            Assert.AreEqual(expected.X, x.ProfileCoordinate, 1e-12);
            Assert.AreEqual(expected.Y, y.ProfileCoordinate, 1e-12);
            Assert.IsTrue(x.IsHighResolution && y.IsHighResolution);
            Assert.AreNotEqual(128.0, x.ProfileCoordinate, "Byte-center projection must not skip the transform.");
        }
        var mixedX = DS4MappedStickAxis.FromSigned(16);
        var mixedY = DS4MappedStickAxis.FromLegacy(140);
        Mapping.ApplySquareStickCoordinates(Global.TEST_PROFILE_INDEX, ref mixedX, ref mixedY, 1);
        Assert.IsTrue(mixedX.IsHighResolution && mixedY.IsHighResolution);
    }

    [TestMethod]
    public void LegacyChainingKeepsIntermediateTruncationAndWarmTransformsAllocateNothing()
    {
        for (int i = 0; i < 256; i++)
        {
            var x = DS4MappedStickAxis.FromLegacy((byte)i);
            var y = DS4MappedStickAxis.FromLegacy((byte)(255 - i));
            byte expectedX = (byte)Math.Clamp(1.35 * (i - 128.0) + 128, 0, 255);
            byte expectedY = (byte)Math.Clamp(1.35 * (127.0 - i) + 128, 0, 255);
            var expected = OriginalSquare(expectedX, expectedY, 5);
            Mapping.ApplyStickSensitivity(ref x, 1.35);
            Mapping.ApplyStickSensitivity(ref y, 1.35);
            Mapping.ApplySquareStickCoordinates(Global.TEST_PROFILE_INDEX, ref x, ref y, 5);
            Assert.AreEqual((byte)expected.X, x.LegacyValue);
            Assert.AreEqual((byte)expected.Y, y.LegacyValue);
        }
        double checksum = 0;
        for (int i = 0; i < 2000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.IsTrue(checksum > 0);
        void Step(int value)
        {
            var x = DS4MappedStickAxis.FromSigned((short)value);
            var y = DS4MappedStickAxis.FromSigned((short)-value);
            Mapping.ApplyStickSensitivity(ref x, 1.35);
            Mapping.ApplyStickSensitivity(ref y, 0.7);
            Mapping.ApplySquareStickCoordinates(Global.TEST_PROFILE_INDEX, ref x, ref y, 5);
            checksum += x.ProfileCoordinate + y.ProfileCoordinate;
        }
    }

    // Frozen pre-migration DS4Windows wrapper + DS4SquareStick equations. The
    // legacy oracle quantizes each returned coordinate at the historical site;
    // precise checks use the unquantized result of the same existing algorithm.
    private static (double X, double Y) OriginalSquare(double rawX, double rawY, double roundness)
    {
        if (rawX == 128 && rawY == 128) return (128, 128);
        double capX = rawX >= 128 ? 127.0 : 128.0;
        double capY = rawY >= 128 ? 127.0 : 128.0;
        double x = (rawX - 128) / capX, y = (rawY - 128) / capY;
        double angle = Math.Atan2(y, -x) + Math.PI;
        double cosine = Math.Cos(angle);
        double scale = 0;
        if (angle <= Math.PI / 4 || angle > 7 * Math.PI / 4) scale = 1.0 / cosine;
        else if (angle > Math.PI / 4 && angle <= 3 * Math.PI / 4) scale = 1.0 / Math.Sin(angle);
        else if (angle > 3 * Math.PI / 4 && angle <= 5 * Math.PI / 4) scale = -1.0 / cosine;
        else if (angle > 5 * Math.PI / 4 && angle <= 7 * Math.PI / 4) scale = -1.0 / Math.Sin(angle);
        double factor = Math.Pow(x / cosine, roundness);
        x += (x * scale - x) * factor;
        y += (y * scale - y) * factor;
        x = x < -1 ? -1 : x > 1 ? 1 : x;
        y = y < -1 ? -1 : y > 1 ? 1 : y;
        return (x * capX + 128, y * capY + 128);
    }
}
