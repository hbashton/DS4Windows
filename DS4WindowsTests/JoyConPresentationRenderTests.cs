using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

[TestClass]
public sealed class JoyConPresentationRenderTests
{
    public TestContext TestContext { get; set; }
    private readonly List<string> renderedFiles = new();
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly (string File, string Binding, string Label)[] Hosts =
    {
        ("MainWindow.xaml", "{Binding ControllerImageSource}", "Controllers"),
        ("Themes/BridgeShellStyles.xaml", "{Binding ControllerImageSource}", "Sidebar"),
        ("ControllerOverviewControl.xaml", "{Binding SelectedController.ControllerImageSource}", "Overview"),
        ("Themes/BridgeShellStyles.xaml", "{Binding EditingControllerImageSource}", "Profile sidebar"),
    };
    private static DrawingImage[] Icons => new[] { JoyConArtwork.Left, JoyConArtwork.Right,
        JoyConArtwork.SidewaysLeft, JoyConArtwork.SidewaysRight, JoyConArtwork.Pair };

    [DataTestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    [DataRow(168)]
    [DataRow(192)]
    public void ProductionThumbnailHostsKeepEveryOrientationInsideAllFourEdges(int dpi)
    {
        RunSta(() =>
        {
            foreach (var spec in Hosts)
            foreach (var icon in Icons)
            {
                Assert.AreEqual(new Point(), icon.Drawing.Bounds.TopLeft, "Icon origin must be normalized.");
                Border host = CreateHost(spec.File, spec.Binding, icon);
                var image = (Image)host.Child;
                // No independently sized child can overrun border/padding at layout time.
                Assert.IsTrue(double.IsNaN(image.Width) && double.IsNaN(image.Height));
                Assert.AreEqual(Stretch.Uniform, image.Stretch);
                var bitmap = Render(host, dpi);
                Rect imageBounds = image.TransformToAncestor(host).TransformBounds(new Rect(image.RenderSize));
                Assert.IsTrue(new Rect(host.RenderSize).Contains(imageBounds), spec.Label);
                int stride = bitmap.PixelWidth * 4;
                byte[] pixels = new byte[stride * bitmap.PixelHeight];
                bitmap.CopyPixels(pixels, stride, 0);
                int count = 0;
                for (int y = 0; y < bitmap.PixelHeight; y++)
                for (int x = 0; x < bitmap.PixelWidth; x++)
                {
                    if (pixels[y * stride + x * 4 + 3] == 0) continue;
                    count++;
                    Assert.IsTrue(x >= 2 && y >= 2 && x < bitmap.PixelWidth - 2 && y < bitmap.PixelHeight - 2,
                        $"{spec.Label} at {dpi} DPI has painted content at edge {x},{y}.");
                }
                Assert.IsTrue(count > 40, $"{spec.Label} must not be blank.");
            }
        });
    }

    [TestMethod]
    public void ThumbnailsIncludeUpperTriggersAndLowestButtonsFromTheMappingArtwork()
    {
        // A nonblank, padded bitmap is not enough: b85 silently omitted ZL/ZR
        // from icons, leaving a flat truncated-looking top despite safe bounds.
        var required = new[]
        {
            new[] { "ZL", "L", "Capture" }, new[] { "ZR", "R", "Home", "C" },
            new[] { "ZL", "L", "Capture" }, new[] { "ZR", "R", "Home", "C" },
            new[] { "ZL", "L", "ZR", "R", "Capture", "Home", "C" },
        };
        for (int i = 0; i < Icons.Length; i++)
        {
            var painted = Shapes(Icons[i].Drawing).ToArray();
            foreach (string name in required[i])
                Assert.IsTrue(painted.Any(g => g.Bounds == JoyConArtwork.ButtonShape(name).Bounds),
                    $"Icon {i} is missing the actual painted {name} shape.");
        }

        static IEnumerable<Geometry> Shapes(Drawing drawing)
        {
            if (drawing is GeometryDrawing geometry && geometry.Brush != null &&
                geometry.Brush is not SolidColorBrush { Color.A: 0 })
                yield return geometry.Geometry;
            if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                foreach (var shape in Shapes(child))
                    yield return shape;
        }
    }

    [TestMethod]
    public void ButtonMasksAreFrozenPaintShapesRotatedWithTheArtwork()
    {
        foreach (JoyConView view in Enum.GetValues<JoyConView>())
        foreach (var layout in Enum.GetValues<Switch2FaceButtonLayout>())
        foreach (var target in JoyConArtwork.Targets(view, layout))
        {
            Assert.IsTrue(target.Highlight.IsFrozen);
            Assert.IsTrue((target.Bounds.TopLeft - target.Highlight.Bounds.TopLeft).Length < .001);
            Assert.IsTrue((target.Bounds.BottomRight - target.Highlight.Bounds.BottomRight).Length < .001);
            Assert.IsTrue(target.Highlight.FillContains(new Point(target.Bounds.X + target.Bounds.Width / 2,
                target.Bounds.Y + target.Bounds.Height / 2)), $"{view}: {target.Control}");
        }
        // Capture is square, not the circular mask previously used for every key.
        Assert.IsTrue(JoyConArtwork.ButtonShape("Capture").FillContains(new Point(164, 182)));
        Assert.IsFalse(JoyConArtwork.ButtonShape("Home").FillContains(new Point(264, 170)));
    }

    [TestMethod]
    public void RenderProductionThumbnailAndHighlightContactSheets()
    {
        RunSta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 33)), null, new Rect(0, 0, 1040, 670));
                for (int row = 0; row < Hosts.Length; row++)
                {
                    var spec = Hosts[row];
                    Label(dc, spec.Label + " · 200%", 20, 15 + row * 165);
                    for (int col = 0; col < Icons.Length; col++)
                    {
                        var host = CreateHost(spec.File, spec.Binding, Icons[col]);
                        host.Background = new SolidColorBrush(Color.FromRgb(23, 33, 44));
                        var bitmap = Render(host, 192);
                        // Keep the physical 200%-DPI size, with room for all views.
                        double scale = Math.Min(1, 180.0 / bitmap.PixelWidth);
                        dc.DrawImage(bitmap, new Rect(20 + col * 205, 42 + row * 165,
                            bitmap.PixelWidth * scale, bitmap.PixelHeight * scale));
                    }
                }
            }
            Save(visual, 1040, 670, "joycon-thumbnail-hosts.png");

            // Match the user's 125% desktop at native pixel size, without
            // resizing the thumbnail after WPF has rendered it.
            visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 33)), null, new Rect(0, 0, 1040, 670));
                for (int row = 0; row < Hosts.Length; row++)
                {
                    var spec = Hosts[row];
                    Label(dc, spec.Label + " · 125%", 20, 15 + row * 165);
                    for (int col = 0; col < Icons.Length; col++)
                    {
                        var host = CreateHost(spec.File, spec.Binding, Icons[col]);
                        host.Background = new SolidColorBrush(Color.FromRgb(23, 33, 44));
                        var bitmap = Render(host, 120);
                        dc.DrawImage(bitmap, new Rect(20 + col * 205, 42 + row * 165,
                            bitmap.PixelWidth, bitmap.PixelHeight));
                    }
                }
            }
            Save(visual, 1040, 670, "joycon-thumbnail-hosts-125.png");

            visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 25, 31)), null, new Rect(0, 0, 880, 1250));
                int row = 0;
                foreach (JoyConView view in Enum.GetValues<JoyConView>())
                {
                    Label(dc, view.ToString(), 20, row * 250 + 5);
                    Label(dc, "Button hover masks", 460, row * 250 + 5);
                    for (int col = 0; col < 2; col++)
                    {
                        dc.PushTransform(new TranslateTransform(col * 440, row * 250 + 25));
                        dc.DrawImage(JoyConArtwork.ForView(view), new Rect(0, 0, 440, 220));
                        if (col == 1)
                            foreach (var target in JoyConArtwork.Targets(view, Switch2FaceButtonLayout.Xbox))
                                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(100, 65, 180, 255)),
                                    new Pen(Brushes.DeepSkyBlue, .5), target.Highlight);
                        dc.Pop();
                    }
                    row++;
                }
            }
            Save(visual, 880, 1250, "joycon-artwork-and-masks.png");
        });
        // Associate attachments on the MSTest thread, not its STA renderer.
        foreach (string path in renderedFiles)
        {
            TestContext.WriteLine(path);
            TestContext.AddResultFile(path);
        }
    }

    private static Border CreateHost(string file, string binding, ImageSource source)
    {
        XDocument document = null;
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root != null; root = root.Parent)
        {
            string path = Path.Combine(root.FullName, "DS4Windows", "DS4Forms", file);
            if (!File.Exists(path)) continue;
            document = XDocument.Load(path);
            break;
        }
        Assert.IsNotNull(document, file);
        var productionImage = document.Descendants(Wpf + "Image").Single(e => (string)e.Attribute("Source") == binding);
        Assert.AreEqual(Wpf + "Border", productionImage.Parent.Name);
        // Use production sizing/padding/stretch, with data and theme stripped.
        // This renders only the artwork host: no app, broker, or hardware opens.
        var markup = new XElement(productionImage.Parent);
        markup.SetAttributeValue("Margin", "0");
        markup.SetAttributeValue("Visibility", "Visible");
        markup.SetAttributeValue("Background", "Transparent");
        markup.SetAttributeValue("BorderBrush", "Transparent");
        markup.Element(Wpf + "Image").Attribute("Source").Remove();
        var host = (Border)XamlReader.Parse(markup.ToString());
        ((Image)host.Child).Source = source;
        return host;
    }

    private static RenderTargetBitmap Render(Border host, int dpi)
    {
        var size = new Size(host.Width, host.Height);
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96),
            (int)Math.Ceiling(size.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(host);
        return bitmap;
    }

    private static void Label(DrawingContext dc, string text, double x, double y) => dc.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, Brushes.WhiteSmoke, 1), new Point(x, y));

    private void Save(DrawingVisual visual, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        // Optional durable output for visual review: some MSTest runners remove
        // successful-run deployment folders (and their image attachments).
        string directory = Environment.GetEnvironmentVariable("DS4W_ARTWORK_EVIDENCE_DIRECTORY")
            ?? TestContext.TestResultsDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        using (var file = File.Create(path)) encoder.Save(file);
        renderedFiles.Add(path);
    }

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Artwork rendering timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
