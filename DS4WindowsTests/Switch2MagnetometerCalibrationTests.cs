using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MagnetometerCalibrationTests
{
    private static readonly Vector3 Bias = new(120.0f, -80.0f, 45.0f);

    [TestMethod]
    public void FullEllipsoidFitRecoversBiasAndUniformMagnitude()
    {
        var session = new Switch2MagnetometerCalibrationSession();
        session.Start();
        ObserveDistortedSphere(session, 1_500,
            1.35f, 0.12f, -0.05f,
            0.12f, 0.82f, 0.08f,
            -0.05f, 0.08f, 1.08f);

        Assert.IsTrue(session.TryComplete(out var calibration,
            out var quality));
        Assert.AreEqual(Switch2MagnetometerCalibrationModel.FullEllipsoidV1,
            calibration.Model);
        Assert.AreEqual(Switch2MagnetometerCalibrationFitFailure.None,
            quality.FullFitFailure);
        Assert.AreEqual(8, quality.OctantCount);
        Assert.IsTrue(quality.RmsRelativeResidual < 0.001);
        Assert.IsTrue(quality.P95RelativeResidual < 0.001);
        Assert.AreEqual(Bias.X, calibration.Bias.X, 0.05f);
        Assert.AreEqual(Bias.Y, calibration.Bias.Y, 0.05f);
        Assert.AreEqual(Bias.Z, calibration.Bias.Z, 0.05f);

        float minimumMagnitude = float.PositiveInfinity;
        float maximumMagnitude = float.NegativeInfinity;
        for (int index = 0; index < 360; index++)
        {
            Vector3 field = SpherePoint(index, 360) * 800.0f;
            Vector3 raw = Bias + InverseTransform(field,
                1.35f, 0.12f, -0.05f,
                0.12f, 0.82f, 0.08f,
                -0.05f, 0.08f, 1.08f);
            Assert.IsTrue(calibration.TryTransform(raw, out var corrected));
            minimumMagnitude = MathF.Min(minimumMagnitude,
                corrected.Length());
            maximumMagnitude = MathF.Max(maximumMagnitude,
                corrected.Length());
        }
        Assert.IsTrue(maximumMagnitude / minimumMagnitude < 1.002f);
    }

    [TestMethod]
    public void SmallerWellCoveredCaptureAdoptsDiagonalFallback()
    {
        var session = new Switch2MagnetometerCalibrationSession();
        session.Start();
        ObserveDistortedSphere(session, 240,
            1.20f, 0.0f, 0.0f,
            0.0f, 0.80f, 0.0f,
            0.0f, 0.0f, 1.05f);

        Assert.IsTrue(session.TryComplete(out var calibration,
            out var quality));
        Assert.AreEqual(
            Switch2MagnetometerCalibrationModel.DiagonalMinMaxV1,
            calibration.Model);
        Assert.AreEqual(
            Switch2MagnetometerCalibrationFitFailure.InsufficientSamples,
            quality.FullFitFailure);
        Assert.AreEqual(240, quality.SampleCount);
    }

    [TestMethod]
    public void NarrowAxisCaptureIsRejectedInsteadOfPoisoningMotion()
    {
        var session = new Switch2MagnetometerCalibrationSession();
        session.Start();
        for (int index = 0; index < 1_000; index++)
        {
            float angle = index * 2.0f * MathF.PI / 1_000.0f;
            Assert.IsTrue(session.TryObserve(Bias + new Vector3(
                MathF.Cos(angle) * 600.0f,
                MathF.Sin(angle) * 600.0f,
                MathF.Sin(angle * 3.0f) * 10.0f)));
        }

        Assert.IsFalse(session.TryComplete(out var calibration,
            out var quality));
        Assert.IsFalse(calibration.IsValid);
        Assert.AreEqual(
            Switch2MagnetometerCalibrationFitFailure.InsufficientAxisRange,
            quality.FullFitFailure);
        Assert.AreEqual(Switch2MagnetometerCalibrationModel.Invalid,
            quality.AdoptedModel);
    }

    [TestMethod]
    public void CancelAndNonFiniteSamplesCannotProduceCalibration()
    {
        var session = new Switch2MagnetometerCalibrationSession();
        Assert.IsFalse(session.TryObserve(Vector3.One));
        session.Start();
        Assert.IsFalse(session.TryObserve(new Vector3(float.NaN, 0.0f,
            0.0f)));
        session.Cancel();

        Assert.IsFalse(session.TryComplete(out _, out var quality));
        Assert.AreEqual(
            Switch2MagnetometerCalibrationFitFailure.NotCollecting,
            quality.FullFitFailure);
    }

    [TestMethod]
    public void AdoptedTransformWarmPathAllocatesNothing()
    {
        var session = new Switch2MagnetometerCalibrationSession();
        session.Start();
        ObserveDistortedSphere(session, 1_000,
            1.10f, 0.05f, 0.0f,
            0.05f, 0.90f, 0.04f,
            0.0f, 0.04f, 1.00f);
        Assert.IsTrue(session.TryComplete(out var calibration, out _));
        Vector3 raw = Bias + new Vector3(400.0f, -200.0f, 500.0f);
        bool valid = true;
        for (int index = 0; index < 1_000; index++)
        {
            valid &= calibration.TryTransform(raw, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            valid &= calibration.TryTransform(raw, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
    }

    private static void ObserveDistortedSphere(
        Switch2MagnetometerCalibrationSession session, int sampleCount,
        float m11, float m12, float m13,
        float m21, float m22, float m23,
        float m31, float m32, float m33)
    {
        for (int index = 0; index < sampleCount; index++)
        {
            Vector3 field = SpherePoint(index, sampleCount) * 800.0f;
            Vector3 raw = Bias + InverseTransform(field,
                m11, m12, m13, m21, m22, m23, m31, m32, m33);
            Assert.IsTrue(session.TryObserve(raw));
        }
    }

    private static Vector3 SpherePoint(int index, int count)
    {
        const float goldenAngle = 2.39996322972865332f;
        float y = 1.0f - 2.0f * ((index + 0.5f) / count);
        float radial = MathF.Sqrt(MathF.Max(0.0f, 1.0f - y * y));
        float angle = goldenAngle * index;
        return new Vector3(MathF.Cos(angle) * radial, y,
            MathF.Sin(angle) * radial);
    }

    private static Vector3 InverseTransform(in Vector3 value,
        float m11, float m12, float m13,
        float m21, float m22, float m23,
        float m31, float m32, float m33)
    {
        float determinant = m11 * (m22 * m33 - m23 * m32) -
            m12 * (m21 * m33 - m23 * m31) +
            m13 * (m21 * m32 - m22 * m31);
        return new Vector3(
            ((m22 * m33 - m23 * m32) * value.X +
                (m13 * m32 - m12 * m33) * value.Y +
                (m12 * m23 - m13 * m22) * value.Z) / determinant,
            ((m23 * m31 - m21 * m33) * value.X +
                (m11 * m33 - m13 * m31) * value.Y +
                (m13 * m21 - m11 * m23) * value.Z) / determinant,
            ((m21 * m32 - m22 * m31) * value.X +
                (m12 * m31 - m11 * m32) * value.Y +
                (m11 * m22 - m12 * m21) * value.Z) / determinant);
    }
}
