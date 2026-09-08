using DS4Windows.Bootstrapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WixToolset.BootstrapperApplicationApi;

namespace DS4Windows.Tests
{
    [TestClass]
    public class UninstallHelperPackagePlanTests
    {
        private static readonly string[] Helpers =
        {
            "PostUninstallCleanup", "ViiperUsbipUninstall",
            "CloseRunningApplicationsForUninstall",
        };

        [TestMethod]
        public void InstallAndRepairCacheEveryUninstallHelperWithoutExecutingIt()
        {
            foreach (var action in new[] { LaunchAction.Install, LaunchAction.Repair })
            foreach (var relation in Enum.GetValues<RelationType>())
            foreach (string package in Helpers)
            {
                AssertPlan(package, action, relation, false, RequestState.Cache);
            }
        }

        [TestMethod]
        public void DirectUninstallExecutesEveryHelper()
        {
            foreach (string package in Helpers)
                AssertPlan(package, LaunchAction.Uninstall, RelationType.None,
                    false, RequestState.Present);
        }

        [TestMethod]
        public void UpgradeUninstallQuiescesButNeverRemovesSharedInfrastructure()
        {
            AssertPlan(Helpers[0], LaunchAction.Uninstall, RelationType.Upgrade,
                false, RequestState.None);
            AssertPlan(Helpers[1], LaunchAction.Uninstall, RelationType.Upgrade,
                false, RequestState.None);
            AssertPlan(Helpers[2], LaunchAction.Uninstall, RelationType.Upgrade,
                false, RequestState.Present);
        }

        [TestMethod]
        public void RecoveryPassNeverPlansUninstallHelpers()
        {
            foreach (var action in Enum.GetValues<LaunchAction>())
            foreach (var relation in Enum.GetValues<RelationType>())
            foreach (string package in Helpers)
                AssertPlan(package, action, relation, true, RequestState.None);
        }

        [TestMethod]
        public void UnrelatedActionsDoNotExecuteOrAcquireHelpers()
        {
            foreach (var action in Enum.GetValues<LaunchAction>())
            {
                if (action is LaunchAction.Install or LaunchAction.Repair or LaunchAction.Uninstall)
                    continue;
                foreach (string package in Helpers)
                    AssertPlan(package, action, RelationType.None, false, RequestState.None);
            }
        }

        [TestMethod]
        public void UnrelatedPackagesRemainUnderExistingPlannerOwnership()
        {
            foreach (string package in new[] { null, "", "ViiperUsbipSetup",
                "CloseRunningApplications", "DS4WindowsMsi", "HidHide", "FakerInput" })
                Assert.IsFalse(UninstallHelperPackagePlan.TryGetState(package,
                    LaunchAction.Install, RelationType.None, false, out _));
        }

        [TestMethod]
        public void PackageIdentityRemainsCaseInsensitive()
        {
            AssertPlan("postuninstallcleanup", LaunchAction.Install,
                RelationType.None, false, RequestState.Cache);
        }

        private static void AssertPlan(string package, LaunchAction action,
            RelationType relation, bool recovery, RequestState expected)
        {
            Assert.IsTrue(UninstallHelperPackagePlan.TryGetState(package,
                action, relation, recovery, out var actual));
            Assert.AreEqual(expected, actual,
                $"{package}: {action}, {relation}, recovery={recovery}");
        }
    }
}
