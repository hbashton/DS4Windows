using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ControlServiceStopRemovalTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void FallbackWorkerRemovalReturnsWhileServiceStopWaitsForThatWorker(
        bool stopAndShutdown, bool disconnectBluetoothAtStop)
    {
        using var fixture = new StopFixture(disconnectBluetoothAtStop);
        // Run the actual public Stop wrapper and its actual per-device call.
        // The private sentinel exits after the observed worker wait, before
        // unrelated audio, registry, driver, or application shutdown code.
        Assert.ThrowsException<StopStageCompleteException>(() =>
        {
            if (stopAndShutdown) fixture.Service.StopAndShutDown(immediateUnplug: true);
            else fixture.Service.Stop(showlog: false, immediateUnplug: true);
        });
        fixture.JoinCallback();
        Assert.IsNull(fixture.CallbackFailure);
        Assert.AreEqual(1, fixture.Device.StopCalls);
        Assert.IsTrue(fixture.CallbackReturnedBeforeWorkerWaitExpired,
            "Removal must not need the service lock held by Stop while Stop waits for its worker. " +
            "The bounded 200 ms fake-worker wait expired; a real unbounded join would deadlock. " +
            $"Stop owned the service gate during the wait: {fixture.StopOwnedServiceGate}.");
    }

    [TestMethod]
    public void ProductionDs4AndDualSenseUseDifferentLifecycleRoutes()
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        var ds4 = new DS4Device(hid, "Typed DS4 route regression");
        var dualSense = new DualSenseDevice(hid, "Composite DualSense route regression");
        try
        {
            Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid,
                ds4.WorkerLifecycleSupport);
            Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.UnsupportedDualSenseCompositeWorkers,
                dualSense.WorkerLifecycleSupport);
            var authority = new ControlServiceLegacyHidSlotAuthority(1, _ => { });
            Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
            Assert.IsFalse(authority.TryBindExactSlot(0, dualSense, false, out _,
                out var rejected, out _));
            Assert.AreEqual(ControlServiceLegacyHidSlotFailure.UnsupportedDevice, rejected);
            Assert.IsTrue(authority.TryBindExactSlot(0, ds4, false, out var binding,
                out var accepted, out _), accepted.ToString());
            Assert.AreSame(ds4, binding.Device);
        }
        finally
        {
            ds4.ReadWaitEv.Dispose();
            dualSense.ReadWaitEv.Dispose();
        }
    }

    [TestMethod]
    public void TypedDs4RetirementDetachesItsRemovalCallbackBeforeStoppingWorker()
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        var device = new DS4Device(hid, "Typed DS4 stop regression");
        var workers = new ControlledTypedWorkers();
        int removals = 0, registryRemovals = 0;
        var authority = new ControlServiceLegacyHidSlotAuthority(1, _ => registryRemovals++, workers);
        var raiseRemoval = typeof(DS4Device).GetMethod("RunRemoval",
            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(device);
        try
        {
            Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
            Assert.IsTrue(authority.TryBindExactSlot(0, device, false, out var binding, out _, out _));
            Assert.IsTrue(authority.TrySubscribeLegacyLifecycle(binding,
                (_, _) => removals++, (_, _) => { }, (_, _) => { }, (_, _) => { }, (_, _) => { }, out _));
            Assert.IsTrue(authority.TrySubscribeReport(binding, (_, _) => { }, out _));
            Assert.IsTrue(authority.TryStartAndActivate(binding, out _, out _, out _));
            bool detachedWhenStopEntered = false;
            workers.DuringStop = () =>
            {
                detachedWhenStopEntered = !binding.RemovalSubscribed;
                raiseRemoval();
            };
            Assert.IsTrue(authority.TryBeginRetirement(binding, out _, out _));
            Assert.IsTrue(authority.TryPublishTerminalNeutral(binding, () => { }, 200, out _, out _));
            Assert.IsTrue(authority.TryFinalizeRetirement(binding, 200, out var failure, out _), failure.ToString());
            Assert.IsTrue(detachedWhenStopEntered);
            Assert.AreEqual(1, workers.StopCalls);
            Assert.AreEqual(0, removals,
                "A fresh post-detachment removal must not invoke the service callback during the typed worker join.");
            Assert.AreEqual(1, registryRemovals);
            Assert.AreEqual(ControlServiceLegacyHidSlotState.Removed, binding.State);
        }
        finally { device.ReadWaitEv.Dispose(); }
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void QueuedStaleOrDuplicateRemovalCannotRetireTheReplacementSlot(int callbackCount)
    {
        var gate = new object();
        var retired = new ControlledFallbackDevice(() => Assert.Fail("A stale source must not be stopped again."));
        var replacement = new ControlledFallbackDevice(() => Assert.Fail("A stale callback must not stop its replacement."));
        var service = CreateRemovalService(gate, retired);
        var retirements = new Task[callbackCount];
        try
        {
            lock (gate)
            {
                for (int i = 0; i < callbackCount; i++)
                    retirements[i] = service.QueueLegacyControllerRetirement(retired);
                Assert.IsTrue(retired.IsRemoving,
                    "Revocation must be admitted before queued cleanup can obtain the service gate.");
                // The old source and replacement deliberately have the same MAC.
                // Exact object ownership, not identity text, must decide cleanup.
                service.DS4Controllers[0] = replacement;
            }
            DrainRetirements(Task.WhenAll(retirements));
            Assert.AreSame(replacement, service.DS4Controllers[0]);
            Assert.IsFalse(replacement.IsRemoving);
            Assert.IsFalse(replacement.IsRemoved);
            Assert.AreEqual(0, retired.StopCalls);
            Assert.AreEqual(0, replacement.StopCalls);
        }
        finally
        {
            service.DS4Controllers[0] = null;
            foreach (Task retirement in retirements)
                if (retirement != null) DrainRetirements(retirement);
            retired.ReadWaitEv.Dispose();
            replacement.ReadWaitEv.Dispose();
        }
    }

    [TestMethod]
    public void QueuedRemovalCannotPublishPreparedHotplugBeforeCleanupObtainsServiceGate()
    {
        var gate = new object();
        var device = new ControlledFallbackDevice(() => { });
        var service = CreateRemovalService(gate, device);
        int notifications = 0;
        service.HotplugController += (_, _, _) => notifications++;
        Task retirement = null;
        try
        {
            lock (gate)
            {
                retirement = service.QueueLegacyControllerRetirement(device);
                Assert.IsTrue(device.IsRemoving);
                Assert.AreSame(device, service.DS4Controllers[0]);
                service.PublishPreparedHotplug(device, 0);
                Assert.AreEqual(0, notifications,
                    "An admitted removal must suppress hotplug even while its old slot awaits queued cleanup.");
                service.DS4Controllers[0] = null;
            }
            DrainRetirements(retirement);
        }
        finally
        {
            service.DS4Controllers[0] = null;
            if (retirement != null) DrainRetirements(retirement);
            device.ReadWaitEv.Dispose();
        }
    }

    [TestMethod]
    public void RemovedControllerCannotPublishPreparedHotplugFromAnUnclearedSlot()
    {
        var device = new ControlledFallbackDevice(() => { });
        var service = CreateRemovalService(new object(), device);
        try
        {
            int notifications = 0;
            service.HotplugController += (_, _, _) => notifications++;
            device.IsRemoved = true;
            service.PublishPreparedHotplug(device, 0);
            Assert.AreEqual(0, notifications);
        }
        finally { device.ReadWaitEv.Dispose(); }
    }

    private static ControlService CreateRemovalService(object gate, DS4Device device)
    {
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new[] { device };
        SetServiceField(service, "serviceLifecycleLock", gate);
        SetServiceField(service, "hidHideManagedDevices", new HidHideManagedDeviceRegistry<DS4Device>(false));
        SetServiceField(service, "mouseCallbackRegistry", new ControlServiceMouseCallbackRegistry());
        return service;
    }

    private static void SetServiceField(ControlService service, string name, object value) =>
        typeof(ControlService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, value);

    private static void DrainRetirements(Task retirement) => Assert.IsTrue(retirement.Wait(5_000),
        "The bounded regression must drain all queued retirement work before releasing its fixture.");

    private sealed class StopFixture : IDisposable
    {
        private readonly bool previousDisconnect = Global.DCBTatStop;
        private readonly object lifecycleGate = new();
        private readonly ManualResetEventSlim requestRemoval = new();
        private readonly ManualResetEventSlim callbackEntered = new();
        private readonly ManualResetEventSlim callbackReturned = new();
        private readonly Thread callbackThread;
        private Task retirement;
        internal readonly ControlService Service;
        internal readonly ControlledFallbackDevice Device;
        internal Exception CallbackFailure;
        internal bool StopOwnedServiceGate;
        internal bool CallbackReturnedBeforeWorkerWaitExpired;

        internal StopFixture(bool disconnectBluetoothAtStop)
        {
            Service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            Device = new ControlledFallbackDevice(AtStop);
            Service.DS4Controllers = new DS4Device[] { Device };
            // Exact DS4 devices use the typed route. This intentionally unknown
            // test subtype takes the same fallback route as composite DualSense;
            // its bounded Stop replaces the worker join, not service code.
            Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.UnsupportedUnknownSubtype,
                Device.WorkerLifecycleSupport);
            Set("serviceLifecycleLock", lifecycleGate);
            Set("hidHideManagedDevices", new HidHideManagedDeviceRegistry<DS4Device>(false));
            Set("legacyHidSlotAuthority", new ControlServiceLegacyHidSlotAuthority(1, _ => { }));
            Set("mouseCallbackRegistry", new ControlServiceMouseCallbackRegistry());
            Set("mouseCallbackRetirementWarning", new int[1]);
            // Resume at the already-closed service's controller-stop phase.
            // No discovery, application constructor, sockets or HID access.
            Set("exactTypedStopRetryPending", true);
            callbackThread = new Thread(() =>
            {
                if (!requestRemoval.Wait(5_000)) return;
                try
                {
                    callbackEntered.Set();
                    // This is the exact admission helper called by On_DS4Removal.
                    // Retain its Task so the fixture also drains cold cleanup.
                    retirement = Service.QueueLegacyControllerRetirement(Device);
                }
                catch (Exception error) { CallbackFailure = error; }
                finally { callbackReturned.Set(); }
            }) { IsBackground = true, Name = "Bounded service-stop removal regression" };
            try
            {
                Global.DCBTatStop = disconnectBluetoothAtStop;
                callbackThread.Start();
            }
            catch
            {
                Global.DCBTatStop = previousDisconnect;
                requestRemoval.Dispose();
                callbackEntered.Dispose();
                callbackReturned.Dispose();
                Device.ReadWaitEv.Dispose();
                throw;
            }
        }

        private void AtStop()
        {
            StopOwnedServiceGate = Monitor.IsEntered(lifecycleGate);
            requestRemoval.Set();
            if (!callbackEntered.Wait(5_000))
                throw new TimeoutException("The dedicated fake worker never entered removal.");
            CallbackReturnedBeforeWorkerWaitExpired = callbackReturned.Wait(200);
            // A baseline failure is allowed to unwind the actual service lock.
            // The now-late callback sees no slot and performs no real teardown.
            Service.DS4Controllers[0] = null;
            throw new StopStageCompleteException();
        }

        private void Set(string name, object value) => typeof(ControlService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Service, value);

        internal void JoinCallback()
        {
            Assert.IsTrue(callbackThread.Join(5_000),
                "The bounded regression must not strand its callback thread.");
            if (retirement != null) DrainRetirements(retirement);
        }

        public void Dispose()
        {
            try
            {
                Service.DS4Controllers[0] = null;
                requestRemoval.Set();
                JoinCallback();
                requestRemoval.Dispose();
                callbackEntered.Dispose();
                callbackReturned.Dispose();
                Device.ReadWaitEv.Dispose();
            }
            finally { Global.DCBTatStop = previousDisconnect; }
        }
    }

    private sealed class ControlledFallbackDevice : DS4Device
    {
        private readonly Action duringStop;
        internal int StopCalls;

        internal ControlledFallbackDevice(Action duringStop)
            : base((HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)),
                "Bounded fallback worker regression")
        {
            this.duringStop = duringStop;
            conType = ConnectionType.BT;
            Mac = "02:00:00:00:00:01";
        }

        public override void StopUpdate()
        {
            StopCalls++;
            duringStop();
        }
    }

    private sealed class ControlledTypedWorkers : IControlServiceLegacyHidWorkerLifecycle
    {
        private readonly object issuer = new();
        internal Action DuringStop;
        internal int StopCalls;

        public DS4DeviceWorkerLifecycleSupport Classify(DS4Device device) => device.WorkerLifecycleSupport;

        public bool TryStart(DS4Device device, out DS4DeviceWorkerLifecycleLease lease,
            out DS4DeviceWorkerLifecycleResult result)
        {
            lease = new DS4DeviceWorkerLifecycleLease(issuer, device, 1);
            result = DS4DeviceWorkerLifecycleResult.Success(DS4DeviceWorkerLifecycleOperation.Start);
            return true;
        }

        public bool TryStop(DS4Device device, in DS4DeviceWorkerLifecycleLease lease,
            int timeoutMilliseconds, out DS4DeviceWorkerLifecycleResult result)
        {
            StopCalls++;
            DuringStop?.Invoke();
            result = DS4DeviceWorkerLifecycleResult.Success(DS4DeviceWorkerLifecycleOperation.Stop);
            return true;
        }
    }

    private sealed class StopStageCompleteException : Exception { }
}
