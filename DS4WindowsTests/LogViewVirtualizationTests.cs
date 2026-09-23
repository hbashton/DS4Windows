using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using DS4WinWPF;

namespace DS4WindowsTests;

[TestClass]
public sealed class LogViewVirtualizationTests
{
    public TestContext TestContext { get; set; }

    [DataTestMethod]
    [DataRow("DarkTheme", 1000)]
    [DataRow("DarkTheme", 2000)]
    [DataRow("DefaultTheme", 1000)]
    [DataRow("DefaultTheme", 2000)]
    public void RealLogListKeepsContainersBoundedWithAccessibleGridRows(string themeName, int itemCount)
    {
        OnSta(() =>
        {
            ListView list = CreateProductionLogList(themeName);
            LogItem[] items = Enumerable.Range(0, itemCount).Select(index => new LogItem
            {
                Datetime = new DateTime(2026, 9, 22, 12, 0, 0).AddSeconds(index),
                Message = $"Bounded log fixture row {index:D4}",
                Warning = index % 17 == 0
            }).ToArray();
            list.ItemsSource = items;
            try
            {
                Layout(list);
                List<ListViewItem> initial = RealizedItems(list);
                WriteLayoutEvidence(list, themeName, itemCount, "initial", initial.Count);
                // Fail before traversing accessibility peers if the template has
                // already materialized the entire collection. The RED fixture
                // never repeats the live 31,000-row automation-tree workload.
                Assert.IsTrue(initial.Count > 0 && initial.Count < 100,
                    $"{themeName}: {initial.Count}/{itemCount} containers realized in a 1200x650 viewport.");
                ExerciseAccessibleRows(list, initial);
                WriteLayoutEvidence(list, themeName, itemCount, "initial-after-accessibility", RealizedItems(list).Count);
                Assert.IsTrue(RealizedItems(list).Count < 100,
                    "Accessibility enumeration must not materialize offscreen log containers.");

                // Bounded disjoint jumps cover the historical auto-scroll work
                // without iterating all rows or recursively walking 31k peers.
                foreach (int index in Enumerable.Range(1, 8).Select(step => step * (itemCount - 1) / 8))
                {
                    ScrollOffscreenList(list, items[index], index);
                    int realized = RealizedItems(list).Count;
                    TestContext.WriteLine($"{themeName}/{itemCount}/scroll-{index}: realized={realized}");
                    Assert.IsTrue(realized > 0 && realized < 200,
                        $"Repeated log scrolling must not accumulate containers: {realized}/{itemCount}.");
                }
                ScrollOffscreenList(list, items[^1], itemCount - 1);
                List<ListViewItem> tail = RealizedItems(list);
                WriteLayoutEvidence(list, themeName, itemCount, "tail", tail.Count);
                Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromIndex(itemCount - 1),
                    "The real log's tail must be reachable, not hidden by the virtualization fix.");
                Assert.IsTrue(tail.Count > 0 && tail.Count < 200,
                    $"{themeName}: scrolling retained {tail.Count}/{itemCount} visual containers.");
                ExerciseAccessibleRows(list, tail);
                WriteLayoutEvidence(list, themeName, itemCount, "tail-after-accessibility", RealizedItems(list).Count);
                Assert.IsTrue(RealizedItems(list).Count < 200,
                    "Tail accessibility enumeration must retain a bounded visual tree.");
                AssertVirtualizing(list);
            }
            finally
            {
                list.ItemsSource = null;
                list.Resources.MergedDictionaries.Clear();
            }
        });
    }

    private static ListView CreateProductionLogList(string themeName)
    {
        // Do not instantiate MainWindow/Application: those own app, controller,
        // notification and process lifetimes. Preserve the real list markup,
        // cell templates and theme; only remove shell event/data hookup.
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(SourcePath("DS4Forms/MainWindow.xaml"))
            .Descendants(wpf + "ListView")
            .Single(element => (string)element.Attribute(x + "Name") == "logListView");
        var markup = new XElement(source);
        markup.Attribute(x + "Name")?.Remove();
        markup.Attribute("MouseDoubleClick")?.Remove();
        markup.Attribute("ItemsSource")?.Remove();
        markup.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        var list = (ListView)XamlReader.Parse(markup.ToString());
        list.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/DS4Windows;component/DS4Forms/Themes/{themeName}.xaml", UriKind.Relative)
        });
        return list;
    }

    private static void Layout(ListView list)
    {
        var viewport = new Size(1200, 650);
        list.ApplyTemplate();
        list.Measure(viewport);
        list.Arrange(new Rect(viewport));
        list.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        list.UpdateLayout();
    }

    private static void AssertVirtualizing(ListView list)
    {
        Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(list), "The log must keep virtualization enabled.");
        Assert.AreEqual(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list),
            "The long-lived log must recycle containers instead of accumulating allocation pressure.");
        Assert.IsTrue(Visuals(list).OfType<VirtualizingStackPanel>().Any(),
            "Verify the effective items host, not just an attached virtualization flag.");
        Assert.IsTrue(ScrollViewer.GetCanContentScroll(list), "The list must delegate scrolling to its virtualizing items host.");
    }

    private static void ScrollOffscreenList(ListView list, LogItem item, int index)
    {
        list.ScrollIntoView(item);
        // An offscreen control has no PresentationSource/IsVisible activation,
        // so BringIntoView alone does not route a viewport change. Drive the
        // real items host's ScrollViewer operation as well, without opening an
        // HWND or installing a fake generator. Tail realization remains a hard
        // assertion; this does not claim to test visible-window focus routing.
        VirtualizingStackPanel host = Visuals(list).OfType<VirtualizingStackPanel>().Single(panel => panel.IsItemsHost);
        Assert.IsNotNull(host.ScrollOwner, "The real items host must retain its ScrollViewer owner.");
        host.ScrollOwner.ScrollToVerticalOffset(index);
        list.InvalidateMeasure();
        Layout(list);
    }

    private static void ExerciseAccessibleRows(ListView list, List<ListViewItem> realized)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(list);
        Assert.IsNotNull(peer, "The fix must preserve UI Automation support.");
        Assert.AreEqual(AutomationControlType.DataGrid, peer.GetAutomationControlType(),
            "The actual GridView must retain its standard accessible table role.");
        // No external UIA listener is attached to this detached control. Use
        // WPF's public refresh before querying a newly recycled viewport.
        peer.ResetChildrenCache();
        List<AutomationPeer> children = peer.GetChildren();
        Assert.IsNotNull(children, "GridView accessibility children must remain exposed.");
        Assert.IsTrue(children.Count > 0);

        // Exercise actual GridViewItemAutomationPeer.GetChildrenCore for a
        // bounded set of realized rows, as on accessibility/tab activation.
        int exercised = 0;
        foreach (ListViewItem container in realized.Take(3))
        {
            var item = (LogItem)container.Content;
            AutomationPeer row = children.FirstOrDefault(child =>
                child is ItemAutomationPeer itemPeer && ReferenceEquals(itemPeer.Item, item));
            Assert.IsNotNull(row, "A realized log row must have a data-item automation peer.");
            Assert.AreEqual("GridViewItemAutomationPeer", row.GetType().Name);
            List<AutomationPeer> cells = row.GetChildren();
            Assert.IsNotNull(cells, "Realized grid rows must expose their cells to assistive technology.");
            Assert.AreEqual(2, cells.Count, "Time and Data columns must both remain accessible.");
            Assert.IsTrue(cells.Any(cell => AccessibleCellContains(cell, item.Message, remainingDepth: 4)),
                "The log message must remain readable through its cell peer.");
            exercised++;
        }
        Assert.IsTrue(exercised > 0);
    }

    private static bool AccessibleCellContains(AutomationPeer peer, string text, int remainingDepth)
    {
        if (peer.GetName().Contains(text, StringComparison.Ordinal)) return true;
        if (remainingDepth == 0) return false;
        // A templated GridView cell exposes its TextBlock below the unnamed
        // ContentPresenter peer. Keep this walk within only the two cells of
        // three realized rows, never recursively enumerate the whole list.
        List<AutomationPeer> children = peer.GetChildren();
        return children != null && children.Take(8).Any(child =>
            AccessibleCellContains(child, text, remainingDepth - 1));
    }

    private void WriteLayoutEvidence(ListView list, string theme, int count, string stage, int realized)
    {
        string viewers = string.Join("; ", Visuals(list).OfType<ScrollViewer>().Select(viewer =>
            $"{viewer.Name}: contentScroll={viewer.CanContentScroll}, viewport={viewer.ViewportHeight}, extent={viewer.ExtentHeight}, offset={viewer.VerticalOffset}"));
        TestContext.WriteLine($"{theme}/{count}/{stage}: realized={realized}, mode={VirtualizingPanel.GetVirtualizationMode(list)}, " +
            $"virtualizing={VirtualizingPanel.GetIsVirtualizing(list)}, listContentScroll={ScrollViewer.GetCanContentScroll(list)}; {viewers}");
    }

    private static List<ListViewItem> RealizedItems(DependencyObject root) => Visuals(root).OfType<ListViewItem>().ToList();

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        foreach (DependencyObject child in Visuals(VisualTreeHelper.GetChild(root, index)))
            yield return child;
    }

    private static string SourcePath(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "DS4Windows", relative));

    private static void OnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Bounded isolated log-list layout timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
