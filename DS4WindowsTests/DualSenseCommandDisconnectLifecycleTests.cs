using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public class DualSenseCommandDisconnectLifecycleTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, true)]
    public void QueuedDisconnectRetiresCommandOwnerBeforeRadioAndExternalStopCompletes(
        bool callRemoval, bool coalesceRemoval, bool failDisconnect)
    {
        HidDevice hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        DualSenseDevice device = new(hid, "queued disconnect lifecycle fixture");
        typeof(DS4Device).GetField("conType", Flags).SetValue(device, ConnectionType.BT);
        using ManualResetEvent outputEntered = new(false);
        using ManualResetEvent releaseOutput = new(false);
        using ManualResetEvent commandReturned = new(false);
        using ManualResetEvent externalStopEntered = new(false);
        using ManualResetEvent externalStopReturned = new(false);
        using ManualResetEvent radioEntered = new(false);
        using ManualResetEvent releaseRadio = new(false);
        int finalizations = 0, radioCalls = 0, reentrantStops = 0;
        bool? observedRemoval = null;
        Thread radioThread = null, commandOwner = null, outputOwner = null, lifecycleOwner = null;
        bool commandAliveAtRadio = true, outputAliveAtRadio = true;
        Exception commandFailure = null, externalFailure = null;
        device.PhysicalOutputWriteTestHook = () => { outputEntered.Set(); releaseOutput.WaitOne(); };
        device.PhysicalOutputFinalizeTestHook = () => Interlocked.Increment(ref finalizations);
        device.PhysicalBluetoothDisconnectTestHook = remove =>
        {
            observedRemoval = remove;
            radioThread = Thread.CurrentThread;
            commandAliveAtRadio = commandOwner.IsAlive;
            outputAliveAtRadio = outputOwner.IsAlive;
            // Removal subscribers run synchronously on this final owner and
            // may request Stop again. That callback must never self-wait.
            Invoke(device, "StopOutputUpdate");
            Interlocked.Increment(ref reentrantStops);
            Interlocked.Increment(ref radioCalls);
            radioEntered.Set();
            releaseRadio.WaitOne();
            if (failDisconnect) throw new InvalidOperationException("Synthetic final radio/removal failure");
            return true;
        };
        Thread stop = new(() =>
        {
            try { externalStopEntered.Set(); Invoke(device, "StopOutputUpdate"); }
            catch (Exception error) { externalFailure = error; }
            finally { externalStopReturned.Set(); }
        }) { IsBackground = true };
        bool returnedBeforeOutputReleased = false, radioBeforeOutputReleased = false;
        bool stoppedBeforeOutputReleased = false, stoppedBeforeRadioReleased = false;
        bool reachedRadio = false, finishedStop = false;
        try
        {
            Invoke(device, "StartPhysicalWorkers");
            commandOwner = Field<Thread>(device, "deviceCommandThread");
            outputOwner = Field<Thread>(device, "physicalOutputThread");
            lifecycleOwner = Field<Thread>(device, "physicalLifecycleThread");
            Assert.IsTrue(outputEntered.WaitOne(2000), "The real output owner never reached its write boundary.");
            device.queueEvent(() =>
            {
                try
                {
                    Assert.IsTrue(device.DisconnectBT(callRemoval));
                    if (coalesceRemoval) Assert.IsTrue(device.DisconnectBT(true));
                }
                catch (Exception error) { commandFailure = error; }
                finally { commandReturned.Set(); }
            });
            Invoke(device, "DrainQueuedInputEvents");
            returnedBeforeOutputReleased = commandReturned.WaitOne(1000);
            stop.Start();
            Assert.IsTrue(externalStopEntered.WaitOne(1000));
            stoppedBeforeOutputReleased = externalStopReturned.WaitOne(50);
            radioBeforeOutputReleased = radioEntered.WaitOne(0);
            releaseOutput.Set();
            reachedRadio = radioEntered.WaitOne(1000);
            stoppedBeforeRadioReleased = externalStopReturned.WaitOne(50);
            releaseRadio.Set();
            finishedStop = stop.Join(2000);
        }
        finally
        {
            // On the known RED deadlock, release the test-only completion
            // gate so the blocked command can retire; no radio I/O exists.
            releaseOutput.Set(); releaseRadio.Set();
            if (!finishedStop)
            {
                // RED code next joins the lifecycle after this event, so
                // remove only the fixture's stale waiter reference first.
                typeof(DualSenseDevice).GetField("physicalLifecycleThread", Flags).SetValue(device, null);
                Field<ManualResetEvent>(device, "physicalLifecycleCompleted").Set();
            }
            if ((stop.ThreadState & ThreadState.Unstarted) == 0) stop.Join(2000);
            commandOwner?.Join(2000);
            lifecycleOwner?.Join(2000);
            outputOwner?.Join(2000);
        }
        Assert.IsTrue(returnedBeforeOutputReleased,
            "The queued disconnect waited for the lifecycle owner that must join this command thread (captured Stop deadlock).");
        Assert.IsFalse(radioBeforeOutputReleased, "Radio disconnected before the physical writer retired.");
        Assert.IsFalse(stoppedBeforeOutputReleased, "External Stop skipped physical writer retirement.");
        Assert.IsTrue(reachedRadio, "The lifecycle never executed the admitted disconnect.");
        Assert.IsFalse(stoppedBeforeRadioReleased, "External Stop returned before the admitted lifecycle disconnect completed.");
        Assert.IsTrue(finishedStop, "External Stop did not complete after lifecycle work was released.");
        Assert.IsNull(commandFailure); Assert.IsNull(externalFailure);
        Assert.AreSame(lifecycleOwner, radioThread, "The lifecycle owner must execute radio I/O after worker retirement.");
        Assert.IsFalse(commandAliveAtRadio); Assert.IsFalse(outputAliveAtRadio);
        Assert.AreEqual(callRemoval || coalesceRemoval, observedRemoval);
        Assert.AreEqual(1, radioCalls); Assert.AreEqual(1, finalizations);
        Assert.AreEqual(1, reentrantStops);
        Assert.IsFalse(commandOwner.IsAlive); Assert.IsFalse(lifecycleOwner.IsAlive);
    }

    private static object Invoke(object target, string method, params object[] args) =>
        typeof(DualSenseDevice).GetMethod(method, Flags).Invoke(target, args);
    private static T Field<T>(object target, string name) =>
        (T)typeof(DualSenseDevice).GetField(name, Flags).GetValue(target);
}
