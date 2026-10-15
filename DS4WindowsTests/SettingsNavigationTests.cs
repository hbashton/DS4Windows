using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;

namespace DS4WindowsTests;

[TestClass]
public sealed class SettingsNavigationTests
{
    [DataTestMethod]
    [DataRow("DarkTheme", 640d)]
    [DataRow("DefaultTheme", 640d)]
    [DataRow("DarkTheme", 980d)]
    [DataRow("DefaultTheme", 980d)]
    [DataRow("DarkTheme", 1800d)]
    [DataRow("DefaultTheme", 1800d)]
    public void ActualSettingsNavigationPreservesEditsAndKeepsEverySectionReachable(string theme, double width)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Grid root = LoadSettings(theme);
                Layout(root, width);
                Assert.AreEqual(width, root.ActualWidth, 1d,
                    "Settings must fill the available width, including a maximized window.");
                var startup = (CheckBox)root.FindName("runAtStartCk");
                var steam = (TextBox)root.FindName("customSteamTxt");
                startup.IsChecked = false;
                steam.Text = @"C:\Games\Steam";
                var navigation = Visuals(root).OfType<RadioButton>()
                    .Where(control => control.GroupName == "SettingsCategory").ToArray();
                Assert.IsTrue(navigation.Length > 1);
                foreach (RadioButton category in navigation)
                {
                    category.IsChecked = true;
                    Layout(root, width);
                    var active = Visuals(root).OfType<StackPanel>()
                        .Where(panel => panel.Name.StartsWith("settings") && panel.Name.EndsWith("Page") && panel.Visibility == Visibility.Visible).ToArray();
                    Assert.AreEqual(1, active.Length, "Exactly one settings section must be displayed.");
                    Assert.AreEqual(category.Name.Replace("Nav", "Page"), active[0].Name);
                    foreach (Expander expander in Visuals(active[0]).OfType<Expander>().ToArray())
                        expander.IsExpanded = true;
                    Layout(root, width);
                    foreach (Control control in Visuals(active[0]).OfType<Control>().Where(control => control.ActualWidth > 0 && control.Visibility == Visibility.Visible))
                    {
                        Point origin = control.TranslatePoint(new Point(), active[0]);
                        Assert.IsTrue(origin.X >= -1 && origin.X + control.ActualWidth <= active[0].ActualWidth + 1,
                            $"{theme}/{width}: {control.Name} overflows {active[0].Name}.");
                    }
                    Assert.IsFalse(startup.IsChecked.Value, "Section changes must not reset checkbox edits.");
                    Assert.AreEqual(@"C:\Games\Steam", steam.Text, "Section changes must not recreate edited fields.");
                }
                var navigator = new DS4WinWPF.DS4Forms.SettingsNavigator((Grid)root.FindName("settingsPages"), navigation);
                navigator.RestoreSection("Controllers");
                Assert.IsTrue(((RadioButton)root.FindName("settingsControllersNav")).IsChecked.Value);
                Assert.IsTrue(navigator.SetSearch("startup") > 0, "Search must find settings outside the selected section.");
                Assert.AreEqual(Visibility.Visible, ((StackPanel)root.FindName("settingsGeneralPage")).Visibility);
                Assert.IsTrue(navigator.SetSearch("controller") > 1);
                Layout(root, width);
                var visibleResults = ((Grid)root.FindName("settingsPages")).Children.OfType<StackPanel>()
                    .Where(page => page.Visibility == Visibility.Visible).OrderBy(Grid.GetRow).ToArray();
                for (int index = 1; index < visibleResults.Length; index++)
                    Assert.IsTrue(visibleResults[index].TranslatePoint(new Point(), visibleResults[index - 1]).Y
                        >= visibleResults[index - 1].ActualHeight - 1, "Search sections must stack without overlapping.");
                Assert.AreEqual(0, navigator.SetSearch("zzzz-no-such-setting"));
                navigator.SetSearch("");
                Layout(root, width);
                Assert.AreEqual(Visibility.Visible, ((StackPanel)root.FindName("settingsControllersPage")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((StackPanel)root.FindName("settingsGeneralPage")).Visibility);
                Assert.IsFalse(startup.IsChecked.Value);
                Assert.AreEqual(@"C:\Games\Steam", steam.Text);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Settings rendering did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Grid LoadSettings(string theme)
    {
        // Exercise the production layout without constructing the main window,
        // starting controller discovery or executing setting-change handlers.
        System.Reflection.Assembly.Load("DotNetProjects.Wpf.Extended.Toolkit");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement settings = XDocument.Load(SourcePath()).Descendants(wpf + "TabItem")
            .Single(element => (string)element.Attribute(x + "Name") == "settingsTab");
        var markup = new XElement(settings.Element(wpf + "Grid"));
        markup.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        markup.SetAttributeValue(XNamespace.Xmlns + "xctk", "http://schemas.xceed.com/wpf/xaml/toolkit");
        foreach (XElement element in markup.DescendantsAndSelf().ToArray())
        {
            if (element.Name.LocalName == "LanguagePackControl")
            {
                element.ReplaceWith(new XElement(wpf + "TextBlock", new XAttribute("Text", "Language selector")));
                continue;
            }
            foreach (XAttribute attribute in element.Attributes().ToArray())
            {
                if (attribute.Name.LocalName is "Click" or "Checked" or "SelectionChanged" or "TextChanged" or "PreviewKeyDown" ||
                    attribute.Name.NamespaceName == "http://schemas.microsoft.com/expression/blend/2008") attribute.Remove();
                else if (attribute.Value.StartsWith("{lex:Loc ")) attribute.Value = attribute.Value[9..^1];
                else if (attribute.Value.StartsWith("{Binding") && !attribute.Value.Contains("ElementName=settings"))
                    attribute.Remove();
            }
        }
        // Give the dictionary its own base theme instead of relying on a
        // process-wide Application (which may belong to another STA test).
        XElement bridge = XDocument.Load(Path.Combine(Path.GetDirectoryName(SourcePath())!, "Themes", "BridgeShellStyles.xaml")).Root;
        bridge.AddFirst(new XElement(wpf + "ResourceDictionary.MergedDictionaries",
            new XElement(wpf + "ResourceDictionary", new XAttribute("Source", $"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml"))));
        var resources = (ResourceDictionary)XamlReader.Parse(bridge.ToString());
        var root = (Grid)XamlReader.Parse(markup.ToString());
        root.Resources.MergedDictionaries.Add(resources);
        return root;
    }

    private static void Layout(FrameworkElement root, double width)
    {
        root.Measure(new Size(width, 620));
        root.Arrange(new Rect(0, 0, width, 620));
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        root.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Visuals(child)) yield return descendant;
        }
    }

    private static string SourcePath([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "DS4Windows", "DS4Forms", "MainWindow.xaml"));
}
