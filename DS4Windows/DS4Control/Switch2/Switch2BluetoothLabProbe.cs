using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

// Optional surface on the EXACT service already owned by the input lease.
// No second BluetoothLEDevice/service open, arbitrary UUID, output or CCCD API.
internal interface ISwitch2BluetoothLabAudioAccess
{
    Task<string> QueryAudioLabAsync(string command, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit portable-lab-only local diagnostics. Idle probes perform no radio
/// I/O. The input path only updates two counters; all pipes/files/GATT work is
/// on this worker. Retirement drains it before the lease disposes its service.
/// </summary>
internal sealed class Switch2BluetoothLabProbe
{
    private readonly CancellationTokenSource stopping = new();
    private readonly ISwitch2BluetoothLabAudioAccess access;
    private readonly Func<CancellationToken, Task<string>> configure;
    private readonly Func<CancellationToken, Task<string>> queryAudioState;
    private readonly Func<bool> connected;
    private readonly string directory;
    private readonly ulong generation;
    private readonly Task worker;
    private long reports, lastReport, maximumReportGap;
    private bool audioSetupAcknowledged;

    internal static bool IsEnabled => PortableLabContext.IsActive &&
        Environment.GetEnvironmentVariable("DS4WINDOWS_SWITCH2_AUDIO_PROBE") == "1";

    internal static Switch2BluetoothLabProbe TryCreate(Switch2ControllerModel model,
        ISwitch2BluetoothWindowsGattService service, ulong generation,
        Func<bool> connected, Func<CancellationToken, Task<string>> configure,
        Func<CancellationToken, Task<string>> queryAudioState = null)
    {
        if (!IsEnabled || PortableLabContext.Current is not { } lab ||
            model != Switch2ControllerModel.ProController2 ||
            service is not ISwitch2BluetoothLabAudioAccess access)
            return null;
        return new Switch2BluetoothLabProbe(Path.Combine(lab.DataPath, "Switch2AudioProbe"),
            access, connected, configure, generation, queryAudioState);
    }

    // Internal injection seam for pipe/lifetime tests; production uses TryCreate.
    internal Switch2BluetoothLabProbe(string directory, ISwitch2BluetoothLabAudioAccess access,
        Func<bool> connected, Func<CancellationToken, Task<string>> configure, ulong generation,
        Func<CancellationToken, Task<string>> queryAudioState = null)
    {
        this.directory = directory;
        this.access = access;
        this.connected = connected;
        this.configure = configure;
        this.queryAudioState = queryAudioState;
        this.generation = generation;
        PipeName = $"ds4w-s2audio-{Environment.ProcessId}-{Guid.NewGuid():N}";
        worker = Task.Run(RunAsync);
    }

    internal string PipeName { get; }
    internal Task Completion => worker;
    internal void ObserveReport(long timestamp)
    {
        Interlocked.Increment(ref reports);
        long previous = Interlocked.Exchange(ref lastReport, timestamp);
        if (previous <= 0 || timestamp <= previous) return;
        long gap = timestamp - previous;
        long observed = Interlocked.Read(ref maximumReportGap);
        while (gap > observed)
        {
            long actual = Interlocked.CompareExchange(ref maximumReportGap, gap, observed);
            if (actual == observed) break;
            observed = actual;
        }
    }
    internal Task StopAsync()
    {
        stopping.Cancel();
        return worker;
    }

    private object Status(string state) => new
    {
        State = state, ProcessId = Environment.ProcessId, PipeName, TransportGeneration = generation,
        Connected = connected(), Reports = Interlocked.Read(ref reports),
        MaximumReportGapMs = 1000.0 * Interlocked.Read(ref maximumReportGap) / Stopwatch.Frequency,
        LastReportAgeMs = Interlocked.Read(ref lastReport) is var last && last > 0
            ? (double?)(1000.0 * (Stopwatch.GetTimestamp() - last) / Stopwatch.Frequency) : null,
        BluetoothPlaybackConfirmed = false,
    };

    private async Task RunAsync()
    {
        string descriptor = Path.Combine(directory, PipeName + ".json");
        try
        {
            PortableLabContext.ValidateNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(descriptor, JsonSerializer.Serialize(Status("listening")), stopping.Token);
            while (!stopping.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                Task<string> operation = null;
                bool stop = false;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    string command = await ReadCommandAsync(pipe, deadline.Token).ConfigureAwait(false);
                    if (!Switch2BluetoothLabAudioProtocol.IsAllowed(command))
                        operation = Task.FromResult("{\"Error\":\"Unknown or oversized command\"}");
                    else if (command is "status" or "stop-probe")
                    {
                        stop = command == "stop-probe";
                        operation = Task.FromResult(JsonSerializer.Serialize(Status(stop ? "stopping" : "active")));
                    }
                    else if (!connected())
                        operation = Task.FromResult("{\"Error\":\"Controller lifetime is not active\"}");
                    else if (Switch2BluetoothLabTone.IsTone(command) && !audioSetupAcknowledged)
                        operation = Task.FromResult("{\"Error\":\"Audio setup must be acknowledged in this controller generation before tones\"}");
                    else if (command == "audio-state")
                        operation = queryAudioState?.Invoke(deadline.Token) ?? Task.FromResult("{\"Error\":\"Audio state query unavailable\"}");
                    else
                        operation = command == "configure-audio" ? ConfigureAudioAsync(deadline.Token) :
                            QueryAudioAsync(command, deadline.Token);
                    string result;
                    try { result = await operation.WaitAsync(deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    { result = "{\"Error\":\"Probe deadline; underlying operation retained until completion\"}"; }
                    catch (Exception error)
                    { result = JsonSerializer.Serialize(new { Error = error.GetType().Name, Detail = error.Message }); }
                    using var replyDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    replyDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                    byte[] bytes = Encoding.UTF8.GetBytes(result + "\n");
                    await pipe.WriteAsync(bytes, replyDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!stopping.IsCancellationRequested)
                {
                    // A client failure is not a controller disconnect. No retry/replay.
                }
                finally
                {
                    // Do not dispose the controller service under a late WinRT result.
                    // Also prevents another probe from overlapping an ambiguous query.
                    if (operation != null)
                        try { await operation.ConfigureAwait(false); } catch { }
                }
                if (stop) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Trace.TraceWarning("Switch 2 lab probe stopped: " + error.GetType().Name);
        }
        finally
        {
            // Keep one tiny final descriptor, not an ever-growing packet log.
            try
            {
                PortableLabContext.ValidateNoReparsePoints(descriptor);
                if (Directory.Exists(directory))
                    await File.WriteAllTextAsync(descriptor, JsonSerializer.Serialize(Status("stopped")));
            }
            catch { }
        }
    }

    private async Task<string> ConfigureAudioAsync(CancellationToken token)
    {
        audioSetupAcknowledged = false;
        string reply = await configure(token).ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(reply);
        audioSetupAcknowledged = !token.IsCancellationRequested &&
            parsed.RootElement.TryGetProperty("SetupAcknowledged", out var accepted) &&
            accepted.ValueKind == JsonValueKind.True;
        return reply;
    }

    private async Task<string> QueryAudioAsync(string command, CancellationToken token) =>
        await access.QueryAudioLabAsync(command, token).ConfigureAwait(false);

    internal static async Task<string> ReadCommandAsync(Stream pipe, CancellationToken token)
    {
        byte[] bytes = new byte[33];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (await pipe.ReadAsync(bytes.AsMemory(i, 1), token).ConfigureAwait(false) != 1) return null;
            if (bytes[i] == (byte)'\n') return Encoding.ASCII.GetString(bytes, 0, i).TrimEnd('\r');
            if (bytes[i] > 127) return null;
        }
        return null;
    }
}
