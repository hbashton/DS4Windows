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
