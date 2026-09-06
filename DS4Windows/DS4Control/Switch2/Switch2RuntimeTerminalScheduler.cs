/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

/// <summary>
/// Transport-neutral off-thread scheduler for the one terminal-neutral request
/// which can synchronously invoke runtime Report subscribers. Owners retain the
/// returned task across timeout/retry; scheduling never creates a second logical
/// terminal epoch.
/// </summary>
internal interface ISwitch2RuntimeTerminalScheduler
{
    bool TrySchedule(Func<Switch2TerminalNeutralRequestResult> callback,
        out Task<Switch2TerminalNeutralRequestResult> task);
}

internal sealed class Switch2RuntimeTerminalScheduler :
    ISwitch2RuntimeTerminalScheduler
{
    internal static readonly Switch2RuntimeTerminalScheduler Instance = new();

    private Switch2RuntimeTerminalScheduler()
    {
    }

    public bool TrySchedule(
        Func<Switch2TerminalNeutralRequestResult> callback,
        out Task<Switch2TerminalNeutralRequestResult> task)
    {
        if (callback == null)
        {
            task = null;
            return false;
        }
        task = Task.Run(callback);
        return true;
    }
}
