using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class DS4DeviceReadFailureRetirementTests
{
    [TestMethod]
    public void TerminalBluetoothReadFailureDoesNotWaitForANewEffectWriteBeforeRemoval()
    {
        using var device = new RecordingDevice();
        using var firstProgress = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        using var removed = new ManualResetEventSlim();
        Exception workerFailure = null;
        device.DuringWrite = () =>
        {
            // The old helper signals here before StopOutputUpdate or Removal.
            // The fixture owns this wait; it never invokes a native HID method.
            firstProgress.Set();
            if (!releaseWrite.Wait(5_000)) throw new TimeoutException("Fixture write was not released.");
        };
        device.Removal += (_, _) => { removed.Set(); firstProgress.Set(); };
        var reader = new Thread(() =>
        {
            try { device.RetireAfterTerminalBluetoothReadFailure(); }
            catch (Exception error) { workerFailure = error; firstProgress.Set(); }
        }) { IsBackground = true, Name = "Bounded DS4 terminal read-failure regression" };
        bool removalBeforeWriteRelease;
        bool joined;
        reader.Start();
        try
        {
            Assert.IsTrue(firstProgress.Wait(5_000), "Failure retirement made no progress.");
            removalBeforeWriteRelease = removed.IsSet;
        }
        finally
        {
            releaseWrite.Set();
            joined = reader.Join(5_000);
        }
        Assert.IsTrue(joined, "The bounded regression must not strand its fixture worker.");
        Assert.IsNull(workerFailure);
        Assert.IsTrue(removalBeforeWriteRelease,
            "A terminal read failure attempted new synchronous effect output before removal. " +
            "A blocked native write at this point would strand the input worker after its error log.");
        Assert.AreEqual(0, device.WriteCalls);
        Assert.AreEqual(1, device.StopCalls);
    }

    [TestMethod]
    public void TerminalRetirementKeepsOutputStopAndClockResetBeforeRemoval()
    {
        using var device = new RecordingDevice();
        var stages = new List<string>();
        device.ReadWaitEv.Set();
        device.SeedControllerClock();
        device.DuringStop = () =>
        {
            Assert.IsFalse(device.ReadWaitEv.IsSet);
            Assert.IsFalse(device.IsDisconnecting);
            stages.Add("output stopped");
        };
        device.Removal += (sender, _) =>
        {
            Assert.AreSame(device, sender);
            Assert.IsTrue(device.IsDisconnecting);
            Assert.IsTrue(device.ControllerClockWasReset);
            Assert.AreEqual(1, device.StopCalls);
            stages.Add("removed");
        };

        device.RetireAfterTerminalBluetoothReadFailure();

        CollectionAssert.AreEqual(new[] { "output stopped", "removed" }, stages);
    }

    [TestMethod]
    public void TerminalReadFailureDoesNotPublishFreshEffectsIntoTheAudioLane()
    {
        using var device = new RecordingDevice();
        device.EnableAudioLane();
        device.setRumble(80, 160);
        int removed = 0;
        device.Removal += (_, _) => removed++;

        device.RetireAfterTerminalBluetoothReadFailure();

        Assert.AreEqual(0, device.AudioEffectPublications,
            "A terminal input failure is not new physical effect intent for the audio owner.");
        Assert.AreEqual(0, device.WriteCalls);
        Assert.AreEqual(1, device.StopCalls);
        Assert.AreEqual(1, removed);
    }

    [TestMethod]
    public void OrdinaryBluetoothEffectsStillUseTheirExistingOutputPath()
    {
        using var device = new RecordingDevice();
        device.setRumble(80, 160);

        Assert.IsTrue(device.SendOrdinaryEffect());

        Assert.IsTrue(DS4Device.UsesControlPipeForEffectOutput(ConnectionType.BT, false));
        Assert.AreEqual(1, device.WriteCalls);
        Assert.AreEqual(0, device.StopCalls);
        Assert.IsFalse(device.IsDisconnecting);
    }

    [TestMethod]
    public void ProductionInputLoopCallsTheTerminalReadRetirementHelper()
    {
        MethodInfo helper = typeof(DS4Device).GetMethod(
            nameof(DS4Device.RetireAfterTerminalBluetoothReadFailure),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] body = typeof(DS4Device).GetMethod("performDs4Input",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.IsTrue(Enumerable.Range(0, body.Length - 4).Any(index =>
            body[index] == 0x28 && BitConverter.ToInt32(body, index + 1) == helper.MetadataToken),
            "The tested helper must remain connected to the actual physical input-loop failure branch.");
    }

    private sealed class RecordingDevice : DS4Device, IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Func<bool, bool, bool, bool> send;
        internal Action DuringWrite;
        internal Action DuringStop;
        internal int WriteCalls;
        internal int StopCalls;
        internal int AudioEffectPublications;

        internal RecordingDevice()
            : base((HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)),
                "Synthetic DS4 read-failure retirement")
        {
            deviceType = InputDeviceType.DS4;
            conType = ConnectionType.BT;
            outReportBuffer = new byte[78];
            outputReport = new byte[78];
            typeof(DS4Device).GetField("btOutputPayloadLen", Fields)!.SetValue(this, 78);
            send = typeof(DS4Device).GetMethod("sendOutputReport", Fields)!
                .CreateDelegate<Func<bool, bool, bool, bool>>(this);
        }

        internal bool SendOrdinaryEffect() => send(true, true, false);
        protected override bool writeOutput()
        {
            WriteCalls++;
            DuringWrite?.Invoke();
            return true;
        }
        protected override void StopOutputUpdate()
        {
            StopCalls++;
            DuringStop?.Invoke();
        }
        internal void SeedControllerClock()
        {
            typeof(DS4Device).GetField("timeStampInit", Fields)!.SetValue(this, true);
            typeof(DS4Device).GetField("timeStampPrevious", Fields)!.SetValue(this, 123u);
        }
        internal bool ControllerClockWasReset =>
            !(bool)typeof(DS4Device).GetField("timeStampInit", Fields)!.GetValue(this)! &&
            (uint)typeof(DS4Device).GetField("timeStampPrevious", Fields)!.GetValue(this)! == 0;
        internal void EnableAudioLane()
        {
            var state = (DualShock4BluetoothAudioState)typeof(DS4Device)
                .GetField("bluetoothAudioState", Fields)!.GetValue(this)!;
            Assert.IsTrue(state.Update(true, false, 60, 50, 40, null));
            Assert.IsTrue(RegisterDualShock4BluetoothAudioControlLane(this, _ => true,
                _ => { AudioEffectPublications++; return true; }));
        }
        public void Dispose()
        {
            UnregisterDualShock4BluetoothAudioControlLane(this);
            ReadWaitEv.Dispose();
        }
    }
}
