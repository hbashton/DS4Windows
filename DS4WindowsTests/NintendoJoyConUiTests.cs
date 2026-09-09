using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms.ViewModels;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class NintendoJoyConUiTests
{
    [TestMethod]
    public void OriginalHalvesUseSameTwoClickLinkCancelAndUnlinkCard()
    {
        var left = Candidate(true, 1);
        var right = Candidate(false, 2);
        var selection = new NintendoJoyConManualPairSelection();
        Assert.AreEqual(Switch2JoyConManualPairSelectionDisposition.Armed, selection.Select(right).Disposition);
        Assert.AreEqual(Switch2JoyConManualPairSelectionDisposition.Cancelled, selection.Select(right).Disposition);
        selection.Select(right);
        var result = selection.Select(left);
        Assert.AreEqual(Switch2JoyConManualPairSelectionDisposition.PairReady, result.Disposition);
        Assert.AreEqual(right, result.Preferred);
        Assert.AreEqual(left, result.Left);
        Assert.AreEqual(right, result.Right);
    }

    [TestMethod]
    public void NewConnectionInReusedSlotCannotCompleteOldSelection()
    {
        var left = Candidate(true, 1);
        var replacement = Candidate(true, 3);
        var selection = new NintendoJoyConManualPairSelection();
        selection.Select(left);
        Assert.IsTrue(selection.Reconcile(new[] { replacement }));
        Assert.IsFalse(selection.IsArmed(replacement));
    }

    [TestMethod]
    public void SameSideAndMixedGenerationAreNotCompatible()
    {
        var selection = new NintendoJoyConManualPairSelection();
        selection.Select(Candidate(true, 1));
        Assert.AreEqual(Switch2JoyConManualPairSelectionDisposition.IncompatibleSide, selection.Select(Candidate(true, 2)).Disposition);
        var newer = new NintendoJoyConCandidate(new Switch2JoyConPairCandidate(4, Switch2ControllerModel.JoyCon2Right, 4));
        Assert.AreEqual(Switch2JoyConManualPairSelectionDisposition.IncompatibleSide, selection.Select(newer).Disposition);
    }

    [TestMethod]
    public void UniversalProfileSettingsAreNeverHiddenByControllerCapabilities()
    {
        var profile = (ProfileSettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ProfileSettingsViewModel));
        Assert.IsTrue(profile.ShowSwitch2Controls);
        Assert.IsTrue(profile.ShowSwitch2JoyConControls);
        Assert.IsTrue(profile.ShowSwitch2StandaloneJoyConControls);
        Assert.IsTrue(profile.ShowSwitch2UsbHeadsetHelp);
    }

    private static NintendoJoyConCandidate Candidate(bool left, ulong generation)
    {
        var device = (JoyConDevice)RuntimeHelpers.GetUninitializedObject(typeof(JoyConDevice));
        typeof(DS4Device).GetField("deviceType", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(device, left ? InputDeviceType.JoyConL : InputDeviceType.JoyConR);
        return new(default, new LegacyJoyConConnection(device, 0, generation));
    }
}
