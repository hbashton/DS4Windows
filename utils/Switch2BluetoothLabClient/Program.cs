using System.IO.Pipes;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DS4Windows.Switch2;

int captureTailMs = 350;
if (args.Length >= 2 && args[^2] == "--capture-tail-ms")
{
    captureTailMs = args[^1] switch { "350" => 350, "2000" => 2000, _ => 0 };
    if (captureTailMs == 0) { Console.Error.WriteLine("Capture tail must be 350 or 2000 ms."); return 2; }
    args = args[..^2];
}
bool tone = args.Length >= 2 && Switch2BluetoothLabCandidate.TryParse(args[1], out _);
bool planFile = args.Length >= 2 && args[1] == "run-plan";
bool receiverPlanFile = args.Length >= 2 && args[1] == "run-receiver-plan";
bool createPlan = args.Length is 4 or 5 && args[0] == "--create-plan" && tone;
bool createDualMono = args.Length == 6 && args[0] == "--create-dual-mono-plan";
bool createHwOpus = args.Length == 4 && args[0] == "--create-hwopus-plan";
if (!createPlan && !createDualMono && !createHwOpus && (planFile || receiverPlanFile ? args.Length != 4 : tone ? args.Length != 3 : args.Length != 2 || args[1] is not ("status" or "inventory" or "headset-header" or "headset-observe" or "configure-audio" or "audio-state" or "stop-probe")))
{
    Console.Error.WriteLine("Use <session.json> <query>, <session.json> <candidate> <Line In ID>, <session.json> run-plan|run-receiver-plan <Line In ID> <plan.json>, or --create-plan <candidate> <generation> <new-plan.json> [offline-framing]. See README.");
    return 2;
}
try
{
    if (createHwOpus)
    {
        var plan = HwOpusPlanFactory.Create(args[1], ulong.Parse(args[2]));
        using var destination = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await destination.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(plan));
        Console.WriteLine(JsonSerializer.Serialize(new { plan.Id, Fingerprint = plan.Fingerprint(), Packets = plan.Packets.Length, HardwareAccessed = false }));
        return 0;
    }
    if (createDualMono)
    {
        var plan = DualMonoPlanFactory.Create(args[1], int.Parse(args[2]), ulong.Parse(args[3]), args[5]);
        using var destination = new FileStream(args[4], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await destination.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(plan));
        Console.WriteLine(JsonSerializer.Serialize(new { plan.Id, Fingerprint = plan.Fingerprint(), Packets = plan.Packets.Length, HardwareAccessed = false }));
        return 0;
    }
    if (createPlan)
    {
        var plan = CreateTonePlan(args[1], ulong.Parse(args[2]));
        if (args.Length == 5) plan = PlanFramer.Apply(plan, args[4]);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        using var destination = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await destination.WriteAsync(bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { plan.Id, Fingerprint = plan.Fingerprint(), Packets = plan.Packets.Length, HardwareAccessed = false }));
        return 0;
    }
    var file = new FileInfo(args[0]);
    if (!file.Exists || file.Length > 4096 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        throw new InvalidDataException("Expected a small local probe-session descriptor.");
    using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
    string pipeName = descriptor.RootElement.GetProperty("PipeName").GetString() ?? "";
    if (!pipeName.StartsWith("ds4w-s2audio-", StringComparison.Ordinal) || pipeName.Length > 100 ||
        pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
        descriptor.RootElement.GetProperty("State").GetString() != "listening")
        throw new InvalidDataException("Expected a live DS4Windows lab probe session.");
    ulong generation = descriptor.RootElement.GetProperty("TransportGeneration").GetUInt64();
    if ((tone || planFile) && (!descriptor.RootElement.TryGetProperty("PacketPlanProtocol", out var protocol) || protocol.GetInt32() != 1))
        throw new InvalidDataException("The running app has no packet-plan bridge. No radio request sent; do not restart automatically.");
    if (receiverPlanFile && (!descriptor.RootElement.TryGetProperty("ReceiverPlanProtocol", out var receiverProtocol) ||
        receiverProtocol.ValueKind != JsonValueKind.Number || !receiverProtocol.TryGetInt32(out int receiverVersion) || receiverVersion != 1))
        throw new InvalidDataException("The running app has no receiver-plan bridge. No radio request sent.");
    Switch2BluetoothLabPlan? packetPlan = tone ? CreateTonePlan(args[1], generation) : null;
    Switch2BluetoothLabReceiverPlan? receiverPlan = null;
    if (planFile)
    {
        var planInfo = new FileInfo(args[3]);
        if (!planInfo.Exists || planInfo.Length > Switch2BluetoothLabPlan.MaximumJsonBytes || planInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Expected a bounded local packet plan.");
        packetPlan = Switch2BluetoothLabPlan.Parse(await File.ReadAllBytesAsync(planInfo.FullName), generation);
    }
    if (receiverPlanFile)
    {
        var planInfo = new FileInfo(args[3]);
        if (!planInfo.Exists || planInfo.Length > Switch2BluetoothLabReceiverPlan.MaximumJsonBytes || planInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Expected a bounded local receiver plan.");
        receiverPlan = Switch2BluetoothLabReceiverPlan.Parse(await File.ReadAllBytesAsync(planInfo.FullName), generation);
    }
    byte[]? requestBody = receiverPlan != null ? JsonSerializer.SerializeToUtf8Bytes(receiverPlan) :
        packetPlan != null ? JsonSerializer.SerializeToUtf8Bytes(packetPlan) : null;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(deadline.Token);
    // Receiver commands can activate a previously primed audio path even when
    // the plan has no audio packets. Every receiver trial therefore measures.
    using var measurement = packetPlan != null || receiverPlan != null ? new LineInMeasurement(args[2], captureTailMs) : null;
    if (measurement != null) await measurement.StartAsync();
    bool responseFailed = true;
    bool captureValid = true;
    try
    {
        string command = receiverPlan != null ? "run-receiver-plan" : packetPlan != null ? "run-plan" : args[1];
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), deadline.Token);
        if (requestBody != null)
        {
            byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, requestBody.Length);
            await pipe.WriteAsync(size, deadline.Token);
            await pipe.WriteAsync(requestBody, deadline.Token);
        }
        using var response = new MemoryStream();
        byte[] chunk = new byte[1024];
        while (response.Length <= 32 * 1024)
        {
            int count = await pipe.ReadAsync(chunk, deadline.Token);
            if (count == 0) break;
            response.Write(chunk, 0, count);
            if (Array.IndexOf(chunk, (byte)'\n', 0, count) >= 0) break;
        }
        if (response.Length == 0 || response.Length > 32 * 1024) throw new InvalidDataException("Invalid probe reply length.");
        string json = Encoding.UTF8.GetString(response.ToArray()).TrimEnd();
        using var validated = JsonDocument.Parse(json);
        Console.WriteLine(json);
        JsonElement result = validated.RootElement;
        responseFailed = receiverPlan != null
            ? !Switch2BluetoothLabReceiverResult.IsCompleted(result, receiverPlan)
            : result.ValueKind != JsonValueKind.Object || Switch2BluetoothLabReceiverResult.HasError(result);
    }
    finally
    {
        // Preserve the bounded observation tail even if a submitted operation
        // times out or returns malformed JSON. Never classify it as success.
        if (measurement != null)
        {
            JsonElement captureResult = JsonSerializer.SerializeToElement(await measurement.StopAsync());
            Console.WriteLine(captureResult.GetRawText());
            captureValid = captureResult.ValueKind == JsonValueKind.Object &&
                captureResult.TryGetProperty("CaptureValid", out var validCapture) &&
                validCapture.ValueKind == JsonValueKind.True;
        }
    }
    return responseFailed || !captureValid ? 1 : 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { Error = error.Message, Type = error.GetType().Name }));
    return 1;
}

static Switch2BluetoothLabPlan CreateTonePlan(string command, ulong generation)
{
    if (!Switch2BluetoothLabCandidate.TryParse(command, out var candidate)) throw new InvalidDataException("Unknown generator candidate.");
    var tone = Switch2BluetoothLabTone.Create(command);
    var plan = new Switch2BluetoothLabPlan
    {
        Id = command.Replace(':', '-').Replace('.', '_'), Generation = generation,
        HeadsetNotifications = candidate.Active,
        Packets = tone.Packets.Select((p, i) => new Switch2BluetoothLabPacket { OffsetMicroseconds = i * candidate.FrameMicroseconds, Payload = p }).ToArray()
    };
    plan.Validate(509);
    return plan;
}
