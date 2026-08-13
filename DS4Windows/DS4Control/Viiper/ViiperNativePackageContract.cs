/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows
{
    /// <summary>
    /// One source of truth for the reviewed VIIPER native package. Release
    /// packaging and the live ping gate both consume these constants. A new
    /// driver or broker therefore requires one intentional repin instead of
    /// independently changing runtime and installer values.
    /// </summary>
    internal static class ViiperNativePackageContract
    {
        internal const string Architecture = "x64";
        internal const string UpstreamRepository = "hbashton/VIIPER";
        internal const string SourceRevision =
            "0bc65bcebdc4556ad04b08fe7c441734502b8376";
        internal const string ServerVersion = "0.1.0";
        internal const ushort DriverAbiMajor = 1;
        internal const ushort DriverAbiMinor = 10;
        internal const uint DriverCapabilities = 0x0000000d;
        internal const string DriverPackageVersion = "0.1.0.12";
        internal const string DriverBuildIdentity =
            "94e3ff987913a88677a1bfcc296c7ab026b9222cc93446674406abeb3a79700c";
    }
}
