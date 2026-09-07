using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2JoyConActionAvailabilityTests
{
    [TestMethod]
    public void ControllerCardShowsLinkCancelAndExplainsAutomaticPairingLock()
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
            Switch2ControllerModel.JoyCon2Left, 101, 102, out var runtime, out _));
        var card = new CompositeDeviceModel(runtime, DS4Windows.Global.TEST_PROFILE_INDEX, null, null);
        var candidate = new Switch2JoyConPairCandidate(10, Switch2ControllerModel.JoyCon2Left, 1);
        int changes = 0;
        card.JoyConLinkActionChanged += (_, _) => changes++;
        card.RefreshJoyConLinkAction(candidate, default, false, true, false);
        Assert.IsTrue(card.JoyConLinkAction.Visible);
        Assert.IsTrue(card.JoyConLinkAction.Enabled);
        Assert.AreEqual("Link", card.JoyConLinkAction.Text);
        card.RefreshJoyConLinkAction(candidate, default, false, true, false);
        Assert.AreEqual(1, changes, "Polling must not churn unchanged UI bindings.");
        card.RefreshJoyConLinkAction(candidate, default, true, true, false);
        Assert.AreEqual("Cancel", card.JoyConLinkAction.Text);
        Assert.IsTrue(card.JoyConLinkAction.IsArmed);
        card.RefreshJoyConLinkAction(candidate, default, false, false, true);
        Assert.IsFalse(card.JoyConLinkAction.Enabled);
        StringAssert.Contains(card.JoyConLinkAction.ToolTip, "automatic pairing");
        card.RefreshJoyConLinkAction(default, default, false, false, false);
        Assert.IsFalse(card.JoyConLinkAction.Visible);
        runtime.ReadWaitEv.Dispose();
    }

    [DataTestMethod]
    [DataRow(false, false, false, 1, 2, false, false, false)]
    [DataRow(true, true, false, 1, 2, false, false, false)]
    [DataRow(true, false, true, 1, 2, false, false, false)]
    [DataRow(true, false, false, 0, 0, false, false, false)]
    [DataRow(true, false, false, 1, 0, false, true, false)]
    [DataRow(true, false, false, 0, 2, false, false, true)]
    [DataRow(true, false, false, 1, 2, true, true, true)]
    [DataRow(true, false, false, 1, 1, false, true, true)]
    [DataRow(true, false, false, -1, 2, false, false, true)]
    public void ActionsRequireTheExactAvailableSelections(bool running, bool automatic,
        bool busy, int left, int right, bool join, bool useLeft, bool useRight)
    {
        var availability = new Switch2JoyConActionAvailability(running, automatic,
            busy, left, right);
        Assert.AreEqual(running && !automatic && !busy, availability.CanSelect);
        Assert.AreEqual(join, availability.CanJoin);
        Assert.AreEqual(useLeft, availability.CanUseLeft);
        Assert.AreEqual(useRight, availability.CanUseRight);
    }

    [TestMethod]
    public void RefreshPreservesExactSideAndIdentityAcrossReordering()
    {
        Switch2JoyConPairCandidate[] candidates =
        {
            new(3, Switch2ControllerModel.JoyCon2Left, 3),
            new(2, Switch2ControllerModel.JoyCon2Right, 2),
            new(1, Switch2ControllerModel.JoyCon2Left, 1),
        };
        Assert.AreEqual(1, Switch2JoyConActionAvailability.PreserveSelection(1,
            candidates, Switch2ControllerModel.JoyCon2Left));
        Assert.AreEqual(2, Switch2JoyConActionAvailability.PreserveSelection(2,
            candidates, Switch2ControllerModel.JoyCon2Right));
        Assert.AreEqual(0, Switch2JoyConActionAvailability.PreserveSelection(2,
            candidates, Switch2ControllerModel.JoyCon2Left));
    }

    [TestMethod]
    public void RemovedSelectionDoesNotBecomeAnotherRemainingController()
    {
        Switch2JoyConPairCandidate[] candidates =
        {
            new(3, Switch2ControllerModel.JoyCon2Left, 3),
        };
        int selected = Switch2JoyConActionAvailability.PreserveSelection(1,
            candidates, Switch2ControllerModel.JoyCon2Left);
        Assert.AreEqual(0, selected);
        Assert.AreEqual(0, Switch2JoyConActionAvailability.PreserveSelection(selected,
            candidates, Switch2ControllerModel.JoyCon2Left));
    }

    [TestMethod]
    public void MultipleCandidatesAndWrongRolesRequireExplicitSelection()
    {
        Switch2JoyConPairCandidate[] candidates =
        {
            new(1, Switch2ControllerModel.JoyCon2Left, 1),
            new(3, Switch2ControllerModel.JoyCon2Left, 3),
            new(4, Switch2ControllerModel.ProController2, 4),
        };
        Assert.AreEqual(0, Switch2JoyConActionAvailability.PreserveSelection(0,
            candidates, Switch2ControllerModel.JoyCon2Left));
        Assert.AreEqual(0, Switch2JoyConActionAvailability.PreserveSelection(0,
            candidates, Switch2ControllerModel.JoyCon2Right));
        Assert.AreEqual(0, Switch2JoyConActionAvailability.PreserveSelection(4,
            candidates, Switch2ControllerModel.ProController2));
    }
}
