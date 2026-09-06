using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Forms.ViewModels;
using Fixture = DS4WindowsTests.Switch2RuntimeRawStickCalibrationTests.Fixture;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2StickCalibrationEditorTests
{
    [TestMethod]
    public void CloseBetweenRuntimeBeginReturnAndUiContinuationReleasesNextInput()
    {
        using var f = new Fixture();
        f.Publish(2100, 2000);
        var vm = new Switch2StickCalibrationViewModel(f.Runtime);
        using var context = new QueuedUiContext();
        var previous = SynchronizationContext.Current;
        Task<bool> starting = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            starting = vm.StartAsync();
            Assert.IsTrue(context.Posted.Wait(1000), "The runtime must have finished Begin before this controlled UI handoff window.");
            Assert.IsFalse(starting.IsCompleted);
            vm.Close();
            f.Publish(3700, 3450);
            Assert.AreNotEqual((short)0, f.Runtime.getCurrentStateRef().LXAxis.ToSigned16(),
                "Closing must revoke suppression before the queued UI continuation gets to run.");
            context.Drain();
            Assert.IsTrue(starting.IsCompleted);
            Assert.IsFalse(starting.GetAwaiter().GetResult());
            Assert.IsFalse(vm.CanSave);
            Assert.AreEqual(0, f.Store.Writes);
        }
        finally { vm.Close(); context.Drain(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [TestMethod]
    public async Task JoinedWizardSelectsAndStoresOnlyTheChosenPhysicalPeer()
    {
        var left = new Switch2RawStickCalibrationCollectorTests.Fixture(
            Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left);
        var right = new Switch2RawStickCalibrationCollectorTests.Fixture(
            Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, generation: 2);
        var store = new Switch2RuntimeRawStickCalibrationTests.RecordingStore();
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(3, 7, 1, 1, 2, 2, out var runtime, out _));
        Assert.IsTrue(runtime.TryBindRawStickCalibrationPersistence(store, left.Peer, right.Peer));
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateBound(7, left.Descriptor, right.Descriptor,
            runtime, new Switch2JoyConPairPolicy(1_000_000), 1000, Switch2RuntimeTerminalScheduler.Instance,
            out var sink, out _, out _));
        runtime.Report += (_, _) => { };
        runtime.StartUpdate();
        var vm = new Switch2StickCalibrationViewModel(runtime);
        try
        {
            void Publish(ushort x, ushort y)
            {
                sink.PublishJoyCon(left.Frame(2048, 2048));
                sink.PublishJoyCon(right.Frame(x, y));
            }
            Publish(2100, 2000);
            vm.SelectedSideIndex = 1;
            Assert.AreEqual("Right stick", vm.SelectedSideLabel);
            StringAssert.Contains(vm.ControllerLabel, "Joined");
            Assert.IsTrue(await vm.StartAsync());
            for (int i = 0; i < 230; i++) Publish(i % 2 == 0 ? (ushort)300 : (ushort)3700,
                i % 2 == 0 ? (ushort)450 : (ushort)3450);
            for (int i = 0; i < 121; i++) Publish(2100, 2000);
            vm.Poll();
            Assert.IsTrue(vm.CanSave);
            await vm.SaveAsync();
            Assert.IsTrue(store.TryLoad(right.Peer, Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right, out var saved));
            Assert.AreEqual((ushort)2100, saved.NeutralX);
            Assert.IsFalse(store.TryLoad(left.Peer, Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left, out _));
            Assert.AreEqual(1, store.Writes);
        }
        finally { vm.Close(); runtime.StopUpdate(); }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2, true, Switch2StickSide.Left, false)]
    [DataRow(Switch2ControllerModel.ProController2, true, Switch2StickSide.Right, false)]
    [DataRow(Switch2ControllerModel.ProController2, false, Switch2StickSide.Left, false)]
    [DataRow(Switch2ControllerModel.ProController2, false, Switch2StickSide.Right, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left, true)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, true)]
    public async Task PhysicalSideWizardCapturesExplicitlySavesAndResets(Switch2ControllerModel model,
        bool usb, Switch2StickSide side, bool horizontal)
    {
        using var f = new Fixture(model, usb, side, horizontal);
        var vm = new Switch2StickCalibrationViewModel(f.Runtime);
        if (model == Switch2ControllerModel.ProController2 && side == Switch2StickSide.Right) vm.SelectedSideIndex = 1;
        Assert.AreEqual(side == Switch2StickSide.Left ? "Left stick" : "Right stick", vm.SelectedSideLabel);
        Assert.IsTrue(vm.CanStart);
        Assert.IsFalse(vm.CanSave);
        f.Publish(3700, 3450);
        Assert.IsTrue(await vm.StartAsync());
        Assert.IsFalse(vm.CanStart);
        Assert.IsTrue(vm.CanCancel);
        int selected = vm.SelectedSideIndex;
        vm.SelectedSideIndex = selected == 0 ? 1 : 0;
        Assert.AreEqual(selected, vm.SelectedSideIndex, "A capture cannot change physical side.");
        DriveReady(f, vm);
        Assert.IsTrue(vm.CanSave);
        Assert.AreEqual(0, f.Store.Writes, "Ready does not authorize automatic persistence.");
        await vm.SaveAsync();
        Assert.IsTrue(vm.CanStart);
        Assert.IsFalse(vm.CanSave);
        Assert.AreEqual(1, f.Store.Writes);
        StringAssert.Contains(vm.ResultText, "saved on this PC and applied");
        StringAssert.Contains(vm.CalibrationStatus, "PC calibration is active");
        Assert.IsTrue(f.Store.TryLoad(f.Source.Peer, model, side, out var stored));
        Assert.AreEqual((ushort)2100, stored.NeutralX);
        await vm.ResetAsync();
        Assert.AreEqual(1, f.Store.Removes);
        StringAssert.Contains(vm.ResultText, "override was removed");
        Assert.IsFalse(f.Store.TryLoad(f.Source.Peer, model, side, out _));
        vm.Close();
    }

    [TestMethod]
    public async Task NoInputRefusesStartAndCancelDoesNotPersistOrLeaveCaptureActive()
    {
        using var f = new Fixture();
        var vm = new Switch2StickCalibrationViewModel(f.Runtime);
        Assert.IsFalse(await vm.StartAsync());
        Assert.IsTrue(vm.CanStart);
        StringAssert.Contains(vm.Heading, "could not start");
        f.Publish(3700, 3450);
        Assert.IsTrue(await vm.StartAsync());
        vm.Cancel();
        Assert.IsTrue(vm.CanStart);
        Assert.IsFalse(vm.CanSave);
        Assert.AreEqual(0, f.Store.Writes);
        f.Publish(3700, 3450);
        Assert.AreNotEqual((short)0, f.Runtime.getCurrentStateRef().LXAxis.ToSigned16());
        Assert.IsTrue(await vm.StartAsync());
        vm.Close();
        Assert.IsFalse(vm.CanStart);
        f.Publish(3700, 3450);
        Assert.AreNotEqual((short)0, f.Runtime.getCurrentStateRef().LXAxis.ToSigned16());
    }

    [TestMethod]
    public async Task FailedResetKeepsRetryReceiptAndAccurateOutcomeAcrossPolls()
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        var vm = new Switch2StickCalibrationViewModel(f.Runtime);
        f.Store.FailMutation = true;
        await vm.ResetAsync();
        string failure = vm.ResultText;
        StringAssert.Contains(failure, "could not be updated");
        vm.Poll();
        Assert.AreEqual(failure, vm.ResultText);
        Assert.IsTrue(vm.CanSave);
        Assert.IsTrue(f.Runtime.HasLocalLeftStickCalibration);
        f.Store.FailMutation = false;
        await vm.SaveAsync();
        Assert.IsFalse(f.Runtime.HasLocalLeftStickCalibration);
        StringAssert.Contains(vm.ResultText, "override was removed");
        vm.Close();
    }

    [TestMethod]
    public async Task CloseDuringBeginRevokesLateReceiptWithoutLateUiNotifications()
    {
        using var f = new Fixture();
        f.Publish(2100, 2000);
        var vm = new Switch2StickCalibrationViewModel(f.Runtime);
        int notifications = 0, callbacks = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Runtime.Report += (_, _) =>
        {
            if (Interlocked.Increment(ref callbacks) != 1) return;
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
        };
        var starting = vm.StartAsync();
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            Assert.IsTrue(vm.IsBusy);
            vm.Close();
            int atClose = notifications;
            release.Set();
            Assert.IsFalse(await starting.WaitAsync(TimeSpan.FromSeconds(2)));
            vm.Poll();
            Assert.AreEqual(atClose, notifications);
            Assert.IsTrue(f.Runtime.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out var next));
            Assert.IsTrue(f.Runtime.CancelRawStickCalibration(next));
        }
        finally { release.Set(); await starting.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelOrCloseDuringSaveCannotClaimUnchangedDiskOrUpdateClosedWindow(bool close)
    {
        using var f = new Fixture(loaded: true);
        f.Publish(2100, 2000);
        var vm = new Switch2StickCalibrationViewModel(f.Runtime);
        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Store.BeforeMutation = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); };
        var saving = vm.ResetAsync();
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            Assert.IsTrue(vm.IsBusy);
            Assert.IsFalse(vm.CanStart);
            if (close) vm.Close(); else vm.Cancel();
            int before = notifications;
            release.Set();
            await saving.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(f.Runtime.HasLocalLeftStickCalibration);
            Assert.AreEqual(1, f.Store.Removes);
            if (close) Assert.AreEqual(before, notifications);
            else
            {
                StringAssert.Contains(vm.ResultText, "PC file was updated");
                StringAssert.Contains(vm.ResultText, "not applied");
                vm.Poll();
                StringAssert.Contains(vm.ResultText, "PC file was updated");
            }
        }
        finally { release.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(5)); vm.Close(); }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task WindowNeverFollowsChangedSlotProfileOrRetiredController(int change)
    {
        using var f = new Fixture();
        f.Runtime.DeviceSlotNumber = 0;
        f.Publish(2100, 2000);
        var vm = new Switch2StickCalibrationViewModel(f.Runtime);
        Assert.IsTrue(await vm.StartAsync());
        if (change == 0) f.Runtime.DeviceSlotNumber = 1;
        else if (change == 1) Global.BeginProfileSwitchRevision(0);
        else f.Runtime.StopUpdate();
        vm.Poll();
        Assert.IsFalse(vm.CanStart);
        Assert.IsFalse(vm.CanSave);
        Assert.IsFalse(vm.CanCancel);
        StringAssert.Contains(vm.Heading, "context changed");
        await vm.SaveAsync();
        Assert.AreEqual(0, f.Store.Writes);
        vm.Close();
    }

    internal static void ValidateStickCalibrationWindow(Application application)
    {
        // Called by the existing single-Application theme test on its STA
        // thread. Shell styles require the real app-level resource scope.
        var previousContext = SynchronizationContext.Current;
        var previousDictionaries = application.Resources.MergedDictionaries.ToArray();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            foreach (string theme in new[] { "DefaultTheme", "DarkTheme" })
            {
                application.Resources.MergedDictionaries.Clear();
                var colors = new ResourceDictionary();
                application.Resources.MergedDictionaries.Add(colors);
                colors.Source = new Uri($"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml", UriKind.Relative);
                var shell = new ResourceDictionary();
                application.Resources.MergedDictionaries.Add(shell);
                shell.Source = new Uri("/DS4Windows;component/DS4Forms/Themes/BridgeShellStyles.xaml", UriKind.Relative);
                foreach (int width in new[] { 460, 620 })
                {
                    using var f = new Fixture();
                    f.Publish(3700, 3450);
                    var window = new Switch2StickCalibrationWindow(f.Runtime);
                    var vm = (Switch2StickCalibrationViewModel)window.DataContext;
                    var content = (FrameworkElement)window.Content;
                    Arrange(content, width);
                    Assert.IsTrue(((Button)window.FindName("StartButton")).IsEnabled);
                    Assert.IsFalse(((Button)window.FindName("SaveButton")).IsEnabled);
                    Assert.AreEqual(2, ((ComboBox)window.FindName("SideChoice")).Items.Count);
                    if (width == 620) Assert.AreEqual(0.0, ((ScrollViewer)window.FindName("CalibrationScroll")).ScrollableHeight,
                        "The default layout must expose Reset without scrolling.");
                    // No Show/ShowDialog or native device. The task's optional
                    // evidence directory receives only rendered fake-runtime UI.
                    RenderIfRequested(content, width, $"stick-calibration-{theme}-{width}-start");
                    CompleteWithDispatcher(vm.StartAsync());
                    DriveReady(f, vm);
                    Arrange(content, width);
                    Assert.IsFalse(((Button)window.FindName("StartButton")).IsEnabled);
                    Assert.IsTrue(((Button)window.FindName("SaveButton")).IsEnabled);
                    Assert.AreEqual("Ready to save", ((TextBlock)window.FindName("StageHeading")).Text);
                    RenderIfRequested(content, width, $"stick-calibration-{theme}-{width}-ready");
                    vm.Close();
                    window.Close();
                }
            }
        }
        finally
        {
            application.Resources.MergedDictionaries.Clear();
            foreach (var dictionary in previousDictionaries) application.Resources.MergedDictionaries.Add(dictionary);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void CompleteWithDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void Arrange(FrameworkElement content, int width)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        int height = width == 620 ? 620 : 550;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
    }

    private static void RenderIfRequested(FrameworkElement content, int width, string name)
    {
        string directory = Environment.GetEnvironmentVariable("DS4W_UI_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, width == 620 ? 620 : 550, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(directory, name + ".png"), FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }

    private sealed class QueuedUiContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object)> queue = new();
        internal readonly ManualResetEventSlim Posted = new();
        public override void Post(SendOrPostCallback callback, object state)
        { queue.Enqueue((callback, state)); Posted.Set(); }
        internal void Drain()
        { while (queue.TryDequeue(out var item)) item.Item1(item.Item2); }
        public void Dispose() => Posted.Dispose();
    }

    private static void DriveReady(Fixture f, Switch2StickCalibrationViewModel vm)
    {
        for (int i = 0; i < 230; i++) f.Publish(i % 2 == 0 ? (ushort)300 : (ushort)3700,
            i % 2 == 0 ? (ushort)450 : (ushort)3450);
        vm.Poll();
        StringAssert.Contains(vm.Heading, "Release");
        for (int i = 0; i < 121; i++) f.Publish(2100, 2000);
        vm.Poll();
        Assert.AreEqual("Ready to save", vm.Heading);
    }
}
