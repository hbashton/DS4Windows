using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ReportDiagnosticsIntegrationTests
{
    [TestMethod]
    public void BatteryIconPolicyChangeRearmsTheSamePercentage()
    {
        TrayIconChoice saved = Global.UseIconChoice;
        try
        {
            Global.UseIconChoice = TrayIconChoice.Battery;
            int delivered = 0;
            using var worker = new ReportDiagnosticsWorker(1, _ => delivered++, startWorker: false);
            worker.Resume();
            var source = worker.Register(0, CreateDevice());
            var snapshot = new ReportDiagnosticsSnapshot
            {
                BatteryNotification = true, Battery = 80,
                BatteryPolicyRevision = Global.TrayIconPolicyRevision,
            };
            Assert.IsTrue(source.TryPublish(snapshot));
            Assert.AreEqual(1, worker.DrainOnce());
            Assert.IsFalse(source.TryPublish(snapshot));
            Global.UseIconChoice = TrayIconChoice.Default;
            Global.UseIconChoice = TrayIconChoice.Battery;
            Assert.AreNotEqual(snapshot.BatteryPolicyRevision, Global.TrayIconPolicyRevision);
            snapshot.BatteryPolicyRevision = Global.TrayIconPolicyRevision;
            Assert.IsTrue(source.TryPublish(snapshot), "Returning to battery mode must refresh an unchanged percentage.");
            Assert.AreEqual(1, worker.DrainOnce());
            Assert.AreEqual(2, delivered);
        }
        finally { Global.UseIconChoice = saved; }
    }

    [TestMethod]
    public void DeferredBatteryEventCarriesExactIdentityAndRejectsRetirement()
    {
        using var worker = new ReportDiagnosticsWorker(1, _ => { }, startWorker: false);
        worker.Resume();
        var source = worker.Register(0, CreateDevice());
        object observedSender = null;
        byte observedBattery = 0;
        int calls = 0;
        EventHandler<byte> handler = (sender, percentage) =>
        {
            observedSender = sender;
            observedBattery = percentage;
            calls++;
        };
        Global.BatteryChanged += handler;
        try
        {
            Global.InvokeBatteryChanged(73, source);
            Assert.AreSame(source, observedSender);
            Assert.AreEqual((byte)73, observedBattery);
            source.Retire();
            Global.InvokeBatteryChanged(11, source);
            Assert.AreEqual(1, calls);
        }
        finally { Global.BatteryChanged -= handler; }
    }

    [TestMethod]
    public void TrayCallbackRechecksIdentityAndPolicyAtExecution()
    {
        TrayIconChoice saved = Global.UseIconChoice;
        try
        {
            // No window, WPF Application, tray subscription or controller IO.
            // Invoke the exact callback as a queued UI action would, after the
            // source/policy changed; rejected actions must never touch UI.
            var tray = (TrayIconViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconViewModel));
            tray.IconSource = "new controller icon";
            var callback = (Action<object, byte>)typeof(TrayIconViewModel)
                .GetMethod("UpdateTrayBattery", BindingFlags.Instance | BindingFlags.NonPublic)
                .CreateDelegate(typeof(Action<object, byte>), tray);
            using var worker = new ReportDiagnosticsWorker(1, _ => { }, startWorker: false);
            worker.Resume();
            var device = CreateDevice();
            var old = worker.Register(0, device);
            var current = worker.Register(0, device);
            Global.UseIconChoice = TrayIconChoice.Battery;
            callback(old, 10);
            Assert.AreEqual("new controller icon", tray.IconSource);
            Global.UseIconChoice = TrayIconChoice.Default;
            callback(current, 20);
            Assert.AreEqual("new controller icon", tray.IconSource);
        }
        finally { Global.UseIconChoice = saved; }
    }

    [TestMethod]
    public void SecondaryControllerEarlyReturnStillPublishesItsOwnDiagnostics()
    {
        bool savedVerbose = Global.VerboseStartupLogging;
        try
        {
            Global.VerboseStartupLogging = false;
            var device = CreateDevice();
            device.PrimaryDevice = false;
            device.OutputMapGyro = false;
            device.error = "secondary source diagnostic";
            var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
            service.DS4Controllers[0] = device;
            service.inWarnMonitor = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
            var states = new DS4State[Global.MAX_DS4_CONTROLLER_COUNT];
            states[0] = new DS4State();
            typeof(ControlService).GetField("CurrentState", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, states);
            typeof(ControlService).GetField("tempStrings", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, new string[Global.MAX_DS4_CONTROLLER_COUNT]);
            ReportDiagnosticsSnapshot delivered = default;
            using var worker = new ReportDiagnosticsWorker(1, snapshot => delivered = snapshot, startWorker: false);
            worker.Resume();
            var source = worker.Register(0, device);
            var report = (Action<DS4Device, EventArgs, int, ReportDiagnosticsWorker.Source>)typeof(ControlService)
                .GetMethod("On_Report", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(DS4Device), typeof(EventArgs), typeof(int), typeof(ReportDiagnosticsWorker.Source) }, null)
                .CreateDelegate(typeof(Action<DS4Device, EventArgs, int, ReportDiagnosticsWorker.Source>), service);
            report(device, EventArgs.Empty, 0, source);
            Assert.IsNull(delivered.Source, "No diagnostic callback is allowed inline.");
            Assert.AreEqual(1, worker.DrainOnce());
            Assert.AreSame(source, delivered.Source);
            Assert.AreEqual("secondary source diagnostic", delivered.DeviceError);
        }
        finally { Global.VerboseStartupLogging = savedVerbose; }
    }

    [TestMethod]
    public void Switch2BatteryCaptureUsesRuntimeTelemetryDuringTheAdmittedReport()
    {
        TrayIconChoice saved = Global.UseIconChoice;
        try
        {
            Global.UseIconChoice = TrayIconChoice.Battery;
            var device = CreateDevice();
            ReportDiagnosticsSnapshot captured = default, delivered = default;
            using var worker = new ReportDiagnosticsWorker(1, snapshot => delivered = snapshot, startWorker: false);
            worker.Resume();
            var source = worker.Register(0, device);
            device.Report += (sender, _) =>
            {
                ControlService.CaptureReportBatteryDiagnostic(sender, ref captured);
                source.TryPublish(captured);
            };
            device.StartUpdate(); // Synthetic runtime only; no HID worker.
            Assert.IsTrue(device.TryPublishPro(Switch2RuntimeInputDeviceTests.CreateProFrame(101, 102, 0,
                batteryVoltageMillivolts: 3000)));
            Assert.AreEqual(1, worker.DrainOnce());
            Assert.IsTrue(delivered.BatteryNotification);
            Assert.AreEqual((int)Switch2BatteryStatus.LowCompatibilityPercentage, delivered.Battery);
            Assert.AreEqual(device.Battery, delivered.Battery);
            Assert.AreEqual(Global.TrayIconPolicyRevision, delivered.BatteryPolicyRevision);
        }
        finally { Global.UseIconChoice = saved; }
    }

    private static Switch2RuntimeInputDevice CreateDevice()
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(101, 102,
            Switch2Transport.Usb, out var device, out _));
        return device;
    }
}
