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
            "164b1b4740bcfffc666457a350390b2bc964d41d";
        internal const string ServerVersion = "0.1.0";
        internal const ushort DriverAbiMajor = 1;
        internal const ushort DriverAbiMinor = 10;
        internal const uint DriverCapabilities = 0x0000000d;
        internal const string DriverPackageVersion = "0.1.0.19";
        internal const string DriverBuildIdentity =
            "50482e30ff90eb8c3fe8fb14f20947b16622b5e3d8ea2a4dd8f54dc2182e14a5";
    }
}
