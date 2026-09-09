using System.Runtime.CompilerServices;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DS4Windows;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

// Production markup and pure presentation policy only: no visible window,
// controller, profile save, or live observation is started by these tests.
[TestClass]
public sealed class BindingWindowLiveInputLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void PreviewAndToggleStayOutsideTheScrollableAndMacroEditor()
    {
        var document = XDocument.Load(SourcePath("BindingWindow.xaml"));
        XElement preview = Named(document, "bindingLiveInput");
        XElement previewScroll = Named(document, "liveInputPreviewScroll");
        XElement toggle = Named(document, "liveInputPreviewToggle");
        XElement scroll = Named(document, "bindingEditorScroll");
        Assert.AreEqual("MappingLiveInputControl", preview.Name.LocalName);
        Assert.AreSame(previewScroll, preview.Parent);
        Assert.AreSame(scroll.Parent, previewScroll.Parent);
        Assert.AreSame(scroll.Parent, toggle.Parent);
        Assert.AreEqual(Wpf + "Grid", previewScroll.Parent.Name);
        Assert.AreEqual("0", (string)toggle.Attribute("Grid.Row"));
        Assert.AreEqual("1", (string)previewScroll.Attribute("Grid.Row"));
        Assert.AreEqual("2", (string)scroll.Attribute("Grid.Row"));
        foreach (string panelName in new[] { "fullPanel", "mapBindingPanel", "extrasSidePanel" })
        {
            XElement panel = Named(document, panelName);
            Assert.IsTrue(panel.Ancestors().Contains(scroll), panelName);
            Assert.IsFalse(preview.Ancestors().Contains(panel), panelName);
            Assert.IsFalse(toggle.Ancestors().Contains(panel), panelName);
        }
    }

    [TestMethod]
    public void PreviewStartsCollapsedAndSmallScreensCanScrollTheExistingEditor()
    {
        var document = XDocument.Load(SourcePath("BindingWindow.xaml"));
        XElement preview = Named(document, "bindingLiveInput");
        XElement previewScroll = Named(document, "liveInputPreviewScroll");
        XElement toggle = Named(document, "liveInputPreviewToggle");
        XElement scroll = Named(document, "bindingEditorScroll");
        Assert.AreEqual("Collapsed", (string)preview.Attribute("Visibility"));
        Assert.AreEqual("Collapsed", (string)previewScroll.Attribute("Visibility"));
        Assert.AreEqual("Collapsed", (string)toggle.Attribute("Visibility"));
        Assert.AreEqual("Show live preview", (string)toggle.Attribute("Content"));
        Assert.AreEqual("Right", (string)toggle.Attribute("HorizontalAlignment"));
        Assert.AreEqual("LiveInputPreviewToggle_Click", (string)toggle.Attribute("Click"));
        Assert.IsNull(preview.Attribute("MaxHeight"), "The preview itself must not clip its content.");
        Assert.AreEqual("180", (string)previewScroll.Attribute("MaxHeight"));
        Assert.AreEqual("Auto", (string)previewScroll.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("Disabled", (string)previewScroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Stretch", (string)preview.Attribute("HorizontalAlignment"));
        Assert.AreEqual("Auto", (string)scroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Auto", (string)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("1020", (string)document.Root.Attribute("Width"));
        Assert.AreEqual("540", (string)document.Root.Attribute("Height"),
            "A hidden preview must not enlarge the default dialog.");
        Assert.AreEqual("630", (string)Named(document, "outConPanel").Attribute("Width"));
        Assert.AreEqual("247", (string)Named(document, "outputControllerCanvas").Attribute("Height"));
    }

    [DataTestMethod]
    [DataRow(BindingWindow.ExposeMode.Full, -1, false)]
    [DataRow(BindingWindow.ExposeMode.Full, 0, true)]
    [DataRow(BindingWindow.ExposeMode.Full, 2, true)]
    [DataRow(BindingWindow.ExposeMode.Keyboard, 0, false)]
    [DataRow(BindingWindow.ExposeMode.Keyboard, 2, false)]
    public void OnlyExplicitFullDialogPhysicalContextMakesPreviewAvailable(
        BindingWindow.ExposeMode mode, int physicalIndex, bool expected)
    {
        Assert.AreEqual(expected, BindingWindow.ShouldShowLiveInput(mode, physicalIndex));
        Assert.IsFalse(BindingWindow.ShouldShowLiveInput(mode,
            ControlService.CURRENT_DS4_CONTROLLER_LIMIT));
        Assert.IsFalse(BindingWindow.ShouldShowLiveInput(mode, Global.TEST_PROFILE_INDEX));
    }

    [TestMethod]
    public void ObservationRequiresUserPreviewChoiceAndLiveWindowLifetime()
    {
        for (int flags = 0; flags < 32; flags++)
        {
            bool context = (flags & 1) != 0;
            bool preview = (flags & 2) != 0;
            bool loaded = (flags & 4) != 0;
            bool visible = (flags & 8) != 0;
            bool closed = (flags & 16) != 0;
            Assert.AreEqual(context && preview && loaded && visible && !closed,
                BindingWindow.ShouldObserveLiveInput(context, preview, loaded, visible, closed),
                $"Context/preview/load/visibility/closed flags: {flags}");
        }
    }

    [DataTestMethod]
    [DataRow(1920d, 1040d, 1020d, 710d)]
    [DataRow(1280d, 680d, 1020d, 656d)]
    [DataRow(800d, 560d, 776d, 536d)]
    [DataRow(640d, 400d, 616d, 376d)]
    public void ExpandedPreviewFitsTheMonitorWorkAreaInDeviceIndependentUnits(
        double workWidth, double workHeight, double expectedWidth, double expectedHeight)
    {
        var size = BindingWindow.GetLiveInputWindowSize(workWidth, workHeight);
        Assert.AreEqual(expectedWidth, size.Width);
        Assert.AreEqual(expectedHeight, size.Height);
        Assert.IsTrue(size.Width < workWidth);
        Assert.IsTrue(size.Height < workHeight);
    }

    [TestMethod]
    public void FirstRevealKeepsBottomControlsInsideNegativeOriginMonitor()
    {
        var position = BindingWindow.GetLiveInputWindowPosition(
            new System.Windows.Rect(-1920, 0, 1920, 1040),
            new System.Windows.Size(1020, 710), new System.Windows.Point(-1100, 700));
        Assert.AreEqual(new System.Windows.Point(-1100, 318), position);
    }

    [TestMethod]
    public void FirstRevealFitsSmallWorkAreaWithoutRecenteringEveryToggle()
    {
        var position = BindingWindow.GetLiveInputWindowPosition(
            new System.Windows.Rect(0, 0, 800, 560),
            BindingWindow.GetLiveInputWindowSize(800, 560), new System.Windows.Point(700, 300));
        Assert.AreEqual(new System.Windows.Point(12, 12), position);
        string source = File.ReadAllText(SourcePath("BindingWindow.xaml.cs"));
        StringAssert.Contains(source, "liveInputBoundsApplied : liveInputContextBoundsApplied)) return;");
        StringAssert.Contains(source, "liveInputBoundsApplied = true;");
        StringAssert.Contains(source, "Left = position.X;");
        StringAssert.Contains(source, "Top = position.Y;");
    }

    [TestMethod]
    public void UnspecifiedInitialPositionFallsBackToCurrentWorkAreaOrigin()
    {
        var position = BindingWindow.GetLiveInputWindowPosition(
            new System.Windows.Rect(100, 50, 1200, 800),
            new System.Windows.Size(1020, 710), new System.Windows.Point(double.NaN, double.NaN));
        Assert.AreEqual(new System.Windows.Point(112, 62), position);
    }

    [TestMethod]
    public void ContextOnlySizeReservesTheToggleWithoutOpeningPreviewAndClampsSmallMonitors()
    {
        Assert.AreEqual(new Size(1020, 576), BindingWindow.GetLiveInputWindowSize(1920, 1040, false));
        Assert.AreEqual(new Size(776, 536), BindingWindow.GetLiveInputWindowSize(800, 560, false));
        Assert.AreEqual(new Size(616, 376), BindingWindow.GetLiveInputWindowSize(640, 400, false));
    }

    [TestMethod]
    public void ExplicitIdentityAndToggleUseOnlyReadOnlyObservation()
    {
        string source = File.ReadAllText(SourcePath("BindingWindow.xaml.cs"));
        string integration = Between(source, "public void SetLiveInputDevice(",
            "internal void SelectSecondAction()");
        StringAssert.Contains(integration,
            "public void SetLiveInputDevice(int physicalIndex, DS4Device expectedDevice)");
        StringAssert.Contains(integration, "hasLiveInputContext ? expectedDevice : null");
        StringAssert.Contains(integration, "LiveInputPreviewVisible = !LiveInputPreviewVisible");
        StringAssert.Contains(integration, "\"Hide live preview\" : \"Show live preview\"");
        StringAssert.Contains(integration, "if (IsLoaded && hasLiveInputContext)");
        StringAssert.Contains(integration,
            "ShouldObserveLiveInput(hasLiveInputContext, LiveInputPreviewVisible, IsLoaded, IsVisible,");
        foreach (string forbidden in new[] { "bindingVM.WriteBinds", "setRumble", "SelectedIndex =",
            "SetLiveInputDevice(deviceNum", "UseDevice(deviceNum", "WriteProfile", "SaveProfile" })
            Assert.IsFalse(integration.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    public void VisibilityUnloadAndFinalCloseControlObservationWithoutChangingSaveBehavior()
    {
        string source = File.ReadAllText(SourcePath("BindingWindow.xaml.cs"));
        foreach (string wire in new[] { "Loaded += BindingWindow_Loaded;",
            "Unloaded += BindingWindow_Unloaded;", "IsVisibleChanged += BindingWindow_IsVisibleChanged;",
            "Closed += BindingWindow_Closed;" })
            StringAssert.Contains(source, wire);
        StringAssert.Contains(Between(source, "private void BindingWindow_Unloaded(",
            "private void BindingWindow_IsVisibleChanged("), "bindingLiveInput.EnableControl(false)");
        StringAssert.Contains(Between(source, "private void BindingWindow_IsVisibleChanged(",
            "private void BindingWindow_Closed("), "UpdateLiveInputObservation()");
        string closed = Between(source, "private void BindingWindow_Closed(", "internal void SelectSecondAction()");
        StringAssert.Contains(closed, "liveInputWindowClosed = true");
        StringAssert.Contains(closed, "bindingLiveInput.EnableControl(false)");
        Assert.IsFalse(closed.Contains("liveInputPreviewRequested =", StringComparison.Ordinal),
            "The parent must be able to retain the user's preview choice after modal close.");
        StringAssert.Contains(source, "private void Window_Closing(");
        StringAssert.Contains(source, "bindingVM.WriteBinds();");
        StringAssert.Contains(source, "UnregisterDataContext();");
    }

    internal static void ValidateRenderedBindingWindows(Application application,
        string resultsDirectory, ICollection<string> renderedFiles)
    {
        // The existing theme test owns the only WPF Application and STA.
        // Measure the real modal content without Show/ShowDialog, an HWND,
        // a physical controller, polling, or a persisted profile edit.
        ResourceDictionary[] original = application.Resources.MergedDictionaries.ToArray();
        try
        {
            foreach (string theme in new[] { "DefaultTheme", "DarkTheme" })
            {
                application.Resources.MergedDictionaries.Clear();
                var colors = new ResourceDictionary();
                application.Resources.MergedDictionaries.Add(colors);
                colors.Source = new Uri($"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml", UriKind.Relative);
                foreach (bool narrow in new[] { false, true })
                {
                    foreach (bool show in new[] { false, true })
                        ValidateRenderedBindingWindow(theme, BindingWindow.ExposeMode.Full,
                            narrow, show, resultsDirectory, renderedFiles);
                    ValidateRenderedBindingWindow(theme, BindingWindow.ExposeMode.Keyboard,
                        narrow, false, resultsDirectory, renderedFiles);
                }
            }
        }
        finally
        {
            application.Resources.MergedDictionaries.Clear();
            foreach (ResourceDictionary dictionary in original)
                application.Resources.MergedDictionaries.Add(dictionary);
        }
    }

    private static void ValidateRenderedBindingWindow(string theme, BindingWindow.ExposeMode mode,
        bool narrow, bool show, string resultsDirectory, ICollection<string> renderedFiles)
    {
        var settings = new DS4ControlSettings(DS4Controls.Cross);
        var window = new BindingWindow(Global.TEST_PROFILE_INDEX, settings, mode);
        var preview = (MappingLiveInputControl)window.FindName("bindingLiveInput");
        try
        {
            Assert.IsFalse(window.LiveInputPreviewVisible);
            Assert.AreEqual(mode == BindingWindow.ExposeMode.Full ? 540d : 300d, window.Height);
            Assert.AreEqual(Visibility.Collapsed,
                ((Button)window.FindName("liveInputPreviewToggle")).Visibility);
            // Null expected identity deliberately cannot resolve a real slot.
            window.SetLiveInputDevice(0, null);
            Assert.AreEqual(mode == BindingWindow.ExposeMode.Full ? 576d : 300d, window.Height);
            window.LiveInputPreviewVisible = show || mode == BindingWindow.ExposeMode.Keyboard;
            Assert.AreEqual(show, window.LiveInputPreviewVisible);
            var content = (FrameworkElement)window.Content;
            var editor = (ScrollViewer)window.FindName("bindingEditorScroll");
            var viewport = (ScrollViewer)window.FindName("liveInputPreviewScroll");
            var toggle = (Button)window.FindName("liveInputPreviewToggle");
            int width = narrow ? 600 : mode == BindingWindow.ExposeMode.Full ? 1000 : 930;
            int height = mode == BindingWindow.ExposeMode.Keyboard ? 260 :
                narrow ? 346 : show ? 678 : 544;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            Assert.IsFalse(preview.IsPolling, "An unshown modal must not poll.");
            Assert.IsFalse(window.IsVisible);
            Assert.IsTrue(editor.ViewportHeight > 0 && editor.ViewportWidth > 0);
            AssertInside(content, editor);
            if (narrow) Assert.IsTrue(editor.ScrollableWidth > 0,
                "Fixed keyboard and controller artwork must remain reachable by scrolling.");
            if (mode == BindingWindow.ExposeMode.Full)
            {
                Assert.AreEqual(Visibility.Visible, toggle.Visibility);
                AssertInside(content, toggle);
                Assert.AreEqual(show ? "Hide live preview" : "Show live preview", toggle.Content);
                Assert.AreEqual(630d, ((FrameworkElement)window.FindName("outConPanel")).ActualWidth);
                var artwork = (FrameworkElement)window.FindName("outputControllerCanvas");
                Assert.AreEqual(247d, artwork.Height);
                Assert.AreEqual(247d, artwork.ActualHeight,
                    1.0 / VisualTreeHelper.GetDpi(artwork).DpiScaleY,
                    "Layout rounding may change the measured height by at most one physical pixel.");
                if (!narrow && !show) Assert.AreEqual(0d, editor.ScrollableHeight,
                    "The compact toggle must not force the previously visible editor to scroll.");
            }
            else Assert.AreEqual(Visibility.Collapsed, toggle.Visibility);
            Assert.AreEqual(show ? Visibility.Visible : Visibility.Collapsed, viewport.Visibility);
            if (show)
            {
                AssertInside(content, viewport);
                Assert.IsTrue(viewport.ViewportHeight > 0 && viewport.ActualHeight <= 180);
                Assert.IsTrue(viewport.ExtentHeight + 0.1 >= preview.ActualHeight,
                    "Every preview row must be available inside the scroll extent.");
            }
            else Assert.AreEqual(0d, viewport.ActualHeight);
            int dpi = narrow ? 144 : 96;
            var bitmap = new RenderTargetBitmap(width * dpi / 96, height * dpi / 96,
                dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(resultsDirectory,
                $"binding-live-{theme}-{mode}-{width}-{(show ? "shown" : "hidden")}.png");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) encoder.Save(stream);
            renderedFiles.Add(path);
            window.LiveInputPreviewVisible = false;
            Assert.IsFalse(preview.IsPolling);
            Assert.AreEqual(Visibility.Collapsed, viewport.Visibility);
        }
        finally
        {
            preview.EnableControl(false);
            // Closing commits only the detached test-local settings object.
            window.Close();
        }
        Assert.IsFalse(preview.IsPolling);
    }

    private static void AssertInside(FrameworkElement content, FrameworkElement child)
    {
        Rect bounds = child.TransformToAncestor(content).TransformBounds(new Rect(child.RenderSize));
        Assert.IsTrue(bounds.Left >= -0.1 && bounds.Top >= -0.1 &&
            bounds.Right <= content.ActualWidth + 0.1 && bounds.Bottom <= content.ActualHeight + 0.1,
            $"{child.Name} ({bounds}) is outside {content.RenderSize}.");
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => (string)element.Attribute(Xaml + "Name") == name);

    private static string SourcePath(string file, [CallerFilePath] string caller = "") =>
        Path.Combine(Path.GetDirectoryName(caller)!, "..", "DS4Windows", "DS4Forms", file);

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(last > first, end);
        return source[first..last];
    }
}
