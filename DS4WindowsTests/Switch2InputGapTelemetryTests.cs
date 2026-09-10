using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2InputGapTelemetryTests
{
    [DataTestMethod]
    [DataRow(false, "1", false)]
    [DataRow(true, "1", true)]
    [DataRow(true, "0", false)]
    [DataRow(true, null, false)]
    public void InputProbeRequiresExplicitPortableLabOptIn(bool lab, string value, bool expected) =>
        Assert.AreEqual(expected, Switch2BluetoothLabProbe.ShouldEnableInputGapProbe(lab, value));

    [TestMethod]
    public void CountersUseStrictThresholdsAndClockEpochsWithoutInventingDuplicateIntervals()
    {
        Switch2InputGapTelemetry telemetry = default;
        telemetry.Observe(1000, 1000);
        foreach (long timestamp in new long[] { 1010, 1030, 1080, 1180, 1281, 1281 })
            telemetry.Observe(timestamp, 1000);
        telemetry.ObservePublicationBusy();
        var first = telemetry.Snapshot();
        Assert.AreEqual(7L, first.AcceptedPublications);
        Assert.AreEqual(5L, first.IntervalCount);
        Assert.AreEqual(1L, first.DuplicateTimestamps);
        Assert.AreEqual(1L, first.PublicationBusy);
        Assert.AreEqual(101L, first.MaximumGapQpc);
        Assert.AreEqual(101.0, first.MaximumGapMilliseconds);
        Assert.AreEqual(4L, first.GapsOver10Milliseconds);
        Assert.AreEqual(3L, first.GapsOver20Milliseconds);
        Assert.AreEqual(2L, first.GapsOver50Milliseconds);
        Assert.AreEqual(1L, first.GapsOver100Milliseconds);

        telemetry.Observe(1, 1000); // Regressed clock starts a new segment.
        var second = telemetry.Snapshot();
        Assert.AreEqual(first.ClockEpoch + 1, second.ClockEpoch);
        Assert.AreEqual(0L, second.IntervalCount);
        Assert.AreEqual(0L, second.MaximumGapQpc);
        Assert.AreEqual(8L, second.AcceptedPublications);
        telemetry.Observe(20, 2000); // Changed frequency cannot share raw ticks.
        Assert.AreEqual(second.ClockEpoch + 1, telemetry.Snapshot().ClockEpoch);
        telemetry.Observe(-1, 2000);
        Assert.AreEqual(1L, telemetry.Snapshot().InvalidTimestamps);
        Assert.AreEqual(0L, telemetry.Snapshot().QpcFrequency);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RealAcceptedPublicationUsesOptInWithoutCountingForeignOrTerminalFrames(bool enabled)
    {
        var device = Pro(enabled);
        try
        {
            Assert.IsTrue(device.TryPublishPro(Frame(100)));
            Assert.IsTrue(device.TryPublishPro(Frame(100_100)));
            Assert.AreEqual(Switch2RuntimePublicationResult.FrameRejected,
                device.TryPublishProDetailed(Switch2RuntimeInputDeviceTests.CreateProFrame(99, 11, 0)));
            var active = device.CaptureInputGapSnapshot();
            Assert.AreEqual(enabled, active.Enabled);
            Assert.AreEqual(enabled ? 2L : 0L, active.Counters.AcceptedPublications);
            Assert.AreEqual(enabled ? 1L : 0L, active.Counters.IntervalCount);
            Assert.AreEqual(InputDeviceType.Switch2Pro, active.Model);
            Assert.AreEqual(Switch2Transport.Usb, active.Transport);
            Assert.AreEqual(7UL, active.LeftDeviceGeneration);
            Assert.AreEqual(11UL, active.LeftTransportGeneration);
            Assert.AreEqual("accepted-physical-report-publication-gaps", active.Measurement);
            device.TryPublishTerminalNeutral();
            var terminal = device.CaptureInputGapSnapshot();
            Assert.AreEqual(Switch2RuntimeInputDeviceState.Terminal, terminal.RuntimeState);
            Assert.IsTrue(terminal.TerminalReserved);
            Assert.AreEqual(active.Counters, terminal.Counters);
        }
        finally { device.TryPublishTerminalNeutral(); }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, Switch2JoyConProfileMode.StandaloneHorizontalLeft)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, Switch2JoyConProfileMode.StandaloneHorizontalRight)]
    public void StandaloneJoyConSnapshotRetainsExactPhysicalSideIdentity(
        Switch2ControllerModel model, Switch2JoyConProfileMode mode)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model, 7, 11, out var device, out _));
        Enable(device, true);
        device.StartUpdate();
        try
        {
            Assert.IsTrue(device.TryPublishStandaloneJoyCon(JoyConFrame(mode, 100)));
            Assert.IsTrue(device.TryPublishStandaloneJoyCon(JoyConFrame(mode, 200_100)));
            var snapshot = device.CaptureInputGapSnapshot();
            Assert.AreEqual(Switch2Transport.BluetoothLe, snapshot.Transport);
            Assert.AreEqual(2L, snapshot.Counters.AcceptedPublications);
            Assert.AreEqual(1L, snapshot.Counters.IntervalCount);
            bool left = model == Switch2ControllerModel.JoyCon2Left;
            Assert.AreEqual(7UL, left ? snapshot.LeftDeviceGeneration : snapshot.RightDeviceGeneration);
            Assert.AreEqual(11UL, left ? snapshot.LeftTransportGeneration : snapshot.RightTransportGeneration);
            Assert.AreEqual(0UL, left ? snapshot.RightDeviceGeneration : snapshot.LeftDeviceGeneration);
            Assert.AreEqual(left ? InputDeviceType.Switch2JoyConLeft : InputDeviceType.Switch2JoyConRight, snapshot.Model);
        }
        finally { device.TryPublishTerminalNeutral(); }
    }

    [TestMethod]
    public void PublicationBusyIsSeparateFromAcceptedInputAndHasNoNestedObservation()
    {
        var device = Pro(true);
        var nestedFrame = Frame(200_100);
        bool nested = false;
        device.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind != Switch2RuntimeReportKind.Regular) return;
            nested = true;
            Assert.AreEqual(Switch2RuntimePublicationResult.PublicationBusy,
                device.TryPublishProDetailed(nestedFrame));
        };
        try
        {
            Assert.IsTrue(device.TryPublishPro(Frame(100)));
            Assert.IsTrue(nested);
            var counters = device.CaptureInputGapSnapshot().Counters;
            Assert.AreEqual(1L, counters.AcceptedPublications);
            Assert.AreEqual(1L, counters.PublicationBusy);
            Assert.AreEqual(0L, counters.IntervalCount);
        }
        finally { device.TryPublishTerminalNeutral(); }
    }

    [TestMethod]
    public void EnabledRealPublicationTelemetryAllocatesNothingInWarmedSynchronousWindow()
    {
        var device = Pro(true);
        var first = Frame(100);
        var second = Frame(100_100);
        bool accepted = true;
        try
        {
            for (int i = 0; i < 256; i++)
                accepted &= device.TryPublishPro((i & 1) == 0 ? first : second);
            long allocated;
            using (StrictAllocationMeasurementScope.Begin())
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 4096; i++)
                    accepted &= device.TryPublishPro((i & 1) == 0 ? first : second);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.IsTrue(accepted);
            Assert.AreEqual(0L, allocated);
            Assert.AreEqual(4352L, device.CaptureInputGapSnapshot().Counters.AcceptedPublications);
        }
        finally { device.TryPublishTerminalNeutral(); }
    }

    [TestMethod]
    public async Task InputOnlyProbeExposesSuccessiveSnapshotsAndRejectsEveryRadioCommand()
    {
        string folder = TemporaryProbeDirectory();
        var device = Pro(true);
        var access = new RefusingAudioAccess();
        var probe = new Switch2BluetoothLabProbe(folder, access, () => true,
            _ => throw new AssertFailedException("Input telemetry cannot configure audio."), 0,
            inputGapSnapshot: device.CaptureInputGapSnapshot);
        try
        {
            Assert.IsTrue(probe.PipeName.StartsWith("ds4w-s2input-", StringComparison.Ordinal));
            using var first = JsonDocument.Parse(await Send(probe, "status"));
            Assert.AreEqual(0L, InputCounters(first).GetProperty("AcceptedPublications").GetInt64());
            Assert.IsTrue(device.TryPublishPro(Frame(100)));
            foreach (string command in new[] { "inventory", "configure-audio", "audio-state", "headset-observe",
                "run-plan", "run-receiver-plan", "tone-opus5", "write", "reset" })
                Assert.IsTrue((await Send(probe, command)).Contains("Error", StringComparison.Ordinal));
            using var second = JsonDocument.Parse(await Send(probe, "status"));
            Assert.AreEqual(1L, InputCounters(second).GetProperty("AcceptedPublications").GetInt64());
            Assert.AreEqual(0, access.Calls);
            Assert.IsTrue(second.RootElement.GetProperty("Input").GetProperty("Enabled").GetBoolean());
            Assert.AreEqual("Switch2Pro", second.RootElement.GetProperty("Input").GetProperty("ModelName").GetString());
            device.TryPublishTerminalNeutral();
            using var last = JsonDocument.Parse(await Send(probe, "stop-probe"));
            Assert.IsTrue(last.RootElement.GetProperty("Input").GetProperty("TerminalReserved").GetBoolean());
            await probe.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, probe.PipeName + ".json")));
            Assert.AreEqual("stopped", descriptor.RootElement.GetProperty("State").GetString());
        }
        finally
        {
            device.TryPublishTerminalNeutral();
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RuntimeRetirementCancelsItsInputProbeOffTheInputCallback(bool abortUnpublished)
    {
        string folder = TemporaryProbeDirectory();
        var device = Pro(true);
        var probe = new Switch2BluetoothLabProbe(folder, null, () => true, null, 0,
            inputGapSnapshot: device.CaptureInputGapSnapshot);
        Field("inputGapProbe").SetValue(device, probe);
        try
        {
            using var initial = JsonDocument.Parse(await Send(probe, "status"));
            if (abortUnpublished) Assert.IsTrue(device.TryAbortUnpublishedActivation());
            else
            {
                device.Report += (_, args) =>
                {
                    if (((Switch2RuntimeReportEventArgs)args).Kind == Switch2RuntimeReportKind.Regular)
                        device.TryPublishTerminalNeutral();
                };
                Assert.IsTrue(device.TryPublishPro(Frame(100)));
            }
            await probe.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsNull(Field("inputGapProbe").GetValue(device));
            Assert.AreEqual(abortUnpublished ? Switch2RuntimeInputDeviceState.AbortedUnpublished :
                Switch2RuntimeInputDeviceState.Terminal, device.CaptureInputGapSnapshot().RuntimeState);
        }
        finally
        {
            device.TryPublishTerminalNeutral();
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static JsonElement InputCounters(JsonDocument json) =>
        json.RootElement.GetProperty("Input").GetProperty("Counters");

    private static Switch2RuntimeInputDevice Pro(bool enabled)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(7, 11, Switch2Transport.Usb, out var device, out _));
        Enable(device, enabled);
        device.StartUpdate();
        return device;
    }

    private static void Enable(Switch2RuntimeInputDevice device, bool enabled) =>
        Field("inputGapTelemetryEnabled").SetValue(device, enabled);

    private static FieldInfo Field(string name) => typeof(Switch2RuntimeInputDevice)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Switch2ProProfileInputFrame Frame(long timestamp) =>
        Switch2RuntimeInputDeviceTests.CreateProFrame(7, 11, 0, timestamp: timestamp);

    private static Switch2JoyConProfileInputFrame JoyConFrame(Switch2JoyConProfileMode mode, long timestamp) =>
        (Switch2JoyConProfileInputFrame)typeof(Switch2RuntimeInputDeviceTests)
            .GetMethod("MapStandalone", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { mode, 0u, 7UL, 11UL, (ushort)0,
                default(Switch2Vector3Raw), 1u, (ushort)0x800, (ushort)0x800, timestamp })!;

    private static string TemporaryProbeDirectory() =>
        Path.Combine(Path.GetTempPath(), "ds4w-input-gap-probe-" + Guid.NewGuid().ToString("N"));

    private static async Task<string> Send(Switch2BluetoothLabProbe probe, string command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", probe.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), deadline.Token);
        using var reader = new StreamReader(pipe);
        return (await reader.ReadLineAsync(deadline.Token))!;
    }

    private sealed class RefusingAudioAccess : ISwitch2BluetoothLabAudioAccess
    {
        internal int Calls;
        public Task<string> QueryAudioLabAsync(string command, CancellationToken token)
        {
            Calls++;
            throw new AssertFailedException("Input telemetry cannot reach GATT/audio.");
        }
    }
}
