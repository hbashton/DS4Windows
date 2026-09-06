using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class DS4StickProfileTransformTests
{
    [DataTestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)]
    [DataRow(5)] [DataRow(6)] [DataRow(7)] [DataRow(8)] [DataRow(9)]
    public void DeadzoneAndOuterMatchFrozenLegacyByteGrid(int variant)
    {
        StickDeadZoneInfo mod = Settings(variant);
        var expected = new DS4State();
        for (int rawX = 0; rawX < 256; rawX++)
        for (int rawY = 0; rawY < 256; rawY++)
        {
            expected.LX = (byte)rawX; expected.LY = (byte)rawY;
            expected.OutputLSOuter = (byte)(rawX ^ rawY);
            var x = DS4MappedStickAxis.FromLegacy((byte)rawX);
            var y = DS4MappedStickAxis.FromLegacy((byte)rawY);
            byte outer = expected.OutputLSOuter;
            LegacyStickProfileOracle.Deadzone(expected, mod);
            DS4StickProfileTransform.ApplyDeadzoneAndOuter(mod, ref x, ref y, ref outer);
            if (x.LegacyValue != expected.LX || y.LegacyValue != expected.LY ||
                outer != expected.OutputLSOuter || x.IsHighResolution || y.IsHighResolution)
                Assert.Fail($"Variant {variant}, ({rawX},{rawY}): expected {expected.LX},{expected.LY},{expected.OutputLSOuter}; got {x.LegacyValue},{y.LegacyValue},{outer}.");
        }
    }

    [DataTestMethod]
    [DataRow(false)] [DataRow(true)]
    public void EveryOutputCurveMatchesFrozenLegacyByteGrid(bool axial)
    {
        var mod = NoopSettings(axial);
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.23, 0.02, 0.82, 0.95, BezierCurve.AxisType.LSRS));
        var expected = new DS4State();
        for (int mode = 0; mode <= 6; mode++)
        for (int rawX = 0; rawX < 256; rawX++)
        for (int rawY = 0; rawY < 256; rawY++)
        {
            expected.LX = (byte)rawX; expected.LY = (byte)rawY;
            var x = DS4MappedStickAxis.FromLegacy((byte)rawX);
            var y = DS4MappedStickAxis.FromLegacy((byte)rawY);
            LegacyStickProfileOracle.Curve(expected, mod, mode, curve);
            DS4StickProfileTransform.ApplyOutputCurve(mod, mode, curve, ref x, ref y);
            if (x.LegacyValue != expected.LX || y.LegacyValue != expected.LY ||
                x.IsHighResolution || y.IsHighResolution)
                Assert.Fail($"Axial {axial}, mode {mode}, ({rawX},{rawY}): expected {expected.LX},{expected.LY}; got {x.LegacyValue},{y.LegacyValue}.");
        }
    }

    [DataTestMethod]
    [DataRow(false)] [DataRow(true)]
    public void PreciseDeadzonesHonorFractionalThresholdsAndCoupling(bool axial)
    {
        var mod = NoopSettings(axial);
        mod.deadZone = mod.xAxisDeadInfo.deadZone = mod.yAxisDeadInfo.deadZone = 8;
        foreach (double coordinate in new[] { 135.99, 136.0, 136.01, 140.25, 255.0 })
        {
            var x = Axis(coordinate);
            var y = DS4MappedStickAxis.FromLegacy(128);
            byte outer = 61;
            DS4StickProfileTransform.ApplyDeadzoneAndOuter(mod, ref x, ref y, ref outer);
            double expected = coordinate <= 136 ? 128 : 128 + (coordinate - 136) / (127 - 8.0) * 127;
            Assert.AreEqual(expected, x.ProfileCoordinate, 1e-11);
            Assert.AreEqual(128.0, y.ProfileCoordinate);
            Assert.IsTrue(x.IsHighResolution);
            Assert.AreEqual(!axial, y.IsHighResolution);
            if (axial) Assert.AreEqual((byte)61, outer, "Axial historically leaves outer binding alone.");
        }
        var negative = Axis(119.90); var centered = Axis(128);
        byte ignored = 0;
        DS4StickProfileTransform.ApplyDeadzoneAndOuter(mod, ref negative, ref centered, ref ignored);
        double scaledDead = -128 * (8.0 / 127.0);
        double negativeExpected = ((119.90 - 128 - scaledDead) / (-128 - scaledDead)) * -128 + 128;
        Assert.AreEqual(negativeExpected, negative.ProfileCoordinate, 1e-11);
    }

    [TestMethod]
    public void PreciseOuterBindingUsesFullVectorBeforeFinalByteQuantization()
    {
        var mod = NoopSettings(false);
        mod.outerBindDeadZone = 0;
        foreach ((double rawX, double rawY) in new[] { (128.49, 128.49), (170.25, 181.75), (31.2, 93.7) })
        {
            var x = Axis(rawX); var y = Axis(rawY); byte outer = 0;
            DS4StickProfileTransform.ApplyDeadzoneAndOuter(mod, ref x, ref y, ref outer);
            double dx = rawX - 128, dy = rawY - 128, angle = Math.Atan2(-dy, dx);
            double maxX = Math.Abs(Math.Cos(angle)) * (rawX >= 128 ? 127 : 128);
            double maxY = Math.Abs(Math.Sin(angle)) * (rawY >= 128 ? 127 : 128);
            byte expected = (byte)(Math.Min(1, Math.Sqrt(dx * dx + dy * dy) /
                Math.Sqrt(maxX * maxX + maxY * maxY)) * 255);
            Assert.AreEqual(expected, outer);
            Assert.AreEqual(rawX, x.ProfileCoordinate, 1e-12);
            Assert.AreEqual(rawY, y.ProfileCoordinate, 1e-12);
        }
    }

    [DataTestMethod]
    [DataRow(false)] [DataRow(true)]
    public void PreciseCurvesKeepSubByteMotionAndPiecewiseBoundaries(bool axial)
    {
        var mod = NoopSettings(axial);
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0, 0, 1, 1, BezierCurve.AxisType.LSRS));
        foreach (double signedUnit in new[] { -1.0, -0.750001, -0.75, -0.4, -0.00001,
            0.00001, 0.399999, 0.4, 0.400001, 0.75, 0.750001, 1.0 })
        for (int mode = 0; mode <= 6; mode++)
        {
            double cap = signedUnit < 0 ? 128 : 127;
            double raw = 128 + signedUnit * cap;
            var x = Axis(raw); var y = DS4MappedStickAxis.FromLegacy(128);
            double magnitude = Math.Abs(signedUnit);
            double shaped = mode switch
            {
                1 => magnitude <= 0.4 ? 0.8 * magnitude : magnitude <= 0.75 ? magnitude - 0.08 : magnitude * 1.32 - 0.32,
                2 => magnitude * magnitude,
                3 => magnitude * magnitude * magnitude,
                4 => -magnitude * (magnitude - 2),
                5 => Math.Pow(magnitude - 1, 3) + 1,
                _ => magnitude,
            };
            double expected = 128 + (signedUnit < 0 ? -shaped : shaped) * cap;
            DS4StickProfileTransform.ApplyOutputCurve(mod, mode, curve, ref x, ref y);
            Assert.AreEqual(expected, x.ProfileCoordinate, 1e-10, $"Mode {mode}, unit {signedUnit}");
            Assert.AreEqual(128.0, y.ProfileCoordinate, 1e-12);
            Assert.IsTrue(x.IsHighResolution);
            Assert.AreEqual(!axial && mode != 0, y.IsHighResolution);
        }
    }

    [TestMethod]
    public void CustomCurvesRetainEverySignedSourcePositionWithoutByteLutInterpolation()
    {
        var mod = NoopSettings(true);
        var curve = new BezierCurve();
        // x(t)=t and y(t)=t^2: independent closed-form oracle.
        Assert.IsTrue(curve.InitBezierCurve(1.0 / 3, 0, 2.0 / 3, 1.0 / 3, BezierCurve.AxisType.LSRS));
        double previous = -1;
        for (int raw = short.MinValue; raw <= short.MaxValue; raw++)
        {
            var x = DS4MappedStickAxis.FromSigned((short)raw);
            var y = DS4MappedStickAxis.FromLegacy(128);
            double coordinate = x.ProfileCoordinate;
            double cap = coordinate < 128 ? 128 : 127;
            double unit = (coordinate - 128) / cap;
            double expected = 128 + unit * Math.Abs(unit) * cap;
            DS4StickProfileTransform.ApplyOutputCurve(mod, 6, curve, ref x, ref y);
            Assert.AreEqual(expected, x.ProfileCoordinate, 1e-9);
            Assert.IsTrue(x.ProfileCoordinate > previous, $"Position {raw} collapsed.");
            previous = x.ProfileCoordinate;
        }
    }

    [TestMethod]
    public void WarmProfileMathAllocatesNothing()
    {
        var mod = Settings(3);
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.23, 0.02, 0.82, 0.95, BezierCurve.AxisType.LSRS));
        double checksum = 0;
        for (int i = 0; i < 2000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(checksum > 0);
        void Step(int value)
        {
            var x = DS4MappedStickAxis.FromSigned((short)(value * 17));
            var y = DS4MappedStickAxis.FromSigned((short)(value * -13));
            byte outer = 0;
            DS4StickProfileTransform.ApplyDeadzoneAndOuter(mod, ref x, ref y, ref outer);
            DS4StickProfileTransform.ApplyOutputCurve(mod, 6, curve, ref x, ref y);
            checksum += x.ProfileCoordinate + y.ProfileCoordinate + outer;
        }
    }

    internal static StickDeadZoneInfo NoopSettings(bool axial)
    {
        var mod = new StickDeadZoneInfo { deadzoneType = axial ? StickDeadZoneInfo.DeadZoneType.Axial : StickDeadZoneInfo.DeadZoneType.Radial };
        mod.xAxisDeadInfo.deadZone = mod.yAxisDeadInfo.deadZone = 0;
        mod.xAxisDeadInfo.antiDeadZone = mod.yAxisDeadInfo.antiDeadZone = 0;
        return mod;
    }

    private static StickDeadZoneInfo Settings(int variant)
    {
        var mod = NoopSettings(variant >= 6);
        switch (variant)
        {
            case 1: mod.deadZone = 8; break;
            case 2: mod.deadZone = 17; mod.antiDeadZone = 26; break;
            case 3: mod.deadZone = 5; mod.antiDeadZone = 13; mod.maxZone = 82; mod.maxOutput = 72.5; mod.verticalScale = 63; break;
            case 4: mod.maxOutputForce = true; mod.verticalScale = 137; mod.outerBindInvert = true; break;
            case 5: mod.deadZone = 126; mod.maxZone = 100; mod.outerBindDeadZone = 100; break;
            case 7: mod.xAxisDeadInfo.deadZone = 9; mod.yAxisDeadInfo.deadZone = 17; mod.yAxisDeadInfo.antiDeadZone = 25; break;
            case 8: mod.xAxisDeadInfo.antiDeadZone = 31; mod.xAxisDeadInfo.maxZone = 85; mod.yAxisDeadInfo.maxOutput = 45.5; break;
            case 9: mod.xAxisDeadInfo.deadZone = 126; mod.yAxisDeadInfo.deadZone = 1; mod.yAxisDeadInfo.maxZone = 51; mod.outerBindInvert = true; break;
        }
        return mod;
    }

    private static DS4MappedStickAxis Axis(double coordinate)
    {
        Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(coordinate, out var axis));
        return axis;
    }
}
