using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ConcurrentMouseMappingTests
{
    private const int Slot = 7, OtherSlot = 6;
    private const uint LeftDown = 2, LeftUp = 4, RightDown = 8, RightUp = 16;
    private const uint W = 0x57, Space = 0x20, Control = 0x11;
    private static readonly FieldInfo StoreField = typeof(Global).GetField(
        "m_Config", BindingFlags.Static | BindingFlags.NonPublic)!;
    private BackingStore previousStore = null!;
    private VirtualKBMBase previousHandler = null!;
    private VirtualKBMMapping previousMapping = null!;
    private Mapping.SyntheticState previousGlobal = null!;
    private Mapping.SyntheticState[] previousDevices = null!;
    private DS4StateFieldMapping[] previousFields = null!, previousOutputFields = null!;
    private Mapping.TwoStageTriggerMappingData[] previousLeftStages = null!, previousRightStages = null!;
    private bool[] previousPressedOnce = null!;
    private double previousHorizontalRemainder, previousVerticalRemainder;
    private RecordingHandler handler = null!;
    private ControlService service = null!;
    private Mouse mouse = null!, otherMouse = null!;

    [TestInitialize]
    public void Initialize()
    {
        previousStore = Global.store;
        previousHandler = Global.outputKBMHandler;
        previousMapping = Global.outputKBMMapping;
        previousGlobal = Mapping.globalState;
        previousDevices = Mapping.deviceState;
        previousFields = Mapping.fieldMappings;
        previousOutputFields = Mapping.outputFieldMappings;
        previousLeftStages = Mapping.l2TwoStageMappingData;
        previousRightStages = Mapping.r2TwoStageMappingData;
        previousPressedOnce = Mapping.pressedonce;
        previousHorizontalRemainder = ReadRemainder("horizontalRemainder");
        previousVerticalRemainder = ReadRemainder("verticalRemainder");

        StoreField.SetValue(null, new BackingStore());
        handler = new RecordingHandler();
        Global.outputKBMHandler = handler;
        var mapping = new SendInputMapping();
        mapping.PopulateConstants();
        mapping.PopulateMappings();
        Global.outputKBMMapping = mapping;
        Mapping.globalState = new();
        Mapping.deviceState = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
            .Select(_ => new Mapping.SyntheticState()).ToArray();
        Mapping.pressedonce = new bool[previousPressedOnce.Length];
        Mapping.fieldMappings = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
            .Select(_ => new DS4StateFieldMapping()).ToArray();
        Mapping.outputFieldMappings = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
            .Select(_ => new DS4StateFieldMapping()).ToArray();
        Mapping.l2TwoStageMappingData = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
            .Select(_ => new Mapping.TwoStageTriggerMappingData()).ToArray();
        Mapping.r2TwoStageMappingData = Enumerable.Range(0, Global.MAX_DS4_CONTROLLER_COUNT)
            .Select(_ => new Mapping.TwoStageTriggerMappingData()).ToArray();
        WriteRemainder("horizontalRemainder", 0);
        WriteRemainder("verticalRemainder", 0);

        var device = new NoHidDevice { lastTimeElapsedDouble = 4 };
        mouse = new Mouse(Slot, device);
        service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        service.DS4Controllers[Slot] = device;
        var otherDevice = new NoHidDevice { lastTimeElapsedDouble = 4 };
        service.DS4Controllers[OtherSlot] = otherDevice;
        otherMouse = new Mouse(OtherSlot, otherDevice);
        Global.store.profileActions[Slot].Clear();
        Global.store.profileActionCount[Slot] = 0;
        Global.store.profileActions[OtherSlot].Clear();
        Global.store.profileActionCount[OtherSlot] = 0;
        Mapping.ResetFlickStickCalibration(Slot);
        Mapping.ResetFlickStickCalibration(OtherSlot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // No Windows input backend is ever connected, including failed cases.
        Mapping.ResetFlickStickCalibration(Slot);
        Mapping.ResetFlickStickCalibration(OtherSlot);
        StoreField.SetValue(null, previousStore);
        Global.outputKBMHandler = previousHandler;
        Global.outputKBMMapping = previousMapping;
        Mapping.globalState = previousGlobal;
        Mapping.deviceState = previousDevices;
        Mapping.fieldMappings = previousFields;
        Mapping.outputFieldMappings = previousOutputFields;
        Mapping.l2TwoStageMappingData = previousLeftStages;
        Mapping.r2TwoStageMappingData = previousRightStages;
        Mapping.pressedonce = previousPressedOnce;
        WriteRemainder("horizontalRemainder", previousHorizontalRemainder);
        WriteRemainder("verticalRemainder", previousVerticalRemainder);
    }

    // Pattern 0 keeps gamepad movement/jump/crouch; 1 maps them to keyboard;
    // 2 adds stick mouse movement; 3 combines keyboard and mouse movement.
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public void R2ClickAndOtherInputsRemainIndependent(int pattern, bool clickFirst)
    {
        bool keyboard = pattern is 1 or 3;
        bool mouseMovement = pattern is 2 or 3;
        Bind(DS4Controls.R2, X360Controls.LeftMouse);
        if (keyboard)
        {
            Bind(DS4Controls.LYNeg, (int)W);
            Bind(DS4Controls.Square, (int)Space);
            Bind(DS4Controls.Circle, (int)Control);
        }
        if (mouseMovement) Bind(DS4Controls.RXPos, X360Controls.MouseRight);

        Frame(Input(false, false));
        Frame(Input(clickFirst, !clickFirst));
        DS4State combined = Frame(Input(true, true));
        Assert.IsTrue(handler.LeftHeld, "Movement/jump/crouch must not suppress left down.");
        Assert.AreEqual((byte)0, combined.R2, "The mapped trigger must not leak its default output.");
        if (keyboard)
            CollectionAssert.AreEquivalent(new[] { W, Space, Control }, handler.Keys.ToArray());
        else
        {
            Assert.AreEqual((byte)210, combined.LX);
            Assert.IsTrue(combined.Square);
            Assert.IsTrue(combined.Circle);
        }
        if (mouseMovement)
            Assert.IsTrue(handler.Moves.Any(move => move.X > 0), "The stick must actually emit movement.");

        int eventsWhileHeld = handler.MouseEvents.Count;
        Frame(Input(true, false));
        Frame(Input(true, true));
        Assert.IsTrue(handler.LeftHeld, "Releasing and repressing other inputs must retain the click.");
        Assert.AreEqual(eventsWhileHeld, handler.MouseEvents.Count,
            "Other inputs must not inject extra mouse button edges.");
        Frame(Input(false, true));
        Assert.IsFalse(handler.LeftHeld, "Left up must be delivered while other inputs remain active.");
        if (keyboard)
            CollectionAssert.AreEquivalent(new[] { W, Space, Control }, handler.Keys.ToArray());
        Frame(Input(false, false));
        Assert.AreEqual(0, handler.Keys.Count);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void FaceButtonClickWorksWithKeyboardMovementJumpAndCrouch()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse);
        Bind(DS4Controls.LYNeg, (int)W);
        Bind(DS4Controls.Square, (int)Space);
        Bind(DS4Controls.Circle, (int)Control);
        Frame(Input(false, true));
        Frame(new DS4State { Cross = true, LY = 0, Square = true, Circle = true });
        Assert.IsTrue(handler.LeftHeld);
        CollectionAssert.AreEquivalent(new[] { W, Space, Control }, handler.Keys.ToArray());
        Frame(Input(false, true));
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [DataTestMethod]
    [DataRow(X360Controls.A)]
    [DataRow(X360Controls.RightMouse)]
    public void HeldUnrelatedToggleCannotSuppressLeftClickPress(X360Controls otherOutput)
    {
        Bind(DS4Controls.R2, X360Controls.LeftMouse);
        Bind(DS4Controls.Cross, otherOutput, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State { Cross = true, R2 = 255 });
        Assert.IsTrue(handler.LeftHeld,
            "A held toggle on another output must not swallow the left-down edge.");
        Frame(new DS4State { Cross = true });
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp },
            handler.MouseEvents.Where(value => value is LeftDown or LeftUp).ToArray());
    }

    [DataTestMethod]
    [DataRow(X360Controls.A)]
    [DataRow(X360Controls.RightMouse)]
    public void NewlyHeldUnrelatedToggleCannotSuppressLeftClickRelease(X360Controls otherOutput)
    {
        Bind(DS4Controls.R2, X360Controls.LeftMouse);
        Bind(DS4Controls.Cross, otherOutput, DS4KeyType.Toggle);
        Frame(new DS4State { R2 = 255 });
        Assert.IsTrue(handler.LeftHeld);
        Frame(new DS4State { R2 = 255, Cross = true });
        Frame(new DS4State { Cross = true });
        Assert.IsFalse(handler.LeftHeld,
            "Releasing left click during another held toggle must not leave it stuck down.");
    }

    [TestMethod]
    public void TwoOrdinaryBindingsReleaseOnlyAfterLastLeftClickOwner()
    {
        Bind(DS4Controls.R2, X360Controls.LeftMouse);
        Bind(DS4Controls.Cross, X360Controls.LeftMouse);
        Frame(new DS4State { R2 = 255 });
        Frame(new DS4State { R2 = 255, Cross = true });
        Frame(new DS4State { Cross = true });
        Assert.IsTrue(handler.LeftHeld);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void LeftAndRightTogglesHaveIndependentPressAndReleaseLifecycles()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Bind(DS4Controls.Circle, X360Controls.RightMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Frame(new DS4State { Circle = true });
        Frame(new DS4State());
        Assert.IsTrue(handler.LeftHeld);
        Assert.IsTrue(handler.RightHeld);

        Frame(new DS4State { Cross = true });
        Assert.IsFalse(handler.LeftHeld);
        Assert.IsTrue(handler.RightHeld, "Turning off left must not release right.");
        Frame(new DS4State());
        Frame(new DS4State { Circle = true });
        Assert.IsFalse(handler.RightHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, RightDown, LeftUp, RightUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void SameOutputTogglesAreOwnedByEachSourceBinding()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Bind(DS4Controls.Circle, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Frame(new DS4State { Circle = true });
        Frame(new DS4State());
        Frame(new DS4State { Cross = true });
        Assert.IsTrue(handler.LeftHeld, "Circle still owns left after Cross toggles off.");
        CollectionAssert.AreEqual(new[] { LeftDown }, handler.MouseEvents);
        Frame(new DS4State());
        Frame(new DS4State { Circle = true });
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void TurningOffToggleDoesNotReleaseAnOrdinaryOwnerOfSameButton()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Bind(DS4Controls.R2, X360Controls.LeftMouse);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State { R2 = 255 });
        Frame(new DS4State { Cross = true, R2 = 255 });
        Assert.IsTrue(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown }, handler.MouseEvents);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void ToggleOwnersOnDifferentControllersReleaseIndependently()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle, OtherSlot);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Frame(new DS4State { Cross = true }, OtherSlot);
        Frame(new DS4State(), OtherSlot);

        Mapping.CommitNeutral(Slot); // This controller stopped mapping/disconnected.
        Assert.IsTrue(handler.LeftHeld, "Another controller still owns the output.");
        Frame(new DS4State());
        Assert.IsTrue(handler.LeftHeld, "Cleared ownership must not revive or cancel another slot.");
        Frame(new DS4State { Cross = true }, OtherSlot);
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void RemovingToggleBindingReleasesItAndReaddingDoesNotRestoreOldLatch()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Bind(DS4Controls.Cross, null);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld, "A removed profile binding must release its output.");
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld, "Readding an idle binding must not inherit a removed latch.");
        Frame(new DS4State { Cross = true });
        Assert.IsTrue(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp, LeftDown }, handler.MouseEvents);
    }

    [TestMethod]
    public void NeutralCommitReleasesToggleAndNextMappedFrameStartsUnlatched()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Mapping.CommitNeutral(Slot);
        Assert.IsFalse(handler.LeftHeld);
        Mapping.CommitNeutral(Slot);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
        Frame(new DS4State { Cross = true });
        Assert.IsTrue(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp, LeftDown }, handler.MouseEvents);
    }

    [TestMethod]
    public void OrdinaryCommitRetainsConfiguredLatchUntilExplicitNeutralRetirement()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Mapping.Commit(Slot);
        Mapping.Commit(Slot);
        Assert.IsTrue(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown }, handler.MouseEvents);
        Mapping.CommitNeutral(Slot);
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void OrdinaryCommitPrunesRemovedBindingEvenWithoutNewMappingFrame()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Bind(DS4Controls.Cross, null);
        Mapping.Commit(Slot);
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void ChangingToggleToOrdinaryBindingDiscardsItsOldLatch()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Bind(DS4Controls.Cross, X360Controls.LeftMouse);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld);
        Frame(new DS4State { Cross = true });
        Assert.IsTrue(handler.LeftHeld);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp, LeftDown, LeftUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void ChangingToggleTargetReleasesOldTargetWithoutPressingNewTarget()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        Frame(new DS4State { Cross = true });
        Frame(new DS4State());
        Bind(DS4Controls.Cross, X360Controls.RightMouse, DS4KeyType.Toggle);
        Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld);
        Assert.IsFalse(handler.RightHeld);
        Frame(new DS4State { Cross = true });
        Assert.IsTrue(handler.RightHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp, RightDown }, handler.MouseEvents);
    }

    [TestMethod]
    public void ShiftChangePrunesInactiveFullPullToggleWithoutActivatingAnotherTarget()
    {
        Bind(DS4Controls.R2FullPull, X360Controls.LeftMouse, DS4KeyType.Toggle);
        DS4ControlSettings setting = Global.store.GetDS4CSetting(Slot, DS4Controls.R2FullPull);
        setting.UpdateSettings(true, X360Controls.RightMouse, string.Empty, DS4KeyType.Toggle, 4);
        Global.R2OutputSettings[Slot].twoStageMode = TwoStageTriggerMode.Normal;
        Frame(new DS4State { R2 = 255, R2Raw = 255 });
        Frame(new DS4State());
        Frame(new DS4State());
        Assert.IsTrue(handler.LeftHeld);
        Frame(new DS4State { Triangle = true });
        Assert.IsFalse(handler.LeftHeld, "Changing the active binding must retire the omitted old owner.");
        Assert.IsFalse(handler.RightHeld, "Modifier change alone must not press the new target.");
        Frame(new DS4State { Triangle = true, R2 = 255, R2Raw = 255 });
        Assert.IsTrue(handler.RightHeld);
        Frame(new DS4State { Triangle = true });
        Frame(new DS4State { Triangle = true });
        Assert.IsTrue(handler.RightHeld);
        Frame(new DS4State());
        Assert.IsFalse(handler.RightHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp, RightDown, RightUp }, handler.MouseEvents);
    }

    [TestMethod]
    public void HeldToggleEmitsOneEdgeAndStaysLatchedThroughIdleFrames()
    {
        Bind(DS4Controls.Cross, X360Controls.LeftMouse, DS4KeyType.Toggle);
        for (int frame = 0; frame < 256; frame++) Frame(new DS4State { Cross = true });
        for (int frame = 0; frame < 256; frame++) Frame(new DS4State());
        Assert.IsTrue(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown }, handler.MouseEvents);
        for (int frame = 0; frame < 256; frame++) Frame(new DS4State { Cross = true });
        for (int frame = 0; frame < 256; frame++) Frame(new DS4State());
        Assert.IsFalse(handler.LeftHeld);
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void TwoStageTriggerToggleStaysLatchedAfterItsZoneBecomesInactive(bool left, bool fullPull)
    {
        DS4Controls control = left ?
            (fullPull ? DS4Controls.L2FullPull : DS4Controls.L2) :
            (fullPull ? DS4Controls.R2FullPull : DS4Controls.R2);
        Bind(control, X360Controls.LeftMouse, DS4KeyType.Toggle);
        (left ? Global.L2OutputSettings[Slot] : Global.R2OutputSettings[Slot]).twoStageMode =
            fullPull ? TwoStageTriggerMode.Normal : TwoStageTriggerMode.HairTrigger;
        byte value = fullPull ? (byte)255 : (byte)100;
        DS4State Pressed() => left ? new DS4State { L2 = value, L2Raw = value } :
            new DS4State { R2 = value, R2Raw = value };

        Frame(Pressed());
        Assert.IsTrue(handler.LeftHeld);
        for (int frame = 0; frame < 4; frame++) Frame(new DS4State());
        Assert.IsTrue(handler.LeftHeld,
            "An idle two-stage trigger is still a configured toggle binding, not a removed owner.");
        Frame(Pressed());
        Assert.IsFalse(handler.LeftHeld, "The next trigger press must turn the same latch off.");
        CollectionAssert.AreEqual(new[] { LeftDown, LeftUp }, handler.MouseEvents);
    }

    private static DS4State Input(bool click, bool other) => new()
    {
        R2 = click ? (byte)255 : (byte)0,
        LX = other ? (byte)210 : (byte)128,
        LY = other ? (byte)0 : (byte)128,
        RX = other ? (byte)255 : (byte)128,
        Square = other,
        Circle = other,
    };

    private static void Bind(DS4Controls input, object output,
        DS4KeyType keyType = DS4KeyType.None, int slot = Slot)
    {
        DS4ControlSettings setting = Global.store.GetDS4CSetting(slot, input);
        setting.UpdateSettings(false, output, string.Empty, keyType);
        if (output is int key) setting.action.actionAlias = (uint)key;
    }

    private DS4State Frame(DS4State source, int slot = Slot)
    {
        source.elapsedTime = .004;
        var mapped = new DS4State();
        source.CopyExtrasTo(mapped);
        Mapping.MapCustom(slot, source, mapped, new DS4StateExposed(source),
            slot == Slot ? mouse : otherMouse, service);
        Mapping.Commit(slot);
        return mapped;
    }

    private static double ReadRemainder(string name) => (double)typeof(Mapping)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static void WriteRemainder(string name, double value) => typeof(Mapping)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);

    private sealed class NoHidDevice : DS4Device
    {
        internal NoHidDevice() : base("Concurrent mapper test", InputDeviceType.DualSense,
            ConnectionType.BT) { }
    }

    private sealed class RecordingHandler : VirtualKBMBase
    {
        internal readonly List<uint> MouseEvents = new();
        internal readonly List<(int X, int Y)> Moves = new();
        internal readonly HashSet<uint> Keys = new();
        internal bool LeftHeld, RightHeld;
        public override bool Connect() => throw new AssertFailedException("No system input allowed.");
        public override bool Disconnect() => throw new AssertFailedException("No system input allowed.");
        public override void MoveRelativeMouse(int x, int y) => Moves.Add((x, y));
        public override void MoveAbsoluteMouse(double x, double y) => Assert.Fail("Unexpected absolute mouse.");
        public override void PerformMouseWheelEvent(int vertical, int horizontal) => Assert.Fail("Unexpected wheel.");
        public override void PerformMouseButtonEvent(uint button)
        {
            MouseEvents.Add(button);
            if (button == LeftDown) LeftHeld = true;
            else if (button == LeftUp) LeftHeld = false;
            else if (button == RightDown) RightHeld = true;
            else if (button == RightUp) RightHeld = false;
        }
        public override void PerformMouseButtonPress(uint button) => PerformMouseButtonEvent(button);
        public override void PerformMouseButtonRelease(uint button) => PerformMouseButtonEvent(button);
        public override void PerformKeyPress(uint key) => Keys.Add(key);
        public override void PerformKeyPressAlt(uint key) => Keys.Add(key);
        public override void PerformKeyRelease(uint key) => Keys.Remove(key);
        public override void PerformKeyReleaseAlt(uint key) => Keys.Remove(key);
        public override string GetDisplayName() => "Concurrent mapper recording";
        public override string GetIdentifier() => "Concurrent mapper recording";
        public override string GetFullDisplayName() => "Concurrent mapper recording";
    }
}
