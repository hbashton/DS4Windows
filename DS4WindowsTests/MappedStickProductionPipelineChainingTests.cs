using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class MappedStickProductionPipelineChainingTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;

    [DataTestMethod]
    [DataRow(false, 2, 5, true)]
    [DataRow(true, 2, 5, true)]
    [DataRow(false, 6, 3, true)]
    [DataRow(true, 6, 3, true)]
    [DataRow(false, 1, 6, false)]
    [DataRow(true, 4, 6, false)]
    [DataRow(false, 0, 4, true)]
    [DataRow(true, 5, 0, true)]
    public void ProductionLegacyChainMatchesFrozenOracleWithDistinctSideSettings(
        bool swapped, int leftMode, int rightMode, bool square)
    {
        using var profile = new IsolatedProfile();
        ConfigureDistinct(swapped, leftMode, rightMode, square);
        foreach (int rawY in new[] { 0, 64, 127, 128, 129, 191, 255 })
        for (int rawX = 0; rawX <= byte.MaxValue; rawX++)
        {
            var input = new DS4State
            {
                LX = (byte)rawX, LY = (byte)rawY,
                RX = (byte)(255 - rawY), RY = (byte)(rawX ^ 73),
                OutputLSOuter = 73, OutputRSOuter = 109,
            };
            var expectedLeft = LegacyChain(input.LX, input.LY, input.OutputLSOuter,
                Global.LSModInfo[Slot], Global.LSSens[Slot], square,
                Global.SquStickInfo[Slot].lsRoundness, leftMode,
                Global.lsOutBezierCurveObj[Slot]);
            var expectedRight = LegacyChain(input.RX, input.RY, input.OutputRSOuter,
                Global.RSModInfo[Slot], Global.RSSens[Slot], square,
                Global.SquStickInfo[Slot].rsRoundness, rightMode,
                Global.rsOutBezierCurveObj[Slot]);
            var output = new DS4State();
            Assert.AreSame(output, Mapping.SetCurveAndDeadzone(Slot, input, output, profile.Owner));
            Assert.AreEqual(expectedLeft.LX, output.LX);
            Assert.AreEqual(expectedLeft.LY, output.LY);
            Assert.AreEqual(expectedRight.LX, output.RX);
            Assert.AreEqual(expectedRight.LY, output.RY);
            Assert.AreEqual(expectedLeft.OutputLSOuter, output.OutputLSOuter);
            Assert.AreEqual(expectedRight.OutputLSOuter, output.OutputRSOuter);
            Assert.IsFalse(output.LXAxis.IsHighResolution || output.LYAxis.IsHighResolution ||
                output.RXAxis.IsHighResolution || output.RYAxis.IsHighResolution);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SwappingCompleteSideConfigurationAndInputSwapsProductionOutputs(bool precise)
    {
        using var profile = new IsolatedProfile();
        ConfigureDistinct(false, 6, 3, true);
        var input = State(precise, 201.25, 79.75, 88.5, 193.125);
        input.OutputLSOuter = 73;
        input.OutputRSOuter = 109;
        var first = new DS4State();
        Mapping.SetCurveAndDeadzone(Slot, input, first, profile.Owner);

        // Rebuild temporary curve objects rather than mutating the saved
        // original profile curves through a mode setter.
        ConfigureDistinct(true, 3, 6, true, swapCurveDefinitions: true);
        var reversedInput = State(precise, 88.5, 193.125, 201.25, 79.75);
        reversedInput.OutputLSOuter = 109;
        reversedInput.OutputRSOuter = 73;
        var second = new DS4State();
        Mapping.SetCurveAndDeadzone(Slot, reversedInput, second, profile.Owner);
        Assert.AreEqual(first.LXAxis, second.RXAxis);
        Assert.AreEqual(first.LYAxis, second.RYAxis);
        Assert.AreEqual(first.RXAxis, second.LXAxis);
        Assert.AreEqual(first.RYAxis, second.LYAxis);
        Assert.AreEqual(first.OutputLSOuter, second.OutputRSOuter);
        Assert.AreEqual(first.OutputRSOuter, second.OutputLSOuter);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PreciseCalibrationDeadzoneSensitivityAndCurveStayInOrderAcrossBothSides(bool swapped)
    {
        using var profile = new IsolatedProfile();
        var radial = Noop(false);
        radial.deadZone = 8;
        var axial = Noop(true);
        axial.xAxisDeadInfo.deadZone = 12;
        Global.LSModInfo[Slot] = swapped ? axial : radial;
        Global.RSModInfo[Slot] = swapped ? radial : axial;
        Global.LSSens[Slot] = swapped ? 3.25 : 1.25;
        Global.RSSens[Slot] = swapped ? 1.25 : 3.25;
        Global.LeftStickDriftXAxis[Slot] = 2;
        Global.RightStickDriftXAxis[Slot] = -3;
        ConfigureCurve(true, swapped ? 3 : 6, squaredCustom: true);
        ConfigureCurve(false, swapped ? 6 : 3, squaredCustom: true);

        DS4State prior = null;
        foreach (double increment in new[] { 0.0, 0.05 })
        {
            var input = State(true, 161.25 + increment, 128, 166.4 + increment, 128);
            var output = new DS4State();
            Mapping.SetCurveAndDeadzone(Slot, input, output, profile.Owner);
            double expectedLeft = PositiveChain(161.25 + increment, 2,
                swapped ? 12 : 8, swapped ? 1 : 1.25, swapped ? 3 : 2);
            double expectedRight = PositiveChain(166.4 + increment, -3,
                swapped ? 8 : 12, swapped ? 1.25 : 1, swapped ? 2 : 3);
            Assert.AreEqual(expectedLeft, output.LXAxis.ProfileCoordinate, 2e-9);
            Assert.AreEqual(expectedRight, output.RXAxis.ProfileCoordinate, 2e-9);
            Assert.AreEqual(128.0, output.LYAxis.ProfileCoordinate);
            Assert.AreEqual(128.0, output.RYAxis.ProfileCoordinate);
            Assert.IsTrue(output.LXAxis.IsHighResolution && output.RXAxis.IsHighResolution);
            if (prior != null)
            {
                Assert.IsTrue(output.LXAxis.ProfileCoordinate > prior.LXAxis.ProfileCoordinate);
                Assert.IsTrue(output.RXAxis.ProfileCoordinate > prior.RXAxis.ProfileCoordinate);
            }
            prior = output;
        }
    }

    [TestMethod]
    public void PreciseSensitivityThenSquareThenCurveIsNotReorderedOrByteQuantized()
    {
        using var profile = new IsolatedProfile();
        Global.LSModInfo[Slot] = Noop(false);
        Global.RSModInfo[Slot] = Noop(false);
        Global.LSSens[Slot] = 0.65;
        Global.RSSens[Slot] = 1.4;
        Global.SquStickInfo[Slot] = new SquareStickInfo
        {
            lsMode = true, rsMode = true, lsRoundness = 1, rsRoundness = 5,
        };
        ConfigureCurve(true, 2);
        ConfigureCurve(false, 3);
        var input = State(true, 171.25, 149.75, 84.125, 162.25);
        var output = new DS4State();
        Mapping.SetCurveAndDeadzone(Slot, input, output, profile.Owner);
        var leftScaled = Sensitivity(171.25, 149.75, 0.65);
        var rightScaled = Sensitivity(84.125, 162.25, 1.4);
        var expectedLeft = RadialPower(Square(leftScaled.X, leftScaled.Y, 1), 2);
        var expectedRight = RadialPower(Square(rightScaled.X, rightScaled.Y, 5), 3);
        Assert.AreEqual(expectedLeft.X, output.LXAxis.ProfileCoordinate, 2e-9);
        Assert.AreEqual(expectedLeft.Y, output.LYAxis.ProfileCoordinate, 2e-9);
        Assert.AreEqual(expectedRight.X, output.RXAxis.ProfileCoordinate, 2e-9);
        Assert.AreEqual(expectedRight.Y, output.RYAxis.ProfileCoordinate, 2e-9);
        Assert.IsTrue(output.LXAxis.IsHighResolution && output.LYAxis.IsHighResolution &&
            output.RXAxis.IsHighResolution && output.RYAxis.IsHighResolution);
        var wrongLeft = RadialPower(leftScaled, 2);
        wrongLeft = Square(wrongLeft.X, wrongLeft.Y, 1);
        var wrongRight = RadialPower(rightScaled, 3);
        wrongRight = Square(wrongRight.X, wrongRight.Y, 5);
        Assert.IsTrue(Math.Abs(wrongLeft.X - expectedLeft.X) > 1e-4);
        Assert.IsTrue(Math.Abs(wrongRight.X - expectedRight.X) > 1e-4);
        Assert.AreNotEqual((double)output.LX, output.LXAxis.ProfileCoordinate);
        Assert.AreNotEqual((double)output.RX, output.RXAxis.ProfileCoordinate);
    }

    [TestMethod]
    public void ProfileFixtureRestoresOriginalCurveObjectsWithoutRecompilingThem()
    {
        var left = Global.lsOutBezierCurveObj[Slot];
        var right = Global.rsOutBezierCurveObj[Slot];
        string leftDefinition = left.AsString, rightDefinition = right.AsString;
        byte[] leftBytes = left.arrayBezierLUT?.ToArray();
        byte[] rightBytes = right.arrayBezierLUT?.ToArray();
        var leftEvaluator = left.CaptureEvaluator();
        var rightEvaluator = right.CaptureEvaluator();
        using (new IsolatedProfile())
        {
            ConfigureDistinct(false, 6, 3, true);
            ConfigureDistinct(true, 4, 6, false);
        }
        Assert.AreSame(left, Global.lsOutBezierCurveObj[Slot]);
        Assert.AreSame(right, Global.rsOutBezierCurveObj[Slot]);
        Assert.AreSame(leftEvaluator, left.CaptureEvaluator());
        Assert.AreSame(rightEvaluator, right.CaptureEvaluator());
        Assert.AreEqual(leftDefinition, left.AsString);
        Assert.AreEqual(rightDefinition, right.AsString);
        if (leftBytes == null) Assert.IsNull(left.arrayBezierLUT);
        else CollectionAssert.AreEqual(leftBytes, left.arrayBezierLUT);
        if (rightBytes == null) Assert.IsNull(right.arrayBezierLUT);
        else CollectionAssert.AreEqual(rightBytes, right.arrayBezierLUT);
    }

    private static void ConfigureDistinct(bool swapped, int leftMode, int rightMode,
        bool square, bool swapCurveDefinitions = false)
    {
        var radial = Noop(false);
        radial.deadZone = 7; radial.antiDeadZone = 13; radial.maxZone = 87;
        radial.maxOutput = 76; radial.verticalScale = 69; radial.outerBindDeadZone = 38;
        var axial = Noop(true);
        axial.xAxisDeadInfo.deadZone = 5; axial.xAxisDeadInfo.antiDeadZone = 11;
        axial.xAxisDeadInfo.maxZone = 92; axial.xAxisDeadInfo.maxOutput = 85;
        axial.yAxisDeadInfo.deadZone = 11; axial.yAxisDeadInfo.antiDeadZone = 22;
        axial.yAxisDeadInfo.maxZone = 79; axial.yAxisDeadInfo.maxOutput = 67;
        Global.LSModInfo[Slot] = swapped ? axial : radial;
        Global.RSModInfo[Slot] = swapped ? radial : axial;
        Global.LSSens[Slot] = swapped ? 0.73 : 1.2;
        Global.RSSens[Slot] = swapped ? 1.2 : 0.73;
        Global.SquStickInfo[Slot] = new SquareStickInfo
        {
            lsMode = square, rsMode = square,
            lsRoundness = swapped ? 5 : 1, rsRoundness = swapped ? 1 : 5,
        };
        ConfigureCurve(true, leftMode, alternateCustom: swapCurveDefinitions);
        ConfigureCurve(false, rightMode, alternateCustom: !swapCurveDefinitions);
        Mapping.ResetStickFilters(Slot);
    }

    private static void ConfigureCurve(bool left, int mode, bool squaredCustom = false,
        bool alternateCustom = false)
    {
        var curve = new BezierCurve();
        if (left)
        {
            Global.lsOutBezierCurveObj[Slot] = curve;
            Global.setLsOutCurveMode(Slot, mode);
        }
        else
        {
            Global.rsOutBezierCurveObj[Slot] = curve;
            Global.setRsOutCurveMode(Slot, mode);
        }
        if (mode == 6)
        {
            // Numeric initialization avoids locale dependence in this test.
            bool accepted = squaredCustom ? curve.InitBezierCurve(1.0 / 3, 0,
                2.0 / 3, 1.0 / 3, BezierCurve.AxisType.LSRS) : alternateCustom ?
                curve.InitBezierCurve(0.42, 0.8, 0.77, 0.1, BezierCurve.AxisType.LSRS) :
                curve.InitBezierCurve(0.23, 0.02, 0.82, 0.95, BezierCurve.AxisType.LSRS);
            Assert.IsTrue(accepted);
        }
    }

    private static StickDeadZoneInfo Noop(bool axial)
    {
        var mod = new StickDeadZoneInfo
        {
            deadzoneType = axial ? StickDeadZoneInfo.DeadZoneType.Axial : StickDeadZoneInfo.DeadZoneType.Radial,
        };
        mod.xAxisDeadInfo.deadZone = mod.yAxisDeadInfo.deadZone = 0;
        mod.xAxisDeadInfo.antiDeadZone = mod.yAxisDeadInfo.antiDeadZone = 0;
        return mod;
    }

    private static DS4State LegacyChain(byte x, byte y, byte outer, StickDeadZoneInfo mod,
        double sensitivity, bool square, double roundness, int mode, BezierCurve curve)
    {
        var expected = new DS4State { LX = x, LY = y, OutputLSOuter = outer };
        LegacyStickProfileOracle.Deadzone(expected, mod);
        if (mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial)
        {
            expected.LX = (byte)Math.Clamp(sensitivity * (expected.LX - 128.0) + 128, 0, 255);
            expected.LY = (byte)Math.Clamp(sensitivity * (expected.LY - 128.0) + 128, 0, 255);
        }
        if (square)
        {
            var squared = Square(expected.LX, expected.LY, roundness);
            expected.LX = (byte)squared.X;
            expected.LY = (byte)squared.Y;
        }
        LegacyStickProfileOracle.Curve(expected, mod, mode, curve);
        return expected;
    }

    private static double PositiveChain(double input, int calibration, int deadzone,
        double sensitivity, int power)
    {
        double normalized = (input - calibration - 128 - deadzone) / (127 - deadzone);
        normalized = Math.Clamp(normalized * sensitivity, 0, 1);
        return 128 + Math.Pow(normalized, power) * 127;
    }

    private static DS4State State(bool precise, double lx, double ly, double rx, double ry) =>
        precise ? new DS4State { LXAxis = Axis(lx), LYAxis = Axis(ly), RXAxis = Axis(rx), RYAxis = Axis(ry) } :
            new DS4State { LX = (byte)lx, LY = (byte)ly, RX = (byte)rx, RY = (byte)ry };

    private static DS4MappedStickAxis Axis(double coordinate)
    {
        Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(coordinate, out var result));
        return result;
    }

    private static (double X, double Y) Sensitivity(double x, double y, double sensitivity) =>
        (Math.Clamp(sensitivity * (x - 128) + 128, 0, 255),
            Math.Clamp(sensitivity * (y - 128) + 128, 0, 255));

    private static (double X, double Y) RadialPower((double X, double Y) point, int power)
    {
        double dx = point.X - 128, dy = point.Y - 128;
        double angle = Math.Atan2(-dy, dx);
        double capX = Math.Max(Math.Abs(dx), Math.Abs(Math.Cos(angle)) * (dx < 0 ? 128 : 127));
        double capY = Math.Max(Math.Abs(dy), Math.Abs(Math.Sin(angle)) * (dy < 0 ? 128 : 127));
        return (128 + (capX == 0 ? 0 : Math.Sign(dx) * Math.Pow(Math.Abs(dx) / capX, power) * capX),
            128 + (capY == 0 ? 0 : Math.Sign(dy) * Math.Pow(Math.Abs(dy) / capY, power) * capY));
    }

    // Existing circle-to-square equations, without calling the production
    // reducer; legacy callers cast each result at its original write site.
    private static (double X, double Y) Square(double rawX, double rawY, double roundness)
    {
        if (rawX == 128 && rawY == 128) return (128, 128);
        double capX = rawX >= 128 ? 127.0 : 128.0, capY = rawY >= 128 ? 127.0 : 128.0;
        double x = (rawX - 128) / capX, y = (rawY - 128) / capY;
        double angle = Math.Atan2(y, -x) + Math.PI;
        double cosine = Math.Cos(angle), scale = 0;
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

    private sealed class IsolatedProfile : IDisposable
    {
        private readonly StickDeadZoneInfo left = Global.LSModInfo[Slot], right = Global.RSModInfo[Slot];
        private readonly StickAntiSnapbackInfo snapLeft = Global.LSAntiSnapbackInfo[Slot], snapRight = Global.RSAntiSnapbackInfo[Slot];
        private readonly SquareStickInfo square = Global.SquStickInfo[Slot];
        private readonly BezierCurve curveLeft = Global.lsOutBezierCurveObj[Slot], curveRight = Global.rsOutBezierCurveObj[Slot];
        private readonly int modeLeft = Global.getLsOutCurveMode(Slot), modeRight = Global.getRsOutCurveMode(Slot);
        private readonly double rotationLeft = Global.LSRotation[Slot], rotationRight = Global.RSRotation[Slot];
        private readonly double sensitivityLeft = Global.LSSens[Slot], sensitivityRight = Global.RSSens[Slot];
        private readonly sbyte driftLX = Global.LeftStickDriftXAxis[Slot], driftLY = Global.LeftStickDriftYAxis[Slot];
        private readonly sbyte driftRX = Global.RightStickDriftXAxis[Slot], driftRY = Global.RightStickDriftYAxis[Slot];
        internal object Owner { get; } = new();

        internal IsolatedProfile()
        {
            Global.LSModInfo[Slot] = Noop(false);
            Global.RSModInfo[Slot] = Noop(false);
            Global.LSAntiSnapbackInfo[Slot] = new StickAntiSnapbackInfo();
            Global.RSAntiSnapbackInfo[Slot] = new StickAntiSnapbackInfo();
            Global.SquStickInfo[Slot] = new SquareStickInfo();
            Global.LSRotation[Slot] = Global.RSRotation[Slot] = 0;
            Global.LSSens[Slot] = Global.RSSens[Slot] = 1;
            Global.LeftStickDriftXAxis[Slot] = Global.LeftStickDriftYAxis[Slot] = 0;
            Global.RightStickDriftXAxis[Slot] = Global.RightStickDriftYAxis[Slot] = 0;
            Global.lsOutBezierCurveObj[Slot] = new BezierCurve();
            Global.rsOutBezierCurveObj[Slot] = new BezierCurve();
            Global.setLsOutCurveMode(Slot, 0);
            Global.setRsOutCurveMode(Slot, 0);
            Mapping.ResetStickFilters(Slot);
        }

        public void Dispose()
        {
            // Restoring a mode can recompile its current curve. Restore modes
            // while temporary objects are still installed, then the originals.
            Global.setLsOutCurveMode(Slot, modeLeft);
            Global.setRsOutCurveMode(Slot, modeRight);
            Global.lsOutBezierCurveObj[Slot] = curveLeft;
            Global.rsOutBezierCurveObj[Slot] = curveRight;
            Global.LSModInfo[Slot] = left; Global.RSModInfo[Slot] = right;
            Global.LSAntiSnapbackInfo[Slot] = snapLeft; Global.RSAntiSnapbackInfo[Slot] = snapRight;
            Global.SquStickInfo[Slot] = square;
            Global.LSRotation[Slot] = rotationLeft; Global.RSRotation[Slot] = rotationRight;
            Global.LSSens[Slot] = sensitivityLeft; Global.RSSens[Slot] = sensitivityRight;
            Global.LeftStickDriftXAxis[Slot] = driftLX; Global.LeftStickDriftYAxis[Slot] = driftLY;
            Global.RightStickDriftXAxis[Slot] = driftRX; Global.RightStickDriftYAxis[Slot] = driftRY;
            Mapping.ResetStickFilters(Slot);
        }
    }
}
