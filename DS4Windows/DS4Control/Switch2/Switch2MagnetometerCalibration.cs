/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The calibration model, fit basis, and conservative acceptance thresholds are
adapted from the GPL-3.0 Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py
(_fit_full_soft_iron_calibration and its diagonal min/max fallback).
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

internal enum Switch2MagnetometerCalibrationModel : byte
{
    Invalid = 0,
    DiagonalMinMaxV1,
    FullEllipsoidV1,
}

internal enum Switch2MagnetometerCalibrationFitFailure : byte
{
    None = 0,
    NotCollecting,
    InsufficientSamples,
    InsufficientFiniteSamples,
    InsufficientAxisRange,
    RankDeficient,
    InvalidEllipsoidScale,
    NonPositiveEllipsoid,
    IllConditioned,
    InsufficientOrientationCoverage,
    ExcessiveFitResidual,
    InvalidFallback,
}

/// <summary>
/// Immutable row-major correction matrix. Validation mirrors the donor's
/// finite, determinant, row-norm, and inverse-condition gates so malformed
/// persisted data can never enter the report path.
/// </summary>
internal readonly struct Switch2MagnetometerMatrix3x3 :
    IEquatable<Switch2MagnetometerMatrix3x3>
{
    private Switch2MagnetometerMatrix3x3(float m11, float m12, float m13,
        float m21, float m22, float m23, float m31, float m32, float m33)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M31 = m31;
        M32 = m32;
        M33 = m33;
    }

    internal float M11 { get; }
    internal float M12 { get; }
    internal float M13 { get; }
    internal float M21 { get; }
    internal float M22 { get; }
    internal float M23 { get; }
    internal float M31 { get; }
    internal float M32 { get; }
    internal float M33 { get; }

    internal static bool TryCreate(float m11, float m12, float m13,
        float m21, float m22, float m23, float m31, float m32, float m33,
        out Switch2MagnetometerMatrix3x3 matrix,
        out double conditionProxy)
    {
        matrix = default;
        conditionProxy = double.PositiveInfinity;
        Span<double> values = stackalloc double[9]
        {
            m11, m12, m13, m21, m22, m23, m31, m32, m33,
        };
        for (int index = 0; index < values.Length; index++)
        {
            if (!double.IsFinite(values[index]))
            {
                return false;
            }
        }

        double determinant = m11 * (m22 * m33 - m23 * m32) -
            m12 * (m21 * m33 - m23 * m31) +
            m13 * (m21 * m32 - m22 * m31);
        if (!double.IsFinite(determinant) ||
            Math.Abs(determinant) <= 1.0e-6)
        {
            return false;
        }

        double row1 = Math.Sqrt(m11 * m11 + m12 * m12 + m13 * m13);
        double row2 = Math.Sqrt(m21 * m21 + m22 * m22 + m23 * m23);
        double row3 = Math.Sqrt(m31 * m31 + m32 * m32 + m33 * m33);
        double minimumRow = Math.Min(row1, Math.Min(row2, row3));
        double maximumRow = Math.Max(row1, Math.Max(row2, row3));
        if (minimumRow <= 1.0e-12 || maximumRow / minimumRow > 3.0)
        {
            return false;
        }

        double i11 = (m22 * m33 - m23 * m32) / determinant;
        double i12 = (m13 * m32 - m12 * m33) / determinant;
        double i13 = (m12 * m23 - m13 * m22) / determinant;
        double i21 = (m23 * m31 - m21 * m33) / determinant;
        double i22 = (m11 * m33 - m13 * m31) / determinant;
        double i23 = (m13 * m21 - m11 * m23) / determinant;
        double i31 = (m21 * m32 - m22 * m31) / determinant;
        double i32 = (m12 * m31 - m11 * m32) / determinant;
        double i33 = (m11 * m22 - m12 * m21) / determinant;
        double frobenius = Math.Sqrt(
            m11 * m11 + m12 * m12 + m13 * m13 +
            m21 * m21 + m22 * m22 + m23 * m23 +
            m31 * m31 + m32 * m32 + m33 * m33);
        double inverseFrobenius = Math.Sqrt(
            i11 * i11 + i12 * i12 + i13 * i13 +
            i21 * i21 + i22 * i22 + i23 * i23 +
            i31 * i31 + i32 * i32 + i33 * i33);
        conditionProxy = frobenius * inverseFrobenius / 3.0;
        if (!double.IsFinite(conditionProxy) || conditionProxy > 3.0)
        {
            return false;
        }

        matrix = new Switch2MagnetometerMatrix3x3(m11, m12, m13,
            m21, m22, m23, m31, m32, m33);
        return true;
    }

    internal Vector3 Transform(in Vector3 value) => new(
        M11 * value.X + M12 * value.Y + M13 * value.Z,
        M21 * value.X + M22 * value.Y + M23 * value.Z,
        M31 * value.X + M32 * value.Y + M33 * value.Z);

    public bool Equals(Switch2MagnetometerMatrix3x3 other) =>
        M11.Equals(other.M11) && M12.Equals(other.M12) &&
        M13.Equals(other.M13) && M21.Equals(other.M21) &&
        M22.Equals(other.M22) && M23.Equals(other.M23) &&
        M31.Equals(other.M31) && M32.Equals(other.M32) &&
        M33.Equals(other.M33);

    public override bool Equals(object obj) => obj is
        Switch2MagnetometerMatrix3x3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(M11, M12, M13, M21),
        HashCode.Combine(M22, M23, M31, M32), M33);
}

internal readonly struct Switch2MagnetometerCalibration :
    IEquatable<Switch2MagnetometerCalibration>
{
    private Switch2MagnetometerCalibration(in Vector3 bias,
        in Switch2MagnetometerMatrix3x3 correction,
        float referenceMagnitude,
        Switch2MagnetometerCalibrationModel model)
    {
        Bias = bias;
        Correction = correction;
        ReferenceMagnitude = referenceMagnitude;
        Model = model;
    }

    internal Vector3 Bias { get; }
    internal Switch2MagnetometerMatrix3x3 Correction { get; }
    internal float ReferenceMagnitude { get; }
    internal Switch2MagnetometerCalibrationModel Model { get; }
    internal bool IsValid => Model is (
            Switch2MagnetometerCalibrationModel.DiagonalMinMaxV1 or
            Switch2MagnetometerCalibrationModel.FullEllipsoidV1) &&
        IsFinite(Bias) && float.IsFinite(ReferenceMagnitude) &&
        ReferenceMagnitude > 1.0e-6f;

    internal static bool TryCreate(in Vector3 bias,
        in Switch2MagnetometerMatrix3x3 correction,
        float referenceMagnitude,
        Switch2MagnetometerCalibrationModel model,
        out Switch2MagnetometerCalibration calibration)
    {
        calibration = default;
        if (model is not (
                Switch2MagnetometerCalibrationModel.DiagonalMinMaxV1 or
                Switch2MagnetometerCalibrationModel.FullEllipsoidV1) ||
            !IsFinite(bias) || !float.IsFinite(referenceMagnitude) ||
            referenceMagnitude <= 1.0e-6f)
        {
            return false;
        }

        // Revalidate the immutable matrix instead of trusting a default or a
        // value materialized from untrusted persistence.
        if (!Switch2MagnetometerMatrix3x3.TryCreate(
                correction.M11, correction.M12, correction.M13,
                correction.M21, correction.M22, correction.M23,
                correction.M31, correction.M32, correction.M33,
                out Switch2MagnetometerMatrix3x3 validated, out _))
        {
            return false;
        }

        calibration = new Switch2MagnetometerCalibration(bias, validated,
            referenceMagnitude, model);
        return true;
    }

    internal bool TryTransform(in Vector3 raw, out Vector3 calibrated)
    {
        calibrated = default;
        if (!IsValid || !IsFinite(raw))
        {
            return false;
        }

        calibrated = Correction.Transform(raw - Bias);
        return IsFinite(calibrated);
    }

    public bool Equals(Switch2MagnetometerCalibration other) =>
        Bias.Equals(other.Bias) && Correction.Equals(other.Correction) &&
        ReferenceMagnitude.Equals(other.ReferenceMagnitude) &&
        Model == other.Model;

    public override bool Equals(object obj) => obj is
        Switch2MagnetometerCalibration other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Bias, Correction,
        ReferenceMagnitude, Model);

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

internal readonly struct Switch2MagnetometerCalibrationQuality
{
    internal Switch2MagnetometerCalibrationQuality(int sampleCount,
        int octantCount, in Vector3 axisRadii, double matrixCondition,
        double rmsRelativeResidual, double p95RelativeResidual,
        Switch2MagnetometerCalibrationFitFailure fullFitFailure,
        Switch2MagnetometerCalibrationModel adoptedModel)
    {
        SampleCount = sampleCount;
        OctantCount = octantCount;
        AxisRadii = axisRadii;
        MatrixCondition = matrixCondition;
        RmsRelativeResidual = rmsRelativeResidual;
        P95RelativeResidual = p95RelativeResidual;
        FullFitFailure = fullFitFailure;
        AdoptedModel = adoptedModel;
    }

    internal int SampleCount { get; }
    internal int OctantCount { get; }
    internal Vector3 AxisRadii { get; }
    internal double MatrixCondition { get; }
    internal double RmsRelativeResidual { get; }
    internal double P95RelativeResidual { get; }
    internal Switch2MagnetometerCalibrationFitFailure FullFitFailure { get; }
    internal Switch2MagnetometerCalibrationModel AdoptedModel { get; }
}

/// <summary>
/// Explicit calibration-mode collector and fitter. The sample buffer exists
/// only while the user asks to calibrate; ordinary reports never allocate.
/// </summary>
internal sealed class Switch2MagnetometerCalibrationSession
{
    internal const int MaximumSamples = 20_000;
    internal const int MinimumFullFitSamples = 500;
    internal const int MinimumFallbackSamples = 50;
    internal const float MinimumAxisRadius = 25.0f;
    internal const double MaximumMatrixCondition = 3.0;
    internal const int MinimumOctants = 7;
    internal const double MaximumRmsRelativeResidual = 0.08;
    internal const double MaximumP95RelativeResidual = 0.15;

    private Vector3[] samples;
    private int sampleCount;
    private Vector3 minimum;
    private Vector3 maximum;
    private bool collecting;

    internal bool IsCollecting => collecting;
    internal int SampleCount => sampleCount;

    internal void Start()
    {
        samples ??= new Vector3[MaximumSamples];
        sampleCount = 0;
        minimum = new Vector3(float.PositiveInfinity);
        maximum = new Vector3(float.NegativeInfinity);
        collecting = true;
    }

    internal bool TryObserve(in Vector3 sample)
    {
        if (!collecting || !IsFinite(sample))
        {
            return false;
        }

        minimum = Vector3.Min(minimum, sample);
        maximum = Vector3.Max(maximum, sample);
        if (sampleCount < MaximumSamples)
        {
            samples[sampleCount++] = sample;
        }
        return true;
    }

    internal void Cancel()
    {
        collecting = false;
        sampleCount = 0;
        minimum = default;
        maximum = default;
    }

    internal bool TryComplete(out Switch2MagnetometerCalibration calibration,
        out Switch2MagnetometerCalibrationQuality quality)
    {
        calibration = default;
        quality = default;
        if (!collecting)
        {
            quality = FailedQuality(
                Switch2MagnetometerCalibrationFitFailure.NotCollecting);
            return false;
        }
        collecting = false;

        Vector3 midpoint = (minimum + maximum) * 0.5f;
        Vector3 radii = (maximum - minimum) * 0.5f;
        Switch2MagnetometerCalibrationFitFailure fullFailure =
            TryFitFull(midpoint, radii, out calibration, out quality);
        if (fullFailure == Switch2MagnetometerCalibrationFitFailure.None)
        {
            return true;
        }

        if (sampleCount < MinimumFallbackSamples || !IsFinite(radii) ||
            radii.X <= MinimumAxisRadius || radii.Y <= MinimumAxisRadius ||
            radii.Z <= MinimumAxisRadius)
        {
            quality = new Switch2MagnetometerCalibrationQuality(sampleCount,
                quality.OctantCount, radii, quality.MatrixCondition,
                quality.RmsRelativeResidual, quality.P95RelativeResidual,
                fullFailure, Switch2MagnetometerCalibrationModel.Invalid);
            return false;
        }

        float meanRadius = (radii.X + radii.Y + radii.Z) / 3.0f;
        if (!Switch2MagnetometerMatrix3x3.TryCreate(
                meanRadius / radii.X, 0.0f, 0.0f,
                0.0f, meanRadius / radii.Y, 0.0f,
                0.0f, 0.0f, meanRadius / radii.Z,
                out Switch2MagnetometerMatrix3x3 diagonal,
                out double fallbackCondition) ||
            !Switch2MagnetometerCalibration.TryCreate(midpoint, diagonal,
                meanRadius,
                Switch2MagnetometerCalibrationModel.DiagonalMinMaxV1,
                out calibration))
        {
            quality = new Switch2MagnetometerCalibrationQuality(sampleCount,
                quality.OctantCount, radii, fallbackCondition,
                quality.RmsRelativeResidual, quality.P95RelativeResidual,
                Switch2MagnetometerCalibrationFitFailure.InvalidFallback,
                Switch2MagnetometerCalibrationModel.Invalid);
            return false;
        }

        quality = new Switch2MagnetometerCalibrationQuality(sampleCount,
            quality.OctantCount, radii, fallbackCondition,
            quality.RmsRelativeResidual, quality.P95RelativeResidual,
            fullFailure,
            Switch2MagnetometerCalibrationModel.DiagonalMinMaxV1);
        return true;
    }

    private Switch2MagnetometerCalibrationFitFailure TryFitFull(
        in Vector3 midpoint, in Vector3 radii,
        out Switch2MagnetometerCalibration calibration,
        out Switch2MagnetometerCalibrationQuality quality)
    {
        calibration = default;
        quality = new Switch2MagnetometerCalibrationQuality(sampleCount, 0,
            radii, double.PositiveInfinity, double.PositiveInfinity,
            double.PositiveInfinity,
            Switch2MagnetometerCalibrationFitFailure.InsufficientSamples,
            Switch2MagnetometerCalibrationModel.Invalid);
        if (sampleCount < MinimumFullFitSamples)
        {
            return Switch2MagnetometerCalibrationFitFailure.
                InsufficientSamples;
        }
        if (!IsFinite(radii) || radii.X <= MinimumAxisRadius ||
            radii.Y <= MinimumAxisRadius || radii.Z <= MinimumAxisRadius)
        {
            return Switch2MagnetometerCalibrationFitFailure.
                InsufficientAxisRange;
        }

        double scale = (radii.X + radii.Y + radii.Z) / 3.0;
        var normal = new double[9, 9];
        var target = new double[9];
        Span<double> designRow = stackalloc double[9];
        int finiteSamples = 0;
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            Vector3 point = samples[sampleIndex];
            if (!IsFinite(point))
            {
                continue;
            }
            finiteSamples++;
            double x = (point.X - midpoint.X) / scale;
            double y = (point.Y - midpoint.Y) / scale;
            double z = (point.Z - midpoint.Z) / scale;
            designRow[0] = x * x;
            designRow[1] = y * y;
            designRow[2] = z * z;
            designRow[3] = 2.0 * x * y;
            designRow[4] = 2.0 * x * z;
            designRow[5] = 2.0 * y * z;
            designRow[6] = x;
            designRow[7] = y;
            designRow[8] = z;
            for (int i = 0; i < designRow.Length; i++)
            {
                target[i] += designRow[i];
                for (int j = 0; j <= i; j++)
                {
                    normal[i, j] += designRow[i] * designRow[j];
                }
            }
        }
        if (finiteSamples < MinimumFullFitSamples)
        {
            return Switch2MagnetometerCalibrationFitFailure.
                InsufficientFiniteSamples;
        }
        for (int i = 0; i < 9; i++)
        {
            for (int j = i + 1; j < 9; j++)
            {
                normal[i, j] = normal[j, i];
            }
        }
        if (!TrySolve(normal, target, out double[] parameters))
        {
            return Switch2MagnetometerCalibrationFitFailure.RankDeficient;
        }

        var quadratic = new double[3, 3]
        {
            { parameters[0], parameters[3], parameters[4] },
            { parameters[3], parameters[1], parameters[5] },
            { parameters[4], parameters[5], parameters[2] },
        };
        double[] linear = { parameters[6], parameters[7], parameters[8] };
        if (!TrySolve(quadratic, linear, out double[] solvedCenter))
        {
            return Switch2MagnetometerCalibrationFitFailure.RankDeficient;
        }
        for (int index = 0; index < 3; index++)
        {
            solvedCenter[index] *= -0.5;
        }

        double qCenterX = quadratic[0, 0] * solvedCenter[0] +
            quadratic[0, 1] * solvedCenter[1] +
            quadratic[0, 2] * solvedCenter[2];
        double qCenterY = quadratic[1, 0] * solvedCenter[0] +
            quadratic[1, 1] * solvedCenter[1] +
            quadratic[1, 2] * solvedCenter[2];
        double qCenterZ = quadratic[2, 0] * solvedCenter[0] +
            quadratic[2, 1] * solvedCenter[1] +
            quadratic[2, 2] * solvedCenter[2];
        double denominator = 1.0 + solvedCenter[0] * qCenterX +
            solvedCenter[1] * qCenterY + solvedCenter[2] * qCenterZ;
        if (!double.IsFinite(denominator) || denominator <= 1.0e-9)
        {
            return Switch2MagnetometerCalibrationFitFailure.
                InvalidEllipsoidScale;
        }

        var shape = new double[3, 3];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                shape[row, column] = (quadratic[row, column] +
                    quadratic[column, row]) / (2.0 * denominator);
            }
        }
        if (!TrySymmetricEigenDecomposition(shape,
                out double[] eigenvalues, out double[,] eigenvectors))
        {
            return Switch2MagnetometerCalibrationFitFailure.
                NonPositiveEllipsoid;
        }
        double minimumEigenvalue = Math.Min(eigenvalues[0],
            Math.Min(eigenvalues[1], eigenvalues[2]));
        double maximumEigenvalue = Math.Max(eigenvalues[0],
            Math.Max(eigenvalues[1], eigenvalues[2]));
        if (!double.IsFinite(minimumEigenvalue) ||
            minimumEigenvalue <= 1.0e-9)
        {
            return Switch2MagnetometerCalibrationFitFailure.
                NonPositiveEllipsoid;
        }
        double matrixCondition = Math.Sqrt(maximumEigenvalue /
            minimumEigenvalue);
        if (!double.IsFinite(matrixCondition) ||
            matrixCondition > MaximumMatrixCondition)
        {
            quality = new Switch2MagnetometerCalibrationQuality(sampleCount,
                0, radii, matrixCondition, double.PositiveInfinity,
                double.PositiveInfinity,
                Switch2MagnetometerCalibrationFitFailure.IllConditioned,
                Switch2MagnetometerCalibrationModel.Invalid);
            return Switch2MagnetometerCalibrationFitFailure.IllConditioned;
        }

        double targetRadius = scale;
        var correction = new double[3, 3];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    correction[row, column] += eigenvectors[row, axis] *
                        Math.Sqrt(eigenvalues[axis]) *
                        eigenvectors[column, axis] *
                        (targetRadius / scale);
                }
            }
        }
        var center = new Vector3(
            (float)(midpoint.X + scale * solvedCenter[0]),
            (float)(midpoint.Y + scale * solvedCenter[1]),
            (float)(midpoint.Z + scale * solvedCenter[2]));
        if (!IsFinite(center) ||
            !Switch2MagnetometerMatrix3x3.TryCreate(
                (float)correction[0, 0], (float)correction[0, 1],
                (float)correction[0, 2], (float)correction[1, 0],
                (float)correction[1, 1], (float)correction[1, 2],
                (float)correction[2, 0], (float)correction[2, 1],
                (float)correction[2, 2],
                out Switch2MagnetometerMatrix3x3 matrix, out _))
        {
            return Switch2MagnetometerCalibrationFitFailure.IllConditioned;
        }

        var magnitudes = new double[finiteSamples];
        int magnitudeIndex = 0;
        int octantMask = 0;
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            Vector3 point = samples[sampleIndex];
            if (!IsFinite(point))
            {
                continue;
            }
            Vector3 centered = point - center;
            Vector3 corrected = matrix.Transform(centered);
            magnitudes[magnitudeIndex++] = corrected.Length();
            int octant = (centered.X >= 0.0f ? 1 : 0) |
                (centered.Y >= 0.0f ? 2 : 0) |
                (centered.Z >= 0.0f ? 4 : 0);
            octantMask |= 1 << octant;
        }
        Array.Sort(magnitudes);
        double medianMagnitude = magnitudes.Length % 2 == 0 ?
            (magnitudes[magnitudes.Length / 2 - 1] +
                magnitudes[magnitudes.Length / 2]) * 0.5 :
            magnitudes[magnitudes.Length / 2];
        if (!double.IsFinite(medianMagnitude) || medianMagnitude <= 1.0e-9)
        {
            return Switch2MagnetometerCalibrationFitFailure.
                InvalidEllipsoidScale;
        }
        double squaredResidualSum = 0.0;
        for (int index = 0; index < magnitudes.Length; index++)
        {
            double relative = magnitudes[index] / medianMagnitude - 1.0;
            squaredResidualSum += relative * relative;
            magnitudes[index] = Math.Abs(relative);
        }
        Array.Sort(magnitudes);
        double rmsResidual = Math.Sqrt(squaredResidualSum /
            magnitudes.Length);
        int p95Index = Math.Max(0,
            (int)Math.Ceiling(magnitudes.Length * 0.95) - 1);
        double p95Residual = magnitudes[p95Index];
        int octantCount = CountBits((byte)octantMask);
        Switch2MagnetometerCalibrationFitFailure qualityFailure =
            octantCount < MinimumOctants ?
                Switch2MagnetometerCalibrationFitFailure.
                    InsufficientOrientationCoverage :
            rmsResidual > MaximumRmsRelativeResidual ||
                    p95Residual > MaximumP95RelativeResidual ?
                Switch2MagnetometerCalibrationFitFailure.
                    ExcessiveFitResidual :
                Switch2MagnetometerCalibrationFitFailure.None;
        quality = new Switch2MagnetometerCalibrationQuality(sampleCount,
            octantCount, radii, matrixCondition, rmsResidual, p95Residual,
            qualityFailure, qualityFailure ==
                    Switch2MagnetometerCalibrationFitFailure.None ?
                Switch2MagnetometerCalibrationModel.FullEllipsoidV1 :
                Switch2MagnetometerCalibrationModel.Invalid);
        if (qualityFailure != Switch2MagnetometerCalibrationFitFailure.None)
        {
            return qualityFailure;
        }

        if (!Switch2MagnetometerCalibration.TryCreate(center, matrix,
                (float)medianMagnitude,
                Switch2MagnetometerCalibrationModel.FullEllipsoidV1,
                out calibration))
        {
            return Switch2MagnetometerCalibrationFitFailure.InvalidFallback;
        }
        return Switch2MagnetometerCalibrationFitFailure.None;
    }

    private Switch2MagnetometerCalibrationQuality FailedQuality(
        Switch2MagnetometerCalibrationFitFailure failure) => new(sampleCount,
            0, default, double.PositiveInfinity, double.PositiveInfinity,
            double.PositiveInfinity, failure,
            Switch2MagnetometerCalibrationModel.Invalid);

    private static bool TrySolve(double[,] matrix, double[] target,
        out double[] solution)
    {
        int size = target.Length;
        solution = new double[size];
        if (matrix.GetLength(0) != size || matrix.GetLength(1) != size)
        {
            return false;
        }
        var augmented = new double[size, size + 1];
        double maximum = 0.0;
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                augmented[row, column] = matrix[row, column];
                maximum = Math.Max(maximum, Math.Abs(matrix[row, column]));
            }
            augmented[row, size] = target[row];
        }
        if (!double.IsFinite(maximum) || maximum <= 1.0e-15)
        {
            return false;
        }

        double pivotFloor = maximum * 1.0e-12;
        for (int column = 0; column < size; column++)
        {
            int pivotRow = column;
            double pivotMagnitude = Math.Abs(augmented[column, column]);
            for (int row = column + 1; row < size; row++)
            {
                double candidate = Math.Abs(augmented[row, column]);
                if (candidate > pivotMagnitude)
                {
                    pivotMagnitude = candidate;
                    pivotRow = row;
                }
            }
            if (!double.IsFinite(pivotMagnitude) ||
                pivotMagnitude <= pivotFloor)
            {
                return false;
            }
            if (pivotRow != column)
            {
                for (int index = column; index <= size; index++)
                {
                    (augmented[column, index], augmented[pivotRow, index]) =
                        (augmented[pivotRow, index],
                            augmented[column, index]);
                }
            }
            double pivot = augmented[column, column];
            for (int index = column; index <= size; index++)
            {
                augmented[column, index] /= pivot;
            }
            for (int row = 0; row < size; row++)
            {
                if (row == column)
                {
                    continue;
                }
                double factor = augmented[row, column];
                for (int index = column; index <= size; index++)
                {
                    augmented[row, index] -= factor *
                        augmented[column, index];
                }
            }
        }
        for (int row = 0; row < size; row++)
        {
            solution[row] = augmented[row, size];
            if (!double.IsFinite(solution[row]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TrySymmetricEigenDecomposition(double[,] source,
        out double[] eigenvalues, out double[,] eigenvectors)
    {
        var matrix = (double[,])source.Clone();
        eigenvectors = new double[3, 3]
        {
            { 1.0, 0.0, 0.0 },
            { 0.0, 1.0, 0.0 },
            { 0.0, 0.0, 1.0 },
        };
        for (int sweep = 0; sweep < 32; sweep++)
        {
            int p = 0;
            int q = 1;
            double largest = Math.Abs(matrix[0, 1]);
            if (Math.Abs(matrix[0, 2]) > largest)
            {
                p = 0;
                q = 2;
                largest = Math.Abs(matrix[0, 2]);
            }
            if (Math.Abs(matrix[1, 2]) > largest)
            {
                p = 1;
                q = 2;
                largest = Math.Abs(matrix[1, 2]);
            }
            if (!double.IsFinite(largest))
            {
                eigenvalues = Array.Empty<double>();
                return false;
            }
            if (largest <= 1.0e-12)
            {
                break;
            }

            double angle = 0.5 * Math.Atan2(2.0 * matrix[p, q],
                matrix[q, q] - matrix[p, p]);
            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);
            double app = matrix[p, p];
            double aqq = matrix[q, q];
            double apq = matrix[p, q];
            matrix[p, p] = cosine * cosine * app -
                2.0 * sine * cosine * apq + sine * sine * aqq;
            matrix[q, q] = sine * sine * app +
                2.0 * sine * cosine * apq + cosine * cosine * aqq;
            matrix[p, q] = 0.0;
            matrix[q, p] = 0.0;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == p || axis == q)
                {
                    continue;
                }
                double aip = matrix[axis, p];
                double aiq = matrix[axis, q];
                matrix[axis, p] = cosine * aip - sine * aiq;
                matrix[p, axis] = matrix[axis, p];
                matrix[axis, q] = sine * aip + cosine * aiq;
                matrix[q, axis] = matrix[axis, q];
            }
            for (int row = 0; row < 3; row++)
            {
                double vip = eigenvectors[row, p];
                double viq = eigenvectors[row, q];
                eigenvectors[row, p] = cosine * vip - sine * viq;
                eigenvectors[row, q] = sine * vip + cosine * viq;
            }
        }
        eigenvalues = new[] { matrix[0, 0], matrix[1, 1], matrix[2, 2] };
        return double.IsFinite(eigenvalues[0]) &&
            double.IsFinite(eigenvalues[1]) &&
            double.IsFinite(eigenvalues[2]);
    }

    private static int CountBits(byte value)
    {
        int count = 0;
        while (value != 0)
        {
            value &= (byte)(value - 1);
            count++;
        }
        return count;
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
