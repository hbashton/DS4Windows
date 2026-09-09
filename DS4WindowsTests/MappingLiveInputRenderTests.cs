using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

[TestClass]
public sealed class MappingLiveInputRenderTests
{
    public TestContext TestContext { get; set; }

    [DataTestMethod]
    [DataRow((int)MappingLiveInputStatus.Disconnected)]
    [DataRow((int)MappingLiveInputStatus.Replaced)]
    [DataRow((int)MappingLiveInputStatus.Stale)]
    [DataRow((int)MappingLiveInputStatus.Waiting)]
    public void NonLiveStatusClearsPressedButtonsAxesAndMeters(int status)
    {
        OnSta(() =>
        {
            var control = new MappingLiveInputControl();
            control.RenderSnapshot(new DS4State
            {
                LX = 255, LY = 0, RX = 0, RY = 255, L2 = 255, R2 = 200, Cross = true,
            }, InputDeviceType.DualSense);
            Assert.AreEqual(100d, ((ProgressBar)control.FindName("leftTriggerMeter")).Value);
            control.RenderStatus((MappingLiveInputStatus)status);
            Assert.AreEqual(0d, ((ProgressBar)control.FindName("leftTriggerMeter")).Value);
            Assert.AreEqual(0d, ((ProgressBar)control.FindName("rightTriggerMeter")).Value);
            Assert.AreEqual("X —\nY —", ((TextBlock)control.FindName("leftStickValue")).Text);
            Assert.AreEqual("X —\nY —", ((TextBlock)control.FindName("rightStickValue")).Text);
            Assert.IsFalse(((TextBlock)control.FindName("pressedButtons")).Text.Contains("Cross"));
            control.EnableControl(true);
            Assert.IsFalse(control.IsPolling, "An unloaded preview must not start observation.");
            control.EnableControl(false);
            Assert.IsFalse(control.IsPolling);
        });
    }

    [DataTestMethod]
    [DataRow("DarkTheme", 400, 96)]
    [DataRow("DarkTheme", 800, 96)]
    [DataRow("DarkTheme", 800, 144)]
    [DataRow("DefaultTheme", 400, 96)]
    [DataRow("DefaultTheme", 800, 96)]
    [DataRow("DefaultTheme", 800, 144)]
    public void PreviewRendersInsideItsBoundsAtNarrowAndWideSizes(string theme, int width, int dpi)
    {
        string renderedPath = null;
        OnSta(() =>
        {
            var control = new MappingLiveInputControl();
            control.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml", UriKind.Relative),
            });
            control.RenderSnapshot(new DS4State
            {
                LX = 186, LY = 65, RX = 72, RY = 168, L2 = 187, R2 = 255,
                Cross = true, R1 = true, DpadUp = true,
            }, InputDeviceType.DualSense);
            control.Measure(new Size(width, double.PositiveInfinity));
            int height = (int)Math.Ceiling(control.DesiredSize.Height);
            Assert.IsTrue(height > 70 && height < 280, $"Compact card height: {height}");
            control.Arrange(new Rect(0, 0, width, height));
            control.UpdateLayout();
            Rect bounds = new(0, 0, width, height);
            foreach (string name in new[] { "liveStatus", "leftStickPlot", "rightStickPlot",
                "leftStickValue", "rightStickValue", "leftTriggerMeter", "rightTriggerMeter", "pressedButtons" })
            {
                var child = (FrameworkElement)control.FindName(name);
                Assert.IsNotNull(child, name);
                Assert.IsTrue(child.ActualWidth > 0 && child.ActualHeight > 0, name);
                Rect childBounds = child.TransformToAncestor(control).TransformBounds(new Rect(child.RenderSize));
                Assert.IsTrue(bounds.Contains(childBounds), $"{name} clips at {width}px: {childBounds}");
            }
            var pressed = (TextBlock)control.FindName("pressedButtons");
            Assert.IsFalse(string.IsNullOrWhiteSpace(pressed.Text));
            Assert.IsTrue(pressed.Foreground is SolidColorBrush);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96d),
                (int)Math.Ceiling(height * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(control);
            Directory.CreateDirectory(TestContext.TestResultsDirectory);
            renderedPath = Path.Combine(TestContext.TestResultsDirectory, $"live-input-{theme}-{width}-{dpi}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(renderedPath)) encoder.Save(stream);
            control.EnableControl(false);
        });
        TestContext.AddResultFile(renderedPath);
    }

    private static void OnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Preview render timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
