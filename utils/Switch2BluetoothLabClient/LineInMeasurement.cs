using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

// Capture only the explicitly supplied, user-confirmed wired Line In.
// No default endpoint fallback, volume writes, microphone or audio files.
internal sealed class LineInMeasurement : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly LineInMeasurementBuffer buffer = new();
    private readonly TaskCompletionSource<bool> baselineReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> tailReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int tailMilliseconds;
    private readonly EndpointLevels before;
    private int baselineFrames;
    private double baselineWaitMs;
    private int tailTargetFrames;

    internal LineInMeasurement(string id, int tailMilliseconds = 350)
    {
        if (tailMilliseconds is not (350 or 2000)) throw new ArgumentOutOfRangeException(nameof(tailMilliseconds));
        this.tailMilliseconds = tailMilliseconds;
        device = enumerator.GetDevice(id);
        if (device.ID != id || device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active ||
            device.FriendlyName != "Line In (Realtek(R) Audio)" || device.AudioEndpointVolume.Mute)
            throw new InvalidOperationException("Expected the explicit active, unmuted Realtek Line In; no fallback or level changes.");
        before = ReadEndpointLevels();
        if (!before.HasUsableLevels)
            throw new InvalidOperationException("Explicit Line In has unavailable or zero channel levels; no settings changed.");
        capture = new WasapiCapture(device);
        if (capture.WaveFormat.SampleRate != 48000 || capture.WaveFormat.Channels != 2 || capture.WaveFormat.BitsPerSample != 32 ||
            !(capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || capture.WaveFormat is WaveFormatExtensible ext &&
              ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
            throw new InvalidOperationException("Line In float/48k/stereo format changed.");
        capture.DataAvailable += (_, e) =>
        {
            buffer.Append(e.Buffer.AsSpan(0, e.BytesRecorded));
            if (!buffer.IsValid)
                baselineReady.TrySetException(new InvalidDataException("Line In baseline capture is invalid; no audio plan should be submitted."));
            else if (buffer.CapturedFrames >= LineInMeasurementBuffer.RequiredBaselineFrames)
                baselineReady.TrySetResult(true);
            int target = Volatile.Read(ref tailTargetFrames);
            if (target > 0 && (!buffer.IsValid || buffer.CapturedFrames >= target)) tailReady.TrySetResult(true);
        };
        capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
    }

    internal async Task StartAsync()
    {
        long start = Stopwatch.GetTimestamp();
        capture.StartRecording();
        // Readiness is actual captured frames, not a wall-clock sleep that can
        // expire before WASAPI has delivered any samples. Stop/error wins over
        // an apparently ready baseline; no plan has been sent at this point.
        TimeSpan remaining = TimeSpan.FromSeconds(2) - Stopwatch.GetElapsedTime(start);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Line In start exceeded the measured-baseline deadline; no audio plan sent.");
        await Task.WhenAny(baselineReady.Task, stopped.Task).WaitAsync(remaining);
        if (stopped.Task.IsCompleted)
            throw new IOException("Line In stopped before the measured baseline was ready.", await stopped.Task);
        await baselineReady.Task;
        baselineFrames = buffer.CapturedFrames;
        baselineWaitMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (!buffer.IsValid || baselineFrames < LineInMeasurementBuffer.RequiredBaselineFrames)
            throw new InvalidDataException("Actual Line In baseline not established; no audio plan sent.");
    }

    internal async Task<object> StopAsync()
    {
        var invalidReasons = new List<string>();
        int framesAtResponse = buffer.CapturedFrames;
        int requiredTailFrames = tailMilliseconds * LineInMeasurementBuffer.SampleRate / 1000;
        int target = checked(framesAtResponse + requiredTailFrames);
        Volatile.Write(ref tailTargetFrames, target);
        if (!buffer.IsValid || buffer.CapturedFrames >= target) tailReady.TrySetResult(true);
        long tailStart = Stopwatch.GetTimestamp();
        try
        {
            // Actual samples after the response, not a sleep that could hide a
            // capture which stopped after baseline. Two extra seconds admit
            // normal WASAPI buffer granularity without an unbounded wait.
            await Task.WhenAny(tailReady.Task, stopped.Task).
                WaitAsync(TimeSpan.FromMilliseconds(tailMilliseconds + 2000));
        }
        catch (TimeoutException) { invalidReasons.Add("MeasuredCaptureTailDeadline"); }
        if (stopped.Task.IsCompleted) invalidReasons.Add("CaptureStoppedBeforeRequestedStop");
        double tailWaitMs = Stopwatch.GetElapsedTime(tailStart).TotalMilliseconds;
        capture.StopRecording();
        var error = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        LineInBufferSummary summary = buffer.Analyze();
        if (error != null) invalidReasons.Add("CaptureStoppedWithError");
        if (summary.CapturedFrames < LineInMeasurementBuffer.RequiredBaselineFrames || baselineFrames < LineInMeasurementBuffer.RequiredBaselineFrames)
            invalidReasons.Add("InsufficientMeasuredBaseline");
        if (!LineInMeasurementBuffer.HasCompletePostSubmitCapture(baselineFrames, framesAtResponse, summary.CapturedFrames, tailMilliseconds))
            invalidReasons.Add("InsufficientMeasuredPostSubmitTail");
        if (summary.DroppedFrames != 0) invalidReasons.Add("CaptureBufferOverflow");
        if (summary.NonfiniteSamples != 0) invalidReasons.Add("NonfiniteSamples");
        if (summary.UnalignedBytes != 0) invalidReasons.Add("UnalignedCaptureData");
        EndpointLevels? after = null;
        try
        {
            after = ReadEndpointLevels();
            if (!after.HasUsableLevels) invalidReasons.Add("EndpointLevelsUnavailableOrMuted");
            if (before.Mute != after.Mute || before.MasterScalar != after.MasterScalar ||
                !before.ChannelScalars.SequenceEqual(after.ChannelScalars)) invalidReasons.Add("EndpointLevelsChanged");
        }
        catch { invalidReasons.Add("EndpointLevelsReadFailed"); }
        return new { Capture = device.ID, summary.CapturedFrames, summary.ObservedFrames, CaptureTailMs = tailMilliseconds,
            StoredAudio = false, CaptureValid = invalidReasons.Count == 0, InvalidReasons = invalidReasons,
            summary.DroppedFrames, BufferOverflow = summary.DroppedFrames != 0, summary.NonfiniteSamples, summary.UnalignedBytes,
            BaselineFramesAtSubmission = baselineFrames, BaselineWaitMs = baselineWaitMs,
            FramesAtResponse = framesAtResponse, RequiredTailFrames = requiredTailFrames,
            MeasuredTailFrames = summary.CapturedFrames - framesAtResponse, TailWaitMs = tailWaitMs,
            CaptureEndpointVolume = after?.MasterScalar, EndpointBefore = before, EndpointAfter = after, summary.Blocks };
    }

    private EndpointLevels ReadEndpointLevels()
    {
        var volume = device.AudioEndpointVolume;
        int channels = volume.Channels.Count;
        if (channels is < 1 or > 32) throw new InvalidDataException("Unexpected Line In volume-channel count.");
        var scalars = new float[channels];
        for (int channel = 0; channel < channels; channel++) scalars[channel] = volume.Channels[channel].VolumeLevelScalar;
        float master = volume.MasterVolumeLevelScalar;
        if (!float.IsFinite(master) || master < 0 || master > 1 ||
            scalars.Any(value => !float.IsFinite(value) || value < 0 || value > 1))
            throw new InvalidDataException("Line In endpoint levels are not finite normalized scalars.");
        return new(volume.Mute, master, scalars);
    }

    private sealed record EndpointLevels(bool Mute, float MasterScalar, float[] ChannelScalars)
    {
        public bool HasUsableLevels => !Mute && float.IsFinite(MasterScalar) && MasterScalar > 0 && MasterScalar <= 1 &&
            ChannelScalars.All(value => float.IsFinite(value) && value > 0 && value <= 1);
    }

    public void Dispose()
    {
        capture.Dispose();
        buffer.Clear();
        device.Dispose();
        enumerator.Dispose();
    }
}
