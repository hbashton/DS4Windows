using System.IO;
using System.Text.Json;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothLabPlanTests
{
    static Switch2BluetoothLabPlan Plan(params Switch2BluetoothLabPacket[] packets) => new()
    { Id = "external-format", Generation = 5, Packets = packets };
    static Switch2BluetoothLabPacket Packet(int offset = 0, int size = 3) => new()
    { OffsetMicroseconds = offset, Payload = Enumerable.Range(0, size).Select(x => (byte)x).ToArray() };

    [TestMethod]
    public void PlanIsFormatAgnosticBoundedAndGenerationSpecific()
    {
        var first = Plan(Packet(), Packet(0, 240), Packet(5000, 509));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(first);
        var second = Switch2BluetoothLabPlan.Parse(bytes, 5);
        Assert.AreEqual(first.Fingerprint(), second.Fingerprint());
        CollectionAssert.AreEqual(first.Packets[1].Payload, second.Packets[1].Payload);
        Assert.ThrowsException<InvalidDataException>(() => Switch2BluetoothLabPlan.Parse(bytes, 6));
        Assert.ThrowsException<JsonException>(() => Switch2BluetoothLabPlan.Parse("{\"TargetUuid\":\"anything\"}"u8, 5));
        foreach (var invalid in new[] { Plan(), Plan(Packet(1)), Plan(Packet(size: 510)), Plan(Packet(), Packet(1000000)),
            Plan(Packet(), Packet(5000), Packet(4000)), Plan(Packet(size: 0)), Plan(Enumerable.Range(0, 5).Select(_ => Packet()).ToArray()),
            Plan(Enumerable.Range(0, 1025).Select(i => Packet(i * 500)).ToArray()),
            Plan(Enumerable.Range(0, 600).Select(i => Packet(i * 1000, 509)).ToArray()) })
            Assert.ThrowsException<InvalidDataException>(() => invalid.Validate(509));
    }

    [TestMethod]
    public async Task MtuAndPreCancelledPlansCannotWrite()
    {
        int calls = 0;
        Task<bool> Write(byte[] _) { calls++; return Task.FromResult(true); }
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => Switch2BluetoothLabPacketSender.SendAsync(Plan(Packet(size: 50)), 49, Write, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => Switch2BluetoothLabPacketSender.SendAsync(Plan(Packet()), 509, Write, new CancellationToken(true)));
        Assert.AreEqual(0, calls);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task LateWriteRemainsOwnedAndCancellationIsReportedEvenOnFinalPacket(int count)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        int calls = 0;
        var send = Switch2BluetoothLabPacketSender.SendAsync(Plan(Enumerable.Range(0, count).Select(_ => Packet()).ToArray()), 509, _ =>
        { calls++; entered.TrySetResult(); return completed.Task; }, stopping.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopping.Cancel();
        Assert.IsFalse(send.IsCompleted);
        completed.SetResult(true);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => send);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task SenderPreservesOpaquePacketsAndStopsOnRejection()
    {
        var plan = Plan(Packet(0, 1), Packet(0, 23), Packet(5000, 19));
        int calls = 0;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Switch2BluetoothLabPacketSender.SendAsync(plan, 509, bytes =>
        {
            CollectionAssert.AreEqual(plan.Packets[calls].Payload, bytes);
            calls++;
            return Task.FromResult(calls < 2);
        }, CancellationToken.None));
        Assert.AreEqual(2, calls);
    }
}
