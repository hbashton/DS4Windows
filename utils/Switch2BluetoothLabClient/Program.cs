using System.IO.Pipes;
using System.Text;
using System.Text.Json;

bool tone = args.Length >= 2 && args[1] is ("tone-opus5" or "tone-opus20" or "tone-rumble-opus5" or "tone-rumble-opus20" or
    "tone-length-opus5" or "tone-length-opus20" or "tone-rumble-length-opus5" or "tone-rumble-length-opus20" or
    "active-opus5" or "active-opus20" or "active-rumble-opus5" or "active-rumble-opus20" or
    "active-length-opus5" or "active-length-opus20" or "active-rumble-length-opus5" or "active-rumble-length-opus20");
if (tone ? args.Length != 3 : args.Length != 2 || args[1] is not ("status" or "inventory" or "headset-header" or "headset-observe" or "configure-audio" or "audio-state" or "stop-probe"))
{
    Console.Error.WriteLine("Use <session.json> status|inventory|headset-header|headset-observe|configure-audio|stop-probe, or <session.json> tone-opus5|tone-opus20|tone-rumble-opus5|tone-rumble-opus20 <explicit Realtek Line In ID>");
    return 2;
}
try
{
    var file = new FileInfo(args[0]);
    if (!file.Exists || file.Length > 4096 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        throw new InvalidDataException("Expected a small local probe-session descriptor.");
    using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
    string pipeName = descriptor.RootElement.GetProperty("PipeName").GetString() ?? "";
    if (!pipeName.StartsWith("ds4w-s2audio-", StringComparison.Ordinal) || pipeName.Length > 100 ||
        pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
        descriptor.RootElement.GetProperty("State").GetString() != "listening")
        throw new InvalidDataException("Expected a live DS4Windows lab probe session.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(deadline.Token);
    using var measurement = tone ? new LineInMeasurement(args[2]) : null;
    if (measurement != null) await measurement.StartAsync();
    await pipe.WriteAsync(Encoding.ASCII.GetBytes(args[1] + "\n"), deadline.Token);
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
