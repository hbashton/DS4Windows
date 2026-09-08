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
    public async Task ToneRequiresAcceptedSameGenerationSetupAndErrorsRemainQueryable()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var access = new FakeAccess();
        bool accepted = false;
        var probe = new Switch2BluetoothLabProbe(folder, access, () => true,
            _ => Task.FromResult(JsonSerializer.Serialize(new { SetupAcknowledged = accepted })), 9);
        try
        {
            Assert.IsTrue((await Send(probe, "tone-opus5")).Contains("Error"));
            Assert.IsTrue((await Send(probe, "active-opus5")).Contains("Error"));
            Assert.IsTrue((await Send(probe, "a:opus:1:20:20:raw")).Contains("Error"));
            Assert.IsTrue((await Send(probe, "t:pcm:2:2.5:0:raw")).Contains("Error"));
            await Send(probe, "configure-audio");
            Assert.IsTrue((await Send(probe, "tone-opus5")).Contains("Error"));
            Assert.AreEqual(0, access.Calls);
            accepted = true;
            await Send(probe, "configure-audio");
            access.Query = (_, _) => throw new InvalidOperationException("Test failure");
            Assert.IsTrue((await Send(probe, "tone-opus5")).Contains("Test failure"));
            Assert.AreEqual(1, access.Calls);
            Assert.IsTrue((await Send(probe, "status")).Contains("active"));
            Assert.IsTrue((await Send(probe, "a:opus:1:20:20:raw")).Contains("Test failure"));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
        }
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

    [TestMethod]
    public async Task SynchronousStateQueryFailureReturnsErrorAndPreservesPipe()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var probe = new Switch2BluetoothLabProbe(folder, new FakeAccess(), () => true,
            _ => Task.FromResult("{}"), 1, _ => throw new InvalidOperationException("state failure"));
        try
        {
            Assert.IsTrue((await Send(probe, "audio-state")).Contains("state failure"));
            Assert.IsTrue((await Send(probe, "status")).Contains("active"));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
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

    [TestMethod]
    public async Task DifferentExternalPacketFormatsReuseTheSameSetupPipeAndService()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var access = new FakeAccess();
        int setups = 0;
        var probe = new Switch2BluetoothLabProbe(folder, access, () => true,
            _ => { setups++; return Task.FromResult("{\"SetupAcknowledged\":true}"); }, 5);
        Switch2BluetoothLabPlan Plan(ulong generation, params byte[] bytes) => new()
        { Id = "opaque-external", Generation = generation, Packets = new[] { new Switch2BluetoothLabPacket { Payload = bytes } } };
        try
        {
            Assert.IsTrue((await SendPlan(probe, Plan(5, 1, 2))).Contains("Error"));
            Assert.AreEqual(0, access.Plans.Count);
            await Send(probe, "configure-audio");
            Assert.IsTrue((await SendPlan(probe, Plan(6, 3, 4))).Contains("Error"));
            Assert.AreEqual(0, access.Plans.Count);
            await SendPlan(probe, Plan(5, 0xde, 0xad, 0xbe, 0xef));
            await SendPlan(probe, Plan(5, 0xf8, 0xff, 0xfe));
            Assert.AreEqual(1, setups);
            Assert.AreEqual(2, access.Plans.Count);
            CollectionAssert.AreEqual(new byte[] { 0xde, 0xad, 0xbe, 0xef }, access.Plans[0].Packets[0].Payload);
            CollectionAssert.AreEqual(new byte[] { 0xf8, 0xff, 0xfe }, access.Plans[1].Packets[0].Payload);
            Assert.IsTrue((await Send(probe, "status")).Contains("active"));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
        }
    }

    [DataTestMethod]
    [DataRow(0, "")]
    [DataRow(1048577, "")]
    [DataRow(1, "{")]
    public async Task InvalidPlanEnvelopeCannotReachGattAndNextStatusStillWorks(int length, string body)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var access = new FakeAccess();
        var probe = new Switch2BluetoothLabProbe(folder, access, () => true,
            _ => Task.FromResult("{\"SetupAcknowledged\":true}"), 5);
        try
        {
            await Send(probe, "configure-audio");
            Assert.IsTrue((await SendPlanEnvelope(probe, length, Encoding.UTF8.GetBytes(body))).Contains("Error"));
            Assert.AreEqual(0, access.Plans.Count);
            Assert.IsTrue((await Send(probe, "status")).Contains("active"));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public async Task StopRetainsAnAdmittedNoncooperativePacketPlan()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-lab-probe-" + Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new FakeAccess { RunPlan = (_, _) => { entered.TrySetResult(); return finish.Task; } };
        var probe = new Switch2BluetoothLabProbe(folder, access, () => true,
            _ => Task.FromResult("{\"SetupAcknowledged\":true}"), 5);
        Task<string> request = null;
        try
        {
            await Send(probe, "configure-audio");
            request = SendPlan(probe, new Switch2BluetoothLabPlan
            {
                Id = "drain", Generation = 5,
                Packets = new[] { new Switch2BluetoothLabPacket { Payload = new byte[] { 0 } } }
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task stopped = probe.StopAsync();
            Assert.IsFalse(stopped.IsCompleted, "Packet-plan ownership must survive cancellation until the actual operation returns.");
            Assert.AreEqual(1, access.Plans.Count);
            finish.TrySetResult("{}");
            await stopped.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            finish.TrySetResult("{}");
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (request != null) try { await request; } catch (IOException) { }
            Directory.Delete(folder, true);
        }
    }

    private static Task<string> SendPlan(Switch2BluetoothLabProbe probe, Switch2BluetoothLabPlan plan)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(plan);
        return SendPlanEnvelope(probe, body.Length, body);
    }

    private static async Task<string> SendPlanEnvelope(Switch2BluetoothLabProbe probe, int envelopeLength, byte[] body)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", probe.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        byte[] length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, envelopeLength);
        await pipe.WriteAsync("run-plan\n"u8.ToArray(), deadline.Token);
        await pipe.WriteAsync(length, deadline.Token);
        await pipe.WriteAsync(body, deadline.Token);
        using var reader = new StreamReader(pipe);
        return await reader.ReadLineAsync(deadline.Token);
    }

    private sealed class FakeAccess : ISwitch2BluetoothLabAudioAccess
    {
        internal int Calls;
        internal readonly List<Switch2BluetoothLabPlan> Plans = new();
        internal Func<string, CancellationToken, Task<string>> Query;
        internal Func<Switch2BluetoothLabPlan, CancellationToken, Task<string>> RunPlan;
        public Task<string> QueryAudioLabAsync(string command, CancellationToken cancellationToken)
        { Calls++; return Query?.Invoke(command, cancellationToken) ?? Task.FromResult("{\"Status\":\"Success\"}"); }
        public Task<string> RunAudioLabPlanAsync(Switch2BluetoothLabPlan plan, CancellationToken cancellationToken)
        { Plans.Add(plan); return RunPlan?.Invoke(plan, cancellationToken) ?? Task.FromResult("{\"Status\":\"Success\"}"); }
    }
}
