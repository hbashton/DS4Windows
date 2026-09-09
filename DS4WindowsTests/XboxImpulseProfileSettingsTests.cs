using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxImpulseProfileSettingsTests
{
    public TestContext TestContext { get; set; }

    private static readonly XNamespace Wpf =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void SynchronizedMasterCheckboxesLiveUnderAdvancedAndNintendoFeedback()
    {
        var editor = LoadEditor();
        var checkboxes = editor.Descendants(Wpf + "CheckBox").Where(node =>
            (string)node.Attribute("IsChecked") == "{Binding MapXboxImpulseTriggers}").ToArray();
        Assert.AreEqual(2, checkboxes.Length);
        var checkbox = checkboxes.Single(node =>
            (string)node.Ancestors(Wpf + "TabItem").First().Attribute("Header") == "Advanced");
        Assert.AreEqual("Translate Xbox impulse-trigger vibration", (string)checkbox.Attribute("Content"));
        Assert.AreEqual("{lex:Loc Rumble}", (string)checkbox.Ancestors(Wpf + "GroupBox").First().Attribute("Header"));
        var tab = checkbox.Ancestors(Wpf + "TabItem").First();
        Assert.AreEqual("Advanced", (string)tab.Attribute("Header"));
        foreach (var element in checkbox.AncestorsAndSelf().TakeWhile(node => node != tab).Append(tab))
        {
            Assert.IsNull(element.Attribute("Visibility"), "The impulse option must be visible for every controller.");
            Assert.IsNull(element.Attribute("IsEnabled"), "The impulse option must not depend on Trigger Lab or hardware capabilities.");
        }
        Assert.IsFalse(editor.Descendants().Attributes().Any(attribute =>
            attribute.Value.Contains("{Binding Switch2MapXboxImpulseTriggersToHdRumble", StringComparison.Ordinal)));
        var tuning = editor.Descendants(Wpf + "StackPanel").Single(node =>
            (string)node.Attribute(Xaml + "Name") == "XboxImpulseRumbleTuningPanel");
        Assert.AreEqual("{Binding MapXboxImpulseTriggers}", (string)tuning.Attribute("IsEnabled"));
        var nintendoCheckbox = checkboxes.Single(node => node != checkbox);
        Assert.AreEqual("Include Xbox trigger rumble", (string)nintendoCheckbox.Attribute("Content"));
        Assert.AreEqual("switch2FeedbackCard", (string)nintendoCheckbox.Ancestors(Wpf + "Expander").First().Attribute(Xaml + "Name"));

        var routing = editor.Descendants(Wpf + "CheckBox").Single(node =>
            (string)node.Attribute("IsChecked") == "{Binding XboxImpulseToAdaptiveTriggers}");
        Assert.AreEqual("Send impulse vibration to adaptive triggers", (string)routing.Attribute("Content"));
        Assert.AreEqual("{Binding SupportsAdaptiveTriggers}", (string)routing.Attribute("IsEnabled"));
        Assert.AreEqual("{Binding MapXboxImpulseTriggers}", (string)routing.Parent.Attribute("IsEnabled"));
        Assert.AreSame(tab, routing.Ancestors(Wpf + "TabItem").First());
        Assert.IsFalse(routing.AncestorsAndSelf().TakeWhile(node => node != tab).Attributes("Visibility").Any());
    }

    [DataTestMethod]
    [DataRow(null, 0, true)]
    [DataRow(InputDeviceType.DualSense, 0x0CE6, true)]
    [DataRow(InputDeviceType.DualSense, 0x0DF2, true)]
    [DataRow(InputDeviceType.DS4, 0x09CC, false)]
    [DataRow(InputDeviceType.Switch2Pro, 0x2069, false)]
    [DataRow(InputDeviceType.JoyConGrip, 0x200E, false)]
    [DoNotParallelize]
    public void ProductionWpfControlsSynchronizeAndKeepRoutingOnUnsupportedHardware(
        InputDeviceType? type, int productId, bool supportsRouting)
    {
        RunSta(() =>
        {
            bool previousImpulse = Global.MapXboxImpulseTriggers[0];
            bool previousRoute = Global.XboxImpulseToAdaptiveTriggers[0];
            var previousHub = DS4WinWPF.App.rootHub;
            var profile = (ProfileSettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ProfileSettingsViewModel));
            typeof(ProfileSettingsViewModel).GetField("controllerUiCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(profile, ControllerUiCapabilities.For(type, ConnectionType.USB, 0x054C, productId));
            var editor = LoadEditor();
            var masters = editor.Descendants(Wpf + "CheckBox").Where(node =>
                (string)node.Attribute("IsChecked") == "{Binding MapXboxImpulseTriggers}")
                .Select(node => (CheckBox)XamlReader.Parse(node.ToString())).ToArray();
            var routingMarkup = editor.Descendants(Wpf + "CheckBox").Single(node =>
                (string)node.Attribute("IsChecked") == "{Binding XboxImpulseToAdaptiveTriggers}").Parent;
            var routingPanel = (StackPanel)ParseThemedControl(routingMarkup);
            var routing = (CheckBox)routingPanel.Children[0];
            try
            {
                DS4WinWPF.App.rootHub = null;
                Global.MapXboxImpulseTriggers[0] = true;
                Global.XboxImpulseToAdaptiveTriggers[0] = true;
                foreach (var master in masters) master.DataContext = profile;
                routingPanel.DataContext = profile;
                FlushBindings();
                Assert.IsTrue(masters.All(master => master.IsChecked == true));
                Assert.AreEqual(supportsRouting, routing.IsEnabled);
                Assert.AreEqual(supportsRouting ? 1d : .5d, routing.Opacity);

                masters[0].SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                FlushBindings();
                Assert.IsFalse(profile.MapXboxImpulseTriggers);
                Assert.IsTrue(masters.All(master => master.IsChecked == false));
                Assert.IsFalse(routing.IsEnabled);
                Assert.AreEqual(.5d, routing.Opacity);
                Assert.AreEqual(true, routing.IsChecked);

                masters[1].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                FlushBindings();
                Assert.IsTrue(profile.MapXboxImpulseTriggers);
                Assert.IsTrue(masters.All(master => master.IsChecked == true));
                Assert.AreEqual(supportsRouting, routing.IsEnabled);
                Assert.AreEqual(supportsRouting ? 1d : .5d, routing.Opacity);
                Assert.IsTrue(profile.XboxImpulseToAdaptiveTriggers, "Availability changes must preserve the saved route.");

                profile.XboxImpulseToAdaptiveTriggers = false;
                FlushBindings();
                Assert.AreEqual(false, routing.IsChecked);
                Assert.IsTrue(masters.All(master => master.IsChecked == true), "Routing is independent of the shared master setting.");
            }
            finally
            {
                foreach (var master in masters) BindingOperations.ClearAllBindings(master);
                BindingOperations.ClearAllBindings(routing);
                BindingOperations.ClearAllBindings(routingPanel);
                Global.MapXboxImpulseTriggers[0] = previousImpulse;
                Global.XboxImpulseToAdaptiveTriggers[0] = previousRoute;
                DS4WinWPF.App.rootHub = previousHub;
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void GlobalAndLegacySettersNotifyTuningWithoutChangingTriggerLab()
    {
        bool previousImpulse = Global.MapXboxImpulseTriggers[0];
        bool previousRoute = Global.XboxImpulseToAdaptiveTriggers[0];
        var previousHub = DS4WinWPF.App.rootHub;
        var previousTriggerLab = Global.store.triggerLabSettings[0];
        var triggerLab = new TriggerLabProfileSettings
        {
            Enabled = false,
            LeftGameRumbleVibration = true,
            RightGameRumbleVibration = false,
        };
        var profile = (ProfileSettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ProfileSettingsViewModel));
        var property = TypeDescriptor.GetProperties(profile)[nameof(ProfileSettingsViewModel.MapXboxImpulseTriggers)];
        int notifications = 0;
        EventHandler onChanged = (_, _) => notifications++;
        property.AddValueChanged(profile, onChanged);
        try
        {
            DS4WinWPF.App.rootHub = null;
            Global.store.triggerLabSettings[0] = triggerLab;
            Assert.AreSame(Global.Switch2MapXboxImpulseTriggersToHdRumble, Global.MapXboxImpulseTriggers);
            Global.MapXboxImpulseTriggers[0] = false;
            profile.XboxImpulseToAdaptiveTriggers = false;

            profile.MapXboxImpulseTriggers = true;
            Assert.IsTrue(profile.Switch2MapXboxImpulseTriggersToHdRumble);
            Assert.IsFalse(profile.XboxImpulseToAdaptiveTriggers);
            Assert.AreEqual(1, notifications);

            profile.Switch2MapXboxImpulseTriggersToHdRumble = false;
            profile.XboxImpulseToAdaptiveTriggers = true;
            Assert.IsFalse(profile.MapXboxImpulseTriggers);
            Assert.AreEqual(2, notifications);
            Assert.AreSame(triggerLab, Global.store.triggerLabSettings[0]);
            Assert.IsFalse(triggerLab.Enabled);
            Assert.IsTrue(triggerLab.LeftGameRumbleVibration);
            Assert.IsFalse(triggerLab.RightGameRumbleVibration);
        }
        finally
        {
            property.RemoveValueChanged(profile, onChanged);
            Global.MapXboxImpulseTriggers[0] = previousImpulse;
            Global.XboxImpulseToAdaptiveTriggers[0] = previousRoute;
            Global.store.triggerLabSettings[0] = previousTriggerLab;
            DS4WinWPF.App.rootHub = previousHub;
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExistingProfileSettingRoundTripsThroughTheOriginalXmlField(bool enabled)
    {
        var serializer = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
        using var reader = new StringReader("<DS4Windows config_version=\"5\"><Switch2MapXboxImpulseTriggersToHdRumble>" +
            enabled.ToString().ToLowerInvariant() + "</Switch2MapXboxImpulseTriggersToHdRumble></DS4Windows>");
        var input = (ProfileDTO)serializer.Deserialize(reader);
        input.DeviceIndex = 0;
        var store = new BackingStore();
        input.MapTo(store);
        Assert.AreEqual(enabled, store.switch2MapXboxImpulseTriggersToHdRumble[0]);
        Assert.IsTrue(store.xboxImpulseToAdaptiveTriggers[0], "Legacy profiles keep the adaptive-trigger route even when their master is off.");

        var output = new ProfileDTO { DeviceIndex = 0, SerializeAppAttrs = false };
        output.MapFrom(store);
        using var writer = new StringWriter();
        serializer.Serialize(writer, output);
        var xml = XDocument.Parse(writer.ToString());
        Assert.AreEqual(enabled.ToString().ToLowerInvariant(),
            xml.Root.Element("Switch2MapXboxImpulseTriggersToHdRumble")?.Value);
        Assert.IsNull(xml.Root.Element("MapXboxImpulseTriggers"), "Do not introduce a second persisted master setting.");
        using var savedReader = new StringReader(writer.ToString());
        Assert.AreEqual(enabled, ((ProfileDTO)serializer.Deserialize(savedReader)).Switch2MapXboxImpulseTriggersToHdRumble);
    }

    [TestMethod]
    public void ProfilesWithoutTheOptionKeepTheEnabledDefault()
    {
        var serializer = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
        using var reader = new StringReader("<DS4Windows config_version=\"5\" />");
        var profile = (ProfileDTO)serializer.Deserialize(reader);
        profile.DeviceIndex = 0;
        var store = new BackingStore();
        profile.MapTo(store);
        Assert.IsTrue(store.switch2MapXboxImpulseTriggersToHdRumble[0]);
        Assert.IsTrue(store.xboxImpulseToAdaptiveTriggers[0]);
    }

    [TestMethod]
    [DoNotParallelize]
    public void ResetProfileRestoresBothImpulseDefaultsOnlyForTheSelectedSlot()
    {
        var store = new BackingStore();
        int slot = Global.TEST_PROFILE_INDEX;
        Assert.IsTrue(store.xboxImpulseToAdaptiveTriggers[slot]);
        store.switch2MapXboxImpulseTriggersToHdRumble[slot] = false;
        store.xboxImpulseToAdaptiveTriggers[slot] = false;
        store.switch2MapXboxImpulseTriggersToHdRumble[0] = false;
        store.xboxImpulseToAdaptiveTriggers[0] = false;
        typeof(BackingStore).GetMethod("ResetProfile", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(store, new object[] { slot });
        Assert.IsTrue(store.switch2MapXboxImpulseTriggersToHdRumble[slot]);
        Assert.IsTrue(store.xboxImpulseToAdaptiveTriggers[slot]);
        Assert.IsFalse(store.switch2MapXboxImpulseTriggersToHdRumble[0]);
        Assert.IsFalse(store.xboxImpulseToAdaptiveTriggers[0]);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void AdaptiveRoutingRoundTripsIndependentlyOfTheMaster(bool enabled, bool adaptive)
    {
        var store = new BackingStore();
        var input = new ProfileDTO
        {
            DeviceIndex = 0,
            Switch2MapXboxImpulseTriggersToHdRumble = enabled,
            XboxImpulseToAdaptiveTriggers = adaptive,
        };
        input.MapTo(store);
        Assert.AreEqual(adaptive, store.xboxImpulseToAdaptiveTriggers[0]);
        var output = new ProfileDTO { DeviceIndex = 0, SerializeAppAttrs = false };
        output.MapFrom(store);
        var serializer = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
        using var writer = new StringWriter();
        serializer.Serialize(writer, output);
        Assert.AreEqual(adaptive.ToString().ToLowerInvariant(),
            XDocument.Parse(writer.ToString()).Root.Element("XboxImpulseToAdaptiveTriggers")?.Value);
        using var reader = new StringReader(writer.ToString());
        var restored = (ProfileDTO)serializer.Deserialize(reader);
        Assert.AreEqual(enabled, restored.Switch2MapXboxImpulseTriggersToHdRumble);
        Assert.AreEqual(adaptive, restored.XboxImpulseToAdaptiveTriggers);
        Assert.IsFalse(restored.TriggerLabSettings.Enabled);
    }

    [DataTestMethod]
    [DataRow("DarkTheme", 360, 120)]
    [DataRow("DarkTheme", 640, 144)]
    [DataRow("DefaultTheme", 360, 120)]
    [DataRow("DefaultTheme", 640, 144)]
    public void ProductionImpulseControlsRenderWithoutClipping(string theme, int width, int dpi)
    {
        string renderedPath = null;
        RunSta(() =>
        {
            var editor = LoadEditor();
            var masterMarkup = editor.Descendants(Wpf + "CheckBox").Single(node =>
                (string)node.Attribute(Xaml + "Name") == "XboxImpulseTriggersCheckBox");
            var routingMarkup = editor.Descendants(Wpf + "CheckBox").Single(node =>
                (string)node.Attribute(Xaml + "Name") == "XboxImpulseToAdaptiveTriggersCheckBox").Parent;
            var panel = new StackPanel();
            var host = new Border { Child = panel, Padding = new Thickness(16) };
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml", UriKind.Relative),
            });
            host.SetResourceReference(Border.BackgroundProperty, "BackgroundColor");
            foreach (bool supportsAdaptive in new[] { true, false })
            {
                var heading = new TextBlock
                {
                    Text = supportsAdaptive ? "Advanced · DualSense / Edge" : "Advanced · other controllers",
                    Margin = new Thickness(0, 12, 0, 8),
                    FontWeight = FontWeights.SemiBold,
                };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundColor");
                panel.Children.Add(heading);
                var section = new StackPanel
                {
                    DataContext = new ImpulseRenderSettings
                    {
                        MapXboxImpulseTriggers = true,
                        XboxImpulseToAdaptiveTriggers = true,
                        SupportsAdaptiveTriggers = supportsAdaptive,
                    },
                };
                section.Children.Add((CheckBox)XamlReader.Parse(masterMarkup.ToString()));
                section.Children.Add(ParseThemedControl(routingMarkup, theme));
                panel.Children.Add(section);
            }
            host.Measure(new Size(width, double.PositiveInfinity));
            int height = (int)Math.Ceiling(host.DesiredSize.Height);
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            Rect bounds = new(0, 0, width, height);
            foreach (var child in Descendants(host).OfType<FrameworkElement>().Where(node =>
                         node is CheckBox or TextBlock or ContentPresenter))
            {
                Assert.IsTrue(child.ActualWidth > 0 && child.ActualHeight > 0);
                Rect childBounds = child.TransformToAncestor(host).TransformBounds(new Rect(child.RenderSize));
                Assert.IsTrue(bounds.Contains(childBounds), $"{child.GetType().Name} clips at {width}px: {childBounds}");
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96d),
                (int)Math.Ceiling(height * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(host);
            Directory.CreateDirectory(TestContext.TestResultsDirectory);
            renderedPath = Path.Combine(TestContext.TestResultsDirectory, $"xbox-impulse-{theme}-{width}-{dpi}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(renderedPath)) encoder.Save(stream);
        });
        TestContext.AddResultFile(renderedPath);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static FrameworkElement ParseThemedControl(XElement markup, string theme = "DefaultTheme")
    {
        // Resolve the production control's BasedOn style in the same theme
        // dictionary as its real host, before extracting the isolated control.
        var wrapperMarkup = new XElement(Wpf + "StackPanel",
            new XElement(Wpf + "StackPanel.Resources",
                new XElement(Wpf + "ResourceDictionary", new XAttribute("Source",
                    $"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml"))),
            new XElement(markup));
        var wrapper = (StackPanel)XamlReader.Parse(wrapperMarkup.ToString());
        var control = (FrameworkElement)wrapper.Children[0];
        wrapper.Children.Clear();
        return control;
    }

    private sealed class ImpulseRenderSettings
    {
        public bool MapXboxImpulseTriggers { get; set; }
        public bool XboxImpulseToAdaptiveTriggers { get; set; }
        public bool SupportsAdaptiveTriggers { get; set; }
    }

    private static void FlushBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static void RunSta(Action action)
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "WPF impulse binding test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static XDocument LoadEditor()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root != null; root = root.Parent)
        {
            string path = Path.Combine(root.FullName, "DS4Windows", "DS4Forms", "ProfileEditor.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
        }
        throw new FileNotFoundException("Production ProfileEditor.xaml not found.");
    }
}
