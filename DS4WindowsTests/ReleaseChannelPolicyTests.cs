using DS4Windows;
using DS4WinWPF.ApiDTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace DS4WindowsTests
{
    [TestClass]
    public class ReleaseChannelPolicyTests
    {
        [TestMethod]
        public void StableBuildOnlyFollowsStableReleases()
        {
            GithubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                Releases(
                    Stable("v4.0.3", "2026-07-20T00:00:00Z"),
                    Prerelease("VIIPERBeta7", "2026-07-22T00:00:00Z")),
                currentBuildIsPrerelease: false);

            Assert.AreEqual("v4.0.3", selected.TagName);
        }

        [TestMethod]
        public void PrereleaseBuildFollowsNewestPrereleaseWhenItIsNewer()
        {
            GithubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                Releases(
                    Stable("v4.0.3", "2026-07-20T00:00:00Z"),
                    Prerelease("VIIPERBeta7", "2026-07-22T00:00:00Z")),
                currentBuildIsPrerelease: true);

            Assert.AreEqual("VIIPERBeta7", selected.TagName);
            Assert.IsTrue(ReleaseChannelPolicy.ShouldUpdate(
                selected, "5.0.0.0", true, installedReleaseTag: null));
        }

        [TestMethod]
        public void NewerStableReleaseWinsForPrereleaseBuild()
        {
            GithubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                Releases(
                    Stable("v4.1.0", "2026-07-23T00:00:00Z"),
                    Prerelease("VIIPERBeta7", "2026-07-22T00:00:00Z")),
                currentBuildIsPrerelease: true);

            Assert.AreEqual("v4.1.0", selected.TagName);
        }

        [TestMethod]
        public void EqualReleaseDatesKeepPrereleaseBuildOnPrereleaseChannel()
        {
            GithubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                Releases(
                    Stable("v4.1.0", "2026-07-23T00:00:00Z"),
                    Prerelease("VIIPERBeta7", "2026-07-23T00:00:00Z")),
                currentBuildIsPrerelease: true);

            Assert.AreEqual("VIIPERBeta7", selected.TagName);
        }

        [TestMethod]
        public void InstalledReleaseMarkerPreventsRepeatedPrereleaseDownload()
        {
            GithubRelease selected = Prerelease(
                "VIIPERBeta7", "2026-07-22T00:00:00Z");

            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(
                selected, "5.0.0.0", true, "viiperbeta7"));
        }

        [TestMethod]
        public void StableReleaseNeverDowngradesHigherPrereleaseVersion()
        {
            GithubRelease selected = Stable(
                "v4.1.0", "2026-07-24T00:00:00Z");

            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(
                selected, "5.0.0", true, installedReleaseTag: null));
        }

        [TestMethod]
        public void StaleStableMarkerDoesNotHideAnOlderBinary()
        {
            GithubRelease selected = Stable(
                "v4.1.0", "2026-07-24T00:00:00Z");

            Assert.IsTrue(ReleaseChannelPolicy.ShouldUpdate(
                selected, "4.0.0", false, installedReleaseTag: "v4.1.0"));
        }

        [TestMethod]
        public void PrereleaseNameIsRecognizedWhenGithubFlagIsIncorrect()
        {
            GithubRelease mislabeled = Stable(
                "3.9.9Beta3Hotfix", "2026-07-22T00:00:00Z");

            Assert.IsTrue(ReleaseChannelPolicy.IsPrerelease(mislabeled));
            Assert.IsTrue(ReleaseChannelPolicy.IsPrereleaseBuild(
                "5.0.0.0 DualSense Beta"));
        }

        [TestMethod]
        public void InstalledPrereleaseMarkerKeepsChangelogOnPrereleaseChannel()
        {
            Assert.IsTrue(ReleaseChannelPolicy.IsPrereleaseInstall(
                "5.0.2.0", "VIIPERRC4.2"));
            Assert.IsFalse(ReleaseChannelPolicy.IsPrereleaseInstall(
                "5.0.2.0", "v5.0.2"));
        }

        [TestMethod]
        public void DraftReleasesAreNeverSelected()
        {
            GithubRelease draft = Prerelease(
                "VIIPERBeta8", "2026-07-24T00:00:00Z");
            draft.Draft = true;

            GithubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                Releases(
                    draft,
                    Prerelease("VIIPERBeta7", "2026-07-23T00:00:00Z")),
                currentBuildIsPrerelease: true);

            Assert.AreEqual("VIIPERBeta7", selected.TagName);
        }

        private static GithubRelease[] Releases(params GithubRelease[] releases)
        {
            return releases;
        }

        private static GithubRelease Stable(string tag, string publishedAt)
        {
            return Release(tag, publishedAt, prerelease: false);
        }

        private static GithubRelease Prerelease(string tag, string publishedAt)
        {
            return Release(tag, publishedAt, prerelease: true);
        }

        private static GithubRelease Release(
            string tag,
            string publishedAt,
            bool prerelease)
        {
            return new GithubRelease
            {
                TagName = tag,
                PreRelease = prerelease,
                PublishedAt = DateTimeOffset.Parse(publishedAt),
            };
        }
    }
}
