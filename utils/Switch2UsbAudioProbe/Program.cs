using System.Text.Json;
using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

using var enumerator = new MMDeviceEnumerator();
if (args.Length == 0 || args[0] == "--list")
{
    foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
    {
        string? defaultId = null;
        try { using var standard = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = standard.ID; }
        catch { }
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(flow, DeviceState.All))
        using (endpoint)
        {
            try
            {
                if (endpoint.State != DeviceState.Active) continue;
                Console.WriteLine(JsonSerializer.Serialize(new { endpoint.ID, Name = endpoint.FriendlyName,
                    Flow = flow.ToString(), State = endpoint.State.ToString(), Default = endpoint.ID == defaultId,
                    Format = endpoint.AudioClient.MixFormat.ToString(), Mute = endpoint.AudioEndpointVolume.Mute,
                    Level = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar }));
            }
            catch (Exception exception)
            { Console.WriteLine(JsonSerializer.Serialize(new { endpoint.ID, Error = exception.Message })); }
        }
    }
    return;
}
bool verifyLineIn = args[0] == "--verify-line-in" && args.Length is 3 or 4;
if (!verifyLineIn && (args[0] != "--tone" || args.Length != 2))
    throw new ArgumentException("Use --list, --tone <render ID>, or --verify-line-in <render ID> <Line In ID> [peak 0.005..0.08].");
double peakLevel = args.Length == 4 ? double.Parse(args[3], CultureInfo.InvariantCulture) : .02;
if (!double.IsFinite(peakLevel) || peakLevel < .005 || peakLevel > .08)
    throw new ArgumentOutOfRangeException(nameof(peakLevel), "Test peak must be between 0.005 and 0.08; endpoint levels are never changed.");
using var render = enumerator.GetDevice(args[1]);
if (render.DataFlow != DataFlow.Render || render.State != DeviceState.Active ||
    render.FriendlyName != "Headphones (Switch 2 Pro Controller)")
    throw new InvalidOperationException("Only the explicitly selected active physical Switch 2 headphones may be tested.");
const int rate = 48000;
const int frames = rate / 2;
using var lineDevice = verifyLineIn ? enumerator.GetDevice(args[2]) : null;
if (lineDevice != null && (lineDevice.DataFlow != DataFlow.Capture || lineDevice.State != DeviceState.Active ||
    lineDevice.FriendlyName != "Line In (Realtek(R) Audio)"))
    throw new InvalidOperationException("Capture is restricted to the user-confirmed Realtek Line In.");
using var capture = lineDevice == null ? null : new WasapiCapture(lineDevice);
var captureSamples = new List<float>();
object sampleGate = new();
var recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
if (capture != null)
{
    if (capture.WaveFormat.SampleRate != rate || capture.WaveFormat.Channels != 2 ||
        capture.WaveFormat.BitsPerSample != 32 ||
        !(capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
          capture.WaveFormat is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
        throw new InvalidOperationException("The observed Line In float/48k/stereo format changed.");
    capture.DataAvailable += (_, e) =>
    {
        lock (sampleGate)
            for (int offset = 0; offset + 4 <= e.BytesRecorded && captureSamples.Count < rate * 2 * 3; offset += 4)
                captureSamples.Add(BitConverter.ToSingle(e.Buffer, offset));
    };
    capture.RecordingStopped += (_, e) => recordingStopped.TrySetResult(e.Exception);
    capture.StartRecording();
    await Task.Delay(350);
}
byte[] samples = new byte[frames * 4];
for (int i = 0; i < frames; i++)
{
    double fade = Math.Min(1, Math.Min(i / 480.0, (frames - 1 - i) / 480.0));
    for (int channel = 0; channel < 2; channel++)
    {
        // Default -34 dBFS peak, bounded to -22 dBFS for level verification.
        // 500 ms, 10 ms fades. No endpoint/default/volume change.
        short value = (short)(32767 * peakLevel * fade * Math.Sin(2 * Math.PI * (channel == 0 ? 440 : 660) * i / rate));
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 4 + channel * 2, 2), value);
    }
}
using var source = new RawSourceWaveStream(new MemoryStream(samples, writable: false), new WaveFormat(rate, 16, 2));
using var output = new WasapiOut(render, AudioClientShareMode.Shared, true, 40);
var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
output.PlaybackStopped += (_, e) => stopped.TrySetResult(e.Exception);
output.Init(source);
output.Play();
var failure = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (failure != null) throw failure;
Console.WriteLine(JsonSerializer.Serialize(new { Render = render.ID, Result = "WASAPI playback completed", DurationMs = 500,
    PeakDbfs = 20 * Math.Log10(peakLevel), PeakLinear = peakLevel, AnalogDeliveryConfirmed = false,
    RenderEndpointVolume = render.AudioEndpointVolume.MasterVolumeLevelScalar,
    CaptureEndpointVolume = lineDevice?.AudioEndpointVolume.MasterVolumeLevelScalar }));
if (capture != null)
{
    await Task.Delay(350);
    capture.StopRecording();
    var recordError = await recordingStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    if (recordError != null) throw recordError;
    float[] recording;
    lock (sampleGate) recording = captureSamples.ToArray();
    const int window = rate / 10;
    var blocks = new List<object>();
    for (int start = 0; start + window <= recording.Length / 2; start += window)
    {
        var channels = new List<object>();
        for (int channel = 0; channel < 2; channel++)
        {
            double squares = 0, peak = 0;
            for (int i = 0; i < window; i++)
            {
                double sample = recording[(start + i) * 2 + channel];
                if (!double.IsFinite(sample)) throw new InvalidDataException("Nonfinite capture sample");
                squares += sample * sample; peak = Math.Max(peak, Math.Abs(sample));
            }
            double Amplitude(double hz)
            {
                double real = 0, imaginary = 0;
                for (int i = 0; i < window; i++)
                {
                    double phase = 2 * Math.PI * hz * i / rate;
                    double sample = recording[(start + i) * 2 + channel];
                    real += sample * Math.Cos(phase); imaginary += sample * Math.Sin(phase);
                }
                return 2 * Math.Sqrt(real * real + imaginary * imaginary) / window;
            }
            channels.Add(new { Channel = channel, Rms = Math.Sqrt(squares / window), Peak = peak,
                Hz440 = Amplitude(440), Hz660 = Amplitude(660) });
        }
        blocks.Add(new { StartMs = start * 1000 / rate, Channels = channels });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Capture = lineDevice!.ID, CapturedFrames = recording.Length / 2,
        StoredAudio = false, Blocks = blocks }));
}
