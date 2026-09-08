using System.IO.Pipes;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DS4Windows.Switch2;

bool tone = args.Length >= 2 && Switch2BluetoothLabCandidate.TryParse(args[1], out _);
bool planFile = args.Length >= 2 && args[1] == "run-plan";
bool createPlan = args.Length is 4 or 5 && args[0] == "--create-plan" && tone;
if (!createPlan && (planFile ? args.Length != 4 : tone ? args.Length != 3 : args.Length != 2 || args[1] is not ("status" or "inventory" or "headset-header" or "headset-observe" or "configure-audio" or "audio-state" or "stop-probe")))
{
    Console.Error.WriteLine("Use <session.json> <query>, <session.json> <candidate> <Line In ID>, <session.json> run-plan <Line In ID> <plan.json>, or --create-plan <candidate> <generation> <new-plan.json> [offline-framing]. See README.");
    return 2;
}
try
{
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
    Switch2BluetoothLabPlan? packetPlan = tone ? CreateTonePlan(args[1], generation) : null;
    if (planFile)
    {
        var planInfo = new FileInfo(args[3]);
        if (!planInfo.Exists || planInfo.Length > Switch2BluetoothLabPlan.MaximumJsonBytes || planInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Expected a bounded local packet plan.");
        packetPlan = Switch2BluetoothLabPlan.Parse(await File.ReadAllBytesAsync(planInfo.FullName), generation);
    }
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(deadline.Token);
    using var measurement = packetPlan != null ? new LineInMeasurement(args[2]) : null;
    if (measurement != null) await measurement.StartAsync();
    await pipe.WriteAsync(Encoding.ASCII.GetBytes((packetPlan != null ? "run-plan" : args[1]) + "\n"), deadline.Token);
    if (packetPlan != null)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packetPlan);
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
        await pipe.WriteAsync(size, deadline.Token);
        await pipe.WriteAsync(body, deadline.Token);
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
    if (measurement != null) Console.WriteLine(JsonSerializer.Serialize(await measurement.StopAsync()));
    return validated.RootElement.TryGetProperty("Error", out _) ? 1 : 0;
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
