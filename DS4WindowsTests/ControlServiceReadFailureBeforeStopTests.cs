using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ControlServiceReadFailureBeforeStopTests
{
    [TestMethod]
    public void Unexpected995IsLoggedBeforeStopAndPendingRemovalCannotDeadlockStop()
    {
        using var fixture = new FailureBeforeStopFixture();
        using var backend = new UnexpectedReadFailureBackend();
        using (var reader = new PipelinedInputReportReader(
            new[] { new byte[64], new byte[64] }, backend))
        {
            HidDevice.ReadStatus status = reader.ReadNext(out _, out int error, out _, out _);
            Assert.AreEqual(HidDevice.ReadStatus.ReadError, status);
            Assert.AreEqual(995, error);
            Assert.IsFalse(fixture.Device.IsExpectedAdministrativeInputReadCancellation(status, error),
                "The initial read failure must precede, not originate from, administrative Stop.");
            fixture.ReportPhysicalReadFailure(error);
        }

        Assert.IsTrue(fixture.RemovalPending.Wait(5_000));
        Assert.AreEqual(1, fixture.FailureLogCount,
            "The real DualSense lifecycle logger must report the failure before Stop is pressed.");
        Assert.AreEqual(0, fixture.StopEnteredOrder);
        Assert.AreEqual(1, backend.WaitCalls);
        Assert.IsTrue(backend.Disposed);

        bool stoppedWithoutIntervention = fixture.PressStopAndDrain();
        Assert.IsTrue(stoppedWithoutIntervention,
            "Read failure 995 was already logged before Stop. Stop then held the service gate " +
            "while waiting for the real DualSense lifecycle owner, whose removal handler could " +
            "not return. The bounded fixture had to interrupt Stop to break that lock cycle.");
        Assert.IsNull(fixture.WorkerFailure);
        Assert.IsNull(fixture.StopFailure);
        Assert.IsTrue(fixture.StopOwnedServiceGate);
        Assert.IsTrue(fixture.FailureLoggedOrder < fixture.RemovalPendingOrder);
        Assert.IsTrue(fixture.RemovalPendingOrder < fixture.StopEnteredOrder);
        Assert.AreEqual(1, fixture.RemovalCount);
        Assert.AreEqual(1, fixture.RemovalReturnedCount);
        Assert.AreEqual(1, fixture.Device.StopCalls);
        Assert.AreEqual(1, fixture.FinalizationCount);
        Assert.IsTrue(fixture.LifecycleCompleted.WaitOne(0));
        Assert.IsFalse(fixture.HidHideRegistry.HasConnections,
            "Cold service removal must also retire its synthetic, empty HidHide binding.");
    }

    private sealed class UnexpectedReadFailureBackend : PipelinedInputReportReader.IReadBackend
    {
        internal int WaitCalls;
        internal bool Disposed;
        public bool TrySubmit(int bufferIndex, out bool completedSynchronously, out int winError)
        { completedSynchronously = false; winError = 0; return true; }
        public HidDevice.ReadStatus WaitForCompletion(uint timeout, out int winError)
        { WaitCalls++; winError = 995; return HidDevice.ReadStatus.ReadError; }
        public void CancelAndDrain() => Assert.Fail("An already-completed error needs no cancellation.");
        public void Dispose() => Disposed = true;
    }

    private sealed class FailureBeforeStopFixture : IDisposable
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly bool previousDisconnect = Global.DCBTatStop;
        private readonly object serviceGate = new();
        private readonly ManualResetEventSlim releaseRemoval = new();
        private readonly Thread lifecycleThread;
        private Thread stopThread;
        private int order;
        internal readonly ControlService Service;
        internal readonly ControlledDualSenseDevice Device;
        internal readonly HidHideManagedDeviceRegistry<DS4Device> HidHideRegistry = new();
        internal readonly ManualResetEventSlim RemovalPending = new();
        internal readonly ManualResetEvent LifecycleCompleted;
        internal Exception WorkerFailure;
        internal Exception StopFailure;
        internal int FailureLogCount;
        internal int FailureLoggedOrder;
        internal int RemovalPendingOrder;
        internal int StopEnteredOrder;
        internal int RemovalCount;
        internal int RemovalReturnedCount;
        internal int FinalizationCount;
        internal bool StopOwnedServiceGate;

        internal FailureBeforeStopFixture()
        {
            Service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            Device = new ControlledDualSenseDevice(BeforeDeviceStop, () => Service.DS4Controllers[0] = null);
            Service.DS4Controllers = new DS4Device[] { Device };
            SetService("serviceLifecycleLock", serviceGate);
            SetService("hidHideManagedDevices", HidHideRegistry);
            SetService("legacyHidSlotAuthority", new ControlServiceLegacyHidSlotAuthority(1, _ => { }));
            SetService("mouseCallbackRegistry", new ControlServiceMouseCallbackRegistry());
            SetService("mouseCallbackRetirementWarning", new int[1]);
            // Enter the real public Stop wrapper at its already-closed retry phase.
            // Discovery, sockets, virtual devices and application shutdown are outside this regression.
            SetService("exactTypedStopRetryPending", true);
            HidHideRegistry.BeginConnection(Device, Array.Empty<string>());
            // The test subclass and production composite DualSense both take the fallback route.
            Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.UnsupportedUnknownSubtype,
                Device.WorkerLifecycleSupport);

            Device.PhysicalOutputFinalizeTestHook = () => Interlocked.Increment(ref FinalizationCount);
            LifecycleCompleted = (ManualResetEvent)typeof(DualSenseDevice)
                .GetField("physicalLifecycleCompleted", Hidden)!.GetValue(Device)!;
            LifecycleCompleted.Reset();
            var lifecycle = typeof(DualSenseDevice).GetMethod("PhysicalLifecycleLoop", Hidden)!
                .CreateDelegate<Action>(Device);
            lifecycleThread = new Thread(() =>
            {
                try { lifecycle(); }
                catch (Exception error) { WorkerFailure = error; }
            }) { IsBackground = true, Name = "Read-failure-before-Stop lifecycle regression" };
            typeof(DualSenseDevice).GetField("physicalLifecycleThread", Hidden)!.SetValue(Device, lifecycleThread);

            // This gate is between the real failure logger and the real service removal handler.
            // It fixes the schedule, not the service's locking or the device's lifecycle algorithm.
            Device.Removal += (_, _) =>
            {
                Interlocked.Increment(ref RemovalCount);
                RemovalPendingOrder = Interlocked.Increment(ref order);
                RemovalPending.Set();
                if (!releaseRemoval.Wait(5_000)) throw new TimeoutException("Stop never released pending removal.");
            };
            Device.Removal += typeof(ControlService).GetMethod("On_DS4Removal", Hidden)!
                .CreateDelegate<EventHandler<EventArgs>>(Service);
            Device.Removal += (_, _) => Interlocked.Increment(ref RemovalReturnedCount);
            AppLogger.GuiLog += ObserveFailureLog;
            Global.DCBTatStop = false;
            lifecycleThread.Start();
        }

        internal void ReportPhysicalReadFailure(int error)
        {
            Type failureKind = typeof(DualSenseDevice).GetNestedType("PhysicalInputFailureKind", Hidden)!;
            typeof(DualSenseDevice).GetMethod("RequestPhysicalRemoval", Hidden, null,
                new[] { failureKind, typeof(int) }, null)!.Invoke(Device,
                new[] { Enum.Parse(failureKind, "BluetoothRead"), (object)error });
        }

        private void ObserveFailureLog(object sender, DebugEventArgs args)
        {
            if (args.Data != Device.getMacAddress() + " disconnected due to read failure: 995") return;
            Interlocked.Increment(ref FailureLogCount);
            FailureLoggedOrder = Interlocked.Increment(ref order);
        }

        internal bool PressStopAndDrain()
        {
            stopThread = new Thread(() =>
            {
                StopEnteredOrder = Interlocked.Increment(ref order);
                try { Service.Stop(showlog: false, immediateUnplug: true); }
                catch (StopStageCompleteException) { }
                catch (Exception error) { StopFailure = error; }
            }) { IsBackground = true, Name = "Read-failure-before-Stop service regression" };
            stopThread.Start();
            bool stopped = stopThread.Join(2_000);
            if (!stopped)
            {
                // Only interrupt the fixture-owned Stop thread. This releases the original
                // synchronous handler's gate so the failing baseline leaves no stranded worker.
                stopThread.Interrupt();
            }
            Assert.IsTrue(stopThread.Join(5_000));
            DrainLifecycleAndColdRemoval();
            return stopped;
        }

        private void BeforeDeviceStop()
        {
            StopOwnedServiceGate = Monitor.IsEntered(serviceGate);
            releaseRemoval.Set();
        }

        private void DrainLifecycleAndColdRemoval()
        {
            Assert.IsTrue(lifecycleThread.Join(5_000), "The real lifecycle owner must not survive the fixture.");
            // On_DS4Removal deliberately hides its Task. The empty binding is an observable
            // cold-cleanup witness: ReleaseHidHideManagedDevice disconnects it at the end,
            // then returns immediately because it contains no IDs and cannot perform IOCTLs.
            Assert.IsTrue(SpinWait.SpinUntil(() => !HidHideRegistry.HasConnections, 5_000),
                "The actual service removal worker did not reach its final no-I/O cleanup boundary.");
        }

        private void SetService(string name, object value) => typeof(ControlService)
            .GetField(name, Hidden)!.SetValue(Service, value);

        public void Dispose()
        {
            try
            {
                Service.DS4Controllers[0] = null;
                releaseRemoval.Set();
                if (stopThread?.IsAlive == true)
                {
                    stopThread.Interrupt();
                    Assert.IsTrue(stopThread.Join(5_000));
                }
                // Ensure constructor/test assertion failures can also release the lifecycle owner.
                if (!RemovalPending.IsSet) ReportPhysicalReadFailure(995);
                DrainLifecycleAndColdRemoval();
                Device.ReadWaitEv.Dispose();
                releaseRemoval.Dispose();
                RemovalPending.Dispose();
            }
            finally
            {
                AppLogger.GuiLog -= ObserveFailureLog;
                Global.DCBTatStop = previousDisconnect;
            }
        }
    }

    private sealed class ControlledDualSenseDevice : DualSenseDevice
    {
        private readonly Action beforeStop;
        private readonly Action releaseSlot;
        internal int StopCalls;
        internal ControlledDualSenseDevice(Action beforeStop, Action releaseSlot)
            : base((HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)),
                "Synthetic unexpected read failure chronology")
        {
            this.beforeStop = beforeStop;
            this.releaseSlot = releaseSlot;
            conType = ConnectionType.BT;
            Mac = "02:00:00:00:00:95";
        }

        public override void StopUpdate()
        {
            StopCalls++;
            beforeStop();
            try { base.StopUpdate(); } // Actual DualSense lifecycle completion wait, not a fake join.
            finally { releaseSlot(); }
            throw new StopStageCompleteException(); // Stop before unrelated application/native teardown.
        }
    }

    private sealed class StopStageCompleteException : Exception { }
}
