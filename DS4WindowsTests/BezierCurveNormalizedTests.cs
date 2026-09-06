using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class BezierCurveNormalizedTests
{
    private static readonly double[][] CustomCurves =
    {
        new[] { 0.25, 0.1, 0.25, 1.0 },
        new[] { 0.0, 1.0, 0.0, 1.0 },
        new[] { 1.0, 0.0, 1.0, 0.0 },
        new[] { 1.0, 0.0, 0.0, 1.0 },
        new[] { 0.2, -0.6, 0.8, 1.8 },
        new[] { 0.0, 0.0, 0.0, 0.0 },
        new[] { 0.0, 0.0, 1.0, 1.0 },
    };

    [TestMethod]
    public void ContinuousCustomCurvesMatchIndependentInverseAtEveryTwelveBitMagnitude()
    {
        foreach (double[] definition in CustomCurves)
        {
            var curve = new BezierCurve();
            Assert.IsTrue(curve.InitBezierCurve(definition[0], definition[1],
                definition[2], definition[3], BezierCurve.AxisType.LSRS));
            var evaluator = curve.CaptureEvaluator();
            for (int code = 0; code <= 4096; code++)
            {
                double input = code / 4096.0;
                Assert.IsTrue(evaluator.TryEvaluateNormalized(input, out double actual));
                double expected = ReferenceContinuous(input, definition);
                Assert.AreEqual(expected, actual, 2e-9,
                    $"Curve {string.Join(",", definition)} input {input:R}");
            }
            Assert.IsTrue(evaluator.TryEvaluateNormalized(0, out double minimum));
            Assert.IsTrue(evaluator.TryEvaluateNormalized(1, out double maximum));
            Assert.AreEqual(0.0, minimum);
            Assert.AreEqual(1.0, maximum);
        }
    }

    [TestMethod]
    public void ContinuousCurvePreservesAdjacentSixteenBitMagnitudesBeyondByteLut()
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.25, 0.1, 0.25, 1.0,
            BezierCurve.AxisType.LSRS));
        var evaluator = curve.CaptureEvaluator();
        double previous = -1;
        for (int code = 0; code <= ushort.MaxValue; code++)
        {
            double input = code / (double)ushort.MaxValue;
            Assert.IsTrue(evaluator.TryEvaluateNormalized(input, out double actual));
            Assert.IsTrue(actual > previous, $"Distinct input collapsed at {code}");
            previous = actual;
        }
    }

    [TestMethod]
    public void FlatEndpointAndInteriorSlopesStayFiniteAndConvergeWithoutByteQuantization()
    {
        foreach (double[] definition in CustomCurves.Skip(1).Take(3))
        {
            var curve = new BezierCurve();
            Assert.IsTrue(curve.InitBezierCurve(definition[0], definition[1],
                definition[2], definition[3], BezierCurve.AxisType.LSRS));
            foreach (double input in new[] { 1e-6, 1e-5, 0.499999,
                         0.5, 0.500001, 1.0 - 1e-5, 1.0 - 1e-6 })
            {
                Assert.IsTrue(curve.TryEvaluateNormalized(input, out double actual));
                Assert.IsTrue(double.IsFinite(actual) && actual >= 0 && actual <= 1);
                Assert.AreEqual(ReferenceContinuous(input, definition), actual, 2e-9);
            }
        }
    }

    [TestMethod]
    public void AdjacentDoublesAtSingularInteriorUseStableClosedFormInverse()
    {
        foreach (double n in new[] { 0.0, 3.0, 1_000.0, 1_000_000.0 })
        {
            var evaluator = BezierCurve.NormalizedEvaluator.Compile(1, n, 0, 1 - n);
            foreach (double input in new[] { Math.BitDecrement(0.5), 0.5,
                         Math.BitIncrement(0.5) })
            {
                // X(0.5+u)=0.5+4u^3; this oracle never subtracts an
                // already-rounded polynomial value near its flat midpoint.
                double u = Math.Cbrt((input - 0.5) / 4.0);
                double expected = Math.Clamp(0.5 + (1.5 - 1.5 * n) * u +
                    (6.0 * n - 2.0) * u * u * u, 0, 1);
                Assert.IsTrue(evaluator.TryEvaluateNormalized(input, out double actual));
                Assert.AreEqual(expected, actual, 2e-9,
                    $"N={n} input={input:R}");
            }
        }
    }

    [TestMethod]
    public void AdjacentDoubleAtFlatUpperEndpointUsesReversedResidual()
    {
        var evaluator = BezierCurve.NormalizedEvaluator.Compile(1, 0, 1, 0);
        double input = Math.BitDecrement(1.0);
        double t = 1.0 - Math.Cbrt(1.0 - input);
        Assert.IsTrue(evaluator.TryEvaluateNormalized(input, out double actual));
        Assert.AreEqual(t * t * t, actual, 2e-9);
    }

    [TestMethod]
    public void ContinuousSpecialModesMatchExistingFormulasIncludingBreakpoints()
    {
        for (int mode = 91; mode <= 95; mode++)
        {
            var curve = new BezierCurve();
            Assert.IsTrue(curve.InitBezierCurve(99, mode, 0, 0,
                BezierCurve.AxisType.LSRS));
            foreach (double input in new[] { 0.0, 1e-9, 0.3999999, 0.4,
                         0.4000001, 0.7499999, 0.75, 0.7500001, 1.0 })
            {
                Assert.IsTrue(curve.TryEvaluateNormalized(input, out double actual));
                Assert.AreEqual(SpecialFormula(mode, input), actual, 2e-15);
            }
        }
    }

    [TestMethod]
    public void CustomOvershootClampsWithoutWrappingOrContaminatingEndpoints()
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.2, -2, 0.8, 3,
            BezierCurve.AxisType.LSRS));
        Assert.IsTrue(curve.TryEvaluateNormalized(0.1, out double below));
        Assert.IsTrue(curve.TryEvaluateNormalized(0.9, out double above));
        Assert.AreEqual(0.0, below);
        Assert.AreEqual(1.0, above);
        Assert.IsTrue(curve.TryEvaluateNormalized(0, out below));
        Assert.IsTrue(curve.TryEvaluateNormalized(1, out above));
        Assert.AreEqual(0.0, below);
        Assert.AreEqual(1.0, above);
    }

    [TestMethod]
    public void InvalidInputIsNeutralAndInvalidDefinitionPreservesFiniteLinearFallback()
    {
        var curve = new BezierCurve();
        foreach (double input in new[] { double.NaN, double.PositiveInfinity,
                     double.NegativeInfinity, -0.0001, 1.0001 })
        {
            Assert.IsFalse(curve.TryEvaluateNormalized(input, out double actual));
            Assert.AreEqual(0.0, actual);
        }
        foreach (double[] definition in new[]
                 {
                     new[] { -0.1, 0.0, 0.5, 1.0 },
                     new[] { 0.5, 0.0, 1.1, 1.0 },
                     new[] { double.NaN, 0.0, 0.5, 1.0 },
                     new[] { 0.5, double.PositiveInfinity, 0.5, 1.0 },
                     new[] { 0.5, 0.0, double.NegativeInfinity, 1.0 },
                     new[] { 0.5, 0.0, 0.5, double.NaN },
                     new[] { 0.5, double.MaxValue, 0.5, -double.MaxValue },
                     new[] { 99.0, 96.0, 0.0, 0.0 },
                 })
        {
            var evaluator = BezierCurve.NormalizedEvaluator.Compile(
                definition[0], definition[1], definition[2], definition[3]);
            Assert.IsFalse(evaluator.TryEvaluateNormalized(0.375, out double actual));
            Assert.AreEqual(0.375, actual);
        }
        Assert.IsTrue(curve.TryEvaluateNormalized(0.375, out double uninitialized));
        Assert.AreEqual(0.375, uninitialized);
    }

    [TestMethod]
    public void CapturedEvaluatorCannotMixColdCurveEditsAcrossCoupledAxes()
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(99, 92, 0, 0, BezierCurve.AxisType.LSRS));
        var predecessor = curve.CaptureEvaluator();
        Assert.IsTrue(predecessor.TryEvaluateNormalized(0.25, out double x));
        Assert.IsTrue(curve.InitBezierCurve(99, 93, 0, 0, BezierCurve.AxisType.LSRS));
        Assert.IsTrue(predecessor.TryEvaluateNormalized(0.75, out double y));
        Assert.AreEqual(0.25 * 0.25, x);
        Assert.AreEqual(0.75 * 0.75, y);
        Assert.IsTrue(curve.TryEvaluateNormalized(0.75, out double successor));
        Assert.AreEqual(0.75 * 0.75 * 0.75, successor);

        // Existing AsString/CustomDefinition setters do not initialize a curve.
        curve.AsString = "0.00, 0.00, 1.00, 1.00";
        curve.CustomDefinition = "not initialized";
        Assert.IsTrue(curve.TryEvaluateNormalized(0.75, out double unchanged));
        Assert.AreEqual(successor, unchanged);
    }

    [TestMethod]
    public void ContinuousHotPathAllocatesNoManagedMemory()
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(0.25, 0.1, 0.25, 1.0,
            BezierCurve.AxisType.LSRS));
        double sum = EvaluateBatch(curve, 4096);
        long before = GC.GetAllocatedBytesForCurrentThread();
        sum += EvaluateBatch(curve, 65_536);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(double.IsFinite(sum) && sum > 0);
    }

    [TestMethod]
    public void EveryLegacyByteRemainsIdenticalForCustomLinearAndSpecialModes()
    {
        var definitions = CustomCurves.Concat(Enumerable.Range(91, 5)
            .Select(mode => new[] { 99.0, (double)mode, 0.0, 0.0 }));
        foreach (double[] definition in definitions)
        foreach (BezierCurve.AxisType axis in Enum.GetValues<BezierCurve.AxisType>())
        {
            var curve = new BezierCurve();
            Assert.IsTrue(curve.InitBezierCurve(definition[0], definition[1],
                definition[2], definition[3], axis));
            byte[] expected = LegacyTable(definition, axis);
            CollectionAssert.AreEqual(expected, curve.arrayBezierLUT);
            for (int value = 0; value <= byte.MaxValue; value++)
                Assert.AreEqual(expected[value], curve.GetBezierEasing((byte)value));
        }
    }

    private static double EvaluateBatch(BezierCurve curve, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            var evaluator = curve.CaptureEvaluator();
            if (evaluator.TryEvaluateNormalized((i & 0xffff) / 65535.0, out double value))
                sum += value;
        }
        return sum;
    }

    private static double ReferenceContinuous(double input, double[] definition)
    {
        if (input == 0 || input == 1)
            return input;
        double lower = 0, upper = 1;
        for (int iteration = 0; iteration < 80; iteration++)
        {
            double t = (lower + upper) * 0.5;
            double x = DeCasteljau(t, definition[0], definition[2]);
            if (x == input)
                return Math.Clamp(DeCasteljau(t, definition[1], definition[3]), 0, 1);
            if (x < input) lower = t;
            else upper = t;
        }
        return Math.Clamp(DeCasteljau((lower + upper) * 0.5,
            definition[1], definition[3]), 0, 1);
    }

    private static double DeCasteljau(double t, double p1, double p2)
    {
        double a = t * p1;
        double b = (1 - t) * p1 + t * p2;
        double c = (1 - t) * p2 + t;
        return (1 - t) * ((1 - t) * a + t * b) +
            t * ((1 - t) * b + t * c);
    }

    private static double SpecialFormula(int mode, double input) => mode switch
    {
        91 => input <= 0.4 ? input * 0.55 :
            input <= 0.75 ? input - 0.18 : input * 1.72 - 0.72,
        92 => input * input,
        93 => input * input * input,
        94 => -1.0 * (input * (input - 2.0)),
        95 => (input - 1.0) * (input - 1.0) * (input - 1.0) + 1.0,
        _ => input,
    };

    // Frozen legacy LUT oracle from BezierCurve.cs before the continuous
    // evaluator addition: GRE(2012)/Mika-N(2019), MIT license retained in the
    // production file. Deliberately preserve the original 4 Newton / 10
    // subdivision iterations, rounding, casts and asymmetric byte mirroring.
    private static byte[] LegacyTable(double[] p, BezierCurve.AxisType axis)
    {
        byte[] result = new byte[256];
        if (p[0] == 0 && p[1] == 0 &&
            ((p[2] == 0 && p[3] == 0) || (p[2] == 1 && p[3] == 1)))
        {
            for (int i = 0; i < result.Length; i++) result[i] = (byte)i;
            return result;
        }
        double maximum = axis == BezierCurve.AxisType.LSRS ? 127 :
            axis == BezierCurve.AxisType.L2R2 ? 255 : 128;
        int center = axis == BezierCurve.AxisType.LSRS ? 128 : 0;
        double[] samples = new double[11];
        for (int i = 0; i < samples.Length; i++) samples[i] = Polynomial(i * 0.1, p[0], p[2]);
        for (int i = 0; i <= maximum; i++)
        {
            double input = i / maximum;
            double output = p[0] == 99 ? SpecialFormula((int)p[1], input) * maximum :
                Math.Clamp(Math.Round(Polynomial(LegacyInverse(input, p, samples),
                    p[1], p[3]) * maximum), 0, maximum);
            result[i + center] = (byte)(output + center);
            if (axis == BezierCurve.AxisType.LSRS)
                result[127 - i] = (byte)(255 - result[i + center]);
        }
        return result;
    }

    private static double Polynomial(double t, double p1, double p2) =>
        (((1.0 - 3.0 * p2 + 3.0 * p1) * t +
            (3.0 * p2 - 6.0 * p1)) * t + 3.0 * p1) * t;

    private static double LegacyInverse(double input, double[] p, double[] samples)
    {
        double start = 0;
        int current = 1;
        for (; current != 10 && samples[current] <= input; current++) start += 0.1;
        current--;
        double guess = start + (input - samples[current]) /
            (samples[current + 1] - samples[current]) * 0.1;
        double Slope(double t) => 3.0 * (1.0 - 3.0 * p[2] + 3.0 * p[0]) * t * t +
            2.0 * (3.0 * p[2] - 6.0 * p[0]) * t + 3.0 * p[0];
        double initialSlope = Slope(guess);
        if (initialSlope >= 0.001)
        {
            for (int i = 0; i < 4; i++)
            {
                double slope = Slope(guess);
                if (slope == 0) return guess;
                guess -= (Polynomial(guess, p[0], p[2]) - input) / slope;
            }
            return guess;
        }
        if (initialSlope == 0) return guess;
        double lower = start, upper = start + 0.1, difference, tValue;
        int count = 0;
        do
        {
            tValue = lower + (upper - lower) / 2.0;
            difference = Polynomial(tValue, p[0], p[2]) - input;
            if (difference > 0) upper = tValue;
            else lower = tValue;
        } while (Math.Abs(difference) > 0.0000001 && ++count < 10);
        return tValue;
    }
}
