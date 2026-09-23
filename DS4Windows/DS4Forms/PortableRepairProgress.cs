using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DS4WinWPF.DS4Forms;

// The startup caller is synchronous. A modal window keeps cancellation and
// painting responsive while the bounded file/network transaction runs off STA.
internal sealed class PortableRepairProgress : Window
{
    internal static T Run<T>(Window owner, string message,
        Func<CancellationToken, Task<T>> operation)
    {
        using CancellationTokenSource cancellation = new();
        Exception failure = null;
        T result = default;
        bool finished = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8,
            Margin = new Thickness(0, 18, 0, 18) });
        var cancel = new Button { Content = "Cancel", MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(cancel);
        var window = new PortableRepairProgress { Title = "DS4Windows — VIIPER repair",
            Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.SetResourceReference(BackgroundProperty, "SurfaceBackgroundColor");
        window.SetResourceReference(ForegroundProperty, "ForegroundColor");
        if (owner?.IsLoaded == true)
        {
            window.Owner = owner;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        void Cancel()
        {
            cancel.IsEnabled = false;
            cancellation.Cancel();
        }
        cancel.Click += (_, _) => Cancel();
        window.Closing += (_, e) => { if (!finished) { e.Cancel = true; Cancel(); } };
        window.Loaded += async (_, _) =>
        {
            try { result = await Task.Run(() => operation(cancellation.Token)); }
            catch (Exception error) { failure = error; }
            finally { finished = true; window.Close(); }
        };
        Application app = Application.Current;
        ShutdownMode previous = app.ShutdownMode;
        Window previousMain = app.MainWindow;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { window.ShowDialog(); }
        finally
        {
            // A startup-only progress window must not become the application's
            // main window or shut it down before normal startup can continue.
            if (app.MainWindow == window) app.MainWindow = previousMain;
            app.ShutdownMode = previous;
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
