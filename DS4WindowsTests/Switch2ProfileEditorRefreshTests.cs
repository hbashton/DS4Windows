using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2ProfileEditorRefreshTests
{
    [TestMethod]
    public void ExistingEditorsRefreshLoadedMasksWithoutCreatingOrChangingProfileTables()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        var previousLock = Global.Switch2GyroLockBindings[slot];
        var previousTuning = Global.Switch2GyroTriggerTunings[slot];
        var previousShift = Global.Switch2ModeShiftSettings[slot];
        var definitions = new[]
        {
            (Switch2JoyConProfileButton.LeftShoulder, "L"),
            (Switch2JoyConProfileButton.RightShoulder, "R"),
        };
        try
        {
            Global.Switch2GyroLockBindings[slot] = null;
            Global.Switch2GyroTriggerTunings[slot] = null;
            Global.Switch2ModeShiftSettings[slot] = default;
            var gyroLock = new Switch2GyroLockEditorViewModel(slot, definitions);
            var tuning = new Switch2GyroTriggerTuningEditorViewModel(slot,
                new[] { "Trigger 0" }, definitions);
            var shift = new Switch2ModeShiftEditorViewModel(slot, definitions);
            gyroLock.RefreshFromProfile();
            tuning.RefreshFromProfile();
            shift.RefreshFromProfile();
            Assert.IsNull(Global.Switch2GyroLockBindings[slot]);
            Assert.IsNull(Global.Switch2GyroTriggerTunings[slot]);
            Assert.IsFalse(gyroLock.HoldButtonChoices.Any(choice => choice.IsSelected));
            Assert.IsFalse(tuning.DampeningButtonChoices.Any(choice => choice.IsSelected));

            var loadedLock = new Switch2GyroLockBindingTable();
            Assert.IsTrue(loadedLock.TrySet(GyroOutMode.Mouse,
                new(Switch2JoyConProfileButton.LeftShoulder,
                    Switch2JoyConProfileButton.RightShoulder)));
            var loadedTuning = new Switch2GyroTriggerTuningTable();
            var value = new Switch2IrGyroTuning(
                Switch2JoyConProfileButton.RightShoulder, 3, 20, 30, 40,
                Switch2JoyConProfileButton.LeftShoulder, 25, 50);
            Assert.IsTrue(loadedTuning.TrySet(GyroOutMode.Mouse, 0, value));
            var loadedShift = new Switch2ModeShiftSettings(
                Switch2JoyConProfileButton.RightShoulder,
                Switch2JoyConProfileButton.LeftShoulder, true, false, true);
            Global.Switch2GyroLockBindings[slot] = loadedLock;
            Global.Switch2GyroTriggerTunings[slot] = loadedTuning;
            Global.Switch2ModeShiftSettings[slot] = loadedShift;
            gyroLock.RefreshFromProfile();
            tuning.RefreshFromProfile();
            shift.RefreshFromProfile();
            AssertSelected(gyroLock.HoldButtonChoices, Switch2JoyConProfileButton.LeftShoulder);
            AssertSelected(gyroLock.ToggleButtonChoices, Switch2JoyConProfileButton.RightShoulder);
            AssertSelected(tuning.DeadzoneButtonChoices, Switch2JoyConProfileButton.RightShoulder);
            AssertSelected(tuning.DampeningButtonChoices, Switch2JoyConProfileButton.LeftShoulder);
            AssertSelected(shift.HoldButtonChoices, Switch2JoyConProfileButton.RightShoulder);
            AssertSelected(shift.ToggleButtonChoices, Switch2JoyConProfileButton.LeftShoulder);
            Assert.AreEqual(3.0, tuning.DeadzoneAmount);
            Assert.AreEqual(25.0, tuning.DampeningAmountPercent);
            Assert.IsTrue(shift.AutoApplyGyroMouse);
            Assert.IsTrue(shift.AutoApplySteering);
            Assert.AreSame(loadedLock, Global.Switch2GyroLockBindings[slot]);
            Assert.AreSame(loadedTuning, Global.Switch2GyroTriggerTunings[slot]);
            Assert.AreEqual(value, loadedTuning.Get(GyroOutMode.Mouse, 0));
            Assert.AreEqual(loadedShift, Global.Switch2ModeShiftSettings[slot]);

            Assert.IsTrue(loadedLock.TrySet(GyroOutMode.MouseJoystick,
                new(Switch2JoyConProfileButton.RightShoulder,
                    Switch2JoyConProfileButton.None)));
            Assert.IsTrue(loadedTuning.TrySet(GyroOutMode.MouseJoystick, 4, value));
            gyroLock.SelectedModeIndex = 1;
            tuning.SelectedModeIndex = 1;
            tuning.SelectedTriggerIndex = 4;
            gyroLock.RefreshFromProfile();
            tuning.RefreshFromProfile();
            Assert.AreEqual(1, gyroLock.SelectedModeIndex);
            Assert.AreEqual(1, tuning.SelectedModeIndex);
            Assert.AreEqual(4, tuning.SelectedTriggerIndex);
            AssertSelected(gyroLock.HoldButtonChoices, Switch2JoyConProfileButton.RightShoulder);
            AssertSelected(tuning.DeadzoneButtonChoices, Switch2JoyConProfileButton.RightShoulder);

            // A cleared preset must also clear all stale visible selections.
            Global.Switch2GyroLockBindings[slot] = null;
            Global.Switch2GyroTriggerTunings[slot] = null;
            Global.Switch2ModeShiftSettings[slot] = default;
            gyroLock.RefreshFromProfile();
            tuning.RefreshFromProfile();
            shift.RefreshFromProfile();
            Assert.IsFalse(gyroLock.ToggleButtonChoices.Any(choice => choice.IsSelected));
            Assert.IsFalse(tuning.DeadzoneButtonChoices.Any(choice => choice.IsSelected));
            Assert.IsFalse(shift.HoldButtonChoices.Any(choice => choice.IsSelected));
            Assert.IsNull(Global.Switch2GyroLockBindings[slot]);
            Assert.IsNull(Global.Switch2GyroTriggerTunings[slot]);

            // Explicit edits still allocate the missing table and store the selection.
            gyroLock.SelectedModeIndex = 0;
            tuning.SelectedModeIndex = 0;
            tuning.SelectedTriggerIndex = 0;
            gyroLock.HoldButtonChoices[0].IsSelected = true;
            tuning.DampeningButtonChoices[1].IsSelected = true;
            Assert.AreEqual(Switch2JoyConProfileButton.LeftShoulder,
                Global.Switch2GyroLockBindings[slot].Get(GyroOutMode.Mouse).HoldButtons);
            Assert.AreEqual(Switch2JoyConProfileButton.RightShoulder,
                Global.Switch2GyroTriggerTunings[slot].Get(GyroOutMode.Mouse, 0).DampeningButtons);
        }
        finally
        {
            Global.Switch2GyroLockBindings[slot] = previousLock;
            Global.Switch2GyroTriggerTunings[slot] = previousTuning;
            Global.Switch2ModeShiftSettings[slot] = previousShift;
        }
    }

    private static void AssertSelected(
        IReadOnlyList<Switch2IrGyroButtonChoice> choices,
        Switch2JoyConProfileButton button) => Assert.AreEqual(button,
            choices.Single(choice => choice.IsSelected).Button);
}
