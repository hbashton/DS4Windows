/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows.Switch2;

internal static class Switch2RumbleDelay
{
    internal const int DefaultMilliseconds = 0;
    internal const int MinimumMilliseconds = 0;
    internal const int MaximumMilliseconds = 9_999;

    internal static bool IsValid(int milliseconds) => milliseconds is >=
        MinimumMilliseconds and <= MaximumMilliseconds;

    internal static int Normalize(int milliseconds) =>
        IsValid(milliseconds) ? milliseconds : DefaultMilliseconds;
}
