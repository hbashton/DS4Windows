using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class TemporaryProfileIntentTests
{
    private const int Slot = 7;

    [TestMethod]
    public void HeldTriggerSubmitsOnceWhilePreparationIsBlocked()
    {
        using var fixture = new Fixture();
        long before = Global.ReadProfileSwitchRevision(Slot);
        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            for (int frame = 0; frame < 64; frame++) fixture.Frame(true);
            Assert.AreEqual(before + 1, Global.ReadProfileSwitchRevision(Slot),
                "A held trigger must not repeatedly supersede its own pending load.");
            Assert.IsFalse(Global.useTempProfile[Slot]);
            Assert.IsTrue(Mapping.actionDone[0].dev[Slot]);
        });
        fixture.WaitIdle();
        Assert.IsTrue(Global.useTempProfile[Slot]);
        Assert.AreEqual("Candidate", Global.tempprofilename[Slot]);
        Assert.AreEqual(1, fixture.Source.Pauses);
        fixture.Frame(false);
        fixture.WaitIdle();
    }

    [TestMethod]
    public void ReleaseBeforeApplyCancelsWithoutPublishingOrResettingOriginal()
    {
        using var fixture = new Fixture();
        long before = Global.ReadProfileSwitchRevision(Slot);
        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            fixture.Frame(true);
            fixture.Frame(false);
            Assert.AreEqual(before + 2, Global.ReadProfileSwitchRevision(Slot));
            Assert.IsNull(Mapping.untriggeraction[Slot]);
        });
        fixture.WaitIdle();
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.AreEqual(0, fixture.Source.Pauses,
            "An activation that never published needs no original-profile reset.");
    }

    [TestMethod]
    public void ActiveHoldDoesNotReloadAndReleaseRestoresExactlyOnce()
    {
        using var fixture = new Fixture();
        fixture.Frame(true);
        fixture.WaitIdle();
        long activeRevision = Global.ReadProfileSwitchRevision(Slot);
        for (int frame = 0; frame < 64; frame++) fixture.Frame(true);
        Assert.AreEqual(activeRevision, Global.ReadProfileSwitchRevision(Slot));
        Assert.AreEqual(1, fixture.Source.Pauses);
        fixture.Frame(false);
        fixture.WaitIdle();
        for (int frame = 0; frame < 64; frame++) fixture.Frame(false);
        Assert.AreEqual(activeRevision + 1, Global.ReadProfileSwitchRevision(Slot));
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
        Assert.AreEqual(2, fixture.Source.Pauses);
    }

    [TestMethod]
    public void RapidPressReleasePressKeepsOnlyNewestHeldIntent()
    {
        using var fixture = new Fixture();
        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            fixture.Frame(true);
            fixture.Frame(false);
            fixture.Frame(true);
        });
        fixture.WaitIdle();
        Assert.IsTrue(Global.useTempProfile[Slot]);
        Assert.AreEqual("Candidate", Global.tempprofilename[Slot]);
        Assert.AreEqual(1, fixture.Source.Pauses);
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        Assert.AreEqual(2, fixture.Source.Pauses);
    }

    [TestMethod]
    public void RepressBeforeQueuedReturnStillReturnsToOriginalNotTemporaryTarget()
    {
        using var fixture = new Fixture();
        fixture.Frame(true);
        fixture.WaitIdle();
        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            fixture.Frame(false);
            fixture.Frame(true);
        });
        fixture.WaitIdle();
        Assert.AreEqual("Candidate", Global.tempprofilename[Slot]);
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);
    }

    [TestMethod]
    public void ExistingTemporaryOriginalIsRestoredAsTemporary()
    {
        using var fixture = new Fixture();
        Global.useTempProfile[Slot] = true;
        Global.tempprofilename[Slot] = "OriginalTemp";
        fixture.Frame(true);
        fixture.WaitIdle();
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.IsTrue(Global.useTempProfile[Slot]);
        Assert.AreEqual("OriginalTemp", Global.tempprofilename[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        Assert.AreEqual(66, (int)fixture.Store.rumble[Slot]);
    }

    [TestMethod]
    public void SharedTriggerKeyRemainsDownWhileAnotherSourceOwnsIt()
    {
        using var fixture = new Fixture();
        fixture.BindControl(DS4Controls.Cross, 0x11);
        fixture.BindControl(DS4Controls.Circle, 0x11);
        fixture.Frame(false, circle: true);
        Assert.IsTrue(fixture.Handler.Keys.Contains(0x11));
        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            fixture.Frame(true, circle: true);
            Assert.IsTrue(fixture.Handler.Keys.Contains(0x11),
                "Profile activation must not directly release another binding's held key.");
            fixture.Frame(false, circle: true);
            Assert.IsTrue(fixture.Handler.Keys.Contains(0x11));
        });
        fixture.WaitIdle();
        Assert.AreEqual(0, fixture.Handler.Releases);
        fixture.Frame(false);
        Assert.IsFalse(fixture.Handler.Keys.Contains(0x11));
        Assert.AreEqual(1, fixture.Handler.Releases);
    }

    [TestMethod]
    public void ExplicitNewProfileSelectionIsNotUndoneByOldTriggerRelease()
    {
        using var fixture = new Fixture();
        fixture.Frame(true);
        fixture.WaitIdle();
        Mapping.RequestRegularProfileReload(Slot, false, fixture.Service, profileName: "Other");
        fixture.WaitIdle();
        long selectedRevision = Global.ReadProfileSwitchRevision(Slot);
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.AreEqual(selectedRevision, Global.ReadProfileSwitchRevision(Slot));
        Assert.AreEqual("Other", Global.ProfilePath[Slot]);
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.AreEqual(21, (int)fixture.Store.rumble[Slot]);
    }

    [TestMethod]
    public void AutomaticReleaseWorksWhenExplicitUnloadControlIsSameAndActionAbsentFromTarget()
    {
        using var fixture = new Fixture(includeActionInCandidate: false);
        fixture.Action.ucontrols = fixture.Action.controls;
        fixture.Action.uTrigger.Add(DS4Controls.Cross);
        fixture.Frame(true);
        fixture.WaitIdle();
        Assert.IsTrue(Mapping.actionDone[0].dev[Slot]);
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
    }

    [TestMethod]
    public void FailedActivationDoesNotRetryWhileHeldAndCanRetryAfterRelease()
    {
        using var fixture = new Fixture();
        fixture.WriteCandidate(valid: false);
        fixture.Frame(true);
        fixture.WaitIdle();
        long failedRevision = Global.ReadProfileSwitchRevision(Slot);
        for (int frame = 0; frame < 32; frame++) fixture.Frame(true);
        Assert.AreEqual(failedRevision, Global.ReadProfileSwitchRevision(Slot));
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.AreEqual(0, fixture.Source.Pauses);
        fixture.WriteCandidate(valid: true);
        fixture.Frame(true);
        fixture.WaitIdle();
        Assert.IsTrue(Global.useTempProfile[Slot]);
        fixture.Frame(false);
        fixture.WaitIdle();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NonAutomaticProfileActionsRetainTheirExistingUnloadPolicy(bool explicitUnload)
    {
        using var fixture = new Fixture(automatic: false, explicitUnload: explicitUnload);
        fixture.Frame(true);
        fixture.WaitIdle();
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.IsTrue(Global.useTempProfile[Slot], "A non-automatic switch persists after trigger release.");
        if (explicitUnload)
        {
            fixture.Frame(false, circle: true);
            fixture.WaitIdle();
            Assert.IsFalse(Global.useTempProfile[Slot]);
        }
    }

    [TestMethod]
    public void NeutralRetirementCancelsQueuedActivationAndAllowsSuccessorHold()
    {
        using var fixture = new Fixture();
        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            fixture.Frame(true);
            Mapping.CommitNeutral(Slot);
            Assert.IsNull(Mapping.untriggeraction[Slot]);
            Assert.IsFalse(Mapping.actionDone[0].dev[Slot]);
            Assert.IsNull(fixture.CurrentIntent);
        });
        fixture.WaitIdle();
        Assert.AreEqual(0, fixture.Source.Pauses);
        Assert.IsFalse(Global.useTempProfile[Slot]);
        Assert.AreEqual(77, (int)fixture.Store.rumble[Slot]);

        fixture.Frame(true);
        fixture.WaitIdle();
        Assert.AreEqual(1, fixture.Source.Pauses);
        Assert.IsTrue(Global.useTempProfile[Slot]);
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        Assert.IsFalse(Global.useTempProfile[Slot]);
    }

    [TestMethod]
    public void NeutralRetirementCancelsQueuedReturnWithoutLoadingIntoSuccessorState()
    {
        using var fixture = new Fixture();
        fixture.Frame(true);
        fixture.WaitIdle();
        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            fixture.Frame(false);
            Mapping.CommitNeutral(Slot);
            // A new runtime can initialize this slot without acquiring the
            // old request's authority or performing another profile selection.
            Global.useTempProfile[Slot] = false;
            Global.tempprofilename[Slot] = string.Empty;
            Global.ProfilePath[Slot] = "Other";
            fixture.Store.rumble[Slot] = 21;
        });
        fixture.WaitIdle();
        Assert.IsNull(fixture.CurrentIntent);
        Assert.AreEqual(1, fixture.Source.Pauses);
        Assert.AreEqual("Other", Global.ProfilePath[Slot]);
        Assert.AreEqual(21, (int)fixture.Store.rumble[Slot]);
        fixture.Frame(true);
        fixture.WaitIdle();
        fixture.Frame(false);
        fixture.WaitIdle();
        Assert.AreEqual("Other", Global.ProfilePath[Slot]);
        Assert.AreEqual(21, (int)fixture.Store.rumble[Slot]);
    }

    [TestMethod]
    public void NeutralRetirementDoesNotClearAnotherSlotsAutomaticIntent()
    {
        using var fixture = new Fixture();
        const int otherSlot = 6;
        Array intents = (Array)typeof(Mapping).GetField("automaticProfileSwitchIntents",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object originalIntent = intents.GetValue(otherSlot);
        SpecialAction originalUntrigger = Mapping.untriggeraction[otherSlot];
        int originalIndex = Mapping.untriggerindex[otherSlot];
        try
        {
            Mapping.ExecuteSerializedProfileMutation(Slot, () =>
            {
                fixture.Frame(true);
                object marker = Activator.CreateInstance(fixture.CurrentIntent.GetType(),
                    BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                    args: new object[] { fixture.Action, "Other", false }, culture: null)!;
                intents.SetValue(marker, otherSlot);
                Mapping.untriggeraction[otherSlot] = fixture.Action;
                Mapping.untriggerindex[otherSlot] = 0;
                Mapping.actionDone[0].dev[otherSlot] = true;
                Mapping.CommitNeutral(Slot);
                Assert.AreSame(marker, intents.GetValue(otherSlot));
                Assert.AreEqual(0, (int)marker.GetType().GetField("Released",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(marker)!);
                Assert.AreSame(fixture.Action, Mapping.untriggeraction[otherSlot]);
                Assert.IsTrue(Mapping.actionDone[0].dev[otherSlot]);
            });
            fixture.WaitIdle();
        }
        finally
        {
            intents.SetValue(originalIntent, otherSlot);
            Mapping.untriggeraction[otherSlot] = originalUntrigger;
            Mapping.untriggerindex[otherSlot] = originalIndex;
        }
    }

    [TestMethod]
    public void NeutralRetirementPreservesNonAutomaticUnloadState()
    {
        using var fixture = new Fixture(automatic: false, explicitUnload: true);
        fixture.Frame(true);
        fixture.WaitIdle();
        bool previousDone = Mapping.actionDone[0].dev[Slot];
        Mapping.CommitNeutral(Slot);
        Assert.AreSame(fixture.Action, Mapping.untriggeraction[Slot]);
        Assert.AreEqual(previousDone, Mapping.actionDone[0].dev[Slot]);
        Assert.IsTrue(Global.useTempProfile[Slot]);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly FieldInfo StoreField = typeof(Global).GetField("m_Config",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly long[] Revisions = (long[])typeof(Global).GetField("profileSwitchRevisions",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private static readonly object[] RequestLocks = (object[])typeof(Mapping).GetField("profileSwitchRequestLocks",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private static readonly Array Requests = (Array)typeof(Mapping).GetField("profileSwitchRequests",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private static readonly Array Intents = (Array)typeof(Mapping).GetField("automaticProfileSwitchIntents",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private readonly BackingStore oldStore = Global.store;
        private readonly string oldRoot = Global.appdatapath;
        private readonly long oldRevision = Global.ReadProfileSwitchRevision(Slot);
        private readonly string oldTemp = Global.tempprofilename[Slot];
        private readonly bool oldUseTemp = Global.useTempProfile[Slot], oldDistance = Global.tempprofileDistance[Slot];
        private readonly bool oldDinput = Global.useDInputOnly[Slot], oldTouch = Global.TouchActive[Slot];
        private readonly bool oldNotification = Global.ProfileChangedNotification;
        private readonly VirtualKBMBase oldHandler = Global.outputKBMHandler;
        private readonly VirtualKBMMapping oldMapping = Global.outputKBMMapping;
        private readonly Mapping.SyntheticState oldGlobal = Mapping.globalState;
        private readonly Mapping.SyntheticState[] oldStates = Mapping.deviceState;
        private readonly DS4StateFieldMapping oldFields = Mapping.fieldMappings[Slot];
        private readonly DS4StateFieldMapping oldOutputFields = Mapping.outputFieldMappings[Slot];
        private readonly List<Mapping.ActionState> oldDone = Mapping.actionDone;
        private readonly SpecialAction oldUntrigger = Mapping.untriggeraction[Slot];
        private readonly int oldUntriggerIndex = Mapping.untriggerindex[Slot];
        private readonly object oldIntent = Intents.GetValue(Slot);
        private readonly bool oldLight = DS4LightBar.forcelight[Slot];
        private readonly byte oldFlash = DS4LightBar.forcedFlash[Slot];
        private readonly string directory, profiles;
        private readonly bool includeAction;
        internal readonly BackingStore Store = new();
        internal readonly RecordingHandler Handler = new();
        internal readonly FakeSource Source = new();
        internal readonly ControlService Service;
        internal readonly SpecialAction Action;
        private readonly Mouse mouse;
        internal object CurrentIntent => Intents.GetValue(Slot);

        internal Fixture(bool automatic = true, bool explicitUnload = false, bool includeActionInCandidate = true)
        {
            WaitIdle();
            includeAction = includeActionInCandidate;
            directory = Path.Combine(Path.GetTempPath(), "ds4w-temp-intent-" + Guid.NewGuid().ToString("N"));
            profiles = Path.Combine(directory, "Profiles");
            Directory.CreateDirectory(profiles);
            StoreField.SetValue(null, Store);
            Global.appdatapath = directory;
            Global.ProfileChangedNotification = false;
            Global.outputKBMHandler = Handler;
            var mapping = new SendInputMapping();
            mapping.PopulateConstants();
            mapping.PopulateMappings();
            Global.outputKBMMapping = mapping;
            Mapping.globalState = new();
            Mapping.deviceState = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
                .Select(_ => new Mapping.SyntheticState()).ToArray();
            Mapping.fieldMappings[Slot] = new();
            Mapping.outputFieldMappings[Slot] = new();
            Mapping.actionDone = new() { new() };
            Mapping.untriggeraction[Slot] = null;
            Mapping.untriggerindex[Slot] = -1;
            Intents.SetValue(null, Slot);
            Global.useTempProfile[Slot] = false;
            Global.tempprofilename[Slot] = string.Empty;
            Global.useDInputOnly[Slot] = true;
            Store.profilePath[Slot] = "Original";
            Store.rumble[Slot] = 77;
            Action = new SpecialAction("HeldTemp", "Cross", "Profile", "Candidate",
                extras: automatic ? "AutomaticUntrigger" : explicitUnload ? "Circle" : "");
            Store.actions.Clear();
            Store.actions.Add(Action);
            Store.profileActions[Slot].Clear();
            Store.profileActions[Slot].Add(Action.name);
            Store.CacheExtraProfileInfo(Slot);
            WriteProfile("Original", 77, true);
            WriteProfile("OriginalTemp", 66, true);
            WriteProfile("Other", 21, false);
            WriteCandidate(true);
            mouse = new Mouse(Slot, Source);
            Service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            Service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
            Service.DS4Controllers[Slot] = Source;
            Service.touchPad = new Mouse[Global.MAX_DS4_CONTROLLER_COUNT];
            Service.touchPad[Slot] = mouse;
            Service.touchreleased = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
            typeof(ControlService).GetField("outputKbmHandlerLock", BindingFlags.Instance |
                BindingFlags.NonPublic)!.SetValue(Service, new object());
        }

        private void WriteProfile(string name, int rumble, bool action) => File.WriteAllText(
            Path.Combine(profiles, name + ".xml"),
            $"<DS4Windows config_version=\"5\"><DinputOnly>True</DinputOnly><RumbleBoost>{rumble}</RumbleBoost>" +
            (action ? "<ProfileActions>HeldTemp</ProfileActions>" : "") + "</DS4Windows>");

        internal void WriteCandidate(bool valid)
        {
            if (valid) WriteProfile("Candidate", 42, includeAction);
            else File.WriteAllText(Path.Combine(profiles, "Candidate.xml"), "<DS4Windows>");
        }

        internal void BindControl(DS4Controls control, int key)
        {
            var setting = Store.GetDS4CSetting(Slot, control);
            setting.UpdateSettings(false, key, string.Empty, DS4KeyType.None);
            setting.action.actionAlias = (uint)key;
        }

        internal void Frame(bool cross, bool circle = false)
        {
            var source = new DS4State { Cross = cross, Circle = circle, elapsedTime = .004 };
            var mapped = new DS4State();
            source.CopyExtrasTo(mapped);
            Mapping.MapCustom(Slot, source, mapped, new DS4StateExposed(source), mouse, Service);
            Mapping.Commit(Slot);
        }

        internal void WaitIdle()
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                lock (RequestLocks[Slot])
                {
                    object request = Requests.GetValue(Slot)!;
                    return !(bool)request.GetType().GetField("Running")!.GetValue(request)! &&
                        !(bool)request.GetType().GetField("Pending")!.GetValue(request)!;
                }
            }, 10_000), "The serialized profile worker did not drain.");
        }

        public void Dispose()
        {
            WaitIdle();
            StoreField.SetValue(null, oldStore);
            Global.appdatapath = oldRoot;
            Global.ProfileChangedNotification = oldNotification;
            Global.tempprofilename[Slot] = oldTemp;
            Global.useTempProfile[Slot] = oldUseTemp;
            Global.tempprofileDistance[Slot] = oldDistance;
            Global.useDInputOnly[Slot] = oldDinput;
            Global.TouchActive[Slot] = oldTouch;
            Global.outputKBMHandler = oldHandler;
            Global.outputKBMMapping = oldMapping;
            Mapping.globalState = oldGlobal;
            Mapping.deviceState = oldStates;
            Mapping.fieldMappings[Slot] = oldFields;
            Mapping.outputFieldMappings[Slot] = oldOutputFields;
            Mapping.actionDone = oldDone;
            Mapping.untriggeraction[Slot] = oldUntrigger;
            Mapping.untriggerindex[Slot] = oldUntriggerIndex;
            Intents.SetValue(oldIntent, Slot);
            DS4LightBar.forcelight[Slot] = oldLight;
            DS4LightBar.forcedFlash[Slot] = oldFlash;
            Interlocked.Exchange(ref Revisions[Slot], oldRevision);
            Source.ReadWaitEv.Dispose();
            foreach (string name in new[] { "Original", "OriginalTemp", "Other", "Candidate" })
                File.Delete(Path.Combine(profiles, name + ".xml"));
            Directory.Delete(profiles);
            Directory.Delete(directory);
        }
    }

    private sealed class FakeSource : DS4Device
    {
        internal int Pauses;
        internal FakeSource() : base("Temporary profile intent test", InputDeviceType.DS4, ConnectionType.USB)
        {
            synced = false; // No output-device/audio/HID work in these intent tests.
            lastTimeElapsedDouble = 4;
        }
        public override bool TryHaltReportingRunAction(Action action)
        {
            Pauses++;
            action();
            return true;
        }
    }

    private sealed class RecordingHandler : VirtualKBMBase
    {
        internal readonly HashSet<uint> Keys = new();
        internal int Releases;
        public override bool Connect() => throw new AssertFailedException("No native input allowed.");
        public override bool Disconnect() => throw new AssertFailedException("No native input allowed.");
        public override void MoveRelativeMouse(int x, int y) { }
        public override void MoveAbsoluteMouse(double x, double y) { }
        public override void PerformMouseWheelEvent(int vertical, int horizontal) { }
        public override void PerformMouseButtonEvent(uint button) { }
        public override void PerformMouseButtonPress(uint button) { }
        public override void PerformMouseButtonRelease(uint button) { }
        public override void PerformKeyPress(uint key) => Keys.Add(key);
        public override void PerformKeyPressAlt(uint key) => Keys.Add(key);
        public override void PerformKeyRelease(uint key) { Keys.Remove(key); Releases++; }
        public override void PerformKeyReleaseAlt(uint key) => PerformKeyRelease(key);
        public override string GetDisplayName() => "Temporary profile test";
        public override string GetIdentifier() => "Temporary profile test";
        public override string GetFullDisplayName() => "Temporary profile test";
    }
}
