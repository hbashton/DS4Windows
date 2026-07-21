/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Admission policy for virtual controller input created by a local
    /// Sunshine host for Moonlight clients. Device parsing remains governed by
    /// the normal supported VID/PID table; this policy only decides whether a
    /// supported virtual HID may enter that parser.
    /// </summary>
    internal static class MoonlightVirtualDevicePolicy
    {
        private const int ProcessCacheMilliseconds = 1000;
        private static long lastProcessProbeTimestamp;
        private static int cachedSunshineRunning;

        internal static bool ShouldAccept(bool isOwnOutput,
            bool isVirtualDevice, bool moonlightEnabled,
            bool sunshineRunning)
        {
            if (isOwnOutput)
            {
                return false;
            }
            if (!isVirtualDevice)
            {
                return true;
            }
            return moonlightEnabled && sunshineRunning;
        }

        internal static bool IsSunshineHostRunning()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Volatile.Read(ref lastProcessProbeTimestamp);
            if (previous != 0 && now - previous <
                Stopwatch.Frequency * ProcessCacheMilliseconds / 1000)
            {
                return Volatile.Read(ref cachedSunshineRunning) != 0;
            }

            bool running = ProbeSunshineProcesses();
            Volatile.Write(ref cachedSunshineRunning, running ? 1 : 0);
            Volatile.Write(ref lastProcessProbeTimestamp, now);
            return running;
        }

        private static bool ProbeSunshineProcesses()
        {
            string[] names = { "sunshine", "sunshine-service", "sunshinesvc" };
            foreach (string name in names)
            {
                Process[] processes = Array.Empty<Process>();
                try
                {
                    processes = Process.GetProcessesByName(name);
                    if (processes.Length > 0)
                    {
                        return true;
                    }
                }
                catch { }
                finally
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
            return false;
        }
    }
}
