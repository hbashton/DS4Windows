using NAudio.CoreAudioApi;
using NAudio.Wave;

// Capture only the explicitly supplied, user-confirmed wired Line In.
// No default endpoint fallback, volume writes, microphone or audio files.
internal sealed class LineInMeasurement : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly float[] samples = new float[48000 * 2 * 8];
    private readonly object gate = new();
    private readonly TaskCompletionSource<Exception?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int count;
    private readonly int tailMilliseconds;

    internal LineInMeasurement(string id, int tailMilliseconds = 350)
    {
        if (tailMilliseconds is not (350 or 2000)) throw new ArgumentOutOfRangeException(nameof(tailMilliseconds));
        this.tailMilliseconds = tailMilliseconds;
        device = enumerator.GetDevice(id);
        if (device.ID != id || device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active ||
            device.FriendlyName != "Line In (Realtek(R) Audio)" || device.AudioEndpointVolume.Mute)
            throw new InvalidOperationException("Expected the explicit active, unmuted Realtek Line In; no fallback or level changes.");
        capture = new WasapiCapture(device);
        if (capture.WaveFormat.SampleRate != 48000 || capture.WaveFormat.Channels != 2 || capture.WaveFormat.BitsPerSample != 32 ||
            !(capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || capture.WaveFormat is WaveFormatExtensible ext &&
              ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
            throw new InvalidOperationException("Line In float/48k/stereo format changed.");
        capture.DataAvailable += (_, e) =>
        {
            lock (gate)
                for (int offset = 0; offset + 4 <= e.BytesRecorded && count < samples.Length; offset += 4)
                    samples[count++] = BitConverter.ToSingle(e.Buffer, offset);
        };
        capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
    }

    internal async Task StartAsync()
    {
        capture.StartRecording();
        await Task.Delay(350); // pre-stimulus baseline
    }

    internal async Task<object> StopAsync()
    {
        await Task.Delay(tailMilliseconds); // explicit bounded post-submit observation, not proof of radio drain
        capture.StopRecording();
        var error = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (error != null) throw error;
        var blocks = new List<object>();
        const int window = 4800;
        lock (gate)
        {
            for (int start = 0; start + window <= count / 2; start += window)
            {
                var channels = new List<object>();
                for (int channel = 0; channel < 2; channel++)
                {
                    double squares = 0, peak = 0;
                    int clipped = 0;
                    for (int i = 0; i < window; i++)
                    {
                        double value = samples[(start + i) * 2 + channel];
                        if (!double.IsFinite(value)) throw new InvalidDataException("Nonfinite Line In sample.");
                        squares += value * value; peak = Math.Max(peak, Math.Abs(value));
                        if (Math.Abs(value) >= .999) clipped++;
                    }
                    double Amplitude(double hz)
                    {
                        double real = 0, imaginary = 0;
                        for (int i = 0; i < window; i++)
                        {
                            double phase = 2 * Math.PI * hz * i / 48000;
                            double value = samples[(start + i) * 2 + channel];
                            real += value * Math.Cos(phase); imaginary += value * Math.Sin(phase);
                        }
                        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / window;
                    }
                    channels.Add(new { Channel = channel, Rms = Math.Sqrt(squares / window), Peak = peak,
                        ClippedSamples = clipped, Hz440 = Amplitude(440), Hz660 = Amplitude(660) });
                }
                blocks.Add(new { StartMs = start / 48, Channels = channels });
            }
            return new { Capture = device.ID, CapturedFrames = count / 2, CaptureTailMs = tailMilliseconds, StoredAudio = false,
                CaptureEndpointVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar, Blocks = blocks };
        }
    }

    public void Dispose()
    {
        capture.Dispose();
        Array.Clear(samples);
        device.Dispose();
        enumerator.Dispose();
    }
}
