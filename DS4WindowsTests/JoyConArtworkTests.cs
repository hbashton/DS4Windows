using DS4Windows.InputDevices;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Forms.ViewModels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Globalization;

namespace DS4WindowsTests;

[TestClass]
public sealed class JoyConArtworkTests
{
    public TestContext TestContext { get; set; }

    [DataTestMethod]
    [DataRow(InputDeviceType.Switch2JoyConLeft, false, 1)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, true, 3)]
    [DataRow(InputDeviceType.Switch2JoyConRight, false, 2)]
    [DataRow(InputDeviceType.Switch2JoyConRight, true, 4)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, false, 0)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, true, 0)]
    public void ViewFollowsModelAndHoldingStyle(InputDeviceType type, bool sideways, int expected)
    {
        var hold = sideways ? Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical;
        Assert.AreEqual((JoyConView)expected, JoyConArtwork.ResolveView(type, hold));
        var icon = JoyConArtwork.ForDevice(type, hold);
        Assert.IsTrue(icon.IsFrozen);
        if (type == InputDeviceType.Switch2JoyConJoined)
            Assert.AreSame(JoyConArtwork.Pair, icon);
        else
            Assert.AreEqual(sideways, icon.Width > icon.Height);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void ConnectedControllerOverrideWinsOverProfileArtwork(Switch2ControllerModel model)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model, 1, 2,
            out var runtime, out _));
        Assert.IsTrue(JoyConArtwork.IsSideways(JoyConArtwork.ResolveView(runtime.DeviceType,
            runtime.ResolveStandaloneJoyConHoldMode(Switch2JoyConHoldMode.Horizontal))));
        Assert.IsTrue(runtime.TrySetStandaloneJoyConHoldMode(Switch2JoyConHoldMode.Vertical, out _));
        Assert.IsFalse(JoyConArtwork.IsSideways(JoyConArtwork.ResolveView(runtime.DeviceType,
            runtime.ResolveStandaloneJoyConHoldMode(Switch2JoyConHoldMode.Horizontal))));
        Assert.IsTrue(runtime.TrySetStandaloneJoyConHoldMode(Switch2JoyConHoldMode.Horizontal, out _));
        Assert.IsTrue(JoyConArtwork.IsSideways(JoyConArtwork.ResolveView(runtime.DeviceType,
            runtime.ResolveStandaloneJoyConHoldMode(Switch2JoyConHoldMode.Vertical))));
    }

    [TestMethod]
    public void EveryViewHasFrozenArtworkAndUniqueInBoundsTargets()
    {
        var bounds = new Rect(0, 0, 440, 220);
        foreach (JoyConView view in Enum.GetValues<JoyConView>())
        {
            var drawing = JoyConArtwork.ForView(view);
            Assert.IsTrue(drawing.IsFrozen);
            Assert.AreEqual(bounds.Size, new Size(drawing.Width, drawing.Height));
            foreach (var layout in Enum.GetValues<Switch2FaceButtonLayout>())
            {
                var targets = JoyConArtwork.Targets(view, layout);
                Assert.AreEqual(targets.Count, targets.Select(t => t.Control).Distinct().Count());
                foreach (var target in targets)
                    Assert.IsTrue(bounds.Contains(target.Bounds), $"{view}: {target.Control}");
            }
            if (view != JoyConView.UprightRight && view != JoyConView.SidewaysRight)
                Assert.IsTrue(bounds.Contains(JoyConArtwork.StickBounds(view, true)));
            if (view != JoyConView.UprightLeft && view != JoyConView.SidewaysLeft)
                Assert.IsTrue(bounds.Contains(JoyConArtwork.StickBounds(view, false)));
        }
    }

    [TestMethod]
    public void SidewaysControlsMatchCanonicalMiniControllerInputs()
    {
        foreach (var view in new[] { JoyConView.SidewaysLeft, JoyConView.SidewaysRight })
        {
            var targets = JoyConArtwork.Targets(view, Switch2FaceButtonLayout.Xbox);
            bool left = view == JoyConView.SidewaysLeft;
            Rect Find(DS4Controls c) => targets.Single(t => t.Control == c).Bounds;
            var transform = new MatrixTransform(JoyConArtwork.ViewTransform(view));
            void At(DS4Controls c, string physical) => Assert.AreEqual(
                transform.TransformBounds(JoyConArtwork.Buttons[physical]), Find(c));
            At(DS4Controls.Triangle, left ? "Up" : "X");
            At(DS4Controls.Cross, left ? "Right" : "B");
            At(DS4Controls.Square, left ? "Down" : "Y");
            At(DS4Controls.Circle, left ? "Left" : "A");
            At(DS4Controls.Options, left ? "Minus" : "Plus");
            At(DS4Controls.PS, left ? "Capture" : "Home");
            At(left ? DS4Controls.Switch2JoyConLeftPaddle1 : DS4Controls.Switch2JoyConRightPaddle1,
                left ? "L" : "R");
            At(left ? DS4Controls.Switch2JoyConLeftPaddle2 : DS4Controls.Switch2JoyConRightPaddle2,
                left ? "ZL" : "ZR");
            Assert.IsTrue(Find(DS4Controls.L1).Right < Find(DS4Controls.R1).Left,
                "SL must be left of SR in both sideways views.");
            Assert.IsTrue(Find(DS4Controls.L1).Bottom < JoyConArtwork.StickBounds(view, left).Top);
            Assert.IsTrue(JoyConArtwork.StickBounds(view, left).Right < Find(DS4Controls.Cross).Left);
            Assert.IsFalse(targets.Any(t => t.Control == DS4Controls.DpadUp));
        }
    }

    [TestMethod]
    public void NintendoLayoutUpdatesTargetsWithoutMovingArtwork()
    {
        foreach (JoyConView view in Enum.GetValues<JoyConView>())
        {
            if (view == JoyConView.UprightLeft) continue;
            var xbox = JoyConArtwork.Targets(view, Switch2FaceButtonLayout.Xbox).ToDictionary(t => t.Control);
            var nintendo = JoyConArtwork.Targets(view, Switch2FaceButtonLayout.Nintendo).ToDictionary(t => t.Control);
            Assert.AreEqual(xbox[DS4Controls.Triangle].Bounds, nintendo[DS4Controls.Square].Bounds);
            Assert.AreEqual(xbox[DS4Controls.Square].Bounds, nintendo[DS4Controls.Triangle].Bounds);
            Assert.AreEqual(xbox[DS4Controls.Cross].Bounds, nintendo[DS4Controls.Circle].Bounds);
            Assert.AreEqual(xbox[DS4Controls.Circle].Bounds, nintendo[DS4Controls.Cross].Bounds);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProfilePresentationNotifiesOnlyWhenItsValueChanges()
    {
        RunSta(() =>
        {
            const int slot = 0;
            var oldHold = Global.Switch2JoyConStandaloneHoldMode[slot];
            var oldLayout = Global.Switch2FaceButtonLayout[slot];
            var oldOutput = Global.outDevTypeTemp[slot];
            try
            {
                _ = new System.Windows.Controls.ContextMenu();
                Global.Switch2JoyConStandaloneHoldMode[slot] = Switch2JoyConHoldMode.Vertical;
                Global.Switch2FaceButtonLayout[slot] = Switch2FaceButtonLayout.Xbox;
                var vm = new ProfileSettingsViewModel(slot);
                int changes = 0;
                vm.JoyConPresentationChanged += (_, _) => changes++;
                vm.Switch2JoyConStandaloneHoldModeIndex = 1;
                vm.Switch2JoyConStandaloneHoldModeIndex = 1;
                vm.Switch2FaceButtonLayoutIndex = 1;
                vm.Switch2FaceButtonLayoutIndex = 1;
                Assert.AreEqual(2, changes);
                vm.Switch2JoyConStandaloneHoldModeIndex = 0;
                vm.Switch2FaceButtonLayoutIndex = 0;
                Assert.AreEqual(4, changes);
            }
            finally
            {
                Global.Switch2JoyConStandaloneHoldMode[slot] = oldHold;
                Global.Switch2FaceButtonLayout[slot] = oldLayout;
                Global.outDevTypeTemp[slot] = oldOutput;
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void ControllerListArtworkNotifiesWhenHoldingStyleChanges()
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
            Switch2ControllerModel.JoyCon2Right, 1, 2, out var runtime, out _));
        Assert.IsTrue(runtime.TrySetStandaloneJoyConHoldMode(Switch2JoyConHoldMode.Vertical, out _));
        var model = new CompositeDeviceModel(runtime, Global.TEST_PROFILE_INDEX, null, null);
        int notifications = 0;
        EventHandler changed = (_, _) => notifications++;
        model.ControllerImageSourceChanged += changed;
        Assert.AreSame(JoyConArtwork.Right, model.ControllerImageSource);
        Assert.IsTrue(model.TryToggleSwitch2StandaloneHoldMode(out _));
        Assert.AreEqual(1, notifications);
        Assert.AreSame(JoyConArtwork.SidewaysRight, model.ControllerImageSource);
        Assert.IsTrue(model.TryToggleSwitch2StandaloneHoldMode(out _));
        Assert.AreEqual(2, notifications);
        Assert.AreSame(JoyConArtwork.Right, model.ControllerImageSource);
        model.ControllerImageSourceChanged -= changed;
        Assert.IsTrue(model.TryToggleSwitch2StandaloneHoldMode(out _));
        Assert.AreEqual(2, notifications);
    }

    [TestMethod]
    public void ListOnlyBadgeFollowsCurrentDiagramWithoutChangingAvailability()
    {
        var mappings = new MappingListViewModel(Global.TEST_PROFILE_INDEX, OutContType.ViiperXboxOne,
            InputDeviceType.Switch2JoyConRight);
        var c = mappings.ControlMap[DS4Controls.Switch2C];
        int notifications = 0;
        c.IsControllerMapListOnlyChanged += (_, _) => notifications++;
        c.SetControllerMapTargetPresence(true);
        c.SetControllerMapTargetPresence(true);
        Assert.IsFalse(c.IsControllerMapListOnly);
        Assert.IsNull(c.PhysicalControllerAvailabilityHint);
        Assert.AreEqual(1, notifications);
        c.SetControllerMapTargetPresence(false);
        Assert.IsTrue(c.IsControllerMapListOnly);
        Assert.AreEqual(2, notifications);
        var leftOnly = mappings.ControlMap[DS4Controls.Switch2JoyConLeftPaddle1];
        leftOnly.SetControllerMapTargetPresence(true);
        Assert.IsFalse(leftOnly.IsAvailableOnPhysicalController);
        Assert.IsFalse(leftOnly.IsControllerMapListOnly);
        Assert.IsNotNull(leftOnly.PhysicalControllerAvailabilityHint);
    }

    [TestMethod]
    public void RenderOrientationContactSheet()
    {
        RunSta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 25, 31)), null, new Rect(0, 0, 880, 750));
                var views = new[] { JoyConView.UprightLeft, JoyConView.UprightRight,
                    JoyConView.SidewaysLeft, JoyConView.SidewaysRight, JoyConView.Pair };
                for (int i = 0; i < views.Length; i++)
                {
                    double x = i % 2 * 440, y = i / 2 * 250;
                    dc.DrawText(new FormattedText(views[i].ToString(), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.WhiteSmoke, 1),
                        new Point(x + 20, y + 5));
                    dc.DrawImage(JoyConArtwork.ForView(views[i]), new Rect(x, y + 25, 440, 220));
                }
            }
            var bitmap = new RenderTargetBitmap(880, 750, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(TestContext.TestResultsDirectory, "joycon-orientations.png");
            Directory.CreateDirectory(TestContext.TestResultsDirectory);
            using (var file = File.Create(path)) encoder.Save(file);
            TestContext.AddResultFile(path);
        });
    }

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "UI check timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

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
