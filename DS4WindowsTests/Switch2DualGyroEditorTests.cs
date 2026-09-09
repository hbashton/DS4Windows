using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2DualGyroEditorTests
{
    [DataTestMethod]
    [DataRow(0, 0)]
    [DataRow(0, 1)]
    [DataRow(1, 0)]
    [DataRow(1, 1)]
    [DataRow(2, 0)]
    [DataRow(2, 1)]
    public void ButtonActionDescriptionExplainsEachModeWithoutChangingSettings(
        int mode, int activation)
    {
        WithKnownDescriptionProfile(() =>
        {
            var editor = CreateDescriptionEditor();
            ProfileSnapshot original = ProfileSnapshot.Capture();
            editor.ModeIndex = mode;
            editor.ActivationModeIndex = activation;
            ProfileSnapshot configured = ProfileSnapshot.Capture();
            Assert.AreEqual(original with { Mode = configured.Mode,
                Activation = configured.Activation }, configured,
                "Description-related mode edits must preserve independent profile settings.");
            Assert.IsFalse(typeof(Switch2DualGyroEditorViewModel)
                .GetProperty(nameof(editor.ButtonActionDescription)).CanWrite,
                "The explanation must not be a writable profile setting.");

            string description = editor.ButtonActionDescription.ToLowerInvariant();
            if (mode == 2)
            {
                foreach (string phrase in new[] { "both", "start", "pause", "that joy-con" })
                    StringAssert.Contains(description, phrase);
            }
            else
            {
                foreach (string phrase in new[] { "either joy-con", "swap", "do not select" })
                    StringAssert.Contains(description, phrase);
            }
            foreach (string phrase in activation == 0 ? new[] { "hold", "release" } :
                new[] { "press", "again" })
                StringAssert.Contains(description, phrase);

            Assert.AreEqual(configured, ProfileSnapshot.Capture(),
                "Reading the explanation must not change aiming, buttons, or the generic gyro gate.");
            editor.RefreshFromProfile();
            Assert.AreEqual(description, editor.ButtonActionDescription.ToLowerInvariant());
            Assert.AreEqual(configured, ProfileSnapshot.Capture(),
                "Refreshing explanatory bindings must not rewrite the profile.");
        });
    }

    [TestMethod]
    public void BoundButtonActionDescriptionRefreshesForModeActivationAndProfileLoad()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                WithKnownDescriptionProfile(() =>
                {
                    var editor = CreateDescriptionEditor();
                    var text = new TextBlock();
                    var changed = new List<string>();
                    editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
                    text.SetBinding(TextBlock.TextProperty,
                        new Binding(nameof(editor.ButtonActionDescription))
                        { Source = editor, Mode = BindingMode.OneWay });
                    try
                    {
                        Assert.AreEqual(editor.ButtonActionDescription, text.Text);
                        ProfileSnapshot original = ProfileSnapshot.Capture();
                        CheckRefresh(() => editor.ModeIndex = 1);
                        CheckRefresh(() => editor.ActivationModeIndex = 1);
                        ProfileSnapshot configured = ProfileSnapshot.Capture();
                        Assert.AreEqual(original with { Mode = configured.Mode,
                            Activation = configured.Activation }, configured);

                        // Loading a profile changes the backing fields, then refreshes
                        // this same bound editor. It must not restore stale UI values.
                        Global.Switch2DualJoyConGyroMode[Global.TEST_PROFILE_INDEX] =
                            Switch2DualGyroMode.SingleSideToggle;
                        Global.Switch2DualJoyConGyroActivationMode[Global.TEST_PROFILE_INDEX] =
                            Switch2DualGyroActivationMode.Hold;
                        ProfileSnapshot loaded = ProfileSnapshot.Capture();
                        CheckRefresh(editor.RefreshFromProfile);
                        Assert.AreEqual(loaded, ProfileSnapshot.Capture());
                        StringAssert.Contains(text.Text.ToLowerInvariant(), "pause");
                        StringAssert.Contains(text.Text.ToLowerInvariant(), "release");
                    }
                    finally
                    {
                        BindingOperations.ClearAllBindings(text);
                    }

                    void CheckRefresh(Action change)
                    {
                        changed.Clear();
                        change();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        CollectionAssert.Contains(changed,
                            nameof(editor.ButtonActionDescription),
                            "The read-only explanation needs its own change notification.");
                        Assert.AreEqual(editor.ButtonActionDescription, text.Text,
                            "The bound explanation must follow the current profile.");
                    }
                });
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "Description binding test timed out.");
        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Switch2DualGyroEditorViewModel CreateDescriptionEditor() => new(
        Global.TEST_PROFILE_INDEX, new[] {
            (Switch2JoyConProfileButton.LeftTrigger, "ZL"),
            (Switch2JoyConProfileButton.RightTrigger, "ZR"),
            (Switch2JoyConProfileButton.LeftRailSL, "Left SL"),
            (Switch2JoyConProfileButton.RightRailSR, "Right SR"),
        });

    private static void WithKnownDescriptionProfile(Action action)
    {
        ProfileSnapshot previous = ProfileSnapshot.Capture();
        try
        {
            new ProfileSnapshot(Switch2DualGyroMode.SwitchDominantSide,
                Switch2DualGyroActivationMode.Hold, true,
                Switch2DualGyroDominantSide.Left,
                Switch2JoyConProfileButton.LeftTrigger | Switch2JoyConProfileButton.LeftRailSL,
                Switch2JoyConProfileButton.RightTrigger | Switch2JoyConProfileButton.RightRailSR,
                "7", true, GyroOutMode.Mouse, 137, 3, true, true).Restore();
            action();
        }
        finally { previous.Restore(); }
    }

    private readonly record struct ProfileSnapshot(Switch2DualGyroMode Mode,
        Switch2DualGyroActivationMode Activation, bool Enabled,
        Switch2DualGyroDominantSide Dominant, Switch2JoyConProfileButton Left,
        Switch2JoyConProfileButton Right, string Triggers, bool TriggerCondition,
        GyroOutMode OutputMode, int Sensitivity, int Invert, bool TriggerTurns, bool Toggle)
    {
        private const int Slot = Global.TEST_PROFILE_INDEX;

        internal static ProfileSnapshot Capture() => new(
            Global.Switch2DualJoyConGyroMode[Slot], Global.Switch2DualJoyConGyroActivationMode[Slot],
            Global.Switch2DualJoyConGyroFusionEnabled[Slot], Global.Switch2DualJoyConGyroDominantSide[Slot],
            Global.Switch2DualJoyConGyroLeftActivationButton[Slot],
            Global.Switch2DualJoyConGyroRightActivationButton[Slot], Global.SATriggers[Slot],
            Global.SATriggerCond[Slot], Global.GyroOutputMode[Slot], Global.GyroSensitivity[Slot],
            Global.GyroInvert[Slot], Global.GyroTriggerTurns[Slot], Global.GyroMouseToggle[Slot]);

        internal void Restore()
        {
            Global.Switch2DualJoyConGyroMode[Slot] = Mode;
            Global.Switch2DualJoyConGyroActivationMode[Slot] = Activation;
            Global.Switch2DualJoyConGyroFusionEnabled[Slot] = Enabled;
            Global.Switch2DualJoyConGyroDominantSide[Slot] = Dominant;
            Global.Switch2DualJoyConGyroLeftActivationButton[Slot] = Left;
            Global.Switch2DualJoyConGyroRightActivationButton[Slot] = Right;
            Global.SATriggers[Slot] = Triggers;
            Global.SATriggerCond[Slot] = TriggerCondition;
            Global.GyroOutputMode[Slot] = OutputMode;
            Global.GyroSensitivity[Slot] = Sensitivity;
            Global.GyroInvert[Slot] = Invert;
            Global.GyroTriggerTurns[Slot] = TriggerTurns;
            Global.GyroMouseToggle[Slot] = Toggle;
        }
    }

    [TestMethod]
    public void DualGyroOffersOnlyTheOwnPhysicalRailsForEachHalf()
    {
        var definitions = new[] {
            (Switch2JoyConProfileButton.LeftRailSL, "Left SL"),
            (Switch2JoyConProfileButton.LeftRailSR, "Left SR"),
            (Switch2JoyConProfileButton.RightRailSL, "Right SL"),
            (Switch2JoyConProfileButton.RightRailSR, "Right SR"),
        };
        var editor = new Switch2DualGyroEditorViewModel(Global.TEST_PROFILE_INDEX, definitions);
        CollectionAssert.AreEqual(new[] { Switch2JoyConProfileButton.LeftRailSL,
            Switch2JoyConProfileButton.LeftRailSR },
            editor.LeftActivationChoices.Select(choice => choice.Button).ToArray());
        CollectionAssert.AreEqual(new[] { Switch2JoyConProfileButton.RightRailSL,
            Switch2JoyConProfileButton.RightRailSR },
            editor.RightActivationChoices.Select(choice => choice.Button).ToArray());
    }

    [TestMethod]
    public void BoundEditorNormalizesDominantSideAndPreservesIndependentMasks()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            const int slot = Global.TEST_PROFILE_INDEX;
            var previousMode = Global.Switch2DualJoyConGyroMode[slot];
            var previousDominant = Global.Switch2DualJoyConGyroDominantSide[slot];
            var previousLeft = Global.Switch2DualJoyConGyroLeftActivationButton[slot];
            var previousRight = Global.Switch2DualJoyConGyroRightActivationButton[slot];
            try
            {
                Global.Switch2DualJoyConGyroMode[slot] = Switch2DualGyroMode.SingleSideToggle;
                Global.Switch2DualJoyConGyroDominantSide[slot] = Switch2DualGyroDominantSide.None;
                Global.Switch2DualJoyConGyroLeftActivationButton[slot] =
                    Switch2JoyConProfileButton.LeftPaddle1;
                Global.Switch2DualJoyConGyroRightActivationButton[slot] =
                    Switch2JoyConProfileButton.RightIrSensor;
                var definitions = new[]
                {
                    (Switch2JoyConProfileButton.LeftPaddle1, "Left rail 1"),
                    (Switch2JoyConProfileButton.LeftPaddle2, "Left rail 2"),
                    (Switch2JoyConProfileButton.LeftIrSensor, "Left IR"),
                    (Switch2JoyConProfileButton.RightIrSensor, "Right IR"),
                };
                var editor = new Switch2DualGyroEditorViewModel(slot, definitions);
                var dominant = new ComboBox { ItemsSource = new[] { "Left", "Right", "Direct merge" } };
                var mode = new ComboBox { ItemsSource = new[] { "Dominant", "Gyro", "Toggle" } };
                dominant.SetBinding(Selector.SelectedIndexProperty,
                    new Binding(nameof(editor.DominantSideIndex)) { Source = editor, Mode = BindingMode.TwoWay });
                mode.SetBinding(Selector.SelectedIndexProperty,
                    new Binding(nameof(editor.ModeIndex)) { Source = editor, Mode = BindingMode.TwoWay });
                Assert.AreEqual(2, dominant.SelectedIndex);
                mode.SetCurrentValue(Selector.SelectedIndexProperty, 0);
                mode.GetBindingExpression(Selector.SelectedIndexProperty).UpdateSource();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.AreEqual(1, dominant.SelectedIndex,
                    "Changing mode must refresh the independently bound dominant-side ComboBox.");
                Assert.AreEqual(Switch2DualGyroDominantSide.Right,
                    Global.Switch2DualJoyConGyroDominantSide[slot]);

                var ir = editor.LeftActivationChoices.Single(choice =>
                    choice.Button == Switch2JoyConProfileButton.LeftIrSensor);
                var checkbox = new CheckBox();
                checkbox.SetBinding(ToggleButton.IsCheckedProperty,
                    new Binding(nameof(ir.IsSelected)) { Source = ir, Mode = BindingMode.TwoWay });
                checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty).UpdateSource();
                Assert.AreEqual(Switch2JoyConProfileButton.LeftPaddle1 |
                    Switch2JoyConProfileButton.LeftIrSensor,
                    Global.Switch2DualJoyConGyroLeftActivationButton[slot]);
                editor.LeftActivationChoices.Single(choice =>
                    choice.Button == Switch2JoyConProfileButton.LeftPaddle1).IsSelected = false;
                Assert.AreEqual(Switch2JoyConProfileButton.LeftIrSensor,
                    Global.Switch2DualJoyConGyroLeftActivationButton[slot]);
                Assert.AreEqual(Switch2JoyConProfileButton.RightIrSensor,
                    Global.Switch2DualJoyConGyroRightActivationButton[slot]);
                Assert.IsFalse(editor.LeftActivationChoices.Any(choice =>
                    choice.Button == Switch2JoyConProfileButton.RightIrSensor));
                Assert.IsFalse(editor.RightActivationChoices.Any(choice =>
                    choice.Button == Switch2JoyConProfileButton.LeftIrSensor));

                var reopened = new Switch2DualGyroEditorViewModel(slot, definitions);
                Assert.AreEqual(1, reopened.DominantSideIndex);
                Assert.IsTrue(reopened.LeftActivationChoices.Single(choice =>
                    choice.Button == Switch2JoyConProfileButton.LeftIrSensor).IsSelected);
                Assert.IsFalse(reopened.LeftActivationChoices.Single(choice =>
                    choice.Button == Switch2JoyConProfileButton.LeftPaddle1).IsSelected);

                // Actual profile loading and presets retain the editor instance.
                var items = new ItemsControl();
                items.SetBinding(ItemsControl.ItemsSourceProperty,
                    new Binding(nameof(editor.LeftActivationChoices)) { Source = editor });
                Global.Switch2DualJoyConGyroLeftActivationButton[slot] =
                    Switch2JoyConProfileButton.LeftPaddle2;
                Global.Switch2DualJoyConGyroRightActivationButton[slot] =
                    Switch2JoyConProfileButton.None;
                Global.Switch2DualJoyConGyroMode[slot] = Switch2DualGyroMode.SingleSideToggle;
                Global.Switch2DualJoyConGyroDominantSide[slot] = Switch2DualGyroDominantSide.None;
                editor.RefreshFromProfile();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.AreSame(editor.LeftActivationChoices, items.ItemsSource);
                Assert.AreEqual(2, mode.SelectedIndex);
                Assert.AreEqual(2, dominant.SelectedIndex);
                Assert.IsTrue(editor.LeftActivationChoices.Single(choice =>
                    choice.Button == Switch2JoyConProfileButton.LeftPaddle2).IsSelected);
                Assert.IsFalse(editor.LeftActivationChoices.Single(choice =>
                    choice.Button == Switch2JoyConProfileButton.LeftIrSensor).IsSelected);
                Assert.IsFalse(editor.RightActivationChoices.Any(choice => choice.IsSelected));
                Assert.AreEqual(Switch2JoyConProfileButton.LeftPaddle2,
                    Global.Switch2DualJoyConGyroLeftActivationButton[slot],
                    "Refresh must not write back stale checkbox values.");
                BindingOperations.ClearAllBindings(items);
                BindingOperations.ClearAllBindings(dominant);
                BindingOperations.ClearAllBindings(mode);
                BindingOperations.ClearAllBindings(checkbox);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Global.Switch2DualJoyConGyroMode[slot] = previousMode;
                Global.Switch2DualJoyConGyroDominantSide[slot] = previousDominant;
                Global.Switch2DualJoyConGyroLeftActivationButton[slot] = previousLeft;
                Global.Switch2DualJoyConGyroRightActivationButton[slot] = previousRight;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "Editor binding test timed out.");
        if (failure != null)
        {
            Assert.Fail(failure.ToString());
        }
    }
}
