using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2DualJoyConGyroModeTests
{
    [DataTestMethod]
    [DataRow(Switch2DualGyroActivationMode.Hold)]
    [DataRow(Switch2DualGyroActivationMode.Toggle)]
    public void MultipleButtonsAndIrUseOneSameSideOrGate(
        Switch2DualGyroActivationMode activationMode)
    {
        Switch2DualGyroModeState state = default;
        const Switch2JoyConProfileButton first =
            Switch2JoyConProfileButton.LeftPaddle1;
        const Switch2JoyConProfileButton second =
            Switch2JoyConProfileButton.LeftIrSensor;
        var configuration = Configuration(
            Switch2DualGyroMode.SwitchDominantSide, activationMode,
            Switch2DualGyroDominantSide.Right, first | second);

        Resolve(ref state, configuration);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            Resolve(ref state, configuration, first).DominantSide);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            Resolve(ref state, configuration, first | second).DominantSide);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            Resolve(ref state, configuration, second).DominantSide,
            "Releasing one input must not release the aggregate gate.");
        Assert.AreEqual(activationMode == Switch2DualGyroActivationMode.Hold ?
                Switch2DualGyroDominantSide.Right :
                Switch2DualGyroDominantSide.Left,
            Resolve(ref state, configuration).DominantSide);
    }

    [DataTestMethod]
    [DataRow(Switch2DualGyroMode.SwitchDominantSide)]
    [DataRow(Switch2DualGyroMode.SwitchGyroSide)]
    [DataRow(Switch2DualGyroMode.SingleSideToggle)]
    public void InheritedHoldReleaseCannotUndoAPressWeNeverAdmitted(
        Switch2DualGyroMode mode)
    {
        Switch2DualGyroModeState state = default;
        var configuration = Configuration(mode,
            Switch2DualGyroActivationMode.Hold,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftPaddle1);
        var baseline = Resolve(ref state, configuration,
            Switch2JoyConProfileButton.LeftPaddle1);
        var released = Resolve(ref state, configuration);
        Assert.AreEqual(baseline.DominantSide, released.DominantSide);
        Assert.AreEqual(baseline.LeftActive, released.LeftActive);
        Assert.AreEqual(baseline.RightActive, released.RightActive);

        var pressed = Resolve(ref state, configuration,
            Switch2JoyConProfileButton.LeftPaddle1);
        Assert.IsTrue(pressed.DominantSide != baseline.DominantSide ||
            pressed.LeftActive != baseline.LeftActive);
        var restored = Resolve(ref state, configuration);
        Assert.AreEqual(baseline.DominantSide, restored.DominantSide);
        Assert.AreEqual(baseline.LeftActive, restored.LeftActive);
        Assert.AreEqual(baseline.RightActive, restored.RightActive);
    }

    [TestMethod]
    public void HoldSwitchDominantSideTracksBothEdges()
    {
        Switch2DualGyroModeState state = default;
        Switch2DualGyroConfiguration configuration = Configuration(
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroActivationMode.Hold,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftPaddle1);

        Switch2DualGyroRuntimePolicy initial = Resolve(ref state,
            configuration);
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            initial.DominantSide);
        Switch2DualGyroRuntimePolicy pressed = Resolve(ref state,
            configuration, Switch2JoyConProfileButton.LeftPaddle1);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            pressed.DominantSide);
        Assert.AreEqual(pressed.DominantSide, Resolve(ref state,
            configuration, Switch2JoyConProfileButton.LeftPaddle1).
                DominantSide, "A held button is not another edge.");
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            Resolve(ref state, configuration).DominantSide,
            "Hold release restores the configured side.");
    }

    [TestMethod]
    public void ToggleSwitchDominantSideIgnoresRelease()
    {
        Switch2DualGyroModeState state = default;
        Switch2DualGyroConfiguration configuration = Configuration(
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroActivationMode.Toggle,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftPaddle1);

        Resolve(ref state, configuration);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            Resolve(ref state, configuration,
                Switch2JoyConProfileButton.LeftPaddle1).DominantSide);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            Resolve(ref state, configuration).DominantSide);
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            Resolve(ref state, configuration,
                Switch2JoyConProfileButton.LeftPaddle1).DominantSide);
    }

    [TestMethod]
    public void SwitchGyroSideProducesExactlyOneActiveImu()
    {
        Switch2DualGyroModeState state = default;
        Switch2DualGyroConfiguration configuration = Configuration(
            Switch2DualGyroMode.SwitchGyroSide,
            Switch2DualGyroActivationMode.Hold,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftPaddle1);

        Switch2DualGyroRuntimePolicy initial = Resolve(ref state,
            configuration);
        Assert.IsFalse(initial.LeftActive);
        Assert.IsTrue(initial.RightActive);
        Switch2DualGyroRuntimePolicy pressed = Resolve(ref state,
            configuration, Switch2JoyConProfileButton.LeftPaddle1);
        Assert.IsTrue(pressed.LeftActive);
        Assert.IsFalse(pressed.RightActive);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            pressed.DominantSide);
        Switch2DualGyroRuntimePolicy released = Resolve(ref state,
            configuration);
        Assert.IsFalse(released.LeftActive);
        Assert.IsTrue(released.RightActive);
    }

    [TestMethod]
    public void SingleSideToggleChangesOnlyOriginatingSide()
    {
        Switch2DualGyroModeState state = default;
        Switch2DualGyroConfiguration configuration = Configuration(
            Switch2DualGyroMode.SingleSideToggle,
            Switch2DualGyroActivationMode.Toggle,
            Switch2DualGyroDominantSide.None,
            Switch2JoyConProfileButton.LeftPaddle1,
            Switch2JoyConProfileButton.RightPaddle1);

        Switch2DualGyroRuntimePolicy initial = Resolve(ref state,
            configuration);
        Assert.IsTrue(initial.LeftActive);
        Assert.IsTrue(initial.RightActive);
        Switch2DualGyroRuntimePolicy leftOff = Resolve(ref state,
            configuration, Switch2JoyConProfileButton.LeftPaddle1);
        Assert.IsFalse(leftOff.LeftActive);
        Assert.IsTrue(leftOff.RightActive);
        Resolve(ref state, configuration);
        Switch2DualGyroRuntimePolicy bothOff = Resolve(ref state,
            configuration, rightButtons:
                Switch2JoyConProfileButton.RightPaddle1);
        Assert.IsFalse(bothOff.LeftActive);
        Assert.IsFalse(bothOff.RightActive);
        Assert.AreEqual(Switch2DualGyroDominantSide.None,
            bothOff.DominantSide);
    }

    [TestMethod]
    public void ConfigurationAndPairChangesSynchronizeHeldButtonWithoutEdge()
    {
        Switch2DualGyroModeState state = default;
        Switch2DualGyroConfiguration first = Configuration(
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroActivationMode.Toggle,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.None);
        Switch2DualGyroConfiguration second = Configuration(
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroActivationMode.Toggle,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftPaddle1);

        Resolve(ref state, first);
        Switch2DualGyroRuntimePolicy reconfigured = Resolve(ref state, second,
            Switch2JoyConProfileButton.LeftPaddle1);
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            reconfigured.DominantSide,
            "A profile edit while held must not manufacture a press edge.");
        ulong configurationEpoch = reconfigured.ConfigurationEpoch;
        Switch2DualGyroRuntimePolicy newPair = Resolve(ref state, second,
            Switch2JoyConProfileButton.LeftPaddle1, pairEpoch: 2);
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            newPair.DominantSide);
        Assert.IsTrue(newPair.ConfigurationEpoch > configurationEpoch);
    }

    [TestMethod]
    public void InvalidModesButtonsAndNoneDominantFailClosed()
    {
        Assert.IsFalse(Switch2DualGyroConfiguration.TryCreate(true,
            Switch2DualGyroMode.Invalid,
            Switch2DualGyroDominantSide.Right,
            Switch2DualGyroActivationMode.Hold,
            Switch2JoyConProfileButton.None,
            Switch2JoyConProfileButton.None, out _));
        Assert.IsFalse(Switch2DualGyroConfiguration.TryCreate(true,
            Switch2DualGyroMode.SwitchGyroSide,
            Switch2DualGyroDominantSide.None,
            Switch2DualGyroActivationMode.Hold,
            Switch2JoyConProfileButton.None,
            Switch2JoyConProfileButton.None, out _));
        Assert.IsFalse(Switch2DualGyroConfiguration.TryCreate(true,
            Switch2DualGyroMode.SingleSideToggle,
            Switch2DualGyroDominantSide.None,
            Switch2DualGyroActivationMode.Toggle,
            (Switch2JoyConProfileButton)(1u << 29),
            Switch2JoyConProfileButton.None, out _));
    }

    [TestMethod]
    public void InvalidIntervalClearsEdgeBaselineBeforeRecovery()
    {
        Switch2DualGyroModeState state = default;
        Switch2DualGyroConfiguration valid = Configuration(
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroActivationMode.Toggle,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftPaddle1);

        Resolve(ref state, valid);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left,
            Resolve(ref state, valid,
                Switch2JoyConProfileButton.LeftPaddle1).DominantSide);
        Assert.IsFalse(Switch2DualJoyConGyroMode.TryResolve(ref state, 1,
            Switch2JoyConProfileButton.None,
            Switch2JoyConProfileButton.None, default, out _));

        Switch2DualGyroRuntimePolicy recovered = Resolve(ref state, valid,
            Switch2JoyConProfileButton.LeftPaddle1);
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            recovered.DominantSide,
            "Recovery synchronizes the held input instead of replaying it.");
    }

    [TestMethod]
    public void WarmModeResolutionAllocatesNothing()
    {
        Switch2DualGyroModeState state = default;
        Switch2DualGyroConfiguration configuration = Configuration(
            Switch2DualGyroMode.SingleSideToggle,
            Switch2DualGyroActivationMode.Toggle,
            Switch2DualGyroDominantSide.Left,
            Switch2JoyConProfileButton.LeftPaddle1,
            Switch2JoyConProfileButton.RightPaddle1);
        bool valid = true;
        for (int index = 0; index < 1_000; index++)
        {
            valid &= Switch2DualJoyConGyroMode.TryResolve(ref state, 1,
                Switch2JoyConProfileButton.None,
                Switch2JoyConProfileButton.None, configuration, out _);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            valid &= Switch2DualJoyConGyroMode.TryResolve(ref state, 1,
                Switch2JoyConProfileButton.None,
                Switch2JoyConProfileButton.None, configuration, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
    }

    [DataTestMethod]
    [DataRow(Switch2DualGyroMode.SwitchDominantSide, Switch2DualGyroActivationMode.Hold, true)]
    [DataRow(Switch2DualGyroMode.SwitchDominantSide, Switch2DualGyroActivationMode.Hold, false)]
    [DataRow(Switch2DualGyroMode.SwitchDominantSide, Switch2DualGyroActivationMode.Toggle, true)]
    [DataRow(Switch2DualGyroMode.SwitchDominantSide, Switch2DualGyroActivationMode.Toggle, false)]
    [DataRow(Switch2DualGyroMode.SwitchGyroSide, Switch2DualGyroActivationMode.Hold, true)]
    [DataRow(Switch2DualGyroMode.SwitchGyroSide, Switch2DualGyroActivationMode.Hold, false)]
    [DataRow(Switch2DualGyroMode.SwitchGyroSide, Switch2DualGyroActivationMode.Toggle, true)]
    [DataRow(Switch2DualGyroMode.SwitchGyroSide, Switch2DualGyroActivationMode.Toggle, false)]
    [DataRow(Switch2DualGyroMode.SingleSideToggle, Switch2DualGyroActivationMode.Hold, true)]
    [DataRow(Switch2DualGyroMode.SingleSideToggle, Switch2DualGyroActivationMode.Hold, false)]
    [DataRow(Switch2DualGyroMode.SingleSideToggle, Switch2DualGyroActivationMode.Toggle, true)]
    [DataRow(Switch2DualGyroMode.SingleSideToggle, Switch2DualGyroActivationMode.Toggle, false)]
    public void AllModesKeepSameSideHandoffHeldAndBothSidesIndependent(
        Switch2DualGyroMode mode, Switch2DualGyroActivationMode activation,
        bool left)
    {
        Switch2DualGyroModeState state = default;
        var ir = left ? Switch2JoyConProfileButton.LeftIrSensor :
            Switch2JoyConProfileButton.RightIrSensor;
        var button = left ? Switch2JoyConProfileButton.LeftPaddle1 :
            Switch2JoyConProfileButton.RightPaddle1;
        var configuration = Configuration(mode, activation,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftIrSensor | Switch2JoyConProfileButton.LeftPaddle1,
            Switch2JoyConProfileButton.RightIrSensor | Switch2JoyConProfileButton.RightPaddle1);
        Switch2DualGyroRuntimePolicy Apply(Switch2JoyConProfileButton buttons) =>
            Resolve(ref state, configuration,
                left ? buttons : Switch2JoyConProfileButton.None,
                left ? Switch2JoyConProfileButton.None : buttons);
        var baseline = Apply(Switch2JoyConProfileButton.None);
        var pressed = Apply(ir);
        SamePolicy(pressed, Apply(ir | button));
        SamePolicy(pressed, Apply(button));
        SamePolicy(pressed, Apply(ir), "Atomic handoff is still one held gate.");
        SamePolicy(activation == Switch2DualGyroActivationMode.Hold ?
            baseline : pressed, Apply(Switch2JoyConProfileButton.None));

        state = default;
        Resolve(ref state, configuration);
        var both = Resolve(ref state, configuration,
            Switch2JoyConProfileButton.LeftIrSensor,
            Switch2JoyConProfileButton.RightIrSensor);
        if (mode == Switch2DualGyroMode.SingleSideToggle)
        {
            Assert.IsFalse(both.LeftActive);
            Assert.IsFalse(both.RightActive);
        }
        else
        {
            SamePolicy(baseline, both,
                "Two physical-side edges must not be collapsed into one global OR.");
        }
    }

    [TestMethod]
    public void UnmatchedInheritedHoldDoesNotUndoOtherSidesAdmittedPress()
    {
        Switch2DualGyroModeState state = default;
        var configuration = Configuration(
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroActivationMode.Hold,
            Switch2DualGyroDominantSide.Right,
            Switch2JoyConProfileButton.LeftIrSensor,
            Switch2JoyConProfileButton.RightIrSensor);
        Resolve(ref state, configuration, Switch2JoyConProfileButton.LeftIrSensor);
        var rightPressed = Resolve(ref state, configuration,
            Switch2JoyConProfileButton.LeftIrSensor, Switch2JoyConProfileButton.RightIrSensor);
        Assert.AreEqual(Switch2DualGyroDominantSide.Left, rightPressed.DominantSide);
        SamePolicy(rightPressed, Resolve(ref state, configuration,
            rightButtons: Switch2JoyConProfileButton.RightIrSensor));
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            Resolve(ref state, configuration).DominantSide);
    }

    [TestMethod]
    public void ConfigurationIdentityIncludesThresholdsAndProfileRevision()
    {
        Assert.IsTrue(Switch2DualGyroConfiguration.TryCreate(true,
            Switch2DualGyroMode.SwitchGyroSide, Switch2DualGyroDominantSide.Right,
            Switch2DualGyroActivationMode.Hold, Switch2JoyConProfileButton.LeftIrSensor,
            Switch2JoyConProfileButton.None, out var first));
        Assert.IsTrue(Switch2DualGyroConfiguration.TryCreate(true,
            first.Mode, first.DominantSide, first.ActivationMode,
            first.LeftActivationButton, first.RightActivationButton,
            out var threshold, leftIrThreshold: Switch2IrActivationThreshold.Balanced));
        Assert.IsTrue(Switch2DualGyroConfiguration.TryCreate(true,
            first.Mode, first.DominantSide, first.ActivationMode,
            first.LeftActivationButton, first.RightActivationButton,
            out var profile, profileRevision: 1));
        Assert.AreNotEqual(first, threshold);
        Assert.AreNotEqual(first, profile);
        foreach (var changed in new[] { threshold, profile })
        {
            Switch2DualGyroModeState state = default;
            Resolve(ref state, first);
            var before = Resolve(ref state, first, Switch2JoyConProfileButton.LeftIrSensor);
            var baseline = Resolve(ref state, changed, Switch2JoyConProfileButton.LeftIrSensor);
            Assert.IsTrue(baseline.ConfigurationEpoch > before.ConfigurationEpoch);
            Assert.IsFalse(baseline.LeftActive);
            Assert.IsTrue(baseline.RightActive);
            SamePolicy(baseline, Resolve(ref state, changed));
        }
        Assert.IsFalse(Switch2DualGyroConfiguration.TryCreate(true,
            first.Mode, first.DominantSide, first.ActivationMode,
            first.LeftActivationButton, first.RightActivationButton,
            out _, leftIrThreshold: (Switch2IrActivationThreshold)255));
        Assert.IsFalse(Switch2DualGyroConfiguration.TryCreate(true,
            first.Mode, first.DominantSide, first.ActivationMode,
            first.LeftActivationButton, first.RightActivationButton,
            out _, profileRevision: -1));
    }

    private static void SamePolicy(in Switch2DualGyroRuntimePolicy expected,
        in Switch2DualGyroRuntimePolicy actual, string message = null)
    {
        Assert.AreEqual(expected.Mode, actual.Mode, message);
        Assert.AreEqual(expected.FusionEnabled, actual.FusionEnabled, message);
        Assert.AreEqual(expected.DominantSide, actual.DominantSide, message);
        Assert.AreEqual(expected.LeftActive, actual.LeftActive, message);
        Assert.AreEqual(expected.RightActive, actual.RightActive, message);
    }

    private static Switch2DualGyroConfiguration Configuration(
        Switch2DualGyroMode mode,
        Switch2DualGyroActivationMode activationMode,
        Switch2DualGyroDominantSide dominantSide,
        Switch2JoyConProfileButton leftButton,
        Switch2JoyConProfileButton rightButton =
            Switch2JoyConProfileButton.None)
    {
        Assert.IsTrue(Switch2DualGyroConfiguration.TryCreate(true, mode,
            dominantSide, activationMode, leftButton, rightButton,
            out Switch2DualGyroConfiguration configuration));
        return configuration;
    }

    private static Switch2DualGyroRuntimePolicy Resolve(
        ref Switch2DualGyroModeState state,
        in Switch2DualGyroConfiguration configuration,
        Switch2JoyConProfileButton leftButtons =
            Switch2JoyConProfileButton.None,
        Switch2JoyConProfileButton rightButtons =
            Switch2JoyConProfileButton.None,
        ulong pairEpoch = 1)
    {
        Assert.IsTrue(Switch2DualJoyConGyroMode.TryResolve(ref state,
            pairEpoch, leftButtons, rightButtons, configuration,
            out Switch2DualGyroRuntimePolicy policy));
        return policy;
    }
}
