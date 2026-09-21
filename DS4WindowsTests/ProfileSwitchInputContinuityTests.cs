using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ProfileSwitchInputContinuityTests
{
    private const int Slot = 7;
    private const uint W = 0x57;

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void HeldMovementSurvivesTemporaryProfilePressAndReleaseWhileWorkerWaits(
        bool returnToTemporary, bool mappedKeyboardAndMouse)
    {
        using var fixture = new Fixture(returnToTemporary, mappedKeyboardAndMouse);
        fixture.MovingFrame(false);
        fixture.AssertMovement();

        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            long before = Global.ReadProfileSwitchRevision(Slot);
            fixture.MovingFrame(true); // Actual Profile special-action dispatch.
            long requested = Global.ReadProfileSwitchRevision(Slot);
            Assert.IsTrue(requested > before);
            for (int frame = 0; frame < 24; frame++)
            {
                fixture.MovingFrame(true);
                fixture.AssertMovement();
            }
            Assert.AreEqual(requested, Global.ReadProfileSwitchRevision(Slot),
                "Holding the switch must not keep invalidating the worker's pending load.");
            Assert.AreEqual(returnToTemporary, Global.useTempProfile[Slot]);
            Assert.AreEqual(11, Global.store.rumble[Slot],
                "A queued worker must not expose partially reset profile settings.");
        });
        fixture.WaitForWorker();
        Assert.IsTrue(Global.useTempProfile[Slot]);
        Assert.AreEqual("Candidate", Global.tempprofilename[Slot]);
        Assert.AreEqual(22, Global.store.rumble[Slot]);
        fixture.MovingFrame(true);
        fixture.AssertMovement();

        Mapping.ExecuteSerializedProfileMutation(Slot, () =>
        {
            long before = Global.ReadProfileSwitchRevision(Slot);
            fixture.MovingFrame(false); // Actual automatic-untrigger dispatch.
            long requested = Global.ReadProfileSwitchRevision(Slot);
            Assert.IsTrue(requested > before);
            for (int frame = 0; frame < 24; frame++)
            {
                fixture.MovingFrame(false);
                fixture.AssertMovement();
            }
            Assert.AreEqual(requested, Global.ReadProfileSwitchRevision(Slot));
            Assert.IsTrue(Global.useTempProfile[Slot]);
            Assert.AreEqual("Candidate", Global.tempprofilename[Slot]);
            Assert.AreEqual(22, Global.store.rumble[Slot]);
        });
        fixture.WaitForWorker();
        Assert.AreEqual(returnToTemporary, Global.useTempProfile[Slot]);
        Assert.AreEqual(returnToTemporary ? "PriorTemporary" : string.Empty,
            Global.tempprofilename[Slot]);
        Assert.AreEqual("Original", Global.ProfilePath[Slot]);
        Assert.AreEqual(11, Global.store.rumble[Slot]);
        fixture.MovingFrame(false);
        fixture.AssertMovement();

        // A real, synced legacy source queues post-load work. This fixture
        // does not pretend to exercise audio/HID setup by suppressing that queue.
        Assert.IsTrue(fixture.QueuedPostLoadCount >= 2);
        fixture.NeutralFrame();
        Assert.AreEqual((byte)128, fixture.Output.Last.LX);
        Assert.IsFalse(fixture.Handler.Keys.Contains(W));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly BindingFlags StaticFields = BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo StoreField = typeof(Global).GetField("m_Config", StaticFields)!;
        private static readonly long[] Revisions = (long[])typeof(Global)
            .GetField("profileSwitchRevisions", StaticFields)!.GetValue(null)!;
        private static readonly object[] WorkerGates = (object[])typeof(Mapping)
            .GetField("profileSwitchRequestLocks", StaticFields)!.GetValue(null)!;
        private static readonly Array Workers = (Array)typeof(Mapping)
            .GetField("profileSwitchRequests", StaticFields)!.GetValue(null)!;
        private readonly object previousStore = StoreField.GetValue(null)!;
        private readonly string previousRoot = Global.appdatapath;
        private readonly VirtualKBMBase previousHandler = Global.outputKBMHandler;
        private readonly VirtualKBMMapping previousMapping = Global.outputKBMMapping;
        private readonly long previousRevision = Global.ReadProfileSwitchRevision(Slot);
        private readonly string previousTemporary = Global.tempprofilename[Slot];
        private readonly bool previousUseTemporary = Global.useTempProfile[Slot];
        private readonly bool previousDistance = Global.tempprofileDistance[Slot];
        private readonly bool previousTouch = Global.TouchActive[Slot];
        private readonly bool previousForceLight = DS4LightBar.forcelight[Slot];
        private readonly byte previousFlash = DS4LightBar.forcedFlash[Slot];
        private readonly OutContType previousActiveOutput = Global.activeOutDevType[Slot];
        private readonly bool previousDInput = Global.useDInputOnly[Slot];
        private readonly Dictionary<FieldInfo, object> mappingState = new();
        private readonly string directory;
        private readonly bool mappedKeyboardAndMouse;
        private readonly DS4Device source;
        private readonly Mouse mouse;
        private readonly ControlService service;
        private readonly InputControllerRegistrationTable table;
        private readonly InputControllerSlotToken token;
        private readonly Func<int, OutputDevice> selectOutput;
        private readonly Queue<Action> postLoadQueue;
        private readonly object postLoadGate;
        internal readonly RecordingHandler Handler = new();
        internal readonly RecordingOutput Output = new();
        private int previousMoveCount;

        internal Fixture(bool returnToTemporary, bool mappedKeyboardAndMouse)
        {
            WaitForWorker();
            this.mappedKeyboardAndMouse = mappedKeyboardAndMouse;
            directory = Path.Combine(Path.GetTempPath(), "ds4w-profile-continuity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "Profiles"));
            var store = new BackingStore();
            StoreField.SetValue(null, store);
            Global.appdatapath = directory;
            Global.outputKBMHandler = Handler;
            var keyMapping = new SendInputMapping();
            keyMapping.PopulateConstants();
            keyMapping.PopulateMappings();
            Global.outputKBMMapping = keyMapping;
            ReplaceMappingField("globalState", new Mapping.SyntheticState());
            ReplaceMappingField("deviceState", Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
                .Select(_ => new Mapping.SyntheticState()).ToArray());
            ReplaceMappingField("fieldMappings", Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
                .Select(_ => new DS4StateFieldMapping()).ToArray());
            ReplaceMappingField("outputFieldMappings", Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
                .Select(_ => new DS4StateFieldMapping()).ToArray());
            ReplaceMappingField("actionDone", new List<Mapping.ActionState> { new() });
            ReplaceMappingField("untriggeraction", new SpecialAction[Global.MAX_DS4_CONTROLLER_COUNT]);
            ReplaceMappingField("untriggerindex", Enumerable.Repeat(-1, Global.MAX_DS4_CONTROLLER_COUNT).ToArray());
            ReplaceMappingField("horizontalRemainder", 0d);
            ReplaceMappingField("verticalRemainder", 0d);

            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            source = new DS4Device(hid, "Profile continuity test");
            typeof(DS4Device).GetField("synced", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(source, true);
            typeof(DS4Device).GetField("gyroMouseSensSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(source, new DS4Device.GyroMouseSens());
            source.lastTimeElapsedDouble = 4;
            source.ReadWaitEv.Set(); // No actual HID reader or transport is started.
            mouse = new Mouse(Slot, source);
            service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
            service.DS4Controllers[Slot] = source;
            service.touchPad = new Mouse[Global.MAX_DS4_CONTROLLER_COUNT];
            service.touchPad[Slot] = mouse;
            service.touchreleased = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
            service.outputDevices = new OutputDevice[Global.MAX_DS4_CONTROLLER_COUNT];
            service.outputDevices[Slot] = Output;
            SetServiceField("outputKbmHandlerLock", new object());
            SetServiceField("gameBarCompatibilityRoutingActive", new int[Global.MAX_DS4_CONTROLLER_COUNT]);
            SetServiceField("gameBarCompatibilityOutputDevices", new OutputDevice[Global.MAX_DS4_CONTROLLER_COUNT]);
            selectOutput = (Func<int, OutputDevice>)typeof(ControlService)
                .GetMethod("GetReportOutputDevice", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Func<int, OutputDevice>), service);
            table = new InputControllerRegistrationTable(Global.MAX_DS4_CONTROLLER_COUNT);
            Assert.IsTrue(table.TryOpen(1, out _));
            SetServiceField("inputRegistrationTable", table);
            Assert.IsTrue(InputControllerRegistration.TryCreate(source, 96_001,
                InputControllerOwnershipKind.LegacyHid, true, false, new RecordingOwner(source),
                out var registration, out _));
            Assert.IsTrue(table.TryReserveAndBindExactSlot(Slot, registration, out token, out _, out _));
            Assert.IsTrue(table.TryActivate(token, out _));
            postLoadQueue = (Queue<Action>)typeof(DS4Device).GetField("eventQueue",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
            postLoadGate = typeof(DS4Device).GetField("eventQueueLock",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;

            store.actions.Clear();
            store.actions.Add(new SpecialAction("HeldTemp", "Cross", "Profile", "Candidate", extras: "AutomaticUntrigger"));
            store.profileActions[Slot].Clear();
            store.profileActions[Slot].Add("HeldTemp");
            store.CalculateProfileActionDicts(Slot);
            store.profileActionCount[Slot] = 1;
            store.profilePath[Slot] = "Original";
            store.rumble[Slot] = 11;
            store.outputDevType[Slot] = OutContType.ViiperX360;
            Global.activeOutDevType[Slot] = OutContType.ViiperX360;
            Global.useDInputOnly[Slot] = false;
            Global.useTempProfile[Slot] = returnToTemporary;
            Global.tempprofilename[Slot] = returnToTemporary ? "PriorTemporary" : string.Empty;
            if (mappedKeyboardAndMouse)
            {
                var setting = store.GetDS4CSetting(Slot, DS4Controls.LYNeg);
                setting.UpdateSettings(false, (int)W, string.Empty, DS4KeyType.None);
                setting.action.actionAlias = W;
                store.GetDS4CSetting(Slot, DS4Controls.RXPos)
                    .UpdateSettings(false, X360Controls.MouseRight, string.Empty, DS4KeyType.None);
            }
            WriteProfile("Original", 11);
            WriteProfile("PriorTemporary", 11);
            WriteProfile("Candidate", 22);
        }

        private void WriteProfile(string name, int rumble)
        {
            string controls = mappedKeyboardAndMouse ?
                "<Control><Key><LYNeg>87</LYNeg></Key><Button><RXPos>Mouse Right</RXPos></Button></Control>" : string.Empty;
            File.WriteAllText(Path.Combine(directory, "Profiles", name + ".xml"),
                $"<DS4Windows config_version=\"5\"><RumbleBoost>{rumble}</RumbleBoost>" +
                "<OutputContDevice>ViiperX360</OutputContDevice><DinputOnly>false</DinputOnly>" +
                "<ProfileActions>HeldTemp</ProfileActions>" + controls + "</DS4Windows>");
        }

        internal void MovingFrame(bool cross)
        {
            previousMoveCount = Handler.Moves;
            Frame(new DS4State { Cross = cross, LX = 210, LY = 0, RX = 255, elapsedTime = .004 });
        }

        internal void NeutralFrame() => Frame(new DS4State { elapsedTime = .004 });

        private void Frame(DS4State state)
        {
            // Model the legacy callback's actual admission, mapper and output
            // stages, without starting the HID thread or a Windows input backend.
            Assert.IsTrue(source.isSynced());
            Assert.IsTrue(source.FireReport, "The completed/waiting switch must leave reports enabled.");
            Assert.IsTrue(table.TryAcquireReportLease(token, source, out var lease, out var failure), failure.ToString());
            using (lease)
            {
                var mapped = new DS4State();
                state.CopyExtrasTo(mapped);
                Mapping.MapCustom(Slot, state, mapped, new DS4StateExposed(state), mouse, service);
                Mapping.Commit(Slot);
                OutputDevice selected = selectOutput(Slot);
                Assert.AreSame(Output, selected);
                selected.ConvertandSendReport(mapped, Slot);
            }
        }

        internal void AssertMovement()
        {
            Assert.AreEqual((byte)210, Output.Last.LX, "Held gamepad movement must reach the selected output.");
            if (mappedKeyboardAndMouse)
            {
                Assert.IsTrue(Handler.Keys.Contains(W), "Held keyboard movement must remain published.");
                Assert.IsTrue(Handler.Moves > previousMoveCount, "The same frame must actually publish mouse movement.");
            }
            else Assert.AreEqual((byte)0, Output.Last.LY);
        }

        internal int QueuedPostLoadCount { get { lock (postLoadGate) return postLoadQueue.Count; } }

        internal void WaitForWorker()
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                lock (WorkerGates[Slot])
                {
                    object request = Workers.GetValue(Slot)!;
                    return !(bool)request.GetType().GetField("Running")!.GetValue(request)! &&
                        !(bool)request.GetType().GetField("Pending")!.GetValue(request)!;
                }
            }, 5000), "The real profile worker must finish before fixture state is restored.");
        }

        private void ReplaceMappingField(string name, object value)
        {
            FieldInfo field = typeof(Mapping).GetField(name, StaticFields)!;
            mappingState.Add(field, field.GetValue(null)!);
            field.SetValue(null, value);
        }

        private void SetServiceField(string name, object value) => typeof(ControlService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);

        public void Dispose()
        {
            WaitForWorker();
            Mapping.CommitNeutral(Slot);
            // These queued physical post-load effects intentionally never run.
            // Their presence is checked above; disposal only drops this owned fake queue.
            lock (postLoadGate) postLoadQueue.Clear();
            StoreField.SetValue(null, previousStore);
            Global.appdatapath = previousRoot;
            Global.outputKBMHandler = previousHandler;
            Global.outputKBMMapping = previousMapping;
            foreach (var pair in mappingState) pair.Key.SetValue(null, pair.Value);
            Global.tempprofilename[Slot] = previousTemporary;
            Global.useTempProfile[Slot] = previousUseTemporary;
            Global.tempprofileDistance[Slot] = previousDistance;
            Global.TouchActive[Slot] = previousTouch;
            DS4LightBar.forcelight[Slot] = previousForceLight;
            DS4LightBar.forcedFlash[Slot] = previousFlash;
            Global.activeOutDevType[Slot] = previousActiveOutput;
            Global.useDInputOnly[Slot] = previousDInput;
            Interlocked.Exchange(ref Revisions[Slot], previousRevision);
            source.ReadWaitEv.Dispose();
            foreach (string name in new[] { "Original", "PriorTemporary", "Candidate" })
                File.Delete(Path.Combine(directory, "Profiles", name + ".xml"));
            Directory.Delete(Path.Combine(directory, "Profiles"));
            Directory.Delete(directory);
        }
    }

    private sealed class RecordingOwner(DS4Device source) : IInputControllerRegistrationOwner
    {
        public InputControllerOwnershipKind Kind => InputControllerOwnershipKind.LegacyHid;
        public bool Authenticates(DS4Device candidate, ulong generation) =>
            ReferenceEquals(source, candidate) && generation == 96_001;
        public bool TryStopAndQuiesce(DS4Device candidate, ulong generation, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        { failure = InputControllerOwnerOperationFailure.StopRejected; return false; }
        public bool TryRemove(DS4Device candidate, ulong generation,
            out InputControllerOwnerOperationFailure failure)
        { failure = InputControllerOwnerOperationFailure.RemoveRejected; return false; }
    }

    internal sealed class RecordingOutput : OutputDevice
    {
        internal DS4State Last = new();
        public override void Connect() => Assert.Fail("No virtual-device connection is allowed.");
        public override void Disconnect() => Assert.Fail("The same-output profile switch must not disconnect.");
        public override string GetDeviceType() => OutContType.ViiperX360.ToString();
        public override void ConvertandSendReport(DS4State state, int device) => state.CopyTo(Last);
        public override void ResetState(bool submit = true) => Assert.Fail("Unexpected output reset.");
        public override void RemoveFeedbacks() => Assert.Fail("Unexpected feedback removal.");
        public override void RemoveFeedback(int index) => Assert.Fail("Unexpected feedback removal.");
    }

    internal sealed class RecordingHandler : VirtualKBMBase
    {
        internal readonly HashSet<uint> Keys = new();
        internal int Moves;
        public override bool Connect() => throw new AssertFailedException("No system input connection is allowed.");
        public override bool Disconnect() => throw new AssertFailedException("No system input disconnection is allowed.");
        public override void MoveRelativeMouse(int x, int y) { if (x > 0) Moves++; }
        public override void MoveAbsoluteMouse(double x, double y) => Assert.Fail("Unexpected absolute mouse output.");
        public override void PerformMouseWheelEvent(int vertical, int horizontal) => Assert.Fail("Unexpected wheel output.");
        public override void PerformMouseButtonEvent(uint button) => Assert.Fail("Unexpected mouse button output.");
        public override void PerformMouseButtonPress(uint button) => PerformMouseButtonEvent(button);
        public override void PerformMouseButtonRelease(uint button) => PerformMouseButtonEvent(button);
        public override void PerformKeyPress(uint key) => Keys.Add(key);
        public override void PerformKeyPressAlt(uint key) => Keys.Add(key);
        public override void PerformKeyRelease(uint key) => Keys.Remove(key);
        public override void PerformKeyReleaseAlt(uint key) => Keys.Remove(key);
        public override string GetDisplayName() => "Profile continuity recorder";
        public override string GetIdentifier() => "Profile continuity recorder";
        public override string GetFullDisplayName() => "Profile continuity recorder";
    }
}
