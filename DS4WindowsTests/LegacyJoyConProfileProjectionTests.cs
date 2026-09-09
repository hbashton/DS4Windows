using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class LegacyJoyConProfileProjectionTests
{
    private const int Slot = 0;
    private ProfileSettings saved;

    [TestInitialize]
    public void Initialize()
    {
        saved = ProfileSettings.Read();
        new ProfileSettings(Switch2JoyConHoldMode.Vertical, Switch2FaceButtonLayout.Xbox, false,
            Switch2DualGyroMode.SwitchDominantSide, Switch2DualGyroDominantSide.Right,
            Switch2DualGyroActivationMode.Hold, 0, 0).Write();
    }

    [TestCleanup]
    public void Cleanup() => saved.Write();

    [TestMethod]
    public void JoinedProjectsBothPhysicalHalvesWithoutInventingSwitch2Reports()
    {
        DS4State left = Half(true, 100), right = Half(false, 200), output = new();
        left.DpadUp = left.L1 = left.SideL = left.Capture = true;
        right.Cross = right.R1 = right.SideR = right.PS = true;
        left.LX = 22; right.RX = 231;
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(left, right), output));
        Assert.IsTrue(output.DpadUp && output.L1 && output.Capture && output.Cross && output.R1 && output.PS);
        Assert.AreEqual((byte)22, output.LX);
        Assert.AreEqual((byte)231, output.RX);
        Assert.AreEqual(200, output.Motion.gyroYawFull);
        Assert.AreEqual(14.0, output.Motion.angVelYaw, 0.0001);
        Assert.IsFalse(output.Switch2RawInputStatus.IsValid);
        Assert.IsFalse(output.Switch2JoyConRawInputStatus.IsValid);
        Assert.IsTrue(NintendoProfileInput.TryRead(output, out var status));
        Assert.AreEqual(Switch2JoyConProfileMode.Joined, status.Mode);
        Assert.IsTrue(NintendoProfileInput.TryReadButton(output, DS4Controls.Switch2JoyConLeftSL));
        Assert.IsTrue(NintendoProfileInput.TryReadButton(output, DS4Controls.Switch2JoyConRightSR));
        Assert.IsFalse(NintendoProfileInput.TryReadButton(output, DS4Controls.Switch2C));
        Assert.AreNotSame(left.Motion, output.Motion);
        Assert.AreNotSame(right.Motion, output.Motion);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SidewaysUsesRailShouldersRotatesStickAndPreservesOuterButtonAliases(bool leftSide)
    {
        Global.Switch2JoyConStandaloneHoldMode[Slot] = Switch2JoyConHoldMode.Horizontal;
        DS4State half = Half(leftSide, 100), output = new();
        half.SideL = true;
        if (leftSide) { half.LX = 220; half.LY = 30; half.L1 = true; half.L2 = 255; half.DpadRight = true; half.Capture = true; }
        else { half.RX = 220; half.RY = 30; half.R1 = true; half.R2 = 255; half.Cross = true; half.R3 = true; }
        Assert.IsTrue(new LegacyJoyConProfileProjection().TryApply(Input(leftSide ? half : null, leftSide ? null : half), output));
        Assert.AreEqual((byte)(leftSide ? 30 : 226), output.LX);
        Assert.AreEqual((byte)(leftSide ? 36 : 220), output.LY);
        Assert.AreEqual((byte)128, output.RX);
        Assert.AreEqual((byte)128, output.RY);
        Assert.IsTrue(output.L1 && output.Cross);
        Assert.IsFalse(output.R1);
        Assert.AreEqual((byte)0, output.L2);
        Assert.AreEqual((byte)0, output.R2);
        Assert.IsTrue(NintendoProfileInput.TryReadButton(output, leftSide ? DS4Controls.Switch2JoyConLeftPaddle1 : DS4Controls.Switch2JoyConRightPaddle1));
        Assert.IsTrue(NintendoProfileInput.TryReadButton(output, leftSide ? DS4Controls.Switch2JoyConLeftPaddle2 : DS4Controls.Switch2JoyConRightPaddle2));
        if (leftSide) Assert.IsTrue(output.PS, "Sideways Capture keeps the established mini-controller Guide mapping.");
        else Assert.IsTrue(output.L3);
    }

    [TestMethod]
    public void UprightRightKeepsRightStickAndProfileFaceLabelsApply()
    {
        DS4State right = Half(false, 100), output = new();
        right.RX = 210; right.RY = 17; right.Circle = right.R3 = true;
        Global.Switch2FaceButtonLayout[Slot] = Switch2FaceButtonLayout.Nintendo;
        Assert.IsTrue(new LegacyJoyConProfileProjection().TryApply(Input(null, right), output));
        Assert.AreEqual((byte)128, output.LX);
        Assert.AreEqual((byte)210, output.RX);
        Assert.AreEqual((byte)17, output.RY);
        Assert.IsTrue(output.Cross && output.R3);
        Assert.IsFalse(output.Circle);
        Assert.AreEqual(Switch2JoyConProfileMode.StandaloneVerticalRight, output.NintendoInputStatus.Mode);
    }

    [TestMethod]
    public void BothHandsReuseExistingFusionInCalibratedUnits()
    {
        Global.Switch2DualJoyConGyroFusionEnabled[Slot] = true;
        Global.Switch2DualJoyConGyroMode[Slot] = Switch2DualGyroMode.SingleSideToggle;
        Global.Switch2DualJoyConGyroDominantSide[Slot] = Switch2DualGyroDominantSide.None;
        DS4State left = Half(true, 100), right = Half(false, 200), output = new();
        Assert.IsTrue(new LegacyJoyConProfileProjection().TryApply(Input(left, right), output));
        Assert.AreEqual(300, output.Motion.gyroYawFull);
        Assert.AreEqual(21.0, output.Motion.angVelYaw, 0.0001);
        Assert.AreEqual(8192, output.Motion.accelYFull);
        Assert.AreEqual(1.0, output.Motion.accelYG, 0.0001);
    }

    [DataTestMethod]
    [DataRow(Switch2DualGyroActivationMode.Hold)]
    [DataRow(Switch2DualGyroActivationMode.Toggle)]
    public void EachPhysicalPauseButtonAffectsItsOwnHand(Switch2DualGyroActivationMode activation)
    {
        Global.Switch2DualJoyConGyroFusionEnabled[Slot] = true;
        Global.Switch2DualJoyConGyroMode[Slot] = Switch2DualGyroMode.SingleSideToggle;
        Global.Switch2DualJoyConGyroDominantSide[Slot] = Switch2DualGyroDominantSide.None;
        Global.Switch2DualJoyConGyroActivationMode[Slot] = activation;
        Global.Switch2DualJoyConGyroLeftActivationButton[Slot] = Switch2JoyConProfileButton.LeftShoulder;
        Global.Switch2DualJoyConGyroRightActivationButton[Slot] = Switch2JoyConProfileButton.RightShoulder;
        DS4State left = Half(true, 100), right = Half(false, 200), output = new();
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(left, right), output));
        Assert.AreEqual(300, output.Motion.gyroYawFull);
        left.L1 = true;
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_010), output));
        Assert.AreEqual(200, output.Motion.gyroYawFull);
        Assert.IsTrue(output.L1, "DJG activation does not consume normal game input.");
        right.R1 = true;
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_020), output));
        Assert.AreEqual(0, output.Motion.gyroYawFull);
        left.L1 = right.R1 = false;
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_030), output));
        Assert.AreEqual(activation == Switch2DualGyroActivationMode.Hold ? 300 : 0, output.Motion.gyroYawFull);
    }

    [TestMethod]
    public void PairBoundaryBaselinesHeldToggleInsteadOfManufacturingAnEdge()
    {
        Global.Switch2DualJoyConGyroFusionEnabled[Slot] = true;
        Global.Switch2DualJoyConGyroMode[Slot] = Switch2DualGyroMode.SingleSideToggle;
        Global.Switch2DualJoyConGyroDominantSide[Slot] = Switch2DualGyroDominantSide.None;
        Global.Switch2DualJoyConGyroActivationMode[Slot] = Switch2DualGyroActivationMode.Toggle;
        Global.Switch2DualJoyConGyroLeftActivationButton[Slot] = Switch2JoyConProfileButton.LeftShoulder;
        DS4State left = Half(true, 100), right = Half(false, 200), output = new();
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(left, right), output));
        left.L1 = true;
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_010), output));
        Assert.AreEqual(200, output.Motion.gyroYawFull);
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_020) with { PairEpoch = 22 }, output));
        Assert.AreEqual(300, output.Motion.gyroYawFull);
        Assert.IsNull(output.Motion.previousAxis);
        Assert.AreEqual(0.0, output.elapsedTime);
    }

    [TestMethod]
    public void StaleHalfCannotHoldButtonsStickOrMotionWhileOtherHalfContinues()
    {
        Global.Switch2DualJoyConGyroFusionEnabled[Slot] = true;
        Global.Switch2DualJoyConGyroMode[Slot] = Switch2DualGyroMode.SingleSideToggle;
        Global.Switch2DualJoyConGyroDominantSide[Slot] = Switch2DualGyroDominantSide.None;
        DS4State left = Half(true, 100), right = Half(false, 200), output = new();
        left.L1 = true; left.LX = 255; right.Cross = true;
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(left, right), output));
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_201) with { LeftTimestampQpc = 1_000 }, output));
        Assert.IsFalse(output.L1);
        Assert.AreEqual((byte)128, output.LX);
        Assert.IsTrue(output.Cross);
        Assert.AreEqual(200, output.Motion.gyroYawFull);
        Assert.IsTrue(output.NintendoInputStatus.LeftPresent, "A stale sample does not invent a topology change.");
        Assert.AreEqual(Switch2JoyConProfileButton.None, output.NintendoInputStatus.LeftButtons);
        Assert.IsFalse(projection.TryApply(Input(left, right, 1_500) with { LeftTimestampQpc = 1_000, RightTimestampQpc = 1_201 }, output));
    }

    [TestMethod]
    public void SourceMutationCannotChangeProjectedMotionOrBoundedHistory()
    {
        DS4State right = Half(false, 100), output = new();
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(null, right), output));
        right.Motion.gyroYawFull = 200;
        Assert.AreEqual(100, output.Motion.gyroYawFull);
        Assert.IsTrue(projection.TryApply(Input(null, right, 1_010), output));
        Assert.AreEqual(100, output.Motion.previousAxis.gyroYawFull);
        Assert.AreNotSame(right.Motion, output.Motion.previousAxis);
        right.Motion.gyroYawFull = 300;
        Assert.IsTrue(projection.TryApply(Input(null, right, 1_020), output));
        Assert.AreEqual(200, output.Motion.previousAxis.gyroYawFull);
        Assert.AreNotSame(output.Motion, output.Motion.previousAxis);
        Assert.AreNotSame(output.Motion.previousAxis, output.Motion.previousAxis.previousAxis);
        Assert.IsNull(output.Motion.previousAxis.previousAxis.previousAxis);
    }

    [TestMethod]
    public void OrientationChangeResetsMotionHistoryEvenWithoutProfileRevisionChange()
    {
        DS4State right = Half(false, 100), output = new();
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(null, right), output));
        Global.Switch2JoyConStandaloneHoldMode[Slot] = Switch2JoyConHoldMode.Horizontal;
        Assert.IsTrue(projection.TryApply(Input(null, right, 1_010), output));
        Assert.IsNull(output.Motion.previousAxis);
        Assert.AreEqual(0.0, output.Motion.elapsed);
    }

    [TestMethod]
    public void DuplicateAndBackwardObservationsCannotReplayOrMutatePublishedInput()
    {
        DS4State left = Half(true, 100), right = Half(false, 200), output = new();
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(left, right), output));
        right.Cross = true;
        Assert.IsFalse(projection.TryApply(Input(left, right), output));
        Assert.IsFalse(output.Cross);
        Assert.IsFalse(projection.TryApply(Input(left, right, 990), output));
        Assert.IsFalse(projection.TryApply(Input(left, right, 1_010) with { RightTimestampQpc = 999 }, output));
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_010), output));
        Assert.IsTrue(output.Cross);
    }

    [TestMethod]
    public void JoinedDsuTimestampDoesNotAlternateBetweenConnectionRelativeClocks()
    {
        DS4State left = Half(true, 1), right = Half(false, 2), output = new();
        left.totalMicroSec = 90_000_000;
        right.totalMicroSec = 1_000;
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsTrue(projection.TryApply(Input(left, right) with { RightTimestampQpc = 999 }, output));
        ulong previous = output.totalMicroSec;
        Assert.IsTrue(projection.TryApply(Input(left, right, 1_010) with { LeftTimestampQpc = 1_000 }, output));
        Assert.AreEqual(previous + 10_000, output.totalMicroSec);
        // A fresh projector at link/unlink keeps the same host timing domain.
        Assert.IsTrue(new LegacyJoyConProfileProjection().TryApply(Input(left, null, 1_020), output));
        Assert.AreEqual(previous + 20_000, output.totalMicroSec);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void MalformedIdentityCannotEnterProfileProjection(int fault)
    {
        DS4State left = Half(true, 1), right = Half(false, 2), output = new();
        var input = Input(left, right);
        input = fault switch
        {
            0 => input with { PairEpoch = 0 },
            1 => input with { LeftGeneration = 0 },
            2 => input with { QpcFrequency = 0 },
            3 => input with { CompletionQpc = -1 },
            4 => input with { LeftTimestampQpc = input.CompletionQpc + 1 },
            _ => input with { ProfileSlot = -1 },
        };
        Assert.IsFalse(new LegacyJoyConProfileProjection().TryApply(input, output));
        Assert.IsFalse(output.NintendoInputStatus.IsDeclared);
    }

    [TestMethod]
    public void DestinationMayNotAliasPhysicalInputAndAbsentHardwareControlsAreIgnored()
    {
        DS4State right = Half(false, 1), output = new();
        right.Mute = right.BLP = right.BRP = right.Touch1 = right.TouchButton = true;
        var projection = new LegacyJoyConProfileProjection();
        Assert.IsFalse(projection.TryApply(Input(null, right), right));
        Assert.IsTrue(projection.TryApply(Input(null, right), output));
        Assert.IsFalse(output.Mute || output.BLP || output.BRP || output.Touch1 || output.TouchButton);
        Assert.IsFalse(NintendoProfileInput.TryReadButton(output, DS4Controls.Switch2JoyConRightIrSensor));
        Assert.IsFalse(NintendoProfileInput.TryReadButton(output, DS4Controls.Switch2JoyConRightPaddle1));
    }

    [TestMethod]
    public void NintendoMetadataSurvivesEveryCanonicalSnapshotCopy()
    {
        DS4State source = new(), destination = new();
        Assert.IsTrue(new LegacyJoyConProfileProjection().TryApply(Input(Half(true, 1), null), source));
        Assert.AreEqual(source.NintendoInputStatus, new DS4State(source).NintendoInputStatus);
        source.CopyTo(destination);
        Assert.AreEqual(source.NintendoInputStatus, destination.NintendoInputStatus);
        destination.NintendoInputStatus = default;
        source.CopyExtrasTo(destination);
        Assert.AreEqual(source.NintendoInputStatus, destination.NintendoInputStatus);
        var owned = new DS4StateOwnedSnapshot();
        owned.Capture(source);
        Assert.AreEqual(source.NintendoInputStatus, owned.State.NintendoInputStatus);
    }

    [TestMethod]
    public void SharedDampeningAndModeShiftReadOriginalButtonsWithoutCrossFamilyIdentity()
    {
        DS4State left = Half(true, 1), output = new();
        left.L1 = true;
        Assert.IsTrue(new LegacyJoyConProfileProjection().TryApply(Input(left, null), output));
        Assert.IsTrue(Switch2GyroTriggerModifier.TryReadInput(output, 1, 0, true, out var input));
        Assert.IsTrue(input.Identity.OriginalNintendo);
        var lookalikeSwitch2Identity = new Switch2GyroTriggerSourceIdentity(true, 0, 1, 1, 0, 0,
            Switch2JoyConProfileMode.StandaloneVerticalLeft);
        Assert.IsFalse(input.Identity.HasSamePhysicalSource(lookalikeSwitch2Identity));
        Switch2GyroTriggerModifierState state = default;
        var tuning = new Switch2IrGyroTuning(0, 0, 0, 0, 0, Switch2JoyConProfileButton.LeftShoulder, 90, 0);
        Assert.IsTrue(Switch2GyroTriggerModifier.TryAdvance(input, tuning, ref state, out var modifier));
        Assert.IsTrue(modifier.DampeningActive);
        Assert.AreEqual(0.1, modifier.DampeningMultiplier, 0.000001);
        Switch2ModeShiftState shift = default;
        var settings = new Switch2ModeShiftSettings(Switch2JoyConProfileButton.LeftShoulder, 0);
        Assert.IsTrue(Switch2ModeShift.TryAdvance(input, settings, false, ref shift, out bool active));
        Assert.IsTrue(active);
        Assert.IsTrue(Switch2ModeShift.IsActivationControl(DS4Controls.L1, settings, output));
    }

    [TestMethod]
    public void SharedStickAssistUsesUprightRightAndDoesNotCarryAcrossOrientationBoundary()
    {
        DS4State right = Half(false, 1), output = new();
        right.RX = 255;
        var projection = new LegacyJoyConProfileProjection();
        Switch2StickAssistProfileLaneState state = default;
        Assert.IsTrue(projection.TryApply(Input(null, right), output));
        Assert.IsFalse(AdvanceStick(output, ref state, out _));
        Assert.IsTrue(projection.TryApply(Input(null, right, 1_010), output));
        Assert.IsTrue(AdvanceStick(output, ref state, out var movement));
        Assert.IsTrue(movement.DeltaX > 0);
        Assert.AreEqual(0.0, movement.DeltaY);
        Global.Switch2JoyConStandaloneHoldMode[Slot] = Switch2JoyConHoldMode.Horizontal;
        Assert.IsTrue(projection.TryApply(Input(null, right, 1_020), output));
        Assert.IsFalse(AdvanceStick(output, ref state, out _));
        Assert.IsTrue(projection.TryApply(Input(null, right, 1_030), output));
        Assert.IsTrue(AdvanceStick(output, ref state, out movement));
        Assert.AreEqual(0.0, movement.DeltaX);
        Assert.IsTrue(movement.DeltaY > 0);
    }

    [TestMethod]
    public void ProjectionAndSharedButtonReadAllocateNothingAfterWarmup()
    {
        DS4State left = Half(true, 100), right = Half(false, 200), output = new();
        var projection = new LegacyJoyConProfileProjection();
        for (int i = 0; i < 256; i++) projection.TryApply(Input(left, right, 1_000 + i), output);
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int i = 0; i < 2_000; i++)
        {
            success &= projection.TryApply(Input(left, right, 2_000 + i), output);
            success &= NintendoProfileInput.TryRead(output, out _);
            success &= Switch2GyroTriggerModifier.TryReadInput(output, 1, 0, true, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.IsTrue(success);
        Assert.AreEqual(0L, allocated);
    }

    private static bool AdvanceStick(DS4State output, ref Switch2StickAssistProfileLaneState state, out Switch2StickAssistResult movement) =>
        Switch2StickAssistProfileLane.TryAdvance(output, output.LXAxis.ProfileCoordinate, output.LYAxis.ProfileCoordinate,
            output.RXAxis.ProfileCoordinate, output.RYAxis.ProfileCoordinate, true, 2, 1, ref state, out movement);

    private static DS4State Half(bool left, int yaw)
    {
        var state = new DS4State { Battery = 80 };
        state.Motion.gyroYawFull = yaw;
        state.Motion.gyroPitchFull = 20;
        state.Motion.gyroRollFull = 30;
        state.Motion.accelYFull = 8192;
        return state;
    }

    private static LegacyJoyConProjectionInput Input(DS4State left, DS4State right, long timestamp = 1_000) =>
        new(left, right, left == null ? 0UL : 1UL, right == null ? 0UL : 2UL,
            left == null ? 0 : timestamp, right == null ? 0 : timestamp, Slot,
            left != null && right != null ? 3UL : 0UL, timestamp, 1_000);

    private readonly record struct ProfileSettings(Switch2JoyConHoldMode HoldMode, Switch2FaceButtonLayout Layout,
        bool Fusion, Switch2DualGyroMode Mode, Switch2DualGyroDominantSide Dominant,
        Switch2DualGyroActivationMode Activation, Switch2JoyConProfileButton LeftButton, Switch2JoyConProfileButton RightButton,
        bool Horizon = false, double SoftDeadzone = 0)
    {
        internal static ProfileSettings Read() => new(Global.Switch2JoyConStandaloneHoldMode[Slot], Global.Switch2FaceButtonLayout[Slot],
            Global.Switch2DualJoyConGyroFusionEnabled[Slot], Global.Switch2DualJoyConGyroMode[Slot], Global.Switch2DualJoyConGyroDominantSide[Slot],
            Global.Switch2DualJoyConGyroActivationMode[Slot], Global.Switch2DualJoyConGyroLeftActivationButton[Slot], Global.Switch2DualJoyConGyroRightActivationButton[Slot],
            Global.Switch2HorizonStabilizationEnabled[Slot], Global.Switch2VirtualGyroSoftDeadzone[Slot]);
        internal void Write()
        {
            Global.Switch2JoyConStandaloneHoldMode[Slot] = HoldMode;
            Global.Switch2FaceButtonLayout[Slot] = Layout;
            Global.Switch2DualJoyConGyroFusionEnabled[Slot] = Fusion;
            Global.Switch2DualJoyConGyroMode[Slot] = Mode;
            Global.Switch2DualJoyConGyroDominantSide[Slot] = Dominant;
            Global.Switch2DualJoyConGyroActivationMode[Slot] = Activation;
            Global.Switch2DualJoyConGyroLeftActivationButton[Slot] = LeftButton;
            Global.Switch2DualJoyConGyroRightActivationButton[Slot] = RightButton;
            Global.Switch2HorizonStabilizationEnabled[Slot] = Horizon;
            Global.Switch2VirtualGyroSoftDeadzone[Slot] = SoftDeadzone;
        }
    }
}
