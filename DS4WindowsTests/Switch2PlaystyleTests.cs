using DS4Windows;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Forms.ViewModels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Globalization;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2PlaystyleTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void ClosingAnUntouchedSecondActionShortcutIsANoOpButChosenActionCommits()
    {
        var settings = new DS4ControlSettings(DS4Controls.Cross);
        var vm = new BindingWindowViewModel(Global.TEST_PROFILE_INDEX, settings);
        vm.PrepareSecondAction();
        vm.WriteBinds();
        Assert.AreEqual(0, settings.shiftTrigger);
        Assert.AreEqual(DS4ControlSettings.ActionType.Default, settings.shiftActionType);
        vm.ShiftOutBind.outputType = OutBinding.OutType.Button;
        vm.ShiftOutBind.control = X360Controls.B;
        vm.WriteBinds();
        Assert.AreEqual(Mapping.SWITCH2_MODE_SHIFT_TRIGGER, settings.shiftTrigger);
        Assert.AreEqual(X360Controls.B, settings.GetSwitch2ModeShiftAction(vm.ModeShiftScope).Action.actionBtn);
    }

    [DataTestMethod]
    [DataRow(0, Mapping.SWITCH2_MODE_SHIFT_TRIGGER)]
    [DataRow(1, 1)]
    [DataRow(Mapping.SWITCH2_MODE_SHIFT_TRIGGER, Mapping.SWITCH2_MODE_SHIFT_TRIGGER)]
    public void SecondActionShortcutPreparesDialogWithoutMutatingProfile(int existingTrigger, int expected)
    {
        var settings = new DS4ControlSettings(DS4Controls.Cross);
        settings.UpdateSettings(true, X360Controls.B, string.Empty, DS4KeyType.None, existingTrigger);
        var vm = new BindingWindowViewModel(Global.TEST_PROFILE_INDEX, settings);
        var originalTrigger = settings.shiftTrigger;
        var originalAction = settings.shiftActionType;
        vm.PrepareSecondAction();
        Assert.AreSame(vm.ShiftOutBind, vm.ActionBinding);
        Assert.IsTrue(vm.ShowShift);
        Assert.AreEqual(expected, vm.ShiftOutBind.ShiftTrigger);
        Assert.AreEqual(originalTrigger, settings.shiftTrigger);
        Assert.AreEqual(originalAction, settings.shiftActionType);
    }

    [TestMethod]
    public void RenderFeatureIllustrations()
    {
        RunSta(() =>
        {
            var pictures = typeof(Switch2FeatureArtwork).GetProperties();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 17, 26)),
                    null, new Rect(0, 0, 800, 640));
                for (int i = 0; i < pictures.Length; i++)
                {
                    int x = i % 2 * 400, y = i / 2 * 160;
                    dc.DrawImage((ImageSource)pictures[i].GetValue(null),
                        new Rect(x + 20, y + 25, 160, 96));
                    dc.DrawText(new FormattedText(pictures[i].Name,
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 17, Brushes.WhiteSmoke, 1),
                        new Point(x + 200, y + 60));
                }
            }
            var bitmap = new RenderTargetBitmap(800, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(TestContext.TestResultsDirectory);
            string path = Path.Combine(TestContext.TestResultsDirectory, "switch2-features.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            TestContext.AddResultFile(path);
        });
    }

    [DataTestMethod]
    [DataRow(true, GyroOutMode.Controls, 0.0, true)]
    [DataRow(false, GyroOutMode.Mouse, 1.0, true)]
    [DataRow(false, GyroOutMode.Controls, 1.0, false)]
    [DataRow(false, GyroOutMode.Controls, 0.0, false)]
    public void MouseLanesDoNotDependOnUnrelatedButtonActions(
        bool surfaceMouse, GyroOutMode mode, double stickAssist, bool expected)
    {
        var store = new BackingStore();
        const int slot = Global.TEST_PROFILE_INDEX;
        store.switch2JoyConIrMouseEnabled[slot] = surfaceMouse;
        store.gyroOutMode[slot] = mode;
        store.switch2GyroMouseStickAssistSensitivity[slot] = stickAssist;
        store.CacheProfileCustomsFlags(slot);
        Assert.AreEqual(expected, store.containsCustomAction[slot]);
        store.switch2JoyConIrMouseEnabled[slot] = false;
        store.switch2GyroMouseStickAssistSensitivity[slot] = 0;
        store.CacheProfileCustomsFlags(slot);
        Assert.IsFalse(store.containsCustomAction[slot], "Disabling the feature must release the extra mapping work.");
    }

    [TestMethod]
    [DoNotParallelize]
    public void AimEditorSharesCanonicalModeAndPreservesCustomActivationUntilExplicitChoice()
    {
        RunSta(() =>
        {
            const int slot = Global.TEST_PROFILE_INDEX;
            var oldMode = Global.GyroOutputMode[slot];
            var oldMouseTriggers = Global.SATriggers[slot];
            var oldStickTriggers = Global.SAMousestickTriggers[slot];
            var oldMouseTurns = Global.GyroTriggerTurns[slot];
            var oldStickTurns = Global.GyroMouseStickTriggerTurns[slot];
            var oldMouseToggle = Global.GyroMouseToggle[slot];
            var oldStickToggle = Global.GyroMouseStickToggle[slot];
            var oldMouseCond = Global.SATriggerCond[slot];
            var oldStickCond = Global.SAMouseStickTriggerCond[slot];
            var oldTemp = Global.outDevTypeTemp[slot];
            var oldCustom = Global.store.containsCustomAction[slot];
            var oldExtras = Global.store.containsCustomExtras[slot];
            try
            {
                _ = new System.Windows.Controls.ContextMenu();
                Global.GyroOutputMode[slot] = GyroOutMode.Controls;
                Global.SATriggers[slot] = "27,28";
                Global.SAMousestickTriggers[slot] = "30";
                Global.GyroTriggerTurns[slot] = false;
                Global.GyroMouseToggle[slot] = true;
                var vm = new ProfileSettingsViewModel(slot);
                var setup = vm.Switch2AimSetup;
                setup.Refresh();
                Assert.IsFalse(setup.CanChooseActivation);
                Assert.AreEqual("27,28", Global.SATriggers[slot]);
                setup.OutputIndex = 1;
                Assert.AreEqual(GyroOutMode.Mouse, Global.GyroOutputMode[slot]);
                Assert.AreEqual(3, setup.ActivationIndex);
                Assert.IsTrue(Global.GyroMouseToggle[slot]);
                Assert.AreEqual("27,28", Global.SATriggers[slot], "Selecting output must preserve custom triggers.");
                setup.ActivationIndex = 0;
                Assert.AreEqual("-1", Global.SATriggers[slot]);
                Assert.IsTrue(vm.GyroMouseTurns);
                Assert.IsFalse(vm.GyroMouseToggle);
                Assert.AreEqual("30", Global.SAMousestickTriggers[slot], "Inactive mode must remain untouched.");
                setup.ActivationIndex = 1;
                Assert.AreEqual("5", Global.SATriggers[slot]);
                Assert.AreEqual("L2", vm.GyroMouseTrigDisplay);
                int notifications = 0;
                setup.PropertyChanged += (_, _) => notifications++;
                vm.GyroMouseToggle = true;
                Assert.AreEqual(3, setup.ActivationIndex);
                Assert.IsTrue(notifications > 0);
                notifications = 0;
                vm.GyroMouseToggle = false;
                vm.GyroMouseTurns = false;
                Assert.AreEqual(3, setup.ActivationIndex);
                Assert.IsTrue(notifications > 0);
                notifications = 0;
                vm.GyroOutModeIndex = 2;
                Assert.IsFalse(setup.IsMouse);
                Assert.IsTrue(notifications > 0, "Changes in the existing Gyro page must refresh this editor.");
                setup.ActivationIndex = 2;
                Assert.AreEqual("7", Global.SAMousestickTriggers[slot]);
                Assert.AreEqual("5", Global.SATriggers[slot]);
                Assert.IsTrue(vm.GyroMouseStickTurns);
                Assert.IsFalse(vm.GyroMouseStickToggle);
                setup.ActivationIndex = 3;
                Assert.AreEqual("7", Global.SAMousestickTriggers[slot]);
                setup.OutputIndex = 4;
                setup.ActivationIndex = 0;
                Assert.AreEqual("7", Global.SAMousestickTriggers[slot]);
            }
            finally
            {
                Global.GyroOutputMode[slot] = oldMode;
                Global.SATriggers[slot] = oldMouseTriggers;
                Global.SAMousestickTriggers[slot] = oldStickTriggers;
                Global.GyroTriggerTurns[slot] = oldMouseTurns;
                Global.GyroMouseStickTriggerTurns[slot] = oldStickTurns;
                Global.GyroMouseToggle[slot] = oldMouseToggle;
                Global.GyroMouseStickToggle[slot] = oldStickToggle;
                Global.SATriggerCond[slot] = oldMouseCond;
                Global.SAMouseStickTriggerCond[slot] = oldStickCond;
                Global.outDevTypeTemp[slot] = oldTemp;
                Global.store.containsCustomAction[slot] = oldCustom;
                Global.store.containsCustomExtras[slot] = oldExtras;
            }
        });
    }

    [TestMethod]
    public void FeatureIllustrationsAreDistinctFrozenAndSameSize()
    {
        var pictures = typeof(Switch2FeatureArtwork).GetProperties()
            .Select(p => (DrawingImage)p.GetValue(null)).ToArray();
        Assert.AreEqual(8, pictures.Length);
        Assert.AreEqual(8, pictures.Distinct().Count());
        foreach (var picture in pictures)
        {
            Assert.IsTrue(picture.IsFrozen);
            Assert.AreEqual(160.0, picture.Width);
            Assert.AreEqual(96.0, picture.Height);
        }
    }

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
