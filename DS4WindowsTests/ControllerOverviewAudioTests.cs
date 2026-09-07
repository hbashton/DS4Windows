using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms.ViewModels;
using DS4WinWPF.DS4Forms;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ControllerOverviewAudioTests
{
    [DataTestMethod]
    [DataRow(InputDeviceType.Switch2Pro, ConnectionType.USB, true)]
    [DataRow(InputDeviceType.Switch2Pro, ConnectionType.BT, true)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, ConnectionType.BT, false)]
    [DataRow(InputDeviceType.Switch2JoyConRight, ConnectionType.BT, false)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, ConnectionType.BT, false)]
    [DataRow(InputDeviceType.SwitchPro, ConnectionType.USB, false)]
    public void UnsupportedAudioBindingsCannotRewriteSharedProfile(
        InputDeviceType type, ConnectionType connection, bool usbHelp)
    {
        using var profile = new AudioProfileScope();
        var model = Model(new PhysicalDevice(type, connection));
        Assert.IsFalse(model.SelectedControllerSupportsAudio);
        Assert.AreEqual(usbHelp, model.SelectedControllerShowsUsbHeadsetHelp);
        int changes = 0;
        model.QuickProfileSettingChanged += (_, _) => changes++;
        var before = profile.Snapshot();
        _ = model.ControllerAudioSourceId;
        model.SpeakerOutputEnabled = false;
        model.HeadsetOnlyAudio = false;
        model.MicrophoneInputEnabled = false;
        model.SpeakerVolumePercent = 99;
        model.HeadphoneVolumePercent = 99;
        model.MicrophoneVolumePercent = 99;
        model.SpeakerCompressionIndex = 2;
        model.SpeakerBassBoostDb = 6;
        model.MicrophoneNoiseSuppressionIndex = 2;
        model.ControllerAudioSourceId = "must-not-select-another-device";
        CollectionAssert.AreEqual(before, profile.Snapshot());
        Assert.AreEqual(0, changes, "An inapplicable binding must not save/reload the active profile.");
    }

    [TestMethod]
    public void GenuineDualSenseStillEditsItsManagedSpeakerLevel()
    {
        using var profile = new AudioProfileScope();
        var model = Model(new PhysicalDevice(InputDeviceType.DualSense, ConnectionType.USB, sony: true));
        Assert.IsTrue(model.SelectedControllerSupportsAudio);
        Assert.IsFalse(model.SelectedControllerShowsUsbHeadsetHelp);
        int changes = 0;
        model.QuickProfileSettingChanged += (_, _) => changes++;
        model.SpeakerVolumePercent = 100;
        Assert.AreEqual(byte.MaxValue, Global.DualSenseSpeakerVolume[0]);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void EmptyOverviewDoesNotAdvertiseAConnectedUsbHeadset()
    {
        var model = new MainWindowsViewModel();
        Assert.IsFalse(model.SelectedControllerShowsUsbHeadsetHelp);
        Assert.IsFalse(model.SelectedControllerSupportsAudio);
    }

    [TestMethod]
    public void EveryOverviewAudioControlHasThePhysicalCapabilityGuard()
    {
        var xml = LoadOverview();
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string binding in new[] { "SpeakerOutputEnabled", "HeadsetOnlyAudio",
            "SpeakerVolumePercent", "HeadphoneVolumePercent", "MicrophoneInputEnabled",
            "MicrophoneVolumePercent", "SpeakerCompressionIndex", "SpeakerBassBoostDb",
            "MicrophoneNoiseSuppressionIndex", "ControllerAudioSourceId" })
        {
            var controls = xml.Descendants().Where(n => n.Attributes().Any(a =>
                a.Value.StartsWith("{Binding " + binding + ",", StringComparison.Ordinal))).ToArray();
            Assert.IsTrue(controls.Length > 0, binding);
            foreach (var control in controls)
                Assert.IsTrue(control.Ancestors().Any(n => ((string)n.Attribute("Visibility"))?
                    .Contains("SelectedControllerSupportsAudio") == true), binding);
        }
        var native = xml.Descendants(wpf + "Border").Single(n =>
            (string)n.Attribute(xaml + "Name") == "nativeUsbHeadsetCard");
        StringAssert.Contains((string)native.Attribute("Visibility"), "SelectedControllerShowsUsbHeadsetHelp");
        Assert.AreEqual("OpenWindowsSoundSettings_Click", (string)native.Descendants(wpf + "Button").Single().Attribute("Click"));
        Assert.IsFalse(native.Descendants(wpf + "Slider").Any());
    }

    private static MainWindowsViewModel Model(DS4Device device)
    {
        var model = new MainWindowsViewModel();
        // Avoid invoking live runtime/endpoint discovery in a pure binding test.
        var card = new CompositeDeviceModel(device, 0, string.Empty, null);
        typeof(MainWindowsViewModel).GetField("selectedController", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, card);
        return model;
    }

    // Called on the existing theme test's STA. There can be only one WPF
    // Application per process, so do not create a competing test app/window.
    internal static void ValidateRenderedOverview(ControllerOverviewControl overview,
        string resultsDirectory, List<string> renderedFiles)
    {
        var managed = (FrameworkElement)overview.FindName("managedAudioLevelsPanel");
        var speaker = (FrameworkElement)overview.FindName("managedSpeakerRouteCard");
        var native = (FrameworkElement)overview.FindName("nativeUsbHeadsetCard");
        foreach (int width in new[] { 760, 1100 })
        {
            overview.DataContext = Model(new PhysicalDevice(InputDeviceType.Switch2Pro, ConnectionType.USB));
            overview.Measure(new Size(width, 950));
            overview.Arrange(new Rect(0, 0, width, 950));
            overview.UpdateLayout();
            Assert.AreEqual(Visibility.Collapsed, managed.Visibility);
            Assert.AreEqual(Visibility.Collapsed, speaker.Visibility);
            Assert.AreEqual(Visibility.Visible, native.Visibility);
            Assert.IsTrue(native.ActualWidth > 150 && native.ActualHeight > 100);
            var image = new RenderTargetBitmap(width, 950, 96, 96, PixelFormats.Pbgra32);
            image.Render(overview);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(resultsDirectory);
            string path = Path.Combine(resultsDirectory, $"overview-usb-headset-{width}.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            renderedFiles.Add(path);
        }
        overview.DataContext = Model(new PhysicalDevice(InputDeviceType.DualSense, ConnectionType.USB, sony: true));
        overview.UpdateLayout();
        Assert.AreEqual(Visibility.Collapsed, native.Visibility);
        Assert.AreEqual(Visibility.Visible, managed.Visibility);
        Assert.AreEqual(Visibility.Visible, speaker.Visibility);
        overview.DataContext = Model(new PhysicalDevice(InputDeviceType.Switch2JoyConLeft, ConnectionType.BT));
        overview.UpdateLayout();
        Assert.AreEqual(Visibility.Collapsed, native.Visibility);
        Assert.AreEqual(Visibility.Collapsed, managed.Visibility);
        Assert.AreEqual(Visibility.Collapsed, speaker.Visibility);
        overview.DataContext = null;
    }

    private static XDocument LoadOverview()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root != null; root = root.Parent)
        {
            string file = Path.Combine(root.FullName, "DS4Windows", "DS4Forms", "ControllerOverviewControl.xaml");
            if (File.Exists(file)) return XDocument.Load(file);
        }
        throw new FileNotFoundException("Production Overview markup not found.");
    }

    private sealed class PhysicalDevice : DS4Device
    {
        internal PhysicalDevice(InputDeviceType type, ConnectionType connection, bool sony = false)
            : base("Overview audio test", type, connection)
        {
            if (sony)
            {
                hDevice = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
                typeof(HidDevice).GetField("_deviceAttributes", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(hDevice, new HidDeviceAttributes(new NativeMethods.HIDD_ATTRIBUTES
                    { VendorID = 0x054C, ProductID = 0x0CE6 }));
            }
        }
        public override bool IsAlive() => true;
    }

    private sealed class AudioProfileScope : IDisposable
    {
        private readonly object[] previous;
        internal AudioProfileScope()
        {
            previous = Snapshot();
            Global.DualSenseEnableSpeakerOutput[0] = true;
            Global.DualSenseHeadsetOnlyAudio[0] = true;
            Global.DualSenseEnableMicrophonePassthrough[0] = true;
            Global.DualSenseSpeakerVolume[0] = 50;
            Global.DualSenseHeadphoneVolume[0] = 50;
            Global.DualSenseMicrophoneVolume[0] = 50;
            Global.DualSenseSpeakerCompression[0] = 0;
            Global.DualSenseSpeakerBassBoost[0] = 0;
            Global.DualSenseMicrophoneNoiseSuppression[0] = 0;
            Global.DualSenseAudioCaptureEndpointId[0] = "saved-source";
        }
        internal object[] Snapshot() => new object[]
        {
            Global.DualSenseEnableSpeakerOutput[0], Global.DualSenseHeadsetOnlyAudio[0],
            Global.DualSenseEnableMicrophonePassthrough[0], Global.DualSenseSpeakerVolume[0],
            Global.DualSenseHeadphoneVolume[0], Global.DualSenseMicrophoneVolume[0],
            Global.DualSenseSpeakerCompression[0], Global.DualSenseSpeakerBassBoost[0],
            Global.DualSenseMicrophoneNoiseSuppression[0], Global.DualSenseAudioCaptureEndpointId[0],
        };
        public void Dispose()
        {
            Global.DualSenseEnableSpeakerOutput[0] = (bool)previous[0];
            Global.DualSenseHeadsetOnlyAudio[0] = (bool)previous[1];
            Global.DualSenseEnableMicrophonePassthrough[0] = (bool)previous[2];
            Global.DualSenseSpeakerVolume[0] = (byte)previous[3];
            Global.DualSenseHeadphoneVolume[0] = (byte)previous[4];
            Global.DualSenseMicrophoneVolume[0] = (byte)previous[5];
            Global.DualSenseSpeakerCompression[0] = (byte)previous[6];
            Global.DualSenseSpeakerBassBoost[0] = (byte)previous[7];
            Global.DualSenseMicrophoneNoiseSuppression[0] = (byte)previous[8];
            Global.DualSenseAudioCaptureEndpointId[0] = (string)previous[9];
        }
    }
}
