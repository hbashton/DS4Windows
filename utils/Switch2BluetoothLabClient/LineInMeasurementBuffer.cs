// Pure, bounded measurement math. No endpoint access, sample export or files.
internal sealed class LineInMeasurementBuffer
{
    internal const int SampleRate = 48000;
    internal const int Channels = 2;
    internal const int RequiredBaselineFrames = 16800;
    private readonly float[] samples;
    private readonly object gate = new();
    private int count;
    private long observedFrames, droppedFrames, nonfiniteSamples, unalignedBytes;

    internal LineInMeasurementBuffer(int maximumFrames = SampleRate * 8)
    {
        if (maximumFrames <= 0 || maximumFrames > SampleRate * 8)
            throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        samples = new float[maximumFrames * Channels];
    }

    internal int CapturedFrames { get { lock (gate) return count / Channels; } }
    internal bool IsValid { get { lock (gate) return droppedFrames == 0 && nonfiniteSamples == 0 && unalignedBytes == 0; } }

    internal static bool HasCompletePostSubmitCapture(int baselineFrames, int framesAtResponse, int finalFrames, int tailMilliseconds) =>
        tailMilliseconds is 350 or 2000 && baselineFrames >= RequiredBaselineFrames && framesAtResponse >= baselineFrames &&
        finalFrames > baselineFrames && (long)finalFrames - framesAtResponse >= (long)tailMilliseconds * SampleRate / 1000;

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            const int frameBytes = Channels * sizeof(float);
            unalignedBytes += bytes.Length % frameBytes;
            int frames = bytes.Length / frameBytes;
            observedFrames += frames;
            for (int frame = 0; frame < frames; frame++)
            {
                bool retain = count + Channels <= samples.Length;
                if (!retain) droppedFrames++;
                for (int channel = 0; channel < Channels; channel++)
                {
                    float value = BitConverter.ToSingle(bytes.Slice(frame * frameBytes + channel * sizeof(float), sizeof(float)));
                    if (!float.IsFinite(value))
                    {
                        nonfiniteSamples++;
                        value = 0; // Keep JSON finite, but never classify this capture as valid.
                    }
                    if (retain) samples[count++] = value;
                }
            }
        }
    }

    internal LineInBufferSummary Analyze()
    {
        lock (gate)
        {
            var blocks = new List<LineInBlock>();
            const int maximumWindowFrames = SampleRate / 10;
            int totalFrames = count / Channels;
            for (int start = 0; start < totalFrames; start += maximumWindowFrames)
            {
                int window = Math.Min(maximumWindowFrames, totalFrames - start);
                var channels = new LineInChannelMetrics[Channels];
                for (int channel = 0; channel < Channels; channel++)
                {
                    double squares = 0, peak = 0;
                    int clipped = 0;
                    for (int i = 0; i < window; i++)
                    {
                        double value = samples[(start + i) * Channels + channel];
                        squares += value * value;
                        peak = Math.Max(peak, Math.Abs(value));
                        if (Math.Abs(value) >= .999) clipped++;
                    }
                    double Amplitude(double hz)
                    {
                        double real = 0, imaginary = 0;
                        for (int i = 0; i < window; i++)
                        {
                            double phase = 2 * Math.PI * hz * i / SampleRate;
                            double value = samples[(start + i) * Channels + channel];
                            real += value * Math.Cos(phase);
                            imaginary += value * Math.Sin(phase);
                        }
                        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / window;
                    }
                    channels[channel] = new(channel, Math.Sqrt(squares / window), peak, clipped, Amplitude(440), Amplitude(660));
                }
                blocks.Add(new(start * 1000.0 / SampleRate, window, window * 1000.0 / SampleRate, channels));
            }
            return new(totalFrames, observedFrames, droppedFrames, nonfiniteSamples, unalignedBytes, blocks.ToArray());
        }
    }

    internal void Clear()
    {
        lock (gate) Array.Clear(samples);
    }
}

internal sealed record LineInChannelMetrics(int Channel, double Rms, double Peak, int ClippedSamples, double Hz440, double Hz660);
internal sealed record LineInBlock(double StartMs, int FrameCount, double DurationMs, LineInChannelMetrics[] Channels);
internal sealed record LineInBufferSummary(int CapturedFrames, long ObservedFrames, long DroppedFrames, long NonfiniteSamples,
    long UnalignedBytes, LineInBlock[] Blocks)
{
    public bool CaptureValid => CapturedFrames >= LineInMeasurementBuffer.RequiredBaselineFrames &&
        DroppedFrames == 0 && NonfiniteSamples == 0 && UnalignedBytes == 0;
}
