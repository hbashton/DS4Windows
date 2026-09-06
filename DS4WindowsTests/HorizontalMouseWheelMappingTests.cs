using DS4Windows;
using DS4Windows.DS4Control;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class HorizontalMouseWheelMappingTests
{
    private VirtualKBMBase previousHandler = null!;
    private VirtualKBMMapping previousMapping = null!;
    private Mapping.SyntheticState previousGlobalState = null!;
    private Mapping.SyntheticState[] previousDeviceState = null!;
    private RecordingHandler handler = null!;

    [TestInitialize]
    public void Initialize()
    {
        previousHandler = Global.outputKBMHandler;
        previousMapping = Global.outputKBMMapping;
        previousGlobalState = Mapping.globalState;
        previousDeviceState = Mapping.deviceState;

        handler = new RecordingHandler();
        var mapping = new SendInputMapping();
        mapping.PopulateConstants();
        mapping.PopulateMappings();
        Global.outputKBMHandler = handler;
        Global.outputKBMMapping = mapping;
        Mapping.globalState = new Mapping.SyntheticState();
        Mapping.deviceState = Enumerable.Range(0,
                Global.MAX_DS4_CONTROLLER_COUNT)
            .Select(_ => new Mapping.SyntheticState()).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Release any digital wheel action admitted by the test before
        // restoring the process-wide mapper owners.
        Mapping.Commit(0);
        Global.outputKBMHandler = previousHandler;
        Global.outputKBMMapping = previousMapping;
        Mapping.globalState = previousGlobalState;
        Mapping.deviceState = previousDeviceState;
    }

    [TestMethod]
    public void OutputEnumAppendsHorizontalWheelWithoutRenumberingUnbound()
    {
        Assert.AreEqual(42, (byte)X360Controls.Unbound);
        Assert.AreEqual(43, (byte)X360Controls.WLEFT);
        Assert.AreEqual(44, (byte)X360Controls.WRIGHT);
        Assert.AreEqual("Mouse Wheel Left",
            Global.getX360ControlString(X360Controls.WLEFT));
        Assert.AreEqual("Mouse Wheel Right",
            Global.getX360ControlString(X360Controls.WRIGHT));
        Assert.AreEqual(X360Controls.WLEFT,
            Global.getX360ControlsByName("Mouse Wheel Left"));
        Assert.AreEqual(X360Controls.WRIGHT,
            Global.getX360ControlsByName("Mouse Wheel Right"));
        Assert.IsTrue(BindAssociation.IsMouseRange(X360Controls.WLEFT));
        Assert.IsTrue(OutBinding.IsMouseRange(X360Controls.WRIGHT));
        Assert.IsTrue(ControlService.ActionMapsToMouse(
            DS4ControlSettings.ActionType.Button, X360Controls.WLEFT));
        Assert.IsTrue(ControlService.ActionMapsToMouse(
            DS4ControlSettings.ActionType.Button, X360Controls.WDOWN));
        Assert.IsFalse(ControlService.ActionMapsToMouse(
            DS4ControlSettings.ActionType.Button, X360Controls.A));
        Assert.IsFalse(ControlService.ActionMapsToMouse(
            DS4ControlSettings.ActionType.Key, X360Controls.WRIGHT));
    }

    [TestMethod]
    public void DigitalHorizontalWheelUsesOnlyHorizontalHandlerLane()
    {
        Mapping.MapClick(0, Mapping.Click.WLEFT);
        Mapping.Commit(0);
        Mapping.MapClick(0, Mapping.Click.WLEFT);
        Mapping.Commit(0);
        Mapping.Commit(0);
        Mapping.MapClick(0, Mapping.Click.WRIGHT);
        Mapping.Commit(0);

        CollectionAssert.AreEqual(new[]
        {
            (Vertical: 0, Horizontal: -120),
            (Vertical: 0, Horizontal: 120),
        }, handler.WheelEvents);
    }

    [TestMethod]
    public void VerticalAndHorizontalWheelActionsRemainIndependent()
    {
        Mapping.MapClick(0, Mapping.Click.WUP);
        Mapping.MapClick(0, Mapping.Click.WRIGHT);
        Mapping.Commit(0);

        CollectionAssert.AreEquivalent(new[]
        {
            (Vertical: 120, Horizontal: 0),
            (Vertical: 0, Horizontal: 120),
        }, handler.WheelEvents);
    }

    [TestMethod]
    public void HeldHorizontalWheelDoesNotInterfereWithShiftTriggers()
    {
        Mapping.MapClick(0, Mapping.Click.WLEFT);
        Mapping.Commit(0);

        var state = new DS4State { Cross = true };
        var fields = new DS4StateFieldMapping();
        fields.PopulateFieldMapping(state, new DS4StateExposed(state), null);

        Assert.IsTrue(Mapping.ShiftTrigger(1, 0, state,
            new DS4StateExposed(state), null, fields));
    }

    private sealed class RecordingHandler : VirtualKBMBase
    {
        internal List<(int Vertical, int Horizontal)> WheelEvents { get; } =
            new();

        public override bool Connect() => true;
        public override bool Disconnect() => true;
        public override void MoveRelativeMouse(int x, int y) { }
        public override void MoveAbsoluteMouse(double x, double y) { }
        public override void PerformMouseWheelEvent(int vertical,
            int horizontal) => WheelEvents.Add((vertical, horizontal));
        public override void PerformMouseButtonEvent(uint mouseButton) { }
        public override void PerformMouseButtonPress(uint mouseButton) { }
        public override void PerformMouseButtonRelease(uint mouseButton) { }
        public override void PerformKeyPress(uint key) { }
        public override void PerformKeyPressAlt(uint key) { }
        public override void PerformKeyRelease(uint key) { }
        public override void PerformKeyReleaseAlt(uint key) { }
        public override string GetDisplayName() => "recording";
        public override string GetIdentifier() => "recording";
        public override string GetFullDisplayName() => "recording";
    }
}
