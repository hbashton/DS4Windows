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
                markedCurrentVersion >= markedReleaseVersion)
            {
                return false;
            }

            if (currentBuildIsPrerelease)
            {
                // A prerelease without a marker predates channel-aware updates. Update it
                // once so the updater can record the exact release tag it installed.
                if (IsPrerelease(selectedRelease))
                {
                    return true;
                }

                // A stable release can replace the prerelease at the same numeric version,
                // but never downgrade a manually installed prerelease with a higher version.
                return TryParseReleaseVersion(currentVersionText, out Version prereleaseVersion) &&
                    TryParseReleaseVersion(selectedRelease.TagName, out Version stableVersion) &&
                    prereleaseVersion <= stableVersion;
            }

            if (IsPrerelease(selectedRelease))
            {
                return false;
            }

            return TryParseReleaseVersion(currentVersionText, out Version currentVersion) &&
                TryParseReleaseVersion(selectedRelease.TagName, out Version selectedVersion) &&
                currentVersion < selectedVersion;
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
