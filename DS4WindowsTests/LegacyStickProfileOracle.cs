// Frozen from the pre-migration Mapping.SetCurveAndDeadzone worktree.
// Kept independent of the production typed reducers: every historical byte
// cast, outer-binding truncation and radial custom-curve LUT stage is retained.
using System;
using DS4Windows;

namespace DS4WindowsTests;

internal static class LegacyStickProfileOracle
{
    internal static void Deadzone(DS4State dState, StickDeadZoneInfo lsMod)
    {
        DS4State cState = dState;
        if (lsMod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial)
        {
            int lsDeadzone = lsMod.deadZone;
            int lsAntiDead = lsMod.antiDeadZone;
            int lsMaxZone = lsMod.maxZone;
            double lsMaxOutput = lsMod.maxOutput;
            double lsVerticalScale = lsMod.verticalScale;
            bool interpret = lsAntiDead > 0 || lsMaxZone != 100 || lsMaxOutput != 100.0 || lsMod.maxOutputForce || lsVerticalScale != StickDeadZoneInfo.DEFAULT_VERTICAL_SCALE;

            if (lsDeadzone > 0 || interpret)
            {
                double lsSquared = Math.Pow(cState.LX - 128f, 2) + Math.Pow(cState.LY - 128f, 2);
                double lsDeadzoneSquared = Math.Pow(lsDeadzone, 2);
                if (lsDeadzone > 0 && lsSquared <= lsDeadzoneSquared)
                {
                    dState.LX = 128;
                    dState.LY = 128;
                }
                else if ((lsDeadzone > 0 && lsSquared > lsDeadzoneSquared) || interpret)
                {
                    double r = Math.Atan2(-(dState.LY - 128.0), (dState.LX - 128.0));
                    double maxXValue = dState.LX >= 128.0 ? 127.0 : -128;
                    double maxYValue = dState.LY >= 128.0 ? 127.0 : -128;
                    double ratio = lsMaxZone / 100.0;
                    double maxOutRatio = lsMaxOutput / 100.0;
                    double verticalScale = lsVerticalScale / 100.0;

                    double maxZoneXNegValue = (ratio * -128) + 128;
                    double maxZoneXPosValue = (ratio * 127) + 128;
                    double maxZoneYNegValue = maxZoneXNegValue;
                    double maxZoneYPosValue = maxZoneXPosValue;
                    double maxZoneX = dState.LX >= 128.0 ? (maxZoneXPosValue - 128.0) : (maxZoneXNegValue - 128.0);
                    double maxZoneY = dState.LY >= 128.0 ? (maxZoneYPosValue - 128.0) : (maxZoneYNegValue - 128.0);

                    double tempLsXDead = 0.0, tempLsYDead = 0.0;
                    double tempOutputX = 0.0, tempOutputY = 0.0;
                    if (lsDeadzone > 0)
                    {
                        tempLsXDead = Math.Abs(Math.Cos(r)) * (lsDeadzone / 127.0) * maxXValue;
                        tempLsYDead = Math.Abs(Math.Sin(r)) * (lsDeadzone / 127.0) * maxYValue;

                        if (lsSquared > lsDeadzoneSquared)
                        {
                            double currentX = Global.Clamp(maxZoneXNegValue, dState.LX, maxZoneXPosValue);
                            double currentY = Global.Clamp(maxZoneYNegValue, dState.LY, maxZoneYPosValue);
                            tempOutputX = ((currentX - 128.0 - tempLsXDead) / (maxZoneX - tempLsXDead));
                            tempOutputY = ((currentY - 128.0 - tempLsYDead) / (maxZoneY - tempLsYDead));
                        }
                    }
                    else
                    {
                        double currentX = Global.Clamp(maxZoneXNegValue, dState.LX, maxZoneXPosValue);
                        double currentY = Global.Clamp(maxZoneYNegValue, dState.LY, maxZoneYPosValue);
                        tempOutputX = (currentX - 128.0) / maxZoneX;
                        tempOutputY = (currentY - 128.0) / maxZoneY;
                    }

                    if (lsVerticalScale != StickDeadZoneInfo.DEFAULT_VERTICAL_SCALE)
                    {
                        tempOutputY = Math.Min(Math.Max(tempOutputY * verticalScale, 0.0), 1.0);
                    }

                    if (lsMaxOutput != 100.0 || lsMod.maxOutputForce)
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
                        dState.LX = (byte)((((1.0 - tempLsXAntiDeadPercent) * tempOutputX + tempLsXAntiDeadPercent)) * maxXValue + 128.0);
                    }
                    else
                    {
                        dState.LX = 128;
                    }

                    if (tempOutputY > 0.0)
                    {
                        dState.LY = (byte)((((1.0 - tempLsYAntiDeadPercent) * tempOutputY + tempLsYAntiDeadPercent)) * maxYValue + 128.0);
                    }
                    else
                    {
                        dState.LY = 128;
                    }
                }
            }

            // Process LS Outer Binding
            dState.OutputLSOuter = 0;
            if (dState.LX != 128 || dState.LY != 128)
            {
                int adjustX = dState.LX - 128;
                int adjustY = dState.LY - 128;
                double r = Math.Atan2(-adjustY, adjustX);
                //double r = Math.Atan2(-(dState.RY - 128.0), (dState.RX - 128.0));
                //double maxXValue = dState.RX >= 128.0 ? 127.0 : -128;
                //double maxYValue = dState.RY >= 128.0 ? 127.0 : -128;
                double hyp = Math.Sqrt((adjustX * adjustX) + (adjustY * adjustY));

                if (hyp != 0.0)
                {
                    int tempX = (int)(Math.Abs(Math.Cos(r)) * (dState.LX >= 128 ? 127 : 128));
                    int tempY = (int)(Math.Abs(Math.Sin(r)) * (dState.LY >= 128 ? 127 : 128));
                    double maxValue = Math.Sqrt((tempX * tempX) + (tempY * tempY));
                    double ratio = hyp / maxValue;
                    if (ratio > 1.0) ratio = 1.0;
                    double currentValue = ratio * 255.0;
                    double deadValue = lsMod.outerBindDeadZone * 0.01 * 255.0;
                    if (!lsMod.outerBindInvert && currentValue > deadValue)
                    {
                        double outputRatio = (currentValue - deadValue) / (double)(255.0 - deadValue);
                        dState.OutputLSOuter = (byte)(outputRatio * 255);
                    }
                    else if (lsMod.outerBindInvert && currentValue < deadValue)
                    {
                        double outputRatio = (deadValue - currentValue) / (double)deadValue;
                        dState.OutputLSOuter = (byte)(outputRatio * 255);
                    }
                }
            }
        }
        else if (lsMod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Axial)
        {
            ref StickDeadZoneInfo.AxisDeadZoneInfo xAxisDeadInfo = ref lsMod.xAxisDeadInfo;
            if (xAxisDeadInfo.deadZone > 0 || xAxisDeadInfo.antiDeadZone > 0 || xAxisDeadInfo.maxZone != 100 || xAxisDeadInfo.maxOutput != 100)
            {
                int distVal = Math.Abs(cState.LX - 128);
                if (xAxisDeadInfo.deadZone > 0 && distVal <= xAxisDeadInfo.deadZone)
                {
                    dState.LX = 128;
                }
                else if ((xAxisDeadInfo.deadZone > 0 && distVal > xAxisDeadInfo.deadZone) || xAxisDeadInfo.antiDeadZone > 0 || xAxisDeadInfo.maxZone != 100 || xAxisDeadInfo.maxOutput != 100)
                {
                    double maxAxisValue = dState.LX >= 128.0 ? 127.0 : -128.0;
                    double ratio = xAxisDeadInfo.maxZone / 100.0;
                    double maxOutRatio = xAxisDeadInfo.maxOutput / 100.0;

                    double maxZoneNegValue = (ratio * -128.0) + 128.0;
                    double maxZonePosValue = (ratio * 127.0) + 128.0;
                    double maxZone = dState.LX >= 128.0 ? (maxZonePosValue - 128.0) : (maxZoneNegValue - 128.0);

                    double tempDead = (xAxisDeadInfo.deadZone > 0) ? ((xAxisDeadInfo.deadZone / 127.0) * maxAxisValue) : 0.0;
                    double currentVal = Global.Clamp(maxZoneNegValue, dState.LX, maxZonePosValue);
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
                        dState.LX = (byte)((((1.0 - tempAntiDeadPercent) * tempOutput + tempAntiDeadPercent)) * maxAxisValue + 128.0);
                    }
                    else
                    {
                        dState.LX = 128;
                    }
                }
            }

            ref StickDeadZoneInfo.AxisDeadZoneInfo yAxisDeadInfo = ref lsMod.yAxisDeadInfo;
            if (yAxisDeadInfo.deadZone > 0 || yAxisDeadInfo.antiDeadZone > 0 || yAxisDeadInfo.maxZone != 100 || yAxisDeadInfo.maxOutput != 100)
            {
                int distVal = Math.Abs(cState.LY - 128);
                if (yAxisDeadInfo.deadZone > 0 && distVal <= yAxisDeadInfo.deadZone)
                {
                    dState.LY = 128;
                }
                else if ((yAxisDeadInfo.deadZone > 0 && distVal > yAxisDeadInfo.deadZone) || yAxisDeadInfo.antiDeadZone > 0 || yAxisDeadInfo.maxZone != 100 || yAxisDeadInfo.maxOutput != 100)
                {
                    double maxAxisValue = dState.LY >= 128.0 ? 127.0 : -128.0;
                    double ratio = yAxisDeadInfo.maxZone / 100.0;
                    double maxOutRatio = yAxisDeadInfo.maxOutput / 100.0;

                    double maxZoneNegValue = (ratio * -128.0) + 128.0;
                    double maxZonePosValue = (ratio * 127.0) + 128.0;
                    double maxZone = dState.LY >= 128.0 ? (maxZonePosValue - 128.0) : (maxZoneNegValue - 128.0);

                    double tempDead = (yAxisDeadInfo.deadZone > 0) ? ((yAxisDeadInfo.deadZone / 127.0) * maxAxisValue) : 0.0;
                    double currentVal = Global.Clamp(maxZoneNegValue, dState.LY, maxZonePosValue);
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
                        dState.LY = (byte)((((1.0 - tempAntiDeadPercent) * tempOutput + tempAntiDeadPercent)) * maxAxisValue + 128.0);
                    }
                    else
                    {
                        dState.LY = 128;
                    }
                }
            }
        }
    }

    internal static void Curve(DS4State dState, StickDeadZoneInfo lsMod, int lsOutCurveMode, BezierCurve curve)
    {
        if (lsOutCurveMode > 0 && (dState.LX != 128 || dState.LY != 128))
        {
            double tempRatioX = 0.0, tempRatioY = 0.0;
            double capX = 0.0, capY = 0.0;
            if (lsMod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial)
            {
                double r = Math.Atan2(-(dState.LY - 128.0), (dState.LX - 128.0));
                double maxOutXRatio = Math.Abs(Math.Cos(r));
                double maxOutYRatio = Math.Abs(Math.Sin(r));
                double sideX = dState.LX - 128; double sideY = dState.LY - 128.0;
                capX = dState.LX >= 128 ? maxOutXRatio * 127.0 : maxOutXRatio * 128.0;
                capY = dState.LY >= 128 ? maxOutYRatio * 127.0 : maxOutYRatio * 128.0;
                double absSideX = Math.Abs(sideX); double absSideY = Math.Abs(sideY);
                if (absSideX > capX) capX = absSideX;
                if (absSideY > capY) capY = absSideY;
                tempRatioX = capX > 0 ? (dState.LX - 128.0) / capX : 0;
                tempRatioY = capY > 0 ? (dState.LY - 128.0) / capY : 0;
            }
            else if (lsMod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Axial)
            {
                capX = dState.LX >= 128 ? 127.0 : 128.0;
                capY = dState.LY >= 128 ? 127.0 : 128.0;
                tempRatioX = (dState.LX - 128.0) / capX;
                tempRatioY = (dState.LY - 128.0) / capY;
            }

            double signX = tempRatioX >= 0.0 ? 1.0 : -1.0;
            double signY = tempRatioY >= 0.0 ? 1.0 : -1.0;

            if (lsOutCurveMode == 1)
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

                dState.LX = (byte)(outputX * signX * capX + 128.0);
                dState.LY = (byte)(outputY * signY * capY + 128.0);
            }
            else if (lsOutCurveMode == 2)
            {
                double outputX = tempRatioX * tempRatioX;
                double outputY = tempRatioY * tempRatioY;
                dState.LX = (byte)(outputX * signX * capX + 128.0);
                dState.LY = (byte)(outputY * signY * capY + 128.0);
            }
            else if (lsOutCurveMode == 3)
            {
                double outputX = tempRatioX * tempRatioX * tempRatioX;
                double outputY = tempRatioY * tempRatioY * tempRatioY;
                dState.LX = (byte)(outputX * capX + 128.0);
                dState.LY = (byte)(outputY * capY + 128.0);
            }
            else if (lsOutCurveMode == 4)
            {
                double absX = Math.Abs(tempRatioX);
                double absY = Math.Abs(tempRatioY);
                double outputX = absX * (absX - 2.0);
                double outputY = absY * (absY - 2.0);
                dState.LX = (byte)(-1.0 * outputX * signX * capX + 128.0);
                dState.LY = (byte)(-1.0 * outputY * signY * capY + 128.0);
            }
            else if (lsOutCurveMode == 5)
            {
                double innerX = Math.Abs(tempRatioX) - 1.0;
                double innerY = Math.Abs(tempRatioY) - 1.0;
                double outputX = innerX * innerX * innerX + 1.0;
                double outputY = innerY * innerY * innerY + 1.0;
                dState.LX = (byte)(1.0 * outputX * signX * capX + 128.0);
                dState.LY = (byte)(1.0 * outputY * signY * capY + 128.0);
            }
            else if (lsOutCurveMode == 6)
            {
                if (lsMod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Radial)
                {
                    // Get max values and circular distance of axes
                    double maxX = (dState.LX >= 128 ? 127 : 128);
                    double maxY = (dState.LY >= 128 ? 127 : 128);
                    byte tempOutX = (byte)(tempRatioX * maxX + 128.0);
                    byte tempOutY = (byte)(tempRatioY * maxY + 128.0);

                    // Perform curve based on byte values from vector
                    byte tempX = curve.arrayBezierLUT[tempOutX];
                    byte tempY = curve.arrayBezierLUT[tempOutY];

                    // Calculate new ratio
                    double tempRatioOutX = (tempX - 128.0) / maxX;
                    double tempRatioOutY = (tempY - 128.0) / maxY;

                    // Map back to stick coordinates
                    dState.LX = (byte)(tempRatioOutX * capX + 128);
                    dState.LY = (byte)(tempRatioOutY * capY + 128);
                    //Console.WriteLine("X(I){0} X(O){1} {2} {3}", tempOutX, dState.LX, tempOutY, dState.LY);
                }
                else if (lsMod.deadzoneType == StickDeadZoneInfo.DeadZoneType.Axial)
                {
                    dState.LX = curve.arrayBezierLUT[dState.LX];
                    dState.LY = curve.arrayBezierLUT[dState.LY];
                }
            }
        }
    }
}
