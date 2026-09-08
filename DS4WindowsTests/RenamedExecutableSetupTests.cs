using System;
using System.IO;
using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class RenamedExecutableSetupTests
{
    private string root;
    private string package;
    private string extras;
    private string setup;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "DS4WindowsTests", Guid.NewGuid().ToString("N"));
        package = Directory.CreateDirectory(Path.Combine(root, "portable")).FullName;
        extras = Directory.CreateDirectory(Path.Combine(package, "extras")).FullName;
        setup = Directory.CreateDirectory(Path.Combine(root, "protected-fixture")).FullName;
        // Sentinel files are never executed; exercise the real staging loop only.
        File.WriteAllText(Path.Combine(package, "DS4Windows.runtimeconfig.json"), "config");
        File.WriteAllText(Path.Combine(package, "DS4Windows.deps.json"), "deps");
        File.WriteAllText(Path.Combine(package, ".ds4windows-managed-files.txt"),
            "DS4Windows.exe\nDS4Windows.runtimeconfig.json\nDS4Windows.deps.json\n");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    [DataTestMethod]
    [DataRow("DS4Windows.exe")]
    [DataRow("My Controller.exe")]
    public void StagesTheExactAdjacentHostUnderCanonicalPackageName(string hostName)
    {
        string host = Path.Combine(package, hostName);
        File.WriteAllText(host, "same apphost bytes");
        string staged = ViiperSetupManager.StageInstallerPackageFiles(extras, setup, host);
        Assert.AreEqual("same apphost bytes", File.ReadAllText(Path.Combine(staged, "DS4Windows.exe")));
        Assert.IsTrue(File.Exists(host), "Staging must not rename or delete the user's executable.");
        CollectionAssert.AreEqual(new[] { "DS4Windows.exe", "DS4Windows.runtimeconfig.json", "DS4Windows.deps.json" },
            File.ReadAllLines(Path.Combine(staged, ".ds4windows-managed-files.txt")));
    }

    [TestMethod]
    public void AliasCreationUsesNormalDotnetSidecarNames()
    {
        string executable = Path.Combine(package, "DS4Windows.exe");
        File.WriteAllText(executable, "apphost");
        ExecutableAliasFiles.Create(executable, "Controller Companion");
        Assert.AreEqual("apphost", File.ReadAllText(Path.Combine(package, "Controller Companion.exe")));
        Assert.AreEqual("config", File.ReadAllText(Path.Combine(package, "Controller Companion.runtimeconfig.json")));
        Assert.AreEqual("deps", File.ReadAllText(Path.Combine(package, "Controller Companion.deps.json")));
    }

    [TestMethod]
    public void MissingSidecarDoesNotLeaveAPartialAlias()
    {
        string executable = Path.Combine(package, "DS4Windows.exe");
        File.WriteAllText(executable, "apphost");
        File.Delete(Path.Combine(package, "DS4Windows.deps.json"));
        Assert.ThrowsException<FileNotFoundException>(() => ExecutableAliasFiles.Create(executable, "Alias"));
        Assert.IsFalse(File.Exists(Path.Combine(package, "Alias.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(package, "Alias.runtimeconfig.json")));
    }

    [TestMethod]
    public void CanonicalExecutableIsNotOverriddenByAnotherAdjacentHost()
    {
        string host = Path.Combine(package, "Alias.exe");
        File.WriteAllText(host, "different bytes");
        File.WriteAllText(Path.Combine(package, "DS4Windows.exe"), "canonical bytes");
        string staged = ViiperSetupManager.StageInstallerPackageFiles(extras, setup, host);
        Assert.AreEqual("canonical bytes", File.ReadAllText(Path.Combine(staged, "DS4Windows.exe")),
            "The later exact hash check must reject mismatched hosts, not silently replace the canonical package.");
    }

    [TestMethod]
    public void MissingDllIsNotSubstitutedWithTheHost()
    {
        string host = Path.Combine(package, "Alias.exe");
        File.WriteAllText(host, "apphost");
        File.AppendAllText(Path.Combine(package, ".ds4windows-managed-files.txt"), "DS4Windows.dll\n");
        Assert.ThrowsException<InvalidOperationException>(() =>
            ViiperSetupManager.StageInstallerPackageFiles(extras, setup, host));
        Assert.IsFalse(File.Exists(Path.Combine(setup, "package", "DS4Windows.dll")));
    }

    [TestMethod]
    public void HostOutsideExactPackageIsRejected()
    {
        string host = Path.Combine(root, "Alias.exe");
        File.WriteAllText(host, "unrelated host");
        Assert.ThrowsException<InvalidOperationException>(() =>
            ViiperSetupManager.StageInstallerPackageFiles(extras, setup, host));
        Assert.IsFalse(Directory.Exists(Path.Combine(setup, "package")));
    }

    [DataTestMethod]
    [DataRow("../escaped.exe")]
    [DataRow("DS4Windows.exe\nDS4Windows.exe")]
    [DataRow("C:/escaped.exe")]
    public void RenamedHostDoesNotRelaxManifestPathValidation(string entries)
    {
        string host = Path.Combine(package, "Alias.exe");
        File.WriteAllText(host, "apphost");
        File.WriteAllText(Path.Combine(package, ".ds4windows-managed-files.txt"), entries);
        Assert.ThrowsException<InvalidOperationException>(() =>
            ViiperSetupManager.StageInstallerPackageFiles(extras, setup, host));
    }

    [DataTestMethod]
    [DataRow("../escape")]
    [DataRow("C:\\escape")]
    [DataRow("safe:stream")]
    [DataRow("DS4Windows")]
    [DataRow("con")]
    [DataRow("NUL.txt")]
    [DataRow("LPT1")]
    [DataRow("trailing.")]
    [DataRow(" trailing")]
    [DataRow("")]
    public void InvalidOrReservedAliasNeverCreatesFiles(string name)
    {
        string executable = Path.Combine(package, "DS4Windows.exe");
        File.WriteAllText(executable, "apphost");
        string[] before = Directory.GetFiles(package);
        Assert.ThrowsException<ArgumentException>(() => ExecutableAliasFiles.Create(executable, name));
        CollectionAssert.AreEquivalent(before, Directory.GetFiles(package));
    }

    [TestMethod]
    public void AliasCollisionPreservesAllPreexistingFiles()
    {
        string executable = Path.Combine(package, "DS4Windows.exe");
        File.WriteAllText(executable, "apphost");
        string collision = Path.Combine(package, "Alias.exe");
        File.WriteAllText(collision, "user-owned file");
        Assert.ThrowsException<IOException>(() => ExecutableAliasFiles.Create(executable, "Alias"));
        Assert.AreEqual("user-owned file", File.ReadAllText(collision));
        Assert.IsFalse(File.Exists(Path.Combine(package, "Alias.runtimeconfig.json")));
        Assert.IsFalse(File.Exists(Path.Combine(package, "Alias.deps.json")));
    }

    [TestMethod]
    public void HandRenamedHostCanCreateAnAliasUsingCanonicalSidecars()
    {
        string executable = Path.Combine(package, "Hand Renamed.exe");
        File.WriteAllText(executable, "apphost");
        ExecutableAliasFiles.Create(executable, "Second Alias");
        Assert.AreEqual("config", File.ReadAllText(Path.Combine(package, "Second Alias.runtimeconfig.json")));
        Assert.AreEqual("deps", File.ReadAllText(Path.Combine(package, "Second Alias.deps.json")));
    }

    [TestMethod]
    public void AliasCleanupOnlyRemovesMatchingCopies()
    {
        string executable = Path.Combine(package, "DS4Windows.exe");
        File.WriteAllText(executable, "apphost");
        ExecutableAliasFiles.Create(executable, "Alias");
        Assert.IsTrue(ExecutableAliasFiles.RemoveOwned(executable, "Alias"));
        Assert.IsFalse(File.Exists(Path.Combine(package, "Alias.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(package, "Alias.runtimeconfig.json")));
        Assert.IsFalse(File.Exists(Path.Combine(package, "Alias.deps.json")));
        Assert.AreEqual("apphost", File.ReadAllText(executable));
    }

    [TestMethod]
    public void ChangedAliasOrCurrentExecutableIsNeverCleanedUp()
    {
        string executable = Path.Combine(package, "DS4Windows.exe");
        File.WriteAllText(executable, "apphost");
        ExecutableAliasFiles.Create(executable, "Alias");
        File.WriteAllText(Path.Combine(package, "Alias.deps.json"), "user change");
        Assert.IsFalse(ExecutableAliasFiles.RemoveOwned(executable, "Alias"));
        Assert.IsTrue(File.Exists(Path.Combine(package, "Alias.exe")));
        Assert.IsFalse(ExecutableAliasFiles.RemoveOwned(executable, "DS4Windows"));
        Assert.IsTrue(File.Exists(executable));
        Assert.IsFalse(ExecutableAliasFiles.RemoveOwned(Path.Combine(package, "Alias.exe"), "Alias"));
    }
}
