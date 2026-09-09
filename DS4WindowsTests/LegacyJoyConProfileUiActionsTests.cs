using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyJoyConProfileUiActionsTests
{
    [DataTestMethod]
    [DataRow(InputDeviceType.JoyConL, (int)JoyConView.SidewaysLeft)]
    [DataRow(InputDeviceType.JoyConR, (int)JoyConView.SidewaysRight)]
    [DataRow(InputDeviceType.JoyConGrip, (int)JoyConView.Pair)]
    public void OriginalArtworkAndMappingAgreeOnProfileHoldingStyle(InputDeviceType type, int view)
    {
        Assert.AreEqual((JoyConView)view, JoyConArtwork.ResolveView(type, Switch2JoyConHoldMode.Horizontal));
        var image = JoyConArtwork.ForDevice(type, Switch2JoyConHoldMode.Horizontal);
        Assert.AreSame(type == InputDeviceType.JoyConL ? JoyConArtwork.SidewaysLeft :
            type == InputDeviceType.JoyConR ? JoyConArtwork.SidewaysRight : JoyConArtwork.Pair, image);
        Assert.IsTrue(image.IsFrozen);
    }

    [TestMethod]
    [DoNotParallelize]
    public void CardHoldActionUsesCurrentProfileAndReportsPersistenceTruthfully()
    {
        var left = Connection(InputDeviceType.JoyConL, 1);
        left.Group = new LegacyJoyConGroup(left, null, 0, new NoProjection());
        var priorMode = Global.Switch2JoyConStandaloneHoldMode[0];
        string priorProfile = Global.ProfilePath[0];
        try
        {
            Global.ProfilePath[0] = "Nintendo UI test";
            int saves = 0;
            Assert.IsTrue(LegacyJoyConProfileUiActions.TrySetHoldMode(left, Switch2JoyConHoldMode.Horizontal,
                (slot, name) => { Assert.AreEqual(0, slot); Assert.AreEqual("Nintendo UI test", name); saves++; return true; }, out bool persisted));
            Assert.IsTrue(persisted);
            Assert.AreEqual(1, saves);
            Assert.AreEqual(Switch2JoyConHoldMode.Horizontal, Global.Switch2JoyConStandaloneHoldMode[0]);
            Assert.IsTrue(LegacyJoyConProfileUiActions.TrySetHoldMode(left, Switch2JoyConHoldMode.Vertical,
                (_, _) => false, out persisted));
            Assert.IsFalse(persisted);
            Assert.AreEqual(Switch2JoyConHoldMode.Vertical, Global.Switch2JoyConStandaloneHoldMode[0]);
            left.Paused = true;
            Assert.IsFalse(LegacyJoyConProfileUiActions.TrySetHoldMode(left, Switch2JoyConHoldMode.Horizontal,
                (_, _) => throw new AssertFailedException("Paused transition must not save."), out _));
        }
        finally { Global.ProfilePath[0] = priorProfile; Global.Switch2JoyConStandaloneHoldMode[0] = priorMode; }
    }

    [TestMethod]
    public void CalibrationTargetsBothExactHalvesAndNoReusedSlot()
    {
        var left = Connection(InputDeviceType.JoyConL, 1);
        var right = Connection(InputDeviceType.JoyConR, 2);
        var leftSingle = new LegacyJoyConGroup(left, null, 0, new NoProjection());
        var rightSingle = new LegacyJoyConGroup(right, null, 0, new NoProjection());
        var pair = new LegacyJoyConGroup(left, right, 3, new NoProjection(), leftSingle, rightSingle);
        left.Group = right.Group = pair;
        var queue = new List<Action>();
        var reset = new List<DS4Device>();
        Assert.IsTrue(LegacyJoyConProfileUiActions.TryResetGyroCalibration(left, reset.Add, (_, action) => queue.Add(action)));
        Assert.AreEqual(2, queue.Count);
        Assert.AreEqual(0, reset.Count, "Calibration belongs on the physical input queue, not the UI thread.");
        queue[0](); queue[1]();
        CollectionAssert.AreEquivalent(new[] { left.Device, right.Device }, reset);
        reset.Clear(); queue.Clear();
        Assert.IsFalse(LegacyJoyConProfileUiActions.IsStandalone(left));
        Assert.IsFalse(LegacyJoyConProfileUiActions.TryResetGyroCalibration(right, reset.Add));
        Assert.IsTrue(LegacyJoyConProfileUiActions.TryResetGyroCalibration(left, reset.Add, (_, action) => queue.Add(action)));
        pair.Active = false;
        left.Group = leftSingle; right.Group = rightSingle;
        queue[0](); queue[1]();
        Assert.AreEqual(0, reset.Count, "Queued old-pair calibration cannot alter a successor logical controller.");
        Assert.IsTrue(LegacyJoyConProfileUiActions.IsStandalone(left));
        Assert.IsTrue(LegacyJoyConProfileUiActions.TryResetGyroCalibration(left, reset.Add));
        CollectionAssert.AreEqual(new[] { left.Device }, reset);
    }

    private static LegacyJoyConConnection Connection(InputDeviceType type, ulong generation) =>
        new(new FakeDevice(type), type == InputDeviceType.JoyConL ? 0 : 1, generation);
    private sealed class NoProjection : ILegacyJoyConProfileProjection
    {
        public bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination) => false;
    }
    private sealed class FakeDevice : DS4Device
    {
        internal FakeDevice(InputDeviceType type) : base("Original Joy-Con UI test", type, ConnectionType.BT) { }
    }
}
