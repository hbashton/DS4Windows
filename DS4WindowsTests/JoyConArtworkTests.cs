using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Forms.ViewModels;
using System.Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class JoyConArtworkTests
{
    [DataTestMethod]
    [DataRow(InputDeviceType.Switch2JoyConLeft, true, false)]
    [DataRow(InputDeviceType.Switch2JoyConRight, false, true)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, false, false)]
    [DataRow(InputDeviceType.JoyConL, true, false)]
    [DataRow(InputDeviceType.JoyConR, false, true)]
    [DataRow(InputDeviceType.JoyConGrip, false, false)]
    public void PhysicalModelSelectsSharedFrozenArtwork(InputDeviceType type, bool left, bool right)
    {
        var expected = left ? JoyConArtwork.Left : right ? JoyConArtwork.Right : JoyConArtwork.Pair;
        var actual = JoyConArtwork.ForDevice(type);
        Assert.AreSame(expected, actual);
        Assert.IsTrue(actual.IsFrozen);
        Assert.IsTrue(actual.Width > 0 && actual.Height > 0);
        Assert.IsTrue(ControllerUiCapabilities.For(type).HasControllerArtwork);
    }

    [TestMethod]
    public void OtherControllersKeepTheirOwnArtwork()
    {
        Assert.IsNull(JoyConArtwork.ForDevice(InputDeviceType.Switch2Pro));
        Assert.IsNull(JoyConArtwork.ForDevice(InputDeviceType.DualSense));
        Assert.AreNotSame(JoyConArtwork.Left, JoyConArtwork.Right);
        Assert.IsTrue(JoyConArtwork.Pair.Width > JoyConArtwork.Left.Width);
    }

    [TestMethod]
    public void MappingTargetsFitTheSharedDiagram()
    {
        var bounds = new Rect(0, 0, 440, 220);
        Assert.AreEqual(440.0, JoyConArtwork.Diagram.Width);
        Assert.AreEqual(220.0, JoyConArtwork.Diagram.Height);
        Assert.IsTrue(JoyConArtwork.Diagram.IsFrozen);
        Assert.AreEqual(16, JoyConArtwork.Buttons.Count);
        foreach (var button in JoyConArtwork.Buttons)
            Assert.IsTrue(bounds.Contains(button.Value), button.Key);
        Assert.IsTrue(bounds.Contains(JoyConArtwork.LeftStick));
        Assert.IsTrue(bounds.Contains(JoyConArtwork.RightStick));
    }

    [DataTestMethod]
    [DataRow(InputDeviceType.Switch2Pro, false)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, true)]
    [DataRow(InputDeviceType.Switch2JoyConRight, true)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, true)]
    [DataRow(InputDeviceType.DualSense, false)]
    public void JoyConOnlySettingsMatchPhysicalHardware(InputDeviceType type, bool expected)
    {
        Assert.AreEqual(expected, ControllerUiCapabilities.For(type).ShowSwitch2JoyConControls);
        Assert.IsTrue(ControllerUiCapabilities.For(null).ShowSwitch2JoyConControls);
    }
}
