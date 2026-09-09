using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace DS4WindowsTests;

[TestClass]
public sealed class ToolTipThemeTests
{
    [DataTestMethod]
    [DataRow("DarkTheme")]
    [DataRow("DefaultTheme")]
    public void ThemeAliasRendersContrastingTextInsideTheThemedSurface(string themeName)
    {
        OnSta(() =>
        {
            ResourceDictionary theme = LoadTheme(themeName);
            var named = (Style)theme["ToolTipStyle"];
            var implicitStyle = (Style)theme[typeof(ToolTip)];
            Assert.IsNotNull(named, "Both themes must supply the existing named style.");
            Assert.AreSame(named, implicitStyle.BasedOn);
            var tip = new ToolTip { Content = "Explain this control", Style = implicitStyle };
            tip.Resources.MergedDictionaries.Add(theme);
            AssertRenderedPalette(tip, theme);
        });
    }

    [DataTestMethod]
    [DataRow("DarkTheme")]
    [DataRow("DefaultTheme")]
    public void ActualSwitch2WrappingOverrideKeepsTheThemeTemplateAndTextColors(string themeName)
    {
        OnSta(() =>
        {
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XElement localStyle = XDocument.Load(SourcePath("DS4Forms/ProfileEditor.xaml"))
                .Descendants(presentation + "Style")
                .Single(element => (string)element.Attribute("TargetType") == "ToolTip");
            var dictionaryXml = new XElement(presentation + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                new XElement(presentation + "ResourceDictionary.MergedDictionaries",
                    new XElement(presentation + "ResourceDictionary",
                        new XAttribute("Source", ThemeUri(themeName)))), new XElement(localStyle));
            var resources = (ResourceDictionary)XamlReader.Parse(dictionaryXml.ToString());
            var style = (Style)resources[typeof(ToolTip)];
            Assert.AreSame(resources["ToolTipStyle"], style.BasedOn,
                "The wrapping override must extend, not replace, themed tooltip chrome.");
            var tip = new ToolTip { Content = "Use this control in game", Resources = resources, Style = style };
            Assert.AreEqual(320d, tip.MaxWidth);
            AssertRenderedPalette(tip, resources);
            Assert.AreEqual(TextWrapping.Wrap, FindVisual<TextBlock>(tip).TextWrapping);
        });
    }

    [TestMethod]
    public void ExistingTooltipTracksPaletteWhenTheActiveThemeChanges()
    {
        OnSta(() =>
        {
            ResourceDictionary dark = LoadTheme("DarkTheme");
            var tip = new ToolTip { Content = "Theme change", Style = (Style)dark["ToolTipStyle"] };
            tip.Resources.MergedDictionaries.Add(dark);
            AssertRenderedPalette(tip, dark);
            ResourceDictionary light = LoadTheme("DefaultTheme");
            tip.Resources.MergedDictionaries.Clear();
            tip.Resources.MergedDictionaries.Add(light);
            AssertRenderedPalette(tip, light);
        });
    }

    [TestMethod]
    public void ExplicitTooltipContentDoesNotOverrideTheReadableThemeWithLiteralColors()
    {
        foreach (string path in Directory.EnumerateFiles(SourcePath("DS4Forms"), "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path);
            foreach (XElement content in document.Descendants().Where(element =>
                         element.Name.LocalName == "ToolTip" || element.Name.LocalName.EndsWith(".ToolTip")))
            foreach (XAttribute color in content.DescendantsAndSelf().Attributes().Where(attribute =>
                         attribute.Name.LocalName is "Foreground" or "Background"))
            {
                Assert.IsTrue(color.Value.StartsWith("{", StringComparison.Ordinal),
                    $"Explicit tooltip colors must follow the theme: {path}: {color}");
            }
        }
    }

    private static void AssertRenderedPalette(ToolTip tip, ResourceDictionary resources)
    {
        tip.ApplyTemplate();
        tip.Measure(new Size(500, 500));
        tip.Arrange(new Rect(tip.DesiredSize));
        tip.UpdateLayout();
        var surface = tip.Template.FindName("ToolTipSurface", tip) as Border;
        Assert.IsNotNull(surface, "The theme must own the surface, not defer its colors to OS tooltip chrome.");
        Assert.AreEqual(ColorOf(resources["ToolTipBackgroundColor"]), ColorOf(surface.Background));
        Assert.AreEqual(ColorOf(resources["ToolTipForegroundColor"]), ColorOf(tip.Foreground));
        Assert.AreEqual(ColorOf(resources["BorderColor"]), ColorOf(surface.BorderBrush));
        TextBlock text = FindVisual<TextBlock>(tip);
        Assert.IsNotNull(text, "The actual tooltip text must be realized without opening a window.");
        Assert.AreEqual(ColorOf(tip.Foreground), ColorOf(text.Foreground));
        double background = Luminance(ColorOf(surface.Background));
        double foreground = Luminance(ColorOf(text.Foreground));
        Assert.IsTrue((Math.Max(background, foreground) + 0.05) / (Math.Min(background, foreground) + 0.05) >= 4.5,
            "Tooltip text must meet normal-text contrast in both active palettes.");
    }

    private static Color ColorOf(object brush) => ((SolidColorBrush)brush).Color;

    private static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            double channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static T FindVisual<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T found) return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            T child = FindVisual<T>(VisualTreeHelper.GetChild(element, index));
            if (child != null) return child;
        }
        return null;
    }

    private static ResourceDictionary LoadTheme(string name) => new() { Source = new Uri(ThemeUri(name), UriKind.Relative) };
    private static string ThemeUri(string name) => $"/DS4Windows;component/DS4Forms/Themes/{name}.xaml";
    private static string SourcePath(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "DS4Windows", relative));

    private static void OnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Initialize WPF's pack-resource support without constructing Application or Window.
                _ = new ToolTip();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Isolated tooltip resource test timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
