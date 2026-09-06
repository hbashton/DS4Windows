/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

/// <summary>
/// Ephemeral, allocation-free capture of ordinary mapped-stick mouse deltas.
/// Mapping remains authoritative; this frame only recovers the already-mapped
/// continuous velocity so the one existing Switch 2 presenter can own its
/// fractional high-rate delivery without presenting the report delta twice.
/// </summary>
internal struct Switch2MappedStickMousePresentationFrame
{
    internal const double MaximumReportIntervalMilliseconds = 100.0;

    internal bool HasHorizontalMapping;
    internal bool HasVerticalMapping;
    internal double DeltaX;
    internal double DeltaY;
    internal double VelocityX;
    internal double VelocityY;

    internal readonly bool Active =>
        HasHorizontalMapping && VelocityX != 0.0 ||
        HasVerticalMapping && VelocityY != 0.0;

    internal bool TryCapture(DS4Controls control, double signedDelta,
        double reportIntervalMilliseconds)
    {
        bool horizontal = control is DS4Controls.LXNeg or DS4Controls.LXPos or
            DS4Controls.RXNeg or DS4Controls.RXPos;
        bool vertical = control is DS4Controls.LYNeg or DS4Controls.LYPos or
            DS4Controls.RYNeg or DS4Controls.RYPos;
        if (!horizontal && !vertical || !double.IsFinite(signedDelta) ||
            !double.IsFinite(reportIntervalMilliseconds) ||
            reportIntervalMilliseconds <= 0.0 ||
            reportIntervalMilliseconds > MaximumReportIntervalMilliseconds)
        {
            return false;
        }

        double intervalSeconds = reportIntervalMilliseconds / 1_000.0;
        double velocity = signedDelta / intervalSeconds;
        if (!double.IsFinite(velocity) || Math.Abs(velocity) >
                Switch2HighRateMousePresenter.MaximumVelocityPixelsPerSecond)
        {
            return false;
        }

        if (horizontal)
        {
            HasHorizontalMapping = true;
            DeltaX = signedDelta;
            VelocityX = velocity;
        }
        else
        {
            HasVerticalMapping = true;
            DeltaY = signedDelta;
            VelocityY = velocity;
        }
        return true;
    }
}
