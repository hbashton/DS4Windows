using System.IO;
using System.Text.Json;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

public partial class Switch2BluetoothLabProbeTests
{
    [TestMethod]
    public async Task ReceiverPlansUseSameGenerationAndAcceptedSetupWithoutRepeatingIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-receiver-probe-" + Guid.NewGuid().ToString("N"));
        int calls = 0;
        bool fail = false;
        var probe = new Switch2BluetoothLabProbe(folder, new FakeAccess(), () => true,
            _ => Task.FromResult("{\"SetupAcknowledged\":true}"), 51, runReceiver: (plan, _) =>
            {
                calls++;
                return Task.FromResult(fail ? "{\"Error\":\"rejected\",\"CommandsAcknowledged\":false}" :
                    JsonSerializer.Serialize(new { CommandsAcknowledged = true,
                        SetupAcknowledged = plan.Commands.Any(request => request[0] == 0x17),
                        Receiver = new { CommandsAcknowledged = true,
                            SetupAcknowledged = plan.Commands.Any(request => request[0] == 0x17) },
                        Audio = plan.Audio == null ? (object)null : new { Sent = plan.Audio.Packets.Length } }));
            });
        try
        {
            using var status = JsonDocument.Parse(await Send(probe, "status"));
            Assert.AreEqual(1, status.RootElement.GetProperty("ReceiverPlanProtocol").GetInt32());
            Assert.IsTrue((await SendReceiver(probe, ReceiverPlan(51, audio: true))).Contains("Error"));
            Assert.AreEqual(0, calls, "No receiver commands before audio setup admission.");
            Assert.IsTrue((await SendReceiver(probe, ReceiverPlan(52))).Contains("Error"));
            Assert.AreEqual(0, calls, "A different generation cannot issue receiver commands.");
            Assert.IsFalse((await SendReceiver(probe, ReceiverPlan(51, setup: true, audio: true))).Contains("Error"));
            Assert.IsFalse((await SendReceiver(probe, ReceiverPlan(51, audio: true))).Contains("Error"));
            Assert.AreEqual(2, calls, "Receiver intervention can reuse accepted setup without reconfiguring.");
            fail = true;
            Assert.IsTrue((await SendReceiver(probe, ReceiverPlan(51))).Contains("Error"));
            Assert.IsTrue((await Send(probe, "tone-opus5")).Contains("Error"));
            Assert.IsTrue((await Send(probe, "status")).Contains("active"));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public async Task ReceiverWindowFailureCannotRetainSetupCapability()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-receiver-probe-" + Guid.NewGuid().ToString("N"));
        var probe = new Switch2BluetoothLabProbe(folder, new FakeAccess(), () => true,
            _ => Task.FromResult("{\"SetupAcknowledged\":true}"), 1, runReceiver: (_, _) =>
                Task.FromResult("{\"Status\":\"Success\",\"LabAudioFenced\":true,\"Receiver\":{\"CommandsAcknowledged\":true,\"SetupAcknowledged\":true}}"));
        try
        {
            await Send(probe, "configure-audio");
            Assert.IsTrue((await SendReceiver(probe, ReceiverPlan(1, setup: true))).Contains("Error"));
            Assert.IsTrue((await Send(probe, "tone-opus5")).Contains("Error"));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public async Task ReceiverOperationIsRetainedUntilItsNotificationWindowAndWriteDrainFinish()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-receiver-probe-" + Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Switch2BluetoothLabProbe(folder, new FakeAccess(), () => true,
            _ => Task.FromResult("{}"), 1, runReceiver: (_, _) => { entered.SetResult(); return release.Task; });
        Task<string> request = SendReceiver(probe, ReceiverPlan(1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsFalse(probe.StopAsync().IsCompleted);
        }
        finally
        {
            release.TrySetResult("{\"CommandsAcknowledged\":true,\"SetupAcknowledged\":false}");
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            try { await request; } catch (IOException) { }
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public async Task ReceiverNestedAudioFailureInvalidatesPreviouslyAcknowledgedSetup()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ds4w-receiver-probe-" + Guid.NewGuid().ToString("N"));
        var probe = new Switch2BluetoothLabProbe(folder, new FakeAccess(), () => true,
            _ => Task.FromResult("{\"SetupAcknowledged\":true}"), 1, runReceiver: (_, _) =>
                Task.FromResult("{\"CommandsAcknowledged\":true,\"SetupAcknowledged\":false,\"Receiver\":{\"CommandsAcknowledged\":true,\"SetupAcknowledged\":false},\"Audio\":{\"Sent\":1,\"Error\":\"write rejected\"}}"));
        try
        {
            await Send(probe, "configure-audio");
            Assert.IsTrue((await SendReceiver(probe, ReceiverPlan(1, audio: true))).Contains("Error"));
            Assert.IsTrue((await Send(probe, "tone-opus5")).Contains("Error"));
        }
        finally
        {
            await probe.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(folder, true);
        }
    }

    private static Switch2BluetoothLabReceiverPlan ReceiverPlan(ulong generation, bool setup = false, bool audio = false) => new()
    {
        Id = "receiver-test", Generation = generation,
        Commands = new[] { Convert.FromHexString(setup ? "179101020007000080BB000002F000" : "1891010100000000") },
        Audio = audio ? new Switch2BluetoothLabPlan { Id = "synthetic", Generation = generation,
            Packets = new[] { new Switch2BluetoothLabPacket { Payload = new byte[] { 0 } } } } : null
    };

    private static Task<string> SendReceiver(Switch2BluetoothLabProbe probe, Switch2BluetoothLabReceiverPlan plan)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        return SendPlanEnvelope(probe, bytes.Length, bytes, "run-receiver-plan");
    }
}
