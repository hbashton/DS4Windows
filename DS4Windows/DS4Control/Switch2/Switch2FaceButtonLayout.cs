/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows.Switch2;

/// <summary>
/// Selects whether Nintendo face buttons retain their physical positions or
/// their printed Nintendo labels at DS4Windows' canonical mapping boundary.
/// </summary>
public enum Switch2FaceButtonLayout : byte
{
    /// <summary>
    /// Physical-position mapping used by standard PC/Xbox games: south is A,
    /// east is B, west is X, and north is Y.
    /// </summary>
    Xbox = 0,

    /// <summary>
    /// Printed-label mapping: Nintendo A/B/X/Y become canonical A/B/X/Y even
    /// though their physical positions differ from an Xbox controller.
    /// </summary>
    Nintendo = 1,
}

internal static class Switch2FaceButtonLayoutProjection
{
    internal static bool IsValid(Switch2FaceButtonLayout layout) =>
        layout is Switch2FaceButtonLayout.Xbox or
            Switch2FaceButtonLayout.Nintendo;

    /// <summary>
    /// Projects physical west/north/south/east observations into the existing
    /// DS4-compatible Square/Triangle/Cross/Circle state without allocating or
    /// changing the source sidecar.
    /// </summary>
    internal static bool TryProject(Switch2FaceButtonLayout layout,
        bool west, bool north, bool south, bool east,
        out bool square, out bool triangle, out bool cross, out bool circle)
    {
        if (!IsValid(layout))
        {
            square = triangle = cross = circle = false;
            return false;
        }

        if (layout == Switch2FaceButtonLayout.Nintendo)
        {
            // Switch A/B/X/Y labels occupy east/south/north/west. Preserve the
            // labels by routing them to canonical A/B/X/Y respectively.
            square = north;
            triangle = west;
            cross = east;
            circle = south;
            return true;
        }

        square = west;
        triangle = north;
        cross = south;
        circle = east;
        return true;
    }
}
