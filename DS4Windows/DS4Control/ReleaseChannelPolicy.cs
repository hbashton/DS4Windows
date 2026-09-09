using DS4WinWPF.ApiDTO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DS4Windows
{
    public static class ReleaseChannelPolicy
    {
        public const string InstalledReleaseFileName = "DS4Windows.release";

        private static readonly Regex prereleaseNameRegex = new(
            @"(?i)(alpha|beta|preview|pre[- ]?release|prerelease|release candidate|viiperrc|(?:^|[^a-z])rc(?:\d|[^a-z]|$))",
            RegexOptions.Compiled);

        // These are release-channel ordinals, not Windows file versions:
        // VIIPERRC4.5 ships binary 5.0.5.0. Never compare 4.5 to 5.0.5.
        private static readonly Regex viiperPrereleaseTagRegex = new(
            @"^VIIPER(?<phase>RC|Beta)(?<number>\d+(?:\.\d+){0,3})?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex numericReleaseTagRegex = new(
            @"^v?(?<number>\d+(?:\.\d+){1,3})(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsPrereleaseBuild(string versionText)
        {
            return !string.IsNullOrWhiteSpace(versionText) &&
                prereleaseNameRegex.IsMatch(versionText);
        }

        public static bool IsPrereleaseInstall(
            string versionText,
            string installedReleaseTag)
        {
            return IsPrereleaseBuild(versionText) ||
                IsPrereleaseBuild(installedReleaseTag);
        }

        public static bool IsPrerelease(GithubRelease release)
        {
            return release is not null &&
                (release.PreRelease || IsPrereleaseBuild(release.TagName));
        }

        public static DateTimeOffset GetReleaseDate(GithubRelease release)
        {
            return release?.PublishedAt ?? release?.CreatedAt ?? DateTimeOffset.MinValue;
        }

        public static GithubRelease SelectPreferredRelease(
            IEnumerable<GithubRelease> releases,
            bool currentBuildIsPrerelease)
        {
            GithubRelease[] published = (releases ?? Array.Empty<GithubRelease>())
                .Where(release => release is not null &&
                    !release.Draft &&
                    !string.IsNullOrWhiteSpace(release.TagName))
                .ToArray();

            GithubRelease latestStable = SelectNewest(
                published.Where(release => !IsPrerelease(release)));
            if (!currentBuildIsPrerelease)
            {
                return latestStable;
            }

            GithubRelease latestPrerelease = SelectNewest(
                published.Where(IsPrerelease));
            if (latestPrerelease is null)
            {
                return latestStable;
            }

            if (latestStable is not null &&
                GetReleaseDate(latestStable) > GetReleaseDate(latestPrerelease))
            {
                return latestStable;
            }

            return latestPrerelease;
        }

        public static bool ShouldUpdate(
            GithubRelease selectedRelease,
            string currentVersionText,
            bool currentBuildIsPrerelease,
            string installedReleaseTag)
        {
            if (selectedRelease is null)
            {
                return false;
            }

            bool markerMatches = !string.IsNullOrWhiteSpace(installedReleaseTag) &&
                string.Equals(installedReleaseTag.Trim(), selectedRelease.TagName,
                    StringComparison.OrdinalIgnoreCase);
            if (markerMatches && IsPrerelease(selectedRelease))
            {
                return false;
            }

            if (markerMatches &&
                TryParseReleaseVersion(currentVersionText, out Version markedCurrentVersion) &&
                TryParseReleaseVersion(selectedRelease.TagName, out Version markedReleaseVersion) &&
                NormalizeVersion(markedCurrentVersion) >= NormalizeVersion(markedReleaseVersion))
            {
                return false;
            }

            if (currentBuildIsPrerelease)
            {
                // A prerelease without a marker predates channel-aware updates. Update it
                // once so the updater can record the exact release tag it installed.
                if (IsPrerelease(selectedRelease))
                {
                    if (TryParseViiperPrereleaseTag(installedReleaseTag,
                            out int currentPhase, out Version currentOrdinal))
                    {
                        if (TryParseViiperPrereleaseTag(selectedRelease.TagName,
                                out int candidatePhase, out Version candidateOrdinal))
                        {
                            return candidatePhase > currentPhase ||
                                candidatePhase == currentPhase && candidateOrdinal > currentOrdinal;
                        }

                        // A future numeric tag can leave this named channel
                        // only with a demonstrably newer binary version. An
                        // unknown suffix/name has no safe automatic ordering.
                        return TryParseReleaseVersion(currentVersionText, out Version currentBinary) &&
                            TryParseNumericReleaseTag(selectedRelease.TagName, out Version candidateBinary) &&
                            NormalizeVersion(candidateBinary) > NormalizeVersion(currentBinary);
                    }

                    if (TryParseNumericReleaseTag(installedReleaseTag, out Version installedNumeric))
                    {
                        // After leaving the named RC channel, do not fall back
                        // to its legacy bootstrap rule. Different suffixes at
                        // one binary version are not ordered by this policy.
                        return TryParseNumericReleaseTag(selectedRelease.TagName, out Version candidateNumeric) &&
                            TryParseReleaseVersion(currentVersionText, out Version runningNumeric) &&
                            candidateNumeric > installedNumeric &&
                            candidateNumeric > NormalizeVersion(runningNumeric);
                    }
                    return string.IsNullOrWhiteSpace(installedReleaseTag);
                }

                // A stable release can replace the prerelease at the same numeric version,
                // but never downgrade a manually installed prerelease with a higher version.
                return TryParseReleaseVersion(currentVersionText, out Version prereleaseVersion) &&
                    TryParseReleaseVersion(selectedRelease.TagName, out Version stableVersion) &&
                    NormalizeVersion(prereleaseVersion) <= NormalizeVersion(stableVersion);
            }

            if (IsPrerelease(selectedRelease))
            {
                return false;
            }

            return TryParseReleaseVersion(currentVersionText, out Version currentVersion) &&
                TryParseReleaseVersion(selectedRelease.TagName, out Version selectedVersion) &&
                NormalizeVersion(currentVersion) < NormalizeVersion(selectedVersion);
        }

        private static Version NormalizeVersion(Version version) => new(
            version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

        private static bool TryParseNumericReleaseTag(string tag, out Version version)
        {
            version = null;
            Match match = numericReleaseTagRegex.Match(tag?.Trim() ?? "");
            if (!match.Success || !Version.TryParse(match.Groups["number"].Value, out Version parsed))
                return false;
            version = NormalizeVersion(parsed);
            return true;
        }

        private static bool TryParseViiperPrereleaseTag(string tag,
            out int phase, out Version ordinal)
        {
            phase = 0;
            ordinal = null;
            Match match = viiperPrereleaseTagRegex.Match(tag?.Trim() ?? "");
            if (!match.Success) return false;
            phase = string.Equals(match.Groups["phase"].Value, "RC",
                StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            string number = match.Groups["number"].Value;
            // The first historical beta is literally VIIPERBeta; RCs have
            // always carried a number. Do not guess other missing ordinals.
            if (number.Length == 0)
            {
                if (phase != 0) return false;
                number = "1";
            }
            if (!number.Contains('.')) number += ".0";
            if (!Version.TryParse(number, out Version parsed)) return false;
            ordinal = NormalizeVersion(parsed);
            return true;
        }

        public static bool TryParseReleaseVersion(string versionText, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return false;
            }

            Match match = Regex.Match(versionText, @"\d+(?:\.\d+){1,3}");
            return match.Success && Version.TryParse(match.Value, out version);
        }

        private static GithubRelease SelectNewest(IEnumerable<GithubRelease> releases)
        {
            return releases
                .OrderByDescending(GetReleaseDate)
                .ThenByDescending(release =>
                    TryParseReleaseVersion(release.TagName, out Version version) ?
                        version : new Version(0, 0, 0))
                .FirstOrDefault();
        }
    }
}
