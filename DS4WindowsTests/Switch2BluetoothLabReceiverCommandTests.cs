using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothLabReceiverPlanTests
{
    [TestMethod]
    public void ReceiverPlanRoundTripsObservedCommandsAndRejectsUnknownFieldsAndGeneration()
    {
        var plan = Plan("0C910102000400002F000000", "0C910104000400002F000000",
            "179101020007000080BB000002F000", "1891010100000000", "189101030001000007");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(plan);
        var parsed = Switch2BluetoothLabReceiverPlan.Parse(json, 5);
        Assert.AreEqual(5, parsed.Commands.Length);
        Assert.IsFalse(parsed.AllowExperimentalParameters);
        Assert.ThrowsException<InvalidDataException>(() => Switch2BluetoothLabReceiverPlan.Parse(json, 6));
        Assert.ThrowsException<JsonException>(() => Switch2BluetoothLabReceiverPlan.Parse("{\"TargetUuid\":\"anything\"}"u8, 5));
        Assert.ThrowsException<InvalidDataException>(() => Switch2BluetoothLabReceiverPlan.Parse(Array.Empty<byte>(), 5));
        Assert.ThrowsException<InvalidDataException>(() => Switch2BluetoothLabReceiverPlan.Parse(
            new byte[Switch2BluetoothLabReceiverPlan.MaximumJsonBytes + 1], 5));
    }

    [TestMethod]
    public void ReceiverPlanRejectsUnknownDangerousTruncatedAndOversizedCommandsBeforeAnyExecution()
    {
        foreach (string command in new[] { "", "0C910103000400002F000000", "0C9101020004000094000000",
                     "0C9101020004000027000000", "1591010100000000", "0291010100000000",
                     "099101070004000001000000", "1891010200000000", "189101030001000008",
                     "179101020007000080BB000002F0", "179101020007000080BB000002F00000" })
            Assert.ThrowsException<InvalidDataException>(() => Plan(command).Validate(), command);
        Assert.ThrowsException<InvalidDataException>(() => Plan("0C910104000400002F000000").Validate());
        Assert.ThrowsException<InvalidDataException>(() => Plan().Validate());
        Assert.ThrowsException<InvalidDataException>(() => Plan(Enumerable.Repeat("1891010100000000", 9).ToArray()).Validate());
        Assert.ThrowsException<InvalidDataException>(() => new Switch2BluetoothLabReceiverPlan
            { Id = "receiver", Generation = 0, Commands = new[] { Convert.FromHexString("1891010100000000") } }.Validate());
        Assert.ThrowsException<InvalidDataException>(() => new Switch2BluetoothLabReceiverPlan
            { Id = new string('x', 49), Generation = 5, Commands = new[] { Convert.FromHexString("1891010100000000") } }.Validate());
    }

    [TestMethod]
    public void ReceiverUnobservedShapesRequireExplicitExperimentalOptInAndStayWithinClosedBounds()
    {
        foreach (byte channels in new byte[] { 1, 2 })
        foreach (ushort samples in new ushort[] { 120, 240, 480, 960 })
        {
            byte[] request = Convert.FromHexString("179101020007000080BB000002F000");
            request[12] = channels;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(13), samples);
            var experimental = new Switch2BluetoothLabReceiverPlan
                { Id = "receiver", Generation = 5, Commands = new[] { request }, AllowExperimentalParameters = true };
            experimental.Validate();
            var normal = new Switch2BluetoothLabReceiverPlan { Id = "receiver", Generation = 5, Commands = new[] { request } };
            if (channels == 2 && samples == 240) normal.Validate();
            else Assert.ThrowsException<InvalidDataException>(() => normal.Validate());
        }
        for (byte state = 0; state < 8; state++)
        {
            byte[] request = Convert.FromHexString("189101030001000007");
            request[8] = state;
            new Switch2BluetoothLabReceiverPlan { Id = "receiver", Generation = 5,
                Commands = new[] { request }, AllowExperimentalParameters = true }.Validate();
            if (state != 7)
                Assert.ThrowsException<InvalidDataException>(() => new Switch2BluetoothLabReceiverPlan
                    { Id = "receiver", Generation = 5, Commands = new[] { request } }.Validate());
        }
        foreach (string invalid in new[] { "179101020007000044AC000002F000", "179101020007000080BB000003F000",
                     "179101020007000080BB0000020100", "189101030001000008" })
            Assert.ThrowsException<InvalidDataException>(() => new Switch2BluetoothLabReceiverPlan
                { Id = "receiver", Generation = 5, Commands = new[] { Convert.FromHexString(invalid) },
                    AllowExperimentalParameters = true }.Validate());
    }

    [TestMethod]
    public void ReceiverAudioMustUseSameGenerationAndOnlyOuterHeadsetWindow()
    {
        foreach (bool headset in new[] { false, true })
        foreach (ulong generation in new ulong[] { 5, 6 })
        {
            var audio = new Switch2BluetoothLabPlan { Id = "audio", Generation = generation,
                HeadsetNotifications = headset, Packets = new[] { new Switch2BluetoothLabPacket { Payload = new byte[] { 1 } } } };
            var plan = new Switch2BluetoothLabReceiverPlan { Id = "receiver", Generation = 5,
                Commands = new[] { Convert.FromHexString("189101030001000007") }, Audio = audio, HeadsetNotifications = true };
            if (generation == 5 && !headset) plan.Validate();
            else Assert.ThrowsException<InvalidDataException>(() => plan.Validate());
        }
        // Prior setup capability is the probe's same-generation responsibility,
        // so structural validation allows 18/03 + audio without resending 17/02.
    }

    [TestMethod]
    public void ReceiverRepliesRequireExactHeadersLengthsStepAndStateEcho()
    {
        foreach (var pair in new[]
                 {
                     ("0C910102000400002F000000", "0C0101021078000000000000"),
                     ("0C910104000400002F000000", "0C0101041078000000000000"),
                     ("179101020007000080BB000002F000", "1701010210780000"),
                     ("1891010100000000", "1801010110780000000040F000006000"),
                     ("189101030001000007", "180101031078000007"),
                 })
        {
            byte[] request = Convert.FromHexString(pair.Item1), response = Convert.FromHexString(pair.Item2);
            Assert.IsTrue(Switch2BluetoothLabReceiverPlan.IsAcknowledged(request, response));
            for (int index = 0; index < 8; index++)
            {
                byte[] malformed = (byte[])response.Clone();
                malformed[index] ^= 0x80;
                Assert.IsFalse(Switch2BluetoothLabReceiverPlan.IsAcknowledged(request, malformed));
            }
            Assert.IsFalse(Switch2BluetoothLabReceiverPlan.IsAcknowledged(request, response.AsSpan(0, response.Length - 1)));
            Assert.IsFalse(Switch2BluetoothLabReceiverPlan.IsAcknowledged(request, response.Concat(new byte[] { 0 }).ToArray()));
        }
        Assert.IsFalse(Switch2BluetoothLabReceiverPlan.IsAcknowledged(Convert.FromHexString("189101030001000007"),
            Convert.FromHexString("180101031078000006")));
        Assert.IsFalse(Switch2BluetoothLabReceiverPlan.IsAcknowledged(Convert.FromHexString("0C910104000400002F000000"),
            Convert.FromHexString("0C0101021078000000000000")));
    }

    internal static Switch2BluetoothLabReceiverPlan Plan(params string[] requests) => new()
        { Id = "receiver", Generation = 5, Commands = requests.Select(Convert.FromHexString).ToArray() };
}

public sealed partial class Switch2BluetoothPlayerLedCommandChannelTests
{
    [TestMethod]
    public async Task ReceiverCommandsUseOneOwnerAndReturnRawAcknowledgementsWithoutPlaybackClaims()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var writes = new List<string>();
        command.WriteOverride = (request, _, token) =>
        {
            writes.Add(Convert.ToHexString(request.Span));
            if (request.Span[0] != 9) Assert.IsFalse(token.CanBeCanceled);
            response.Emit(ReceiverReply(request.Span));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        var plan = Switch2BluetoothLabReceiverPlanTests.Plan("0C910102000400002F000000", "0C910104000400002F000000",
            "179101020007000080BB000002F000", "1891010100000000", "189101030001000007");
        using var result = JsonDocument.Parse(await channel.RunLabReceiverCommandsAsync(plan, CancellationToken.None));
        Assert.IsTrue(result.RootElement.GetProperty("CommandsAcknowledged").GetBoolean());
        Assert.IsTrue(result.RootElement.GetProperty("SetupAcknowledged").GetBoolean());
        Assert.IsFalse(result.RootElement.GetProperty("BluetoothPlaybackConfirmed").GetBoolean());
        Assert.IsFalse(result.RootElement.TryGetProperty("Error", out _));
        Assert.AreEqual(5, result.RootElement.GetProperty("Responses").GetArrayLength());
        Assert.AreEqual("180101031078000007", result.RootElement.GetProperty("Responses")[4].GetProperty("Response").GetString());
        CollectionAssert.AreEqual(plan.Commands.Select(Convert.ToHexString).ToArray(), writes);
        Assert.IsTrue((await channel.SetPlayerAsync(1, CancellationToken.None)).Succeeded);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ReceiverInvalidLaterCommandIsRejectedBeforeTheFirstWriteWithoutFencingValidOwner()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (request, _, _) => { response.Emit(ReceiverReply(request.Span)); return ValueTask.FromResult(true); };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        string rejected = await channel.RunLabReceiverCommandsAsync(Switch2BluetoothLabReceiverPlanTests.Plan(
            "179101020007000080BB000002F000", "0291010100000000"), CancellationToken.None);
        StringAssert.Contains(rejected, "Error");
        Assert.AreEqual(0, command.WriteCalls);
        Assert.IsTrue((await channel.SetPlayerAsync(1, CancellationToken.None)).Succeeded);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReceiverStaleMaskAckCannotSatisfyEnable(bool completeEnable)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = (request, _, _) =>
        {
            response.Emit(Convert.FromHexString("0C0101021078000000000000"));
            if (request.Span[3] == 4) entered.TrySetResult();
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var deadline = new CancellationTokenSource();
        var pending = channel.RunLabReceiverCommandsAsync(Switch2BluetoothLabReceiverPlanTests.Plan(
            "0C910102000400002F000000", "0C910104000400002F000000"), deadline.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(pending.IsCompleted);
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Busy,
            (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.AreEqual(Switch2BluetoothMemoryReadChannelFailure.Busy,
            (await channel.ReadMemoryAsync(1, 0x013000, CancellationToken.None)).Failure);
        if (completeEnable) response.Emit(Convert.FromHexString("0C0101041078000000000000"));
        else deadline.Cancel();
        using var result = JsonDocument.Parse(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(completeEnable, result.RootElement.GetProperty("CommandsAcknowledged").GetBoolean());
        Assert.IsFalse(result.RootElement.GetProperty("SetupAcknowledged").GetBoolean());
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("1801010110780000000040F000006000")]
    [DataRow("180101031078000006")]
    [DataRow("18010103107800000700")]
    [DataRow("1801010310780000070000000000000000")]
    public async Task ReceiverWrongStepEchoOrLengthFencesWithoutSendingTheNextCommand(string invalidResponse)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) => { response.Emit(Convert.FromHexString(invalidResponse)); return ValueTask.FromResult(true); };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        string result = await channel.RunLabReceiverCommandsAsync(Switch2BluetoothLabReceiverPlanTests.Plan(
            "189101030001000007", "1891010100000000"), CancellationToken.None);
        StringAssert.Contains(result, "Error");
        Assert.AreEqual(1, command.WriteCalls);
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
            (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ReceiverWriteFailureKeepsOnlyPriorSetupSuccessAndFencesSuccessors(int failedStep)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (request, _, _) =>
        {
            if (command.WriteCalls == failedStep) return ValueTask.FromResult(false);
            response.Emit(ReceiverReply(request.Span));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var result = JsonDocument.Parse(await channel.RunLabReceiverCommandsAsync(Switch2BluetoothLabReceiverPlanTests.Plan(
            "179101020007000080BB000002F000", "189101030001000007", "1891010100000000"), CancellationToken.None));
        Assert.IsFalse(result.RootElement.GetProperty("CommandsAcknowledged").GetBoolean());
        Assert.AreEqual(failedStep == 2, result.RootElement.GetProperty("SetupAcknowledged").GetBoolean());
        Assert.AreEqual(failedStep, result.RootElement.GetProperty("Responses").GetArrayLength());
        Assert.AreEqual(failedStep, command.WriteCalls);
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
            (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReceiverCancelledLateWriteRetainsInnerWindowUntilDrainAndNeverSendsSuccessor(bool eventuallyWritten)
    {
        var events = new List<string>();
        var command = FakeCharacteristic.Command(events);
        var response = FakeCharacteristic.Response(events);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = (request, _, token) =>
        {
            Assert.IsFalse(token.CanBeCanceled);
            response.Emit(ReceiverReply(request.Span));
            entered.TrySetResult();
            return new ValueTask<bool>(release.Task);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        var pending = channel.RunLabReceiverCommandsAsync(Switch2BluetoothLabReceiverPlanTests.Plan(
            "179101020007000080BB000002F000", "189101030001000007"), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.IsFalse(pending.IsCompleted, "The outer probe bounds its client, but its inner CC window must retain the write.");
            Task<bool> retirement = channel.RetireAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(retirement.IsCompleted);
            CollectionAssert.DoesNotContain(events, "detach");
            Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
                (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
            release.TrySetResult(eventuallyWritten);
            using var result = JsonDocument.Parse(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(result.RootElement.TryGetProperty("Error", out _));
            Assert.AreEqual("1701010210780000", result.RootElement.GetProperty("Responses")[0].GetProperty("Response").GetString());
            Assert.IsFalse(result.RootElement.GetProperty("Responses")[0].GetProperty("Acknowledged").GetBoolean());
            Assert.IsTrue(await retirement.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(1, command.WriteCalls);
        }
        finally { cancellation.Cancel(); release.TrySetResult(true); await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [TestMethod]
    public async Task ReceiverSnapshotsFutureRequestsBeforeTheFirstWrite()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var plan = Switch2BluetoothLabReceiverPlanTests.Plan("179101020007000080BB000002F000", "189101030001000007");
        command.WriteOverride = (request, _, _) =>
        {
            if (command.WriteCalls == 1) plan.Commands[1][8] = 0xFF;
            else Assert.AreEqual((byte)7, request.Span[8]);
            response.Emit(ReceiverReply(request.Span));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var result = JsonDocument.Parse(await channel.RunLabReceiverCommandsAsync(plan, CancellationToken.None));
        Assert.IsTrue(result.RootElement.GetProperty("CommandsAcknowledged").GetBoolean());
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    private static byte[] ReceiverReply(ReadOnlySpan<byte> request) => request[0] switch
    {
        0x0C => Convert.FromHexString(request[3] == 2 ? "0C0101021078000000000000" : "0C0101041078000000000000"),
        0x17 => Convert.FromHexString("1701010210780000"),
        0x18 when request[3] == 1 => Convert.FromHexString("1801010110780000000040F000006000"),
        0x18 => new byte[] { 0x18, 1, 1, 3, 0x10, 0x78, 0, 0, request[8] },
        _ => Convert.FromHexString("0901000000000000"),
    };
}
