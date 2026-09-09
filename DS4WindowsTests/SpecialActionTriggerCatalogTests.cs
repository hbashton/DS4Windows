using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class SpecialActionTriggerCatalogTests
{
    public TestContext TestContext { get; set; }
    // Localization keeps process-wide weak targets. Their owning dispatcher
    // must keep pumping between editor instances, just like the actual app.
    // This background dispatcher owns no windows and ends with the testhost.
    private static readonly Lazy<Dispatcher> EditorDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Special Actions headless WPF dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    [TestMethod]
    public void OneCatalogHasUniqueCanonicalTagsAndRuntimeEnumsForEveryChecklistEntry()
    {
        var entries = SpecialActionTriggerCatalog.Entries;
        Assert.AreEqual(entries.Count, entries.Select(item => item.Tag).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(entries.Count, entries.Select(item => item.Control).Distinct().Count());
        foreach (var entry in entries)
        {
            Assert.AreNotEqual(DS4Controls.None, entry.Control);
            var macro = new SpecialAction("Catalog", entry.Tag, "Macro", "65/65");
            var multi = new SpecialAction("Catalog", entry.Tag, "MultiAction", "65/65,66/66,67/67");
            var profile = new SpecialAction("Catalog", entry.Tag, "Profile", "Target", extras: entry.Tag);
            var key = new SpecialAction("Catalog", entry.Tag, "Key", "65", extras: "Press\n" + entry.Tag);
            foreach (var action in new[] { macro, multi, profile, key })
                CollectionAssert.AreEqual(new[] { entry.Control }, action.trigger, entry.Tag);
            CollectionAssert.AreEqual(new[] { entry.Control }, profile.uTrigger, entry.Tag);
            CollectionAssert.AreEqual(new[] { entry.Control }, key.uTrigger, entry.Tag);
        }
        foreach (DS4Controls control in Enum.GetValues<DS4Controls>().Where(value => value >= DS4Controls.Switch2C))
            Assert.IsTrue(entries.Any(item => item.Control == control), control.ToString());
        Assert.AreNotEqual(entries.Single(item => item.Tag == "Mute").Control,
            entries.Single(item => item.Tag == "Switch 2 C").Control);
    }

    [TestMethod]
    public void EveryExtraCheckboxActuallyAddsAndRemovesItsRegularAndUnloadTrigger()
    {
        OnSta(() =>
        {
            var editor = new SpecialActionEditor(Global.TEST_PROFILE_INDEX, new ProfileList());
            Assert.AreEqual(SpecialActionTriggerCatalog.Entries.Count, editor.TriggerCheckBoxes.Count);
            CollectionAssert.AreEqual(editor.TriggerCheckBoxes.Select(box => box.Tag).ToArray(),
                editor.UnloadTriggerCheckBoxes.Select(box => box.Tag).ToArray());
            foreach (string tag in ExtraTags())
            {
                foreach (bool unload in new[] { false, true })
                {
                    CheckBox box = (unload ? editor.UnloadTriggerCheckBoxes : editor.TriggerCheckBoxes)
                        .Single(candidate => (string)candidate.Tag == tag);
                    Assert.IsTrue(box.IsEnabled, tag);
                    Assert.AreEqual(Visibility.Visible, box.Visibility, tag);
                    Click(box, true);
                    Click(box, true); // A repeated routed event must not duplicate a condition.
                    var selected = unload ? editor.EditorViewModel.ControlUnloadTriggerList :
                        editor.EditorViewModel.ControlTriggerList;
                    Assert.AreEqual(1, selected.Count(value => value == tag), tag);
                    Click(box, false);
                    Assert.IsFalse(selected.Contains(tag), tag);
                }
            }
        });
    }

    [DataTestMethod]
    [DataRow("Macro", "65/65", "")]
    [DataRow("MultiAction", "65/65,66/66,67/67", "")]
    [DataRow("Profile", "Target", "Cross/AutomaticUntrigger")]
    [DataRow("Key", "65", "Press\nCross")]
    public void ActualEditorSaveAndActionsXmlReloadPreserveSelectedExtras(string type, string details, string extras)
    {
        string stage = "starting STA";
        OnSta(() =>
        {
            stage = "preparing isolated action storage";
            string directory = Path.Combine(Path.GetTempPath(), "DS4Windows-special-action-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string previousPath = Global.store.m_Actions;
            var previousActions = Global.GetActions().ToArray();
            var previousDone = Mapping.actionDone.ToArray();
            try
            {
                Global.store.m_Actions = Path.Combine(directory, "Actions.xml");
                Global.GetActions().Clear();
                var original = new SpecialAction("Extra button test", "Cross", type, details, extras: extras);
                Global.GetActions().Add(original);
                var profiles = new ProfileList();
                profiles.AddProfileSort("Target");
                stage = "constructing real SpecialActionEditor";
                var editor = new SpecialActionEditor(Global.TEST_PROFILE_INDEX, profiles, original);
                stage = "clicking actual trigger checkboxes";
                Click(editor.TriggerCheckBoxes.Single(box => (string)box.Tag == "Cross"), false);
                Click(editor.UnloadTriggerCheckBoxes.Single(box => (string)box.Tag == "Cross"), false);
                foreach (string tag in ExtraTags())
                {
                    Click(editor.TriggerCheckBoxes.Single(box => (string)box.Tag == tag), true);
                    if (type is "Profile" or "Key")
                        Click(editor.UnloadTriggerCheckBoxes.Single(box => (string)box.Tag == tag), true);
                }

                bool saved = false;
                editor.Saved += (_, _) => saved = true;
                stage = "checking Save validation before any possible dialog";
                Assert.IsTrue(editor.EditorViewModel.EditMode);
                Assert.AreEqual(original.name, editor.EditorViewModel.ActionName);
                Assert.IsTrue(editor.EditorViewModel.IsValid(new SpecialAction("", "", "", "")));
                stage = "clicking real Save button";
                ((Button)editor.FindName("saveBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                stage = "verifying saved Actions.xml";
                Assert.IsTrue(saved, "The real Save button must complete validation and persistence.");
                Assert.IsTrue(File.Exists(Global.store.m_Actions));
                string xml = File.ReadAllText(Global.store.m_Actions);
                foreach (string tag in ExtraTags()) StringAssert.Contains(xml, tag);
                stage = "reloading Actions.xml";
                Assert.IsTrue(Global.LoadActions());
                SpecialAction loaded = Global.GetActions().Single();
                CollectionAssert.AreEqual(ExtraTags(), loaded.controls.Split('/'));
                Assert.IsFalse(loaded.trigger.Contains(DS4Controls.None));
                stage = "reopening saved action in real editor";
                var reopened = new SpecialActionEditor(Global.TEST_PROFILE_INDEX, profiles, loaded);
                CollectionAssert.AreEqual(ExtraTags(), reopened.EditorViewModel.ControlTriggerList.ToArray());
                foreach (string tag in ExtraTags())
                    Assert.IsTrue(reopened.TriggerCheckBoxes.Single(box => (string)box.Tag == tag).IsChecked == true, tag);
                if (type is "Profile" or "Key")
                {
                    CollectionAssert.AreEqual(ExtraTags(), reopened.EditorViewModel.ControlUnloadTriggerList.ToArray());
                    Assert.IsFalse(loaded.uTrigger.Contains(DS4Controls.None));
                    foreach (string tag in ExtraTags())
                        Assert.IsTrue(reopened.UnloadTriggerCheckBoxes.Single(box => (string)box.Tag == tag).IsChecked == true, tag);
                }
                if (type == "Profile") Assert.IsTrue(loaded.automaticUntrigger);
                if (type == "Key") Assert.IsTrue(loaded.keyType.HasFlag(DS4KeyType.Toggle));
            }
            finally
            {
                Global.store.m_Actions = previousPath;
                Global.GetActions().Clear();
                Global.GetActions().AddRange(previousActions);
                Mapping.actionDone.Clear();
                Mapping.actionDone.AddRange(previousDone);
                Directory.Delete(directory, recursive: true);
            }
        }, () => stage);
    }

    [TestMethod]
    public void OpeningOlderOrFutureTokensNeverSilentlyDeletesSelections()
    {
        OnSta(() =>
        {
            const string tags = "Capture/SideL/SideR/Future Extra";
            var editor = new SpecialActionEditor(Global.TEST_PROFILE_INDEX, new ProfileList(),
                new SpecialAction("Preserve", tags, "Key", "65", extras: "Release\n" + tags));
            CollectionAssert.AreEqual(tags.Split('/'), editor.EditorViewModel.ControlTriggerList.ToArray());
            CollectionAssert.AreEqual(tags.Split('/'), editor.EditorViewModel.ControlUnloadTriggerList.ToArray());
            foreach (var boxes in new[] { editor.TriggerCheckBoxes, editor.UnloadTriggerCheckBoxes })
                foreach (string tag in tags.Split('/')) Assert.IsTrue(boxes.Single(box => (string)box.Tag == tag).IsChecked == true);
            Click(editor.TriggerCheckBoxes.Single(box => (string)box.Tag == "Future Extra"), false);
            Assert.IsFalse(editor.EditorViewModel.ControlTriggerList.Contains("Future Extra"));
            Assert.IsTrue(editor.EditorViewModel.ControlUnloadTriggerList.Contains("Future Extra"));
        });
    }

    [TestMethod]
    public void MacroOptionsAreNotMisrepresentedAsUnknownUnloadButtons()
    {
        OnSta(() =>
        {
            var editor = new SpecialActionEditor(Global.TEST_PROFILE_INDEX, new ProfileList(),
                new SpecialAction("Macro options", "Switch 2 C", "Macro", "65/65",
                    extras: "Scan Code/RunOnRelease/Repeat"));
            Assert.AreEqual(0, editor.EditorViewModel.ControlUnloadTriggerList.Count);
            Assert.AreEqual(SpecialActionTriggerCatalog.Entries.Count, editor.UnloadTriggerCheckBoxes.Count);
            Assert.IsTrue(editor.EditorViewModel.SavedAction.keyType.HasFlag(DS4KeyType.ScanCode));
            Assert.IsTrue(editor.EditorViewModel.SavedAction.keyType.HasFlag(DS4KeyType.RepeatMacro));
            Assert.IsTrue(editor.EditorViewModel.SavedAction.pressRelease);
        });
    }

    [TestMethod]
    public void RealSpecialActionEvaluatorReadsExtrasAndReleasesInvalidNintendoMetadata()
    {
        var evaluate = typeof(Mapping).GetMethod("getBoolSpecialActionMapping", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(evaluate);
        var state = new DS4State { Mute = true, Capture = true, SideL = true, SideR = true,
            FnL = true, FnR = true, BLP = true, BRP = true };
        state.Switch2JoyConRawInputStatus = new Switch2JoyConRawInputStatus
        {
            IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            CButton = true, LeftPaddle1 = true, LeftPaddle2 = true, RightPaddle1 = true, RightPaddle2 = true,
            LeftRailSL = true, LeftRailSR = true, RightRailSL = true, RightRailSR = true,
            LeftPresent = true, RightPresent = true, LeftIrDistance = 999, RightIrDistance = 999,
            LeftIrRoughness = 3999, RightIrRoughness = 3999,
        };
        var exposed = new DS4StateExposed(state);
        var fields = new DS4StateFieldMapping(state, exposed, null);
        foreach (var entry in SpecialActionTriggerCatalog.Entries.Where(item => ExtraTags().Contains(item.Tag)))
        {
            var parsed = new SpecialAction("Runtime", entry.Tag, "Macro", "65/65");
            Assert.IsTrue((bool)evaluate.Invoke(null, new object[] { 0, parsed.trigger.Single(), state, exposed, null, fields }), entry.Tag);
        }
        state.Switch2JoyConRawInputStatus.IsValid = false;
        fields.PopulateFieldMapping(state, exposed, null);
        foreach (var entry in SpecialActionTriggerCatalog.Entries.Where(item => item.Control >= DS4Controls.Switch2C))
            Assert.IsFalse((bool)evaluate.Invoke(null, new object[] { 0, entry.Control, state, exposed, null, fields }), entry.Tag);
        Assert.IsTrue(fields.buttons[(int)DS4Controls.Mute], "C metadata must not masquerade as DualSense Mute.");
        var unknownChord = new SpecialAction("Future", "Mute/Future Extra", "Macro", "65/65");
        CollectionAssert.AreEqual(new[] { DS4Controls.Mute, DS4Controls.None }, unknownChord.trigger);
        Assert.IsFalse(unknownChord.trigger.All(control => (bool)evaluate.Invoke(null,
            new object[] { 0, control, state, exposed, null, fields })),
            "Keeping an unknown condition must not weaken a chord to just its recognized buttons.");
    }

    [DataTestMethod]
    [DataRow("DarkTheme", 720, 96, false)]
    [DataRow("DarkTheme", 1000, 144, true)]
    [DataRow("DefaultTheme", 720, 96, true)]
    [DataRow("DefaultTheme", 1000, 144, false)]
    public void RealEditorRendersReadableCheckableExtrasWithoutHorizontalClipping(string theme, int width, int dpi, bool unload)
    {
        // Capture the row's TestContext on the test runner, not on the
        // persistent UI dispatcher whose ExecutionContext spans earlier rows.
        string renderDirectory = TestContext.TestResultsDirectory;
        string imagePath = Path.Combine(renderDirectory, $"special-actions-{theme}-{width}-{dpi}-{unload}.png");
        OnSta(() =>
        {
            var editor = new SpecialActionEditor(Global.TEST_PROFILE_INDEX, new ProfileList(),
                new SpecialAction("Extra buttons", "Switch 2 C", "Key", "65", extras: "Press\nCapture"));
            editor.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml", UriKind.Relative),
            });
            editor.Background = (Brush)editor.FindResource("BackgroundColor");
            editor.Measure(new Size(width, 700));
            editor.Arrange(new Rect(0, 0, width, 700));
            editor.UpdateLayout();
            if (unload)
            {
                ((Button)editor.FindName("pressKeyToggleTriggerBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                editor.UpdateLayout();
            }
            var viewer = (ScrollViewer)editor.FindName(unload ? "unloadTriggersListView" : "triggersListView");
            Assert.AreEqual(Visibility.Visible, viewer.Visibility);
            Assert.IsTrue(viewer.ActualWidth >= 280);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "All controls must remain reachable by scrolling.");
            Assert.AreEqual(0d, viewer.ScrollableWidth, "Names should wrap instead of disappearing past a column edge.");
            var foreground = (SolidColorBrush)editor.FindResource("ForegroundColor");
            var background = (SolidColorBrush)editor.FindResource("BackgroundColor");
            Assert.AreNotEqual(foreground.Color, background.Color);
            var boxes = unload ? editor.UnloadTriggerCheckBoxes : editor.TriggerCheckBoxes;
            foreach (CheckBox box in boxes)
            {
                Assert.IsTrue(box.ActualWidth > 0 && box.ActualHeight > 0, (string)box.Tag);
                foreach (TextBlock text in ((StackPanel)box.Content).Children)
                {
                    Assert.AreEqual(foreground.Color, ((SolidColorBrush)text.Foreground).Color, text.Text);
                    Assert.AreEqual(TextWrapping.Wrap, text.TextWrapping);
                    Rect bounds = text.TransformToAncestor(viewer).TransformBounds(new Rect(text.RenderSize));
                    Assert.IsTrue(bounds.Left >= -0.5 && bounds.Right <= viewer.ActualWidth + 0.5, $"Clipped {text.Text}: {bounds}");
                }
            }
            // The initially visible region includes the controls requested in the report.
            foreach (string tag in new[] { "Switch 2 C", "Bottom Left Paddle", "Bottom Right Paddle", "Function Left", "Function Right", "Mute", "Capture" })
            {
                var box = boxes.Single(candidate => (string)candidate.Tag == tag);
                Rect bounds = box.TransformToAncestor(viewer).TransformBounds(new Rect(box.RenderSize));
                Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= viewer.ActualHeight, tag);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96d),
                (int)Math.Ceiling(700 * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(editor);
            Directory.CreateDirectory(renderDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(imagePath);
            encoder.Save(stream);
        });
        Assert.IsTrue(File.Exists(imagePath));
        TestContext.AddResultFile(imagePath);
    }

    private static string[] ExtraTags() => SpecialActionTriggerCatalog.Entries
        .Where(entry => entry.Control >= DS4Controls.Switch2C || entry.Control is
            DS4Controls.Mute or DS4Controls.Capture or DS4Controls.SideL or DS4Controls.SideR or
            DS4Controls.FnL or DS4Controls.FnR or DS4Controls.BLP or DS4Controls.BRP)
        .Select(entry => entry.Tag).ToArray();

    private static void Click(CheckBox box, bool selected)
    {
        box.IsChecked = selected;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static void OnSta(Action action, Func<string> progress = null)
    {
        var operation = EditorDispatcher.Value.InvokeAsync(action);
        Assert.AreSame(operation.Task, Task.WhenAny(operation.Task, Task.Delay(TimeSpan.FromSeconds(30))).GetAwaiter().GetResult(),
            $"Headless WPF test timed out: {progress?.Invoke() ?? "render / selection"}.");
        operation.Task.GetAwaiter().GetResult();
    }
}
