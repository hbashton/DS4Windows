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

        [DataTestMethod]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.3", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.4", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.5", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.5.1", true)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.5", false)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.5.1", false)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.5.2", true)]
        [DataRow("VIIPERRC4.5.2", "VIIPERRC4.5.1", false)]
        [DataRow("VIIPERRC4.5.2", "VIIPERRC4.5.2", false)]
        [DataRow("VIIPERRC4.5.2", "VIIPERRC4.5.3", true)]
        [DataRow("VIIPERRC4.5.3", "VIIPERRC4.5.2", false)]
        [DataRow("VIIPERRC4.5.3", "VIIPERRC4.5.3", false)]
        [DataRow("VIIPERRC4.5.3", "VIIPERRC4.5.4", true)]
        [DataRow("VIIPERRC4.5.4", "VIIPERRC4.5.3", false)]
        [DataRow("VIIPERRC4.5.4", "VIIPERRC4.5.4", false)]
        [DataRow("VIIPERRC4.5.4", "VIIPERRC4.5.5", true)]
        [DataRow("VIIPERRC4.5.5", "VIIPERRC4.5.4", false)]
        [DataRow("VIIPERRC4.5.5", "VIIPERRC4.5.5", false)]
        [DataRow("VIIPERRC4.5.5", "VIIPERRC4.5.6", true)]
        [DataRow("VIIPERRC4.5.6", "VIIPERRC4.5.5", false)]
        [DataRow("VIIPERRC4.5.6", "VIIPERRC4.5.6", false)]
        [DataRow("VIIPERRC4.5.6", "VIIPERRC4.5.7", true)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.6", true)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.6", true)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.10", true)]
        [DataRow("VIIPERRC4.10", "VIIPERRC4.9", false)]
        [DataRow("VIIPERRC4", "VIIPERRC4.0", false)]
        [DataRow("VIIPERRC4", "VIIPERRC4.1", true)]
        [DataRow("VIIPERRC4.5", "VIIPERBeta9", false)]
        [DataRow("VIIPERBeta9", "VIIPERRC1", true)]
        [DataRow("VIIPERBeta8", "VIIPERBeta7", false)]
        [DataRow("VIIPERBeta", "VIIPERBeta2", true)]
        [DataRow(" viiperrc4.5 ", "VIIPERRC4.3", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.5-hotfix-unknown", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC9999999999999999999", false)]
        public void NamedPrereleaseOrderingCannotRollBackTesters(
            string installed, string candidate, bool update)
        {
            // A newly published/re-published old release is still a downgrade.
            var selected = Prerelease(candidate, "2026-09-09T00:00:00Z");
            Assert.AreEqual(update, ReleaseChannelPolicy.ShouldUpdate(
                selected, "5.0.5.0", true, installed));
        }

        [DataTestMethod]
        [DataRow("v5.0.4.0-beta1", false)]
        [DataRow("v5.0.5.0-beta1", false)]
        [DataRow("v5.0.6.0-rc1", true)]
        public void NamedCandidateCanOnlyMoveToProvablyNewerNumericPrerelease(
            string candidate, bool update)
        {
            Assert.AreEqual(update, ReleaseChannelPolicy.ShouldUpdate(
                Prerelease(candidate, "2026-09-09T00:00:00Z"),
                "5.0.5.0", true, "VIIPERRC4.5"));
        }

        [TestMethod]
        public void Rc45DoesNotOfferOlderPublicRc43ButCanPromoteToSameVersionStable()
        {
            var selected = ReleaseChannelPolicy.SelectPreferredRelease(Releases(
                Stable("v4.0.3", "2026-07-20T00:00:00Z"),
                Prerelease("VIIPERRC4.3", "2026-08-21T00:00:00Z")), true);
            Assert.AreEqual("VIIPERRC4.3", selected.TagName);
            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(selected,
                "5.0.5.0", true, "VIIPERRC4.5"));
            Assert.IsTrue(ReleaseChannelPolicy.ShouldUpdate(
                Stable("v5.0.5.0", "2026-09-10T00:00:00Z"),
                "5.0.5.0", true, "VIIPERRC4.5"));
            Assert.IsTrue(ReleaseChannelPolicy.ShouldUpdate(
                Stable("v5.0.5", "2026-09-10T00:00:00Z"),
                "5.0.5.0", true, "VIIPERRC4.5"));
            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(
                Stable("v5.0.5.0", "2026-09-10T00:00:00Z"),
                "5.0.5", false, "v5.0.5"));
        }

        [DataTestMethod]
        [DataRow("v5.0.6.0-rc1", "VIIPERRC4.3", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.5.0-rc9", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.6-rc2", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.7.0-rc1", true)]
        [DataRow(" v5.0.6.0-rc1 ", " v5.0.7.0-rc1 ", true)]
        [DataRow("v5.0.6.0-rc1", "v5.0.9999999999999-rc1", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.7.0.0.0-rc1", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.7.0 broken", false)]
        [DataRow("unrecognized-preview", "VIIPERRC4.3", false)]
        [DataRow("VIIPERRC4.5-hotfix-unknown", "VIIPERRC4.3", false)]
        public void MarkedPrereleasesDoNotFallBackToLegacyUnconditionalUpdates(
            string installed, string candidate, bool update)
        {
            Assert.AreEqual(update, ReleaseChannelPolicy.ShouldUpdate(
                Prerelease(candidate, "2026-09-09T00:00:00Z"),
                "5.0.6.0", true, installed));
        }

        [DataTestMethod]
        [DataRow("v5.0.6.0.0.0-rc1")]
        [DataRow("v5.0.6.0 broken")]
        public void NamedPrereleaseRejectsPartiallyParsedNumericCandidate(string candidate)
        {
            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(
                Prerelease(candidate, "2026-09-09T00:00:00Z"),
                "5.0.5.0", true, "VIIPERRC4.5"));
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
