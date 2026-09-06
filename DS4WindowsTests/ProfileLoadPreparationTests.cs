using DS4Windows;
using DS4Windows.DS4Control;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class ProfileLoadPreparationTests
{
    [DataTestMethod]
    [DataRow("<DS4Windows>")]
    [DataRow("<WrongRoot />")]
    [DataRow("")]
    [DataRow("<DS4Windows xmlns=\"urn:wrong\" />")]
    [DataRow("<DS4Windows config_version=\"5\"><Color>no,0,0</Color></DS4Windows>")]
    [DataRow("<DS4Windows config_version=\"5\"><Color>256,0,0</Color></DS4Windows>")]
    [DataRow("<DS4Windows config_version=\"5\"><Control><Macro><Cross>1//2</Cross></Macro></Control></DS4Windows>")]
    [DataRow("<DS4Windows config_version=\"5\"><Control><Macro><Cross>2147483648</Cross></Macro></Control></DS4Windows>")]
    [DataRow("<DS4Windows config_version=\"5\"><ShiftControl><Macro><Cross Trigger=\"1\">1//2</Cross></Macro></ShiftControl></DS4Windows>")]
    [DataRow("<DS4Windows config_version=\"5\"><Control><Button><Cross>A</Cross><Cross>B</Cross></Button></Control></DS4Windows>")]
    public void InvalidExistingProfileDoesNotResetLiveState(string xml)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ds4w-invalid-profile-{Guid.NewGuid():N}.xml");
        var store = new BackingStore();
        store.profilePath[0] = "Keep this profile";
        store.rumble[0] = 77;
        store.touchSensitivity[0] = 43;
        store.buttonMouseInfos[0].buttonSensitivity = 61;
        store.outputDevType[0] = OutContType.ViiperXboxOne;
        store.profileActions[0].Clear();
        store.profileActions[0].Add("Keep this action");
        DS4ControlSettings mapping = store.ds4settings[0].Single(value => value.control == DS4Controls.Cross);
        mapping.actionType = DS4ControlSettings.ActionType.Key;
        mapping.action.actionKey = 65;
        bool oldForceLight = DS4LightBar.forcelight[0];
        byte oldFlash = DS4LightBar.forcedFlash[0];
        try
        {
            File.WriteAllText(path, xml);
            DS4LightBar.forcelight[0] = true;
            DS4LightBar.forcedFlash[0] = 9;
            Exception failure = null;
            bool loaded = true;
            try
            {
                // A rejected candidate must not access a service at all. This also
                // catches accidental mouse/preload/output/launch side effects.
                loaded = store.LoadProfileNew(0, true, null, path);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Assert.AreEqual(77, (int)store.rumble[0], "The current profile was reset before rejection.");
            Assert.AreEqual(43, (int)store.touchSensitivity[0]);
            Assert.AreEqual(61, store.buttonMouseInfos[0].buttonSensitivity);
            Assert.AreEqual(OutContType.ViiperXboxOne, store.outputDevType[0]);
            Assert.AreEqual("Keep this profile", store.profilePath[0]);
            CollectionAssert.AreEqual(new[] { "Keep this action" }, store.profileActions[0]);
            Assert.AreEqual(DS4ControlSettings.ActionType.Key, mapping.actionType);
            Assert.AreEqual(65, mapping.action.actionKey);
            Assert.IsTrue(DS4LightBar.forcelight[0]);
            Assert.AreEqual((byte)9, DS4LightBar.forcedFlash[0]);
            Assert.AreEqual(xml, File.ReadAllText(path), "Rejected migrations must not be saved.");
            Assert.IsNull(failure, failure?.ToString());
            Assert.IsFalse(loaded);
        }
        finally
        {
            DS4LightBar.forcelight[0] = oldForceLight;
            DS4LightBar.forcedFlash[0] = oldFlash;
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MissingPreparationDoesNotApplyLegacyMissingFileFallback()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ds4w-missing-profile-{Guid.NewGuid():N}.xml");
        var store = new BackingStore();
        int slot = Global.TEST_PROFILE_INDEX;
        store.rumble[slot] = 77;
        Assert.IsFalse(PreparedProfileLoad.TryPrepare(path, slot, out var candidate,
            out var failure, out _));
        Assert.IsNull(candidate);
        Assert.AreEqual(ProfilePreparationFailure.Missing, failure);
        Assert.AreEqual(77, (int)store.rumble[slot]);

        // This test-only slot bypasses physical output and mapping state. The
        // service constructor (which discovers hardware) is deliberately not run.
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        Assert.IsFalse(store.LoadProfileNew(slot, false, service, path));
        Assert.AreEqual((int)BackingStore.DEFAULT_RUMBLE, (int)store.rumble[slot]);
    }

    [TestMethod]
    public void LockedFileIsUnreadableWithoutAProfileCandidate()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ds4w-locked-profile-{Guid.NewGuid():N}.xml");
        try
        {
            using var locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            Assert.IsFalse(PreparedProfileLoad.TryPrepare(path, 0, out var candidate,
                out var failure, out _));
            Assert.IsNull(candidate);
            Assert.AreEqual(ProfilePreparationFailure.Unreadable, failure);
            var store = new BackingStore();
            store.rumble[0] = 77;
            Assert.IsFalse(store.LoadProfileNew(0, true, null, out bool changed, path));
            Assert.IsFalse(changed);
            Assert.AreEqual(77, (int)store.rumble[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DataTestMethod]
    [DataRow("5", false)]
    [DataRow("4", true)]
    public void PreparedSnapshotIsSingleUseAndDoesNotRereadOrSave(string version, bool migrated)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ds4w-snapshot-profile-{Guid.NewGuid():N}.xml");
        string xml = $"<DS4Windows config_version=\"{version}\"><RumbleBoost>77</RumbleBoost><Color>1,2,3</Color><Control><Macro><Cross>1/2/3</Cross></Macro></Control></DS4Windows>";
        try
        {
            File.WriteAllText(path, xml);
            Assert.IsTrue(PreparedProfileLoad.TryPrepare(path, 2, out var candidate,
                out var failure, out string error), error);
            Assert.AreEqual(ProfilePreparationFailure.None, failure);
            Assert.AreEqual(migrated, candidate.Migrated);
            Assert.AreEqual(2, candidate.Device);
            Assert.AreEqual(path, candidate.Path);
            Assert.AreEqual(xml, File.ReadAllText(path), "Preparing must not persist migrations.");
            File.WriteAllText(path, "This file was replaced after preparation.");
            var store = new BackingStore();
            candidate.ApplyTo(store);
            Assert.AreEqual(77, (int)store.rumble[2]);
            Assert.AreEqual((byte)1, store.lightbarSettingInfo[2].ds4winSettings.m_Led.red);
            var cross = store.ds4settings[2].Single(value => value.control == DS4Controls.Cross);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, cross.action.actionMacro);
            var other = new BackingStore();
            other.rumble[2] = 32;
            Assert.ThrowsException<InvalidOperationException>(() => candidate.ApplyTo(other));
            Assert.AreEqual(32, (int)other.rumble[2]);
            Assert.AreEqual("This file was replaced after preparation.", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void PreparationDoesNotReadRunningKeyboardBackendAndApplyResolvesAliases()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ds4w-key-profile-{Guid.NewGuid():N}.xml");
        VirtualKBMMapping oldMapping = Global.outputKBMMapping;
        try
        {
            File.WriteAllText(path, "<DS4Windows config_version=\"5\"><Control><Key><Cross>65</Cross></Key></Control><ShiftControl><Key><Circle Trigger=\"1\">66</Circle></Key></ShiftControl></DS4Windows>");
            Global.outputKBMMapping = null;
            Assert.IsTrue(PreparedProfileLoad.TryPrepare(path, 0, out var candidate,
                out _, out string error), error);
            // This mapping object performs no OS input. It is deliberately
            // installed after prepare, so aliases must be resolved at apply.
            Global.outputKBMMapping = new SendInputMapping();
            var store = new BackingStore();
            candidate.ApplyTo(store);
            var cross = store.ds4settings[0].Single(value => value.control == DS4Controls.Cross);
            var circle = store.ds4settings[0].Single(value => value.control == DS4Controls.Circle);
            Assert.AreEqual(DS4ControlSettings.ActionType.Key, cross.actionType);
            Assert.AreEqual(65U, cross.action.actionAlias);
            Assert.AreEqual(DS4ControlSettings.ActionType.Key, circle.shiftActionType);
            Assert.AreEqual(66U, circle.shiftAction.actionAlias);
        }
        finally
        {
            Global.outputKBMMapping = oldMapping;
            File.Delete(path);
        }
    }

    [TestMethod]
    public void SuccessfulLegacyLoaderAppliesPreparedProfileToTestSlot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ds4w-valid-profile-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, "<DS4Windows config_version=\"5\"><RumbleBoost>77</RumbleBoost><Color>1,2,3</Color></DS4Windows>");
            int slot = Global.TEST_PROFILE_INDEX;
            var store = new BackingStore();
            var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            Assert.IsTrue(store.LoadProfileNew(slot, false, service, out bool changed,
                path, xinputChange: false, postLoad: false));
            Assert.IsTrue(changed);
            Assert.AreEqual(77, (int)store.rumble[slot]);
            Assert.AreEqual((byte)1, store.lightbarSettingInfo[slot].ds4winSettings.m_Led.red);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void RejectedRegularLoadPreservesActiveTemporaryProfileMetadata()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        FieldInfo storeField = typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);
        object oldStore = storeField.GetValue(null);
        string oldRoot = Global.appdatapath;
        string oldTemp = Global.tempprofilename[slot];
        bool oldUseTemp = Global.useTempProfile[slot];
        bool oldDistance = Global.tempprofileDistance[slot];
        string directory = Path.Combine(Path.GetTempPath(), $"ds4w-profile-metadata-{Guid.NewGuid():N}");
        string profiles = Path.Combine(directory, "Profiles");
        string path = Path.Combine(profiles, "Invalid.xml");
        try
        {
            Directory.CreateDirectory(profiles);
            File.WriteAllText(path, "<DS4Windows>");
            var store = new BackingStore();
            store.profilePath[slot] = "Invalid";
            store.rumble[slot] = 77;
            storeField.SetValue(null, store);
            Global.appdatapath = directory;
            Global.tempprofilename[slot] = "Active temporary";
            Global.useTempProfile[slot] = true;
            Global.tempprofileDistance[slot] = true;
            Assert.IsFalse(Global.LoadProfile(slot, true, null));
            Assert.AreEqual("Active temporary", Global.tempprofilename[slot]);
            Assert.IsTrue(Global.useTempProfile[slot]);
            Assert.IsTrue(Global.tempprofileDistance[slot]);
            Assert.AreEqual(77, (int)store.rumble[slot]);
        }
        finally
        {
            storeField.SetValue(null, oldStore);
            Global.appdatapath = oldRoot;
            Global.tempprofilename[slot] = oldTemp;
            Global.useTempProfile[slot] = oldUseTemp;
            Global.tempprofileDistance[slot] = oldDistance;
            File.Delete(path);
            Directory.Delete(profiles);
            Directory.Delete(directory);
        }
    }
}
