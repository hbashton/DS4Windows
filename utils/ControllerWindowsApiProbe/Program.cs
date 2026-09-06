using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Gaming.Input;

// A Windows API consumer, not another mapper or physical controller writer.
// Only the reviewed F00D:BEED synthetic lab persona may receive feedback.
internal static class Program
{
    [STAThread]
    public static void Main() => new Application().Run(new ProbeWindow());
}

internal sealed class ProbeWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock reading = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> pulseButtons = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly StreamWriter evidence;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private Gamepad? selected;
    private Gamepad? pulseTarget;
    private bool closing;
    private bool pulseActive;
    private long reads;
    private long changes;
    private GamepadReading? previous;

    internal ProbeWindow()
    {
        Title = "Portable Xbox One Windows API probe";
        Width = 620;
        Height = 410;
        var results = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(results);
        var path = Path.Combine(results, $"wgi-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        evidence = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Xbox One / Series: Windows.Gaming.Input", FontSize = 20 });
        panel.Children.Add(new TextBlock {
            Text = "Targets only the F00D:BEED lab virtual pad. Each button requests one 200 ms pulse at 20%, followed by neutral. This does not measure latency or prove physical delivery.",
            Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(status);
        reading.Margin = new Thickness(0, 12, 0, 12);
        panel.Children.Add(reading);
        var buttons = new WrapPanel();
        AddPulse(buttons, "Left body", new GamepadVibration { LeftMotor = 0.2 });
        AddPulse(buttons, "Right body", new GamepadVibration { RightMotor = 0.2 });
        AddPulse(buttons, "Left impulse", new GamepadVibration { LeftTrigger = 0.2 });
        AddPulse(buttons, "Right impulse", new GamepadVibration { RightTrigger = 0.2 });
        panel.Children.Add(buttons);
        feedback.Margin = new Thickness(0, 12, 0, 12);
        panel.Children.Add(feedback);
        panel.Children.Add(new TextBlock { Text = "Evidence: " + path, TextWrapping = TextWrapping.Wrap });
        Content = panel;
        Write(new { kind = "start", api = "Windows.Gaming.Input", vendor = "F00D", product = "BEED" });
        timer.Tick += (_, _) => Observe();
        Loaded += (_, _) => timer.Start();
        Closing += (_, _) => {
            closing = true;
            timer.Stop();
            if (pulseTarget is not null) Neutral(pulseTarget, "window-closing");
            Write(new { kind = "summary", reads, changes });
        };
        Closed += (_, _) => evidence.Dispose();
    }

    private void AddPulse(Panel panel, string label, GamepadVibration vibration)
    {
        var button = new Button { Content = label, IsEnabled = false, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += async (_, _) => await Pulse(label, vibration);
        pulseButtons.Add(button);
        panel.Children.Add(button);
    }

    private static bool IsLab(Gamepad gamepad)
    {
        var raw = RawGameController.FromGameController(gamepad);
        return raw is not null && raw.HardwareVendorId == 0xF00D && raw.HardwareProductId == 0xBEED;
    }

    private void Observe()
    {
        try
        {
            var pads = Gamepad.Gamepads.ToArray();
            var matches = pads.Where(IsLab).ToArray();
            var next = matches.Length == 1 ? matches[0] : null;
            if (!Equals(next, selected))
            {
                selected = next;
                previous = null;
                Write(new { kind = "selection", gamepads = pads.Length, labMatches = matches.Length });
            }
            status.Text = $"WGI gamepads: {pads.Length}; matching lab pads: {matches.Length}. " +
                (selected is null ? "No unique target; pulses disabled." : "Lab Xbox pad available.");
            foreach (var button in pulseButtons) button.IsEnabled = selected is not null && !pulseActive;
            if (selected is null) return;
            var current = selected.GetCurrentReading();
            reads++;
            bool changed = previous is not { } prior || prior.Buttons != current.Buttons ||
                prior.LeftTrigger != current.LeftTrigger || prior.RightTrigger != current.RightTrigger ||
                prior.LeftThumbstickX != current.LeftThumbstickX || prior.LeftThumbstickY != current.LeftThumbstickY ||
                prior.RightThumbstickX != current.RightThumbstickX || prior.RightThumbstickY != current.RightThumbstickY;
            if (changed)
            {
                changes++;
                Write(new { kind = "input", current.Timestamp, buttons = (ulong)current.Buttons,
                    current.LeftTrigger, current.RightTrigger, current.LeftThumbstickX,
                    current.LeftThumbstickY, current.RightThumbstickX, current.RightThumbstickY });
                previous = current;
            }
            reading.Text = $"Reads: {reads}; changes: {changes}\nButtons: {current.Buttons}; LT {current.LeftTrigger:F2}; RT {current.RightTrigger:F2}\n" +
                $"Left ({current.LeftThumbstickX:F3}, {current.LeftThumbstickY:F3}); right ({current.RightThumbstickX:F3}, {current.RightThumbstickY:F3})";
        }
        catch (Exception ex)
        {
            selected = null;
            foreach (var button in pulseButtons) button.IsEnabled = false;
            status.Text = "WGI query failed: " + ex.Message;
        }
    }

    private async Task Pulse(string channel, GamepadVibration vibration)
    {
        if (closing || pulseActive || selected is not { } target) return;
        // Revalidate the captured object and uniqueness immediately before output.
        var matches = Gamepad.Gamepads.Where(IsLab).ToArray();
        if (matches.Length != 1 || !Equals(matches[0], target)) return;
        pulseActive = true;
        pulseTarget = target;
        foreach (var button in pulseButtons) button.IsEnabled = false;
        try
        {
            target.Vibration = vibration;
            Write(new { kind = "pulse-api-returned", channel, strength = 0.2, requestedMilliseconds = 200 });
            feedback.Text = channel + " pulse requested.";
            await Task.Delay(200);
        }
        catch (Exception ex)
        {
            Write(new { kind = "pulse-error", channel, error = ex.Message });
            feedback.Text = "Pulse API failed: " + ex.Message;
        }
        finally
        {
            // Closing already neutralizes this exact target before disposing the log.
            if (!closing) Neutral(target, channel);
            pulseTarget = null;
            pulseActive = false;
        }
    }

    private void Neutral(Gamepad target, string reason)
    {
        try
        {
            target.Vibration = default;
            Write(new { kind = "neutral-api-returned", reason });
            feedback.Text = reason + ": neutral requested. Physical effect requires confirmation.";
        }
        catch (Exception ex)
        {
            Write(new { kind = "neutral-error", reason, error = ex.Message });
            feedback.Text = "Neutral API failed: " + ex.Message;
        }
    }

    private void Write(object record) => evidence.WriteLine(JsonSerializer.Serialize(new {
        utc = DateTime.UtcNow, elapsedMs = elapsed.Elapsed.TotalMilliseconds, record }));
}
