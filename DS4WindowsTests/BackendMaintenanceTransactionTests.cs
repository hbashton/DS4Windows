using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DS4Windows;
using DS4Windows.DS4Control;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class BackendMaintenanceTransactionTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepairDrainsBeforeMutationAndRestoresOnlyOriginallyRunningService(bool running)
    {
        var transaction = new BackendMaintenanceTransaction();
        var gate = new object();
        var order = new List<string>();
        void Record(string phase)
        {
            Assert.IsTrue(Monitor.IsEntered(gate));
            Assert.IsTrue(transaction.IsActive);
            Assert.IsTrue(transaction.RejectControllerStart);
            order.Add(phase);
        }
        transaction.Execute(gate, () => { Record("state"); return running; },
            () => { Record("stop"); return true; }, () => Record("repair/verify"),
            () => { Record("start"); return true; });
        CollectionAssert.AreEqual(running
            ? new[] { "state", "stop", "repair/verify", "start" }
            : new[] { "state", "stop", "repair/verify" }, order);
        Assert.IsFalse(transaction.IsActive);
        Assert.IsFalse(transaction.RejectControllerStart);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RejectedDrainNeverChangesBrokerOrStartsEvenWhenRunningBecameFalse(bool running)
    {
        var transaction = new BackendMaintenanceTransaction();
        Assert.ThrowsException<InvalidOperationException>(() => transaction.Execute(new object(),
            () => running, () => false, () => Assert.Fail("Mutation after rejected drain"),
            () => { Assert.Fail("Start after rejected drain"); return true; }));
        Assert.IsFalse(transaction.IsActive);
        Assert.IsFalse(transaction.RejectControllerStart, "The broker was never retired.");
        transaction.Execute(new object(), () => false, () => true, () => { },
            () => { Assert.Fail("Rejected drain must not invent a retained resume request."); return true; });
    }

    [TestMethod]
    public void DrainExceptionRemainsOriginalAndDoesNotInvokeMutation()
    {
        var transaction = new BackendMaintenanceTransaction();
        var expected = new IOException("drain sentinel");
        var actual = Assert.ThrowsException<IOException>(() => transaction.Execute(new object(),
            () => true, () => throw expected, () => Assert.Fail(), () => { Assert.Fail(); return true; }));
        Assert.AreSame(expected, actual);
        Assert.IsFalse(transaction.RejectControllerStart);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedOrCanceledRepairLeavesStartBlockedAndPreservesFailure(bool canceled)
    {
        var transaction = new BackendMaintenanceTransaction();
        Exception expected = canceled ? new OperationCanceledException() : new IOException("repair sentinel");
        Exception actual = null;
        try
        {
            transaction.Execute(new object(), () => true, () => true, () => throw expected,
                () => { Assert.Fail("No start on an uncertain repair"); return true; });
        }
        catch (Exception error) { actual = error; }
        Assert.AreSame(expected, actual);
        Assert.IsFalse(transaction.IsActive);
        Assert.IsTrue(transaction.RejectControllerStart);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedRestartRetainsFence(bool throws)
    {
        var transaction = new BackendMaintenanceTransaction();
        Assert.ThrowsException<InvalidOperationException>(() => transaction.Execute(new object(),
            () => true, () => true, () => { }, () => throws
                ? throw new InvalidOperationException("restart sentinel") : false));
        Assert.IsFalse(transaction.IsActive);
        Assert.IsTrue(transaction.RejectControllerStart);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OnlySuccessfulRetryClearsFailureFenceAndRestoresOriginalRunIntent(bool originallyRunning)
    {
        var transaction = new BackendMaintenanceTransaction();
        var gate = new object();
        Assert.ThrowsException<IOException>(() => transaction.Execute(gate,
            () => originallyRunning, () => true, () => throw new IOException(), () => true));
        Assert.ThrowsException<InvalidOperationException>(() => transaction.Execute(gate,
            () => false, () => false, () => Assert.Fail(), () => true));
        Assert.IsTrue(transaction.RejectControllerStart);
        int restarts = 0;
        transaction.Execute(gate, () => false, () => true, () => { },
            () => { restarts++; return true; });
        Assert.AreEqual(originallyRunning ? 1 : 0, restarts,
            "A failed repair must not erase the original running intent or invent it for a stopped app.");
        Assert.IsFalse(transaction.RejectControllerStart);
        transaction.Execute(gate, () => false, () => true, () => { },
            () => { Assert.Fail("Completed success must clear the old resume request."); return true; });
    }

    [TestMethod]
    public void NestedRepairCannotMutateInsideAnExistingTransaction()
    {
        var transaction = new BackendMaintenanceTransaction();
        var gate = new object();
        transaction.Execute(gate, () => false, () => true, () =>
            Assert.ThrowsException<InvalidOperationException>(() => transaction.Execute(gate,
                () => false, () => { Assert.Fail(); return true; }, () => Assert.Fail(), () => true)), () => true);
        Assert.IsFalse(transaction.RejectControllerStart);
    }

    [TestMethod]
    public void ActualStartAndHotplugRejectDuringMutationAndAfterFailedRepair()
    {
        var transaction = new BackendMaintenanceTransaction();
        var gate = new object();
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        Set(service, "backendMaintenance", transaction);
        Set(service, "serviceLifecycleLock", gate);
        service.running = true; // Start otherwise succeeds without entering real device startup.
        using var inMutation = new ManualResetEventSlim();
        using var finishMutation = new ManualResetEventSlim();
        Task repair = Task.Run(() => Assert.ThrowsException<IOException>(() =>
            transaction.Execute(gate, () => true, () => true, () =>
            {
                inMutation.Set();
                Assert.IsTrue(finishMutation.Wait(5000));
                throw new IOException("expected repair failure");
            }, () => { Assert.Fail(); return true; })));
        try
        {
            Assert.IsTrue(inMutation.Wait(5000));
            Assert.IsTrue(service.BackendMaintenanceActive);
            Task<bool> start = Task.Run(() => service.Start());
            Task<bool> hotplug = Task.Run(() => service.HotPlug());
            Assert.IsTrue(Task.WaitAll(new Task[] { start, hotplug }, 5000),
                "Rejected requests must not wait for file/process maintenance.");
            Assert.IsFalse(start.Result);
            Assert.IsFalse(hotplug.Result);
        }
        finally { finishMutation.Set(); Assert.IsTrue(repair.Wait(5000)); }
        Assert.IsFalse(service.BackendMaintenanceActive);
        Assert.IsTrue(service.BackendMaintenanceRequiresRepair);
        Assert.IsFalse(service.Start());
        Assert.IsFalse(service.HotPlug());
        transaction.Execute(gate, () => false, () => true, () => { }, () => true);
        Assert.IsFalse(service.BackendMaintenanceRequiresRepair);
        Assert.IsTrue(service.Start(), "A successful repair clears the actual service start fence.");
    }

    [TestMethod]
    public void RevokedDsxListenerDrainsAndReleasesItsPortBeforeRepairAndRestart()
    {
        var transaction = new BackendMaintenanceTransaction();
        var gate = new object();
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        Set(service, "backendMaintenance", transaction);
        Set(service, "serviceLifecycleLock", gate);
        service.running = true;
        using var listener = new DSXUdpServer();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var callbackEntered = new ManualResetEventSlim();
        using var callbackMayReturn = new ManualResetEventSlim();
        using var callbackReturned = new ManualResetEventSlim();
        Exception callbackFailure = null;
        listener.OnRGBUpdate += (_, _, _, _, _) =>
        {
            callbackEntered.Set();
            try
            {
                Assert.IsTrue(callbackMayReturn.Wait(5000));
                Assert.IsFalse(service.Start());
                Assert.IsFalse(service.HotPlug());
            }
            catch (Exception error) { callbackFailure = error; }
            finally { callbackReturned.Set(); }
        };
        Assert.IsTrue(listener.StartForTesting());
        int port = listener.Port;
        byte[] packet = Encoding.UTF8.GetBytes("{\"instructions\":[{\"type\":2,\"parameters\":[0,1,2,3,255]}]}");
        client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
        try
        {
            Assert.IsTrue(callbackEntered.Wait(5000));
            transaction.Execute(gate, () => true, () =>
            {
                callbackMayReturn.Set();
                listener.Dispose();
                Assert.IsTrue(callbackReturned.IsSet, "Dispose must drain the admitted callback before mutation.");
                Assert.IsNull(callbackFailure);
                return true;
            }, () => Assert.IsFalse(listener.IsRunning), () =>
            {
                using var replacement = new DSXUdpServer();
                Assert.IsTrue(replacement.Start(port, "127.0.0.1"),
                    "The previous DSX socket must be closed before the service creates its replacement.");
                return true;
            });
        }
        finally { callbackMayReturn.Set(); }
        Assert.IsFalse(transaction.RejectControllerStart);
    }

    private static void Set(ControlService service, string name, object value) =>
        typeof(ControlService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, value);
}
