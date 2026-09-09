using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DS4Windows.Bootstrapper;
using WixToolset.BootstrapperApplicationApi;

namespace DS4WindowsTests;

[TestClass]
public sealed class InstallerUpgradeRegistrationTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [TestMethod]
    public void ManagedBundleAndHiddenMsiKeepTheirExistingMachineWideUpgradeFamilies()
    {
        XElement package = Load("DS4Windows.Package/Product.wxs").Root.Element(Wix + "Package");
        XElement bundle = Load("DS4Windows.Bundle/Bundle.wxs").Root.Element(Wix + "Bundle");
        Assert.AreEqual("65E808E3-D35A-4825-AE11-8D9415F16446", (string)package.Attribute("UpgradeCode"));
        Assert.AreEqual("BC70CCB1-AD65-42A0-B468-8A7278A37A62", (string)bundle.Attribute("UpgradeCode"));
        Assert.AreEqual("perMachine", (string)package.Attribute("Scope"));
        Assert.AreEqual("DS4WindowsManagedV2", (string)bundle.Attribute("Tag"));
        XElement embeddedMsi = bundle.Descendants(Wix + "MsiPackage")
            .Single(element => (string)element.Attribute("Id") == "DS4WindowsMsi");
        Assert.AreEqual("no", (string)embeddedMsi.Attribute("Visible"),
            "The bundle owns the visible Apps & Features entry; its MSI must not add another.");
    }

    [TestMethod]
    public void MsiMajorUpgradeHandlesRepeatedFirstThreeVersionFields()
    {
        XElement upgrade = Load("DS4Windows.Package/Product.wxs")
            .Descendants(Wix + "MajorUpgrade").Single();
        Assert.AreEqual("yes", (string)upgrade.Attribute("AllowSameVersionUpgrades"),
            "Windows Installer ignores the fourth product-version field; RC rebuilds still replace the old MSI.");
        Assert.AreNotEqual("yes", (string)upgrade.Attribute("AllowDowngrades"));
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void UninstallPreflightRemainsAvailableAfterOriginalInstallerIsGone(
        bool repairFirst, bool invokedByUpgrade)
    {
        XElement chain = Load("DS4Windows.Bundle/Bundle.wxs").Descendants(Wix + "Chain").Single();
        LaunchAction initialAction = repairFirst ? LaunchAction.Repair : LaunchAction.Install;
        RelationType uninstallRelation = invokedByUpgrade ? RelationType.Upgrade : RelationType.None;
        var cachedHelpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uninstallHelpers = new List<string>();

        foreach (XElement package in chain.Elements())
        {
            string id = (string)package.Attribute("Id");
            if (!UninstallHelperPackagePlan.TryGetState(id, initialAction,
                    RelationType.None, false, out RequestState initialState)) continue;
            Assert.AreEqual("ExePackage", package.Name.LocalName);
            Assert.AreEqual("$(var.SetupActionsPath)", (string)package.Attribute("SourceFile"));
            Assert.AreEqual("0", (string)package.Attribute("DetectCondition"));
            Assert.AreEqual("yes", (string)package.Attribute("Permanent"));
            Assert.AreEqual(RequestState.Cache, initialState,
                $"{id} must be cached without execution before the downloaded bundle can disappear.");
            cachedHelpers.Add(id);

            Assert.IsTrue(UninstallHelperPackagePlan.TryGetState(id, LaunchAction.Uninstall,
                uninstallRelation, false, out RequestState uninstallState));
            if (uninstallState == RequestState.Present) uninstallHelpers.Add(id);
        }

        CollectionAssert.AreEquivalent(new[] { "PostUninstallCleanup", "ViiperUsbipUninstall",
            "CloseRunningApplicationsForUninstall" }, cachedHelpers.ToArray());
        Assert.IsTrue(uninstallHelpers.All(cachedHelpers.Contains),
            "An outgoing uninstall must not need the original attached container for an uncached helper.");
        Assert.AreEqual("CloseRunningApplicationsForUninstall",
            (string)chain.Elements().Last().Attribute("Id"),
            "The reverse uninstall chain must quiesce before touching MSI-owned files.");
        CollectionAssert.AreEquivalent(invokedByUpgrade
            ? new[] { "CloseRunningApplicationsForUninstall" }
            : cachedHelpers.ToArray(), uninstallHelpers.ToArray(),
            "A related upgrade must never execute the direct shared-infrastructure uninstaller.");

        string bootstrapper = File.ReadAllText(SourcePath(
            "DS4Windows.Bootstrapper/InstallerApplication.cs"));
        StringAssert.Contains(bootstrapper, "UninstallHelperPackagePlan.TryGetState(e.PackageId,");
        StringAssert.Contains(bootstrapper, "e.State = helperState;");
    }

    private static XDocument Load(string path) => XDocument.Load(SourcePath(path));

    private static string SourcePath(string path, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "..", "installer", path));
}
