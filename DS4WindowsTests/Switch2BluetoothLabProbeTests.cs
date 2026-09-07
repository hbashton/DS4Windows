using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothLabProbeTests
{
    [TestMethod]
    public async Task RepeatedQueriesReuseAccessAndLeaveInputCountersActive()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var access = new FakeAccess();
        int setup = 0;
        var probe = new Switch2BluetoothLabProbe(folder, access, () => true,
            _ => { setup++; return Task.FromResult("{\"SetupAcknowledged\":true}"); }, 51);
        try
        {
            probe.ObserveReport(Stopwatch.GetTimestamp());
            using var first = JsonDocument.Parse(await Send(probe, "status"));
            Assert.AreEqual(1L, first.RootElement.GetProperty("Reports").GetInt64());
            Assert.AreEqual(0, access.Calls, "An idle/status probe must not generate radio traffic.");
            await Send(probe, "inventory");
            await Send(probe, "headset-header");
            probe.ObserveReport(Stopwatch.GetTimestamp());
            using var second = JsonDocument.Parse(await Send(probe, "status"));
            Assert.AreEqual(2L, second.RootElement.GetProperty("Reports").GetInt64());
            Assert.AreEqual(51UL, second.RootElement.GetProperty("TransportGeneration").GetUInt64());
            Assert.AreEqual(2, access.Calls);
            await Send(probe, "configure-audio");
            Assert.AreEqual(1, setup);
            Assert.IsTrue((await Send(probe, "write-anything")).Contains("Error"));
            // An overlong caller may still be writing when the server rejects
            // it. Either an error reply or a closed pipe is a valid rejection.
            try { Assert.IsTrue((await Send(probe, new string('x', 40)))?.Contains("Error") != false); }
            catch (IOException) { }
            Assert.AreEqual(2, access.Calls);
            await Send(probe, "stop-probe");
            await probe.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public async Task StopRetainsAnAdmittedNoncooperativeGattOperation()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new FakeAccess { Query = (_, _) => { entered.TrySetResult(); return finish.Task; } };
        var probe = new Switch2BluetoothLabProbe(folder, access, () => true, _ => Task.FromResult("{}"), 1);
        Task<string> request = Send(probe, "inventory");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task stopped = probe.StopAsync();
            Assert.IsFalse(stopped.IsCompleted, "Service ownership must survive a cancelled waiter.");
            finish.TrySetResult("{}");
            await stopped.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, access.Calls);
        }
        finally
        {
            finish.TrySetResult("{}");
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            try { await request; } catch { }
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void SetupIsExactAndNoArbitraryOperationsAreAllowed()
    {
        CollectionAssert.AreEqual(Convert.FromHexString("179101020007000080BB000002F000"),
            Switch2BluetoothLabAudioProtocol.CreateSetupRequest());
        Assert.IsTrue(Switch2BluetoothLabAudioProtocol.IsSetupAcknowledged(Convert.FromHexString("1701010210780000")));
        Assert.IsFalse(Switch2BluetoothLabAudioProtocol.IsSetupAcknowledged(Convert.FromHexString("1702010210780000")));
        Assert.IsFalse(Switch2BluetoothLabAudioProtocol.IsSetupAcknowledged(new byte[7]));
        foreach (string invalid in new[] { "", "pair", "read-memory", "write", "inventory ", "STATUS", "firmware" })
            Assert.IsFalse(Switch2BluetoothLabAudioProtocol.IsAllowed(invalid));
    }

    [TestMethod]
    public async Task ReportObservationAllocatesNothing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var probe = new Switch2BluetoothLabProbe(folder, new FakeAccess(), () => true, _ => Task.FromResult("{}"), 1);
        try
        {
            for (int i = 0; i < 100; i++) probe.ObserveReport(i);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) probe.ObserveReport(i);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static async Task<string> Send(Switch2BluetoothLabProbe probe, string command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", probe.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), deadline.Token);
        using var reader = new StreamReader(pipe);
        return await reader.ReadLineAsync(deadline.Token);
    }

    private sealed class FakeAccess : ISwitch2BluetoothLabAudioAccess
    {
        internal int Calls;
        internal Func<string, CancellationToken, Task<string>> Query;
        public Task<string> QueryAudioLabAsync(string command, CancellationToken cancellationToken)
        { Calls++; return Query?.Invoke(command, cancellationToken) ?? Task.FromResult("{\"Status\":\"Success\"}"); }
    }
}
