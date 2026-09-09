using DS4Windows;
using System.IO;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyJoyConPairStoreTests
{
    private string folder;
    private string path;
    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), "ds4w-original-joycon-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "pairs.json");
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    [TestMethod]
    public void RemembersExactHalvesAndPreferredProfileOwnerAcrossReload()
    {
        var store = new LegacyJoyConPairStore(path);
        store.Remember("L1", "R1", false);
        store.Remember("L2", "R2", true);
        var loaded = new LegacyJoyConPairStore(path);
        Assert.AreEqual(2, loaded.Pairs.Count);
        Assert.AreEqual(new LegacyJoyConSavedPair("L1", "R1", false), loaded.Pairs[0]);
        loaded.Forget("L1", "R1");
        Assert.AreEqual(1, new LegacyJoyConPairStore(path).Pairs.Count);
    }

    [TestMethod]
    public void RelinkingEitherMemberRetiresOnlyConflictingPairs()
    {
        var store = new LegacyJoyConPairStore(path);
        store.Remember("L1", "R1", true);
        store.Remember("L2", "R2", true);
        store.Remember("L3", "R3", true);
        store.Remember("L1", "R2", false);
        Assert.AreEqual(2, store.Pairs.Count);
        Assert.AreEqual(new LegacyJoyConSavedPair("L3", "R3", true), store.Pairs[0]);
        Assert.AreEqual(new LegacyJoyConSavedPair("L1", "R2", false), store.Pairs[1]);
    }

    [TestMethod]
    public void DamagedStoreIsPreservedAndDoesNotCrashDiscovery()
    {
        File.WriteAllText(path, "not json");
        var store = new LegacyJoyConPairStore(path);
        Assert.AreEqual(0, store.Pairs.Count);
        Assert.ThrowsException<InvalidDataException>(() => store.Remember("L", "R", true));
        store.Forget("L", "R");
        Assert.AreEqual("not json", File.ReadAllText(path));
    }

    [DataTestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[{\"Left\":\"L\",\"Right\":\"L\",\"PreferLeft\":true}]")]
    [DataRow("[{\"Left\":\"\",\"Right\":\"R\",\"PreferLeft\":true}]")]
    public void SemanticallyInvalidJsonIsPreservedWithoutPreventingStartup(string contents)
    {
        File.WriteAllText(path, contents);
        var store = new LegacyJoyConPairStore(path);
        Assert.AreEqual(0, store.Pairs.Count);
        Assert.ThrowsException<InvalidDataException>(() => store.Remember("L", "R", true));
        Assert.AreEqual(contents, File.ReadAllText(path));
    }

    [TestMethod]
    public void OversizedStoreDoesNotPreventStartupOrGetOverwritten()
    {
        string contents = new string(' ', 128 * 1024 + 1);
        File.WriteAllText(path, contents);
        var store = new LegacyJoyConPairStore(path);
        Assert.AreEqual(0, store.Pairs.Count);
        store.Forget("L", "R");
        Assert.AreEqual(contents, File.ReadAllText(path));
    }

    [TestMethod]
    public void ForgettingAnUnsavedSessionPairNeedsNoWritableSettingsPath()
    {
        // A regular file at the parent path prevents creation of the optional
        // settings directory, matching a failed session-pair save.
        string blockedDirectory = Path.Combine(folder, "not-a-directory");
        File.WriteAllText(blockedDirectory, "keep");
        var store = new LegacyJoyConPairStore(Path.Combine(blockedDirectory, "pairs.json"));
        Assert.ThrowsException<IOException>(() => store.Remember("L", "R", true));
        store.Forget("L", "R");
        Assert.AreEqual(0, store.Pairs.Count);
        Assert.AreEqual("keep", File.ReadAllText(blockedDirectory));
    }

    [TestMethod]
    public void FailedRemovalOfAnActuallySavedPairDoesNotForgetItInMemory()
    {
        var store = new LegacyJoyConPairStore(path);
        store.Remember("L", "R", true);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.Forget("L", "R"); Assert.Fail("A locked saved pair cannot be forgotten on disk."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        Assert.AreEqual(new LegacyJoyConSavedPair("L", "R", true), store.Pairs.Single());
        Assert.AreEqual(1, new LegacyJoyConPairStore(path).Pairs.Count);
    }
}
