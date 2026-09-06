/*
DS4Windows
Copyright (C) 2023 Travis Nickles
Copyright (C) 2026 hbashton

Shared profile equations moved from Mapping.cs. GNU GPL version 3 or later;
see the project license. The legacy operation order and write-site truncation
remain authoritative for byte-based controllers.
*/
using System;

namespace DS4Windows;

internal static class DS4StickProfileTransform
{
    internal static void ApplyDeadzoneAndOuter(StickDeadZoneInfo mod,
        ref DS4MappedStickAxis x, ref DS4MappedStickAxis y, ref byte outer)
    {
        bool coupledPrecision = x.IsHighResolution || y.IsHighResolution;
        bool radial = mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial;
        bool preciseX = radial ? coupledPrecision : x.IsHighResolution;
        bool preciseY = radial ? coupledPrecision : y.IsHighResolution;
        if (mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial)
        {
            int lsDeadzone = mod.deadZone;
            int lsAntiDead = mod.antiDeadZone;
            int lsMaxZone = mod.maxZone;
            double lsMaxOutput = mod.maxOutput;
            double lsVerticalScale = mod.verticalScale;
            bool interpret = lsAntiDead > 0 || lsMaxZone != 100 || lsMaxOutput != 100.0 || mod.maxOutputForce || lsVerticalScale != StickDeadZoneInfo.DEFAULT_VERTICAL_SCALE;

            if (lsDeadzone > 0 || interpret)
            {
                double lsSquared = Math.Pow(x.ProfileCoordinate - 128f, 2) + Math.Pow(y.ProfileCoordinate - 128f, 2);
                double lsDeadzoneSquared = Math.Pow(lsDeadzone, 2);
                if (lsDeadzone > 0 && lsSquared <= lsDeadzoneSquared)
                {
                    Store(ref x, 128, preciseX);
                    Store(ref y, 128, preciseY);
                }
                else if ((lsDeadzone > 0 && lsSquared > lsDeadzoneSquared) || interpret)
                {
                    double r = Math.Atan2(-(y.ProfileCoordinate - 128.0), (x.ProfileCoordinate - 128.0));
                    double maxXValue = x.ProfileCoordinate >= 128.0 ? 127.0 : -128;
                    double maxYValue = y.ProfileCoordinate >= 128.0 ? 127.0 : -128;
                    double ratio = lsMaxZone / 100.0;
                    double maxOutRatio = lsMaxOutput / 100.0;
                    double verticalScale = lsVerticalScale / 100.0;

                    double maxZoneXNegValue = (ratio * -128) + 128;
                    double maxZoneXPosValue = (ratio * 127) + 128;
                    double maxZoneYNegValue = maxZoneXNegValue;
                    double maxZoneYPosValue = maxZoneXPosValue;
                    double maxZoneX = x.ProfileCoordinate >= 128.0 ? (maxZoneXPosValue - 128.0) : (maxZoneXNegValue - 128.0);
                    double maxZoneY = y.ProfileCoordinate >= 128.0 ? (maxZoneYPosValue - 128.0) : (maxZoneYNegValue - 128.0);

                    double tempLsXDead = 0.0, tempLsYDead = 0.0;
                    double tempOutputX = 0.0, tempOutputY = 0.0;
                    if (lsDeadzone > 0)
                    {
                        tempLsXDead = Math.Abs(Math.Cos(r)) * (lsDeadzone / 127.0) * maxXValue;
                        tempLsYDead = Math.Abs(Math.Sin(r)) * (lsDeadzone / 127.0) * maxYValue;

                        if (lsSquared > lsDeadzoneSquared)
                        {
                            double currentX = Global.Clamp(maxZoneXNegValue, x.ProfileCoordinate, maxZoneXPosValue);
                            double currentY = Global.Clamp(maxZoneYNegValue, y.ProfileCoordinate, maxZoneYPosValue);
                            tempOutputX = ((currentX - 128.0 - tempLsXDead) / (maxZoneX - tempLsXDead));
                            tempOutputY = ((currentY - 128.0 - tempLsYDead) / (maxZoneY - tempLsYDead));
                        }
                    }
                    else
                    {
                        double currentX = Global.Clamp(maxZoneXNegValue, x.ProfileCoordinate, maxZoneXPosValue);
                        double currentY = Global.Clamp(maxZoneYNegValue, y.ProfileCoordinate, maxZoneYPosValue);
                        tempOutputX = (currentX - 128.0) / maxZoneX;
                        tempOutputY = (currentY - 128.0) / maxZoneY;
                    }

                    if (lsVerticalScale != StickDeadZoneInfo.DEFAULT_VERTICAL_SCALE)
                    {
                        tempOutputY = Math.Min(Math.Max(tempOutputY * verticalScale, 0.0), 1.0);
                    }

                    if (lsMaxOutput != 100.0 || mod.maxOutputForce)
                    {
                        double maxOutXRatio = Math.Abs(Math.Cos(r)) * maxOutRatio;
                        // Expand output a bit
                        maxOutXRatio = Math.Min(maxOutXRatio / 0.99, 1.0);

                        double maxOutYRatio = Math.Abs(Math.Sin(r)) * maxOutRatio;
                        // Expand output a bit
                        maxOutYRatio = Math.Min(maxOutYRatio / 0.99, 1.0);

                        tempOutputX = Math.Min(Math.Max(tempOutputX, 0.0), maxOutXRatio);
                        tempOutputY = Math.Min(Math.Max(tempOutputY, 0.0), maxOutYRatio);
                    }

                    double tempLsXAntiDeadPercent = 0.0, tempLsYAntiDeadPercent = 0.0;
                    if (lsAntiDead > 0)
                    {
                        tempLsXAntiDeadPercent = (lsAntiDead * 0.01) * Math.Abs(Math.Cos(r));
                        tempLsYAntiDeadPercent = (lsAntiDead * 0.01) * Math.Abs(Math.Sin(r));
                    }

                    if (tempOutputX > 0.0)
                    {
                        Store(ref x, ((((1.0 - tempLsXAntiDeadPercent) * tempOutputX + tempLsXAntiDeadPercent)) * maxXValue + 128.0), preciseX);
                    }
                    else
                    {
                        Store(ref x, 128, preciseX);
                    }

                    if (tempOutputY > 0.0)
                    {
                        Store(ref y, ((((1.0 - tempLsYAntiDeadPercent) * tempOutputY + tempLsYAntiDeadPercent)) * maxYValue + 128.0), preciseY);
                    }
                    else
                    {
                        Store(ref y, 128, preciseY);
                    }
                }
            }

            // Outer binding consumes the post-deadzone vector, before later
            // sensitivity/square/curve stages. Only its final output is a byte.
            outer = 0;
            if (x.ProfileCoordinate != 128 || y.ProfileCoordinate != 128)
            {
                double adjustX = x.ProfileCoordinate - 128;
                double adjustY = y.ProfileCoordinate - 128;
                double r = Math.Atan2(-adjustY, adjustX);
                double hyp = Math.Sqrt((adjustX * adjustX) + (adjustY * adjustY));

                if (hyp != 0.0)
                {
                    double tempX = (Math.Abs(Math.Cos(r)) * (x.ProfileCoordinate >= 128 ? 127 : 128));
                    if (!coupledPrecision) tempX = (int)tempX;
                    double tempY = (Math.Abs(Math.Sin(r)) * (y.ProfileCoordinate >= 128 ? 127 : 128));
                    if (!coupledPrecision) tempY = (int)tempY;
                    double maxValue = Math.Sqrt((tempX * tempX) + (tempY * tempY));
                    double ratio = hyp / maxValue;
                    if (ratio > 1.0) ratio = 1.0;
                    double currentValue = ratio * 255.0;
                    double deadValue = mod.outerBindDeadZone * 0.01 * 255.0;
                    if (!mod.outerBindInvert && currentValue > deadValue)
                    {
                        double outputRatio = (currentValue - deadValue) / (double)(255.0 - deadValue);
                        outer = (byte)(outputRatio * 255);
                    }
                    else if (mod.outerBindInvert && currentValue < deadValue)
                    {
                        double outputRatio = (deadValue - currentValue) / (double)deadValue;
                        outer = (byte)(outputRatio * 255);
                    }
                }
            }
        }
        else if (mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Axial)
        {
            ref StickDeadZoneInfo.AxisDeadZoneInfo xAxisDeadInfo = ref mod.xAxisDeadInfo;
            if (xAxisDeadInfo.deadZone > 0 || xAxisDeadInfo.antiDeadZone > 0 || xAxisDeadInfo.maxZone != 100 || xAxisDeadInfo.maxOutput != 100)
            {
                double distVal = Math.Abs(x.ProfileCoordinate - 128);
                if (xAxisDeadInfo.deadZone > 0 && distVal <= xAxisDeadInfo.deadZone)
                {
                    Store(ref x, 128, preciseX);
                }
                else if ((xAxisDeadInfo.deadZone > 0 && distVal > xAxisDeadInfo.deadZone) || xAxisDeadInfo.antiDeadZone > 0 || xAxisDeadInfo.maxZone != 100 || xAxisDeadInfo.maxOutput != 100)
                {
                    double maxAxisValue = x.ProfileCoordinate >= 128.0 ? 127.0 : -128.0;
                    double ratio = xAxisDeadInfo.maxZone / 100.0;
                    double maxOutRatio = xAxisDeadInfo.maxOutput / 100.0;

                    double maxZoneNegValue = (ratio * -128.0) + 128.0;
                    double maxZonePosValue = (ratio * 127.0) + 128.0;
                    double maxZone = x.ProfileCoordinate >= 128.0 ? (maxZonePosValue - 128.0) : (maxZoneNegValue - 128.0);

                    double tempDead = (xAxisDeadInfo.deadZone > 0) ? ((xAxisDeadInfo.deadZone / 127.0) * maxAxisValue) : 0.0;
                    double currentVal = Global.Clamp(maxZoneNegValue, x.ProfileCoordinate, maxZonePosValue);
                    double tempOutput = (currentVal - 128.0 - tempDead) / (maxZone - tempDead);

                    if (xAxisDeadInfo.maxOutput != 100.0)
                    {
                        // Expand output a bit
                        maxOutRatio = Math.Min(maxOutRatio / 0.99, 1.0);
                        tempOutput = Math.Min(Math.Max(tempOutput, 0.0), maxOutRatio);
                    }

                    double tempAntiDeadPercent = 0.0;
                    if (xAxisDeadInfo.antiDeadZone > 0)
                    {
                        tempAntiDeadPercent = xAxisDeadInfo.antiDeadZone * 0.01;
                    }

                    if (tempOutput > 0.0)
                    {
                        Store(ref x, ((((1.0 - tempAntiDeadPercent) * tempOutput + tempAntiDeadPercent)) * maxAxisValue + 128.0), preciseX);
                    }
                    else
                    {
                        Store(ref x, 128, preciseX);
                    }
                }
            }

            ref StickDeadZoneInfo.AxisDeadZoneInfo yAxisDeadInfo = ref mod.yAxisDeadInfo;
            if (yAxisDeadInfo.deadZone > 0 || yAxisDeadInfo.antiDeadZone > 0 || yAxisDeadInfo.maxZone != 100 || yAxisDeadInfo.maxOutput != 100)
            {
                double distVal = Math.Abs(y.ProfileCoordinate - 128);
                if (yAxisDeadInfo.deadZone > 0 && distVal <= yAxisDeadInfo.deadZone)
                {
                    Store(ref y, 128, preciseY);
                }
                else if ((yAxisDeadInfo.deadZone > 0 && distVal > yAxisDeadInfo.deadZone) || yAxisDeadInfo.antiDeadZone > 0 || yAxisDeadInfo.maxZone != 100 || yAxisDeadInfo.maxOutput != 100)
                {
                    double maxAxisValue = y.ProfileCoordinate >= 128.0 ? 127.0 : -128.0;
                    double ratio = yAxisDeadInfo.maxZone / 100.0;
                    double maxOutRatio = yAxisDeadInfo.maxOutput / 100.0;

                    double maxZoneNegValue = (ratio * -128.0) + 128.0;
                    double maxZonePosValue = (ratio * 127.0) + 128.0;
                    double maxZone = y.ProfileCoordinate >= 128.0 ? (maxZonePosValue - 128.0) : (maxZoneNegValue - 128.0);

                    double tempDead = (yAxisDeadInfo.deadZone > 0) ? ((yAxisDeadInfo.deadZone / 127.0) * maxAxisValue) : 0.0;
                    double currentVal = Global.Clamp(maxZoneNegValue, y.ProfileCoordinate, maxZonePosValue);
                    double tempOutput = (currentVal - 128.0 - tempDead) / (maxZone - tempDead);

                    if (yAxisDeadInfo.maxOutput != 100.0)
                    {
                        // Expand output a bit
                        maxOutRatio = Math.Min(maxOutRatio / 0.99, 1.0);
                        tempOutput = Math.Min(Math.Max(tempOutput, 0.0), maxOutRatio);
                    }

                    double tempAntiDeadPercent = 0.0;
                    if (yAxisDeadInfo.antiDeadZone > 0)
                    {
                        tempAntiDeadPercent = yAxisDeadInfo.antiDeadZone * 0.01;
                    }

                    if (tempOutput > 0.0)
                    {
                        Store(ref y, ((((1.0 - tempAntiDeadPercent) * tempOutput + tempAntiDeadPercent)) * maxAxisValue + 128.0), preciseY);
                    }
                    else
                    {
                        Store(ref y, 128, preciseY);
                    }
                }
            }
        }
    }

    internal static void ApplyOutputCurve(StickDeadZoneInfo mod, int mode, BezierCurve curve,
        ref DS4MappedStickAxis x, ref DS4MappedStickAxis y)
    {
        bool radial = mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial;
        bool coupledPrecision = x.IsHighResolution || y.IsHighResolution;
        bool preciseX = radial ? coupledPrecision : x.IsHighResolution;
        bool preciseY = radial ? coupledPrecision : y.IsHighResolution;
        if (mode > 0 && (x.ProfileCoordinate != 128 || y.ProfileCoordinate != 128))
        {
            double tempRatioX = 0.0, tempRatioY = 0.0;
            double capX = 0.0, capY = 0.0;
            if (mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial)
            {
                double r = Math.Atan2(-(y.ProfileCoordinate - 128.0), (x.ProfileCoordinate - 128.0));
                double maxOutXRatio = Math.Abs(Math.Cos(r));
                double maxOutYRatio = Math.Abs(Math.Sin(r));
                double sideX = x.ProfileCoordinate - 128; double sideY = y.ProfileCoordinate - 128.0;
                capX = x.ProfileCoordinate >= 128 ? maxOutXRatio * 127.0 : maxOutXRatio * 128.0;
                capY = y.ProfileCoordinate >= 128 ? maxOutYRatio * 127.0 : maxOutYRatio * 128.0;
                double absSideX = Math.Abs(sideX); double absSideY = Math.Abs(sideY);
                if (absSideX > capX) capX = absSideX;
                if (absSideY > capY) capY = absSideY;
                tempRatioX = capX > 0 ? (x.ProfileCoordinate - 128.0) / capX : 0;
                tempRatioY = capY > 0 ? (y.ProfileCoordinate - 128.0) / capY : 0;
            }
            else if (mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Axial)
            {
                capX = x.ProfileCoordinate >= 128 ? 127.0 : 128.0;
                capY = y.ProfileCoordinate >= 128 ? 127.0 : 128.0;
                tempRatioX = (x.ProfileCoordinate - 128.0) / capX;
                tempRatioY = (y.ProfileCoordinate - 128.0) / capY;
            }

            double signX = tempRatioX >= 0.0 ? 1.0 : -1.0;
            double signY = tempRatioY >= 0.0 ? 1.0 : -1.0;

            if (mode == 1)
            {
                double absX = Math.Abs(tempRatioX);
                double absY = Math.Abs(tempRatioY);
                double outputX = 0.0;
                double outputY = 0.0;

                if (absX <= 0.4)
                {
                    outputX = 0.8 * absX;
                }
                else if (absX <= 0.75)
                {
                    outputX = absX - 0.08;
                }
                else if (absX > 0.75)
                {
                    outputX = (absX * 1.32) - 0.32;
                }

                if (absY <= 0.4)
                {
                    outputY = 0.8 * absY;
                }
                else if (absY <= 0.75)
                {
                    outputY = absY - 0.08;
                }
                else if (absY > 0.75)
                {
                    outputY = (absY * 1.32) - 0.32;
                }

                Store(ref x, (outputX * signX * capX + 128.0), preciseX);
                Store(ref y, (outputY * signY * capY + 128.0), preciseY);
            }
            else if (mode == 2)
            {
                double outputX = tempRatioX * tempRatioX;
                double outputY = tempRatioY * tempRatioY;
                Store(ref x, (outputX * signX * capX + 128.0), preciseX);
                Store(ref y, (outputY * signY * capY + 128.0), preciseY);
            }
            else if (mode == 3)
            {
                double outputX = tempRatioX * tempRatioX * tempRatioX;
                double outputY = tempRatioY * tempRatioY * tempRatioY;
                Store(ref x, (outputX * capX + 128.0), preciseX);
                Store(ref y, (outputY * capY + 128.0), preciseY);
            }
            else if (mode == 4)
            {
                double absX = Math.Abs(tempRatioX);
                double absY = Math.Abs(tempRatioY);
                double outputX = absX * (absX - 2.0);
                double outputY = absY * (absY - 2.0);
                Store(ref x, (-1.0 * outputX * signX * capX + 128.0), preciseX);
                Store(ref y, (-1.0 * outputY * signY * capY + 128.0), preciseY);
            }
            else if (mode == 5)
            {
                double innerX = Math.Abs(tempRatioX) - 1.0;
                double innerY = Math.Abs(tempRatioY) - 1.0;
                double outputX = innerX * innerX * innerX + 1.0;
                double outputY = innerY * innerY * innerY + 1.0;
                Store(ref x, (1.0 * outputX * signX * capX + 128.0), preciseX);
                Store(ref y, (1.0 * outputY * signY * capY + 128.0), preciseY);
            }
            else if (mode == 6 &&
                (radial || mod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Axial))
            {
                // One immutable curve generation for both coupled axes.
                BezierCurve.NormalizedEvaluator evaluator = curve.CaptureEvaluator();
                ApplyCustomCurveAxis(ref x, tempRatioX, capX, radial, preciseX, curve, evaluator);
                ApplyCustomCurveAxis(ref y, tempRatioY, capY, radial, preciseY, curve, evaluator);
            }
        }
    }

    private static void ApplyCustomCurveAxis(ref DS4MappedStickAxis axis,
        double ratio, double cap, bool radial, bool precise, BezierCurve curve,
        BezierCurve.NormalizedEvaluator evaluator)
    {
        if (precise)
        {
            double magnitude = Math.Abs(ratio);
            if (!evaluator.TryEvaluateNormalized(magnitude, out double curved))
                curved = magnitude; // Invalid definition retains the linear input.
            double signed = ratio < 0.0 ? -curved : curved;
            Store(ref axis, signed * cap + 128.0, true);
        }
        else if (radial)
        {
            // The old vector-to-byte, byte LUT and final byte writes are all
            // intentional for legacy carriers. Do not use them for precise input.
            double maximum = axis.LegacyValue >= 128 ? 127.0 : 128.0;
            byte vectorByte = (byte)(ratio * maximum + 128.0);
            byte curvedByte = curve.arrayBezierLUT[vectorByte];
            double curvedRatio = (curvedByte - 128.0) / maximum;
            Store(ref axis, curvedRatio * cap + 128.0, false);
        }
        else
        {
            axis = DS4MappedStickAxis.FromLegacy(curve.arrayBezierLUT[axis.LegacyValue]);
        }
    }

    private static void Store(ref DS4MappedStickAxis axis, double coordinate, bool precise)
    {
        if (precise)
            // Saturate finite transform roundoff/overshoot at the profile
            // limits; nonfinite calculations still fail neutral in the carrier.
            DS4MappedStickAxis.TryFromProfileCoordinate(
                double.IsFinite(coordinate) ? Math.Clamp(coordinate, 0.0, 255.0) : coordinate,
                out axis);
        else
            axis = DS4MappedStickAxis.FromLegacy((byte)coordinate);
    }
}
