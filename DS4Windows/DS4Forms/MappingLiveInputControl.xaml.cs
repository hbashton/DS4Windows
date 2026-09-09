using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WinWPF.DS4Forms;

public partial class MappingLiveInputControl : UserControl
{
    private readonly MappingLiveInputSnapshot snapshot;
    private readonly MappingLiveInputProjection projection = new();
    private readonly DispatcherTimer timer;
    private bool enabled;

    public MappingLiveInputControl() : this(new MappingLiveInputSource()) { }

    internal MappingLiveInputControl(IMappingLiveInputSource source)
    {
        InitializeComponent();
        snapshot = new MappingLiveInputSnapshot(source);
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 30)
        };
        timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
        RenderStatus(MappingLiveInputStatus.Waiting);
    }

    public void UseDevice(int physicalIndex) =>
        UseDevice(physicalIndex, snapshot.ResolveDevice(physicalIndex));

    public void UseDevice(int physicalIndex, DS4Device expectedDevice)
    {
        Dispatcher.VerifyAccess();
        snapshot.UseDevice(physicalIndex, expectedDevice);
        RenderStatus(MappingLiveInputStatus.Waiting);
    }

    public void EnableControl(bool state)
    {
        Dispatcher.VerifyAccess();
        enabled = state;
        UpdateTimer();
    }

    internal bool IsPolling => timer.IsEnabled;
    internal static bool ShouldPoll(bool enabled, bool loaded, bool visible) => enabled && loaded && visible;
    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateTimer();
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        timer.Stop();
        snapshot.Reset();
        RenderStatus(MappingLiveInputStatus.Waiting);
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTimer();

    private void UpdateTimer()
    {
        if (ShouldPoll(enabled, IsLoaded, IsVisible)) timer.Start();
        else
        {
            timer.Stop();
            snapshot.Reset();
            RenderStatus(MappingLiveInputStatus.Waiting);
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (!ShouldPoll(enabled, IsLoaded, IsVisible)) { UpdateTimer(); return; }
        MappingLiveInputStatus status = snapshot.Read(Environment.TickCount64);
        if (status == MappingLiveInputStatus.Live) RenderSnapshot(snapshot.Raw, snapshot.DeviceType);
        else RenderStatus(status);
    }

    internal void RenderSnapshot(DS4State raw, InputDeviceType type)
    {
        projection.Update(raw, type);
        if (!projection.Valid) { RenderStatus(MappingLiveInputStatus.Waiting); return; }
        liveStatus.Text = "Live";
        RenderStick(leftStickPlot, leftStickDot, leftStickValue, projection.LeftX, projection.LeftY, projection.LeftPresent);
        RenderStick(rightStickPlot, rightStickDot, rightStickValue, projection.RightX, projection.RightY, projection.RightPresent);
        leftTriggerName.Text = projection.LeftTriggerName;
        rightTriggerName.Text = projection.RightTriggerName;
        leftTriggerMeter.Value = projection.LeftTrigger;
        rightTriggerMeter.Value = projection.RightTrigger;
        leftTriggerValue.Text = $"{projection.LeftTrigger:0}%";
        rightTriggerValue.Text = $"{projection.RightTrigger:0}%";
        pressedButtons.Text = projection.PressedButtons.Length == 0 ? "No buttons pressed" : projection.PressedButtons;
        pressedButtons.ToolTip = pressedButtons.Text;
    }

    private void RenderStick(Canvas plot, FrameworkElement dot, TextBlock value, int x, int y, bool present)
    {
        plot.Opacity = present ? 1 : 0.35;
        value.Text = present ? $"X {x}\nY {y}" : "Not present";
        double px = present ? Math.Clamp(x / (double)projection.AxisMaximum, 0, 1) : 0.5;
        double py = present ? Math.Clamp(y / (double)projection.AxisMaximum, 0, 1) : 0.5;
        if (projection.InvertPlotY) py = 1 - py;
        Canvas.SetLeft(dot, px * 40);
        Canvas.SetTop(dot, py * 40);
    }

    internal void RenderStatus(MappingLiveInputStatus status)
    {
        liveStatus.Text = status switch
        {
            MappingLiveInputStatus.Disconnected => "Disconnected",
            MappingLiveInputStatus.Replaced => "Controller changed",
            MappingLiveInputStatus.Stale => "No recent input",
            _ => "Waiting"
        };
        pressedButtons.Text = status switch
        {
            MappingLiveInputStatus.Replaced => "Reopen the editor to preview the new controller.",
            MappingLiveInputStatus.Disconnected => "Controller disconnected. Reconnect and reopen the editor.",
            MappingLiveInputStatus.Stale => "Waiting for fresh controller input…",
            _ => snapshot.HasBoundDevice ? "Waiting for controller input…" :
                "Open this profile from a connected controller."
        };
        pressedButtons.ToolTip = pressedButtons.Text;
        leftStickValue.Text = rightStickValue.Text = "X —\nY —";
        leftTriggerValue.Text = rightTriggerValue.Text = "—";
        leftTriggerMeter.Value = rightTriggerMeter.Value = 0;
        leftStickPlot.Opacity = rightStickPlot.Opacity = 0.35;
        Canvas.SetLeft(leftStickDot, 20); Canvas.SetTop(leftStickDot, 20);
        Canvas.SetLeft(rightStickDot, 20); Canvas.SetTop(rightStickDot, 20);
    }
}
