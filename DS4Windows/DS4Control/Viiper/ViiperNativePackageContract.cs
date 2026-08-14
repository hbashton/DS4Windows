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
            "77994024403b189f5fce55466b588d26ed3fb309";
        internal const string ServerVersion = "0.1.0";
        internal const ushort DriverAbiMajor = 1;
        internal const ushort DriverAbiMinor = 10;
        internal const uint DriverCapabilities = 0x0000000d;
        internal const string DriverPackageVersion = "0.1.0.23";
        internal const string DriverBuildIdentity =
            "3769e8eab5493c9eea662f5ebd063fff99b37766f4da8d60a6ffea5d3737a3c9";
    }

    /// <summary>
    /// Local driver iteration may opt out of exact VIIPER version/build pin
    /// equality without weakening the production default. The relaxed build
    /// still requires authenticated native UDE, exact ABI/capabilities and
    /// limits, a canonical four-part package version, a canonical loaded
    /// driver identity, and the trusted LocalSystem service process.
    /// </summary>
    internal static class ViiperRuntimeBuildPolicy
    {
#if VIIPER_LOCAL_TEST_RELAXED_VERSION_MATCHING
        internal const bool EnforceExactViiperVersionMatching = false;
#else
        internal const bool EnforceExactViiperVersionMatching = true;
#endif
    }
}
