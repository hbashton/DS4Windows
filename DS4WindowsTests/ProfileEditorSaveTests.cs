using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DS4Windows;
using DS4WinWPF;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ProfileEditorSaveTests
{
    private string directory, previousRoot, previousProfile, previousOlderProfile;
    private byte previousRumble;
    private const string Original = "<DS4Windows><RumbleBoost>77</RumbleBoost></DS4Windows>";

    [TestInitialize]
    public void Initialize()
    {
        directory = Directory.CreateTempSubdirectory("ds4w-editor-save-").FullName;
        Directory.CreateDirectory(Path.Combine(directory, "Profiles"));
        previousRoot = Global.appdatapath;
        previousProfile = Global.ProfilePath[0];
        previousOlderProfile = Global.OlderProfilePath[0];
        previousRumble = Global.store.rumble[0];
        Global.appdatapath = directory;
        Global.ProfilePath[0] = Global.OlderProfilePath[0] = "Original";
        Global.store.rumble[0] = 88;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Global.appdatapath = previousRoot;
        Global.ProfilePath[0] = previousProfile;
        Global.OlderProfilePath[0] = previousOlderProfile;
        Global.store.rumble[0] = previousRumble;
        foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void LockedSaveReportsFailureWithoutSavedEventsAndCanBeRetried()
    {
        string original = WriteProfile("Original", Original);
        var entity = new ProfileEntity { Name = "Original" };
        var editor = CreateEditor(entity);
        int saved = 0;
        entity.ProfileSaved += (_, _) => saved++;
        using (var held = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.IsFalse(editor.SaveProfileChanges("Original"));
            Assert.AreEqual(0, saved);
            Assert.AreEqual(Original, File.ReadAllText(original));
        }
        Assert.IsTrue(editor.SaveProfileChanges("Original"));
        Assert.AreEqual(1, saved);
        AssertSavedXml(original);
    }

    [TestMethod]
    public void ReadOnlySaveReportsFailureWithoutAnnouncingSuccess()
    {
        string original = WriteProfile("Original", Original);
        File.SetAttributes(original, FileAttributes.ReadOnly);
        var entity = new ProfileEntity { Name = "Original" };
        int saved = 0;
        entity.ProfileSaved += (_, _) => saved++;
        Assert.IsFalse(CreateEditor(entity).SaveProfileChanges("Original"));
        Assert.AreEqual(0, saved);
        Assert.AreEqual(Original, File.ReadAllText(original));
    }

    [TestMethod]
    public void RenameCollisionPreservesBothFilesNameSelectionAndEvents()
    {
        string original = WriteProfile("Original", Original);
        string collision = WriteProfile("Other", "other profile");
        var entity = new ProfileEntity { Name = "Original" };
        int events = 0;
        entity.NameChanged += (_, _) => events++;
        entity.ProfileSaved += (_, _) => events++;
        entity.ProfileDeleted += (_, _) => events++;
        Assert.IsFalse(CreateEditor(entity).SaveProfileChanges("Other"));
        Assert.AreEqual("Original", entity.Name);
        Assert.AreEqual("Original", Global.ProfilePath[0]);
        Assert.AreEqual("Original", Global.OlderProfilePath[0]);
        Assert.AreEqual(0, events);
        Assert.AreEqual(Original, File.ReadAllText(original));
        Assert.AreEqual("other profile", File.ReadAllText(collision));
    }

    [TestMethod]
    public void SuccessfulRenameUpdatesSelectionAndEventsAfterSavingWithoutDeletionEvent()
    {
        string original = WriteProfile("Original", Original);
        string target = ProfilePath("Renamed");
        var entity = new ProfileEntity { Name = "Original" };
        int renamed = 0, saved = 0, deleted = 0;
        entity.NameChanged += (_, _) => { renamed++; AssertSavedXml(target); };
        entity.ProfileSaved += (_, _) =>
        {
            saved++;
            Assert.AreEqual("Renamed", Global.ProfilePath[0]);
            AssertSavedXml(target);
        };
        entity.ProfileDeleted += (_, _) => deleted++;
        Assert.IsTrue(CreateEditor(entity).SaveProfileChanges("Renamed"));
        Assert.IsFalse(File.Exists(original));
        Assert.AreEqual("Renamed", entity.Name);
        Assert.AreEqual("Renamed", Global.OlderProfilePath[0]);
        Assert.AreEqual(1, renamed);
        Assert.AreEqual(1, saved);
        Assert.AreEqual(0, deleted);
    }

    [TestMethod]
    public void FailedOriginalRemovalKeepsOldSelectionAndReportsFailureWithBothCopiesPreserved()
    {
        string original = WriteProfile("Original", Original);
        var entity = new ProfileEntity { Name = "Original" };
        int events = 0;
        entity.NameChanged += (_, _) => events++;
        entity.ProfileSaved += (_, _) => events++;
        using var held = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.IsFalse(CreateEditor(entity).SaveProfileChanges("Renamed"));
        Assert.AreEqual("Original", entity.Name);
        Assert.AreEqual("Original", Global.ProfilePath[0]);
        Assert.AreEqual("Original", Global.OlderProfilePath[0]);
        Assert.AreEqual(0, events);
        Assert.AreEqual(Original, File.ReadAllText(original));
        AssertSavedXml(ProfilePath("Renamed"));
    }

    [TestMethod]
    public void NewProfileCollisionDoesNotOverwriteOrPublishCreatedEvent()
    {
        string target = WriteProfile("Other", Original);
        var editor = CreateEditor(null);
        int created = 0;
        editor.CreatedProfile += (_, _) => created++;
        Assert.IsFalse(editor.SaveProfileChanges("Other"));
        Assert.AreEqual(0, created);
        Assert.AreEqual("Original", Global.ProfilePath[0]);
        Assert.AreEqual(Original, File.ReadAllText(target));
    }

    [TestMethod]
    public void FailedNewProfileCanBeRetriedAndPublishesOnlyAfterSuccessfulWrite()
    {
        var editor = CreateEditor(null);
        int created = 0;
        editor.CreatedProfile += (_, name) => { created++; AssertSavedXml(ProfilePath(name)); };
        string profiles = Path.Combine(directory, "Profiles");
        Directory.Move(profiles, profiles + "-unavailable");
        Assert.IsFalse(editor.SaveProfileChanges("New"));
        Assert.AreEqual(0, created);
        Assert.AreEqual("Original", Global.ProfilePath[0]);
        Directory.Move(profiles + "-unavailable", profiles);
        Assert.IsTrue(editor.SaveProfileChanges("New"));
        Assert.AreEqual(1, created);
        Assert.AreEqual("New", Global.ProfilePath[0]);
        Assert.AreEqual("New", Global.OlderProfilePath[0]);
    }

    private string ProfilePath(string name) => Path.Combine(directory, "Profiles", name + ".xml");
    private string WriteProfile(string name, string xml)
    {
        string path = ProfilePath(name);
        File.WriteAllText(path, xml);
        return path;
    }
    private static void AssertSavedXml(string path) =>
        Assert.AreEqual("88", XDocument.Load(path).Root.Element("RumbleBoost").Value);

    private static ProfileEditor CreateEditor(ProfileEntity entity)
    {
        // Exercise the editor's real persistence/event path without its UI
        // constructor, controller service, device discovery or preview output.
        var editor = (ProfileEditor)RuntimeHelpers.GetUninitializedObject(typeof(ProfileEditor));
        typeof(ProfileEditor).GetField("currentProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(editor, entity);
        return editor;
    }
}
