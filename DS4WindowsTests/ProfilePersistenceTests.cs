using System.Text;
using System.Xml.Linq;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ProfilePersistenceTests
{
    private string directory;
    private const string Original = "<DS4Windows><Color>1,2,3</Color></DS4Windows>";
    private const string Updated = "<DS4Windows><Color>4,5,6</Color></DS4Windows>";

    [TestInitialize]
    public void Initialize() => directory = Directory.CreateTempSubdirectory("ds4w-profile-save-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void CreationAndReplacementWriteCompleteUtf8XmlWithoutTemporaryFiles()
    {
        string path = Path.Combine(directory, "Profile.xml");
        ProfilePersistence.Save(path, Original);
        Assert.AreEqual(Original, File.ReadAllText(path));
        ProfilePersistence.Save(path, Updated);
        CollectionAssert.AreEqual(new UTF8Encoding(false).GetBytes(Updated), File.ReadAllBytes(path));
        Assert.AreEqual("4,5,6", XDocument.Load(path).Root.Element("Color").Value);
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InterruptedWritePreservesPreviousProfileOrLeavesNewDestinationAbsent(bool existing)
    {
        string path = Path.Combine(directory, "Profile.xml");
        if (existing) File.WriteAllText(path, Original);
        var failure = new IOException("Synthetic interrupted write");
        IOException observed = Assert.ThrowsException<IOException>(() => ProfilePersistence.Save(path, Updated,
            writeTemporary: (temporary, xml) =>
            {
                File.WriteAllText(temporary, "<DS4Windows>");
                throw failure;
            }));
        Assert.AreSame(failure, observed);
        Assert.AreEqual(existing, File.Exists(path));
        if (existing) Assert.AreEqual(Original, File.ReadAllText(path));
        Assert.AreEqual(existing ? 1 : 0, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void LockedDestinationPreservesPreviousProfileAndRemovesTemporaryFile()
    {
        string path = Path.Combine(directory, "Profile.xml");
        File.WriteAllText(path, Original);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.ThrowsException<IOException>(() => ProfilePersistence.Save(path, Updated));
        Assert.AreEqual(Original, File.ReadAllText(path));
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void ReadOnlyDestinationPreservesPreviousProfileAndRemovesTemporaryFile()
    {
        string path = Path.Combine(directory, "Profile.xml");
        File.WriteAllText(path, Original);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.ThrowsException<UnauthorizedAccessException>(() => ProfilePersistence.Save(path, Updated));
        Assert.AreEqual(Original, File.ReadAllText(path));
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void RenameWritesCompleteDestinationBeforeRemovingOriginal()
    {
        string original = Path.Combine(directory, "Original.xml");
        string target = Path.Combine(directory, "Renamed.xml");
        File.WriteAllText(original, Original);
        ProfilePersistence.Save(target, Updated, originalPath: original,
            writeTemporary: (temporary, xml) =>
            {
                Assert.AreEqual(Original, File.ReadAllText(original));
                Assert.IsFalse(File.Exists(target));
                File.WriteAllText(temporary, xml);
            });
        Assert.IsFalse(File.Exists(original));
        Assert.AreEqual(Updated, File.ReadAllText(target));
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void InterruptedRenamePreservesOriginalAndLeavesDestinationAbsent()
    {
        string original = Path.Combine(directory, "Original.xml");
        string target = Path.Combine(directory, "Renamed.xml");
        File.WriteAllText(original, Original);
        Assert.ThrowsException<IOException>(() => ProfilePersistence.Save(target, Updated,
            originalPath: original, writeTemporary: (temporary, _) =>
            {
                File.WriteAllText(temporary, "incomplete");
                throw new IOException("Synthetic interrupted rename");
            }));
        Assert.AreEqual(Original, File.ReadAllText(original));
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreationAndRenameCannotOverwriteCollisionEvenIfItAppearsDuringWrite(bool rename)
    {
        string original = Path.Combine(directory, "Original.xml");
        string target = Path.Combine(directory, "Other.xml");
        File.WriteAllText(original, Original);
        Assert.ThrowsException<IOException>(() => ProfilePersistence.Save(target, Updated,
            originalPath: rename ? original : null, overwrite: false,
            writeTemporary: (temporary, xml) =>
            {
                File.WriteAllText(temporary, xml);
                File.WriteAllText(target, Original);
            }));
        Assert.AreEqual(Original, File.ReadAllText(original));
        Assert.AreEqual(Original, File.ReadAllText(target));
        Assert.AreEqual(2, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void OriginalDeletionFailurePreservesBothCopiesAndReportsIncompleteRename()
    {
        string original = Path.Combine(directory, "Original.xml");
        string target = Path.Combine(directory, "Renamed.xml");
        File.WriteAllText(original, Original);
        using var held = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        IOException error = Assert.ThrowsException<IOException>(() =>
            ProfilePersistence.Save(target, Updated, originalPath: original));
        StringAssert.Contains(error.Message, "Both copies");
        Assert.AreEqual(Original, File.ReadAllText(original));
        Assert.AreEqual(Updated, File.ReadAllText(target));
        Assert.AreEqual(2, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void CaseOnlyRenameUsesAtomicReplacementInSameDirectory()
    {
        string original = Path.Combine(directory, "Original.xml");
        File.WriteAllText(original, Original);
        ProfilePersistence.Save(Path.Combine(directory, "original.xml"), Updated, originalPath: original);
        Assert.AreEqual(Updated, File.ReadAllText(original));
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void CrossDirectoryRenameIsRejectedBeforeWriting()
    {
        string original = Path.Combine(directory, "Original.xml");
        string target = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "Other")).FullName, "New.xml");
        File.WriteAllText(original, Original);
        Assert.ThrowsException<IOException>(() => ProfilePersistence.Save(target, Updated, originalPath: original));
        Assert.AreEqual(Original, File.ReadAllText(original));
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public void ProfileSaveUsesExistingSerializerAndPreservesReadOnlyDestination()
    {
        string previousRoot = Global.appdatapath;
        try
        {
            Global.appdatapath = directory;
            string profiles = Directory.CreateDirectory(Path.Combine(directory, "Profiles")).FullName;
            string path = Path.Combine(profiles, "Candidate.xml");
            var store = new BackingStore();
            store.rumble[0] = 77;
            Assert.IsTrue(store.SaveProfileNew(0, "Candidate"));
            Assert.AreEqual("77", XDocument.Load(path).Root.Element("RumbleBoost").Value);
            byte[] previous = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            store.rumble[0] = 88;
            Assert.IsFalse(store.SaveProfileNew(0, "Candidate"));
            CollectionAssert.AreEqual(previous, File.ReadAllBytes(path));
            Assert.AreEqual(1, Directory.GetFiles(profiles).Length);
        }
        finally { Global.appdatapath = previousRoot; }
    }

    [TestMethod]
    public void SerializationFailureReturnsFalseAndPreservesExistingFile()
    {
        string previousRoot = Global.appdatapath;
        try
        {
            Global.appdatapath = directory;
            string profiles = Directory.CreateDirectory(Path.Combine(directory, "Profiles")).FullName;
            string path = Path.Combine(profiles, "Candidate.xml");
            File.WriteAllText(path, Original);
            var store = new BackingStore();
            store.launchProgram[0] = "Invalid XML character: \0";
            Assert.IsFalse(store.SaveProfileNew(0, "Candidate"));
            Assert.AreEqual(Original, File.ReadAllText(path));
            Assert.AreEqual(1, Directory.GetFiles(profiles).Length);
        }
        finally { Global.appdatapath = previousRoot; }
    }

    [TestMethod]
    public void ExistingXmlSuffixIsNormalizedForBothSaveAndOriginalName()
    {
        string previousRoot = Global.appdatapath;
        try
        {
            Global.appdatapath = directory;
            string profiles = Directory.CreateDirectory(Path.Combine(directory, "Profiles")).FullName;
            var store = new BackingStore();
            Assert.IsTrue(store.SaveProfileNew(0, "Candidate.xml"));
            store.rumble[0] = 88;
            Assert.IsTrue(store.SaveProfileNew(0, "Candidate.xml", "Candidate.xml"));
            Assert.AreEqual("88", XDocument.Load(Path.Combine(profiles, "Candidate.xml")).Root.Element("RumbleBoost").Value);
            Assert.AreEqual(1, Directory.GetFiles(profiles).Length);
        }
        finally { Global.appdatapath = previousRoot; }
    }
}
