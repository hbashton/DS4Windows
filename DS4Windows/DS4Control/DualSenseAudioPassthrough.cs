using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DS4Windows
{
    public sealed class DualSenseAudioPassthrough : IDisposable
    {
        private const int ControllerCount = ControlService.MAX_DS4_CONTROLLER_COUNT;
        private readonly object syncRoot = new object();
        private readonly SlotPlayback[] slots = new SlotPlayback[ControllerCount];
        private WasapiLoopbackCapture capture;
        private WaveFormat captureFormat;
        private string captureEndpointId = string.Empty;

        public void Start(int slot, byte speakerVolume, string requestedCaptureEndpointId)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            lock (syncRoot)
            {
                requestedCaptureEndpointId ??= string.Empty;
                MMDevice endpoint = FindControllerEndpoint(slot);
                if (endpoint == null)
                {
                    AppLogger.LogToGui("DualSense audio passthrough could not find a controller speaker endpoint.", true);
                    Stop(slot);
                    return;
                }

                if (slots[slot] != null && string.Equals(slots[slot].EndpointId, endpoint.ID, StringComparison.Ordinal))
                {
                    slots[slot].SpeakerVolume = speakerVolume;
                    EnsureCaptureStarted(requestedCaptureEndpointId);
                    return;
                }

                Stop(slot);

                try
                {
                    WaveFormat outputFormat = endpoint.AudioClient.MixFormat;
                    var provider = new BufferedWaveProvider(outputFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(250),
                        DiscardOnBufferOverflow = true,
                    };

                    var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 40);
                    output.Init(provider);
                    output.Play();

                    slots[slot] = new SlotPlayback(endpoint.ID, output, provider, outputFormat, speakerVolume);
                    EnsureCaptureStarted(requestedCaptureEndpointId);
                    AppLogger.LogToGui($"DualSense audio passthrough started for controller {slot + 1}: {endpoint.FriendlyName}", false);
                }
                catch (Exception ex)
                {
                    AppLogger.LogToGui($"DualSense audio passthrough failed to start: {ex.Message}", true);
                    Stop(slot);
                }
            }
        }

        public void Stop(int slot)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            lock (syncRoot)
            {
                SlotPlayback playback = slots[slot];
                slots[slot] = null;
                playback?.Dispose();

                if (!slots.Any(item => item != null))
                {
                    StopCapture();
                }
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i]?.Dispose();
                    slots[i] = null;
                }

                StopCapture();
            }
        }

        private void EnsureCaptureStarted(string requestedCaptureEndpointId)
        {
            requestedCaptureEndpointId ??= string.Empty;

            if (capture != null && string.Equals(captureEndpointId, requestedCaptureEndpointId, StringComparison.Ordinal))
            {
                return;
            }

            StopCapture();

            MMDevice sourceEndpoint = FindCaptureEndpoint(requestedCaptureEndpointId);
            capture = sourceEndpoint != null ? new WasapiLoopbackCapture(sourceEndpoint) : new WasapiLoopbackCapture();
            captureEndpointId = requestedCaptureEndpointId;
            captureFormat = capture.WaveFormat;
            capture.DataAvailable += Capture_DataAvailable;
            capture.RecordingStopped += Capture_RecordingStopped;
            capture.StartRecording();

            string sourceName = sourceEndpoint?.FriendlyName ?? "Default";
            AppLogger.LogToGui($"DualSense audio passthrough capture source: {sourceName}", false);
        }

        private void StopCapture()
        {
            WasapiLoopbackCapture oldCapture = capture;
            capture = null;
            captureFormat = null;
            captureEndpointId = string.Empty;

            if (oldCapture == null)
            {
                return;
            }

            oldCapture.DataAvailable -= Capture_DataAvailable;
            oldCapture.RecordingStopped -= Capture_RecordingStopped;

            try
            {
                oldCapture.StopRecording();
            }
            catch { }

            oldCapture.Dispose();
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                AppLogger.LogToGui($"DualSense audio passthrough capture stopped: {e.Exception.Message}", true);
            }
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            lock (syncRoot)
            {
                if (captureFormat == null || captureFormat.Encoding != WaveFormatEncoding.IeeeFloat ||
                    captureFormat.BitsPerSample != 32 || captureFormat.Channels < 1)
                {
                    return;
                }

                int frames = e.BytesRecorded / (sizeof(float) * captureFormat.Channels);
                if (frames <= 0)
                {
                    return;
                }

                foreach (SlotPlayback slot in slots)
                {
                    slot?.WriteFromCapture(e.Buffer, frames, captureFormat);
                }
            }
        }

        private MMDevice FindControllerEndpoint(int slot)
        {
            HashSet<string> usedIds = slots
                .Where((item, index) => item != null && index != slot)
                .Select(item => item.EndpointId)
                .ToHashSet(StringComparer.Ordinal);

            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(device => !usedIds.Contains(device.ID) && IsDualSenseEndpoint(device));
        }

        private static MMDevice FindCaptureEndpoint(string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                return null;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice endpoint = enumerator.GetDevice(endpointId);
                return endpoint?.State == DeviceState.Active ? endpoint : null;
            }
            catch
            {
                AppLogger.LogToGui("DualSense audio passthrough capture source was not found. Falling back to default audio endpoint.", true);
                return null;
            }
        }

        private static bool IsDualSenseEndpoint(MMDevice device)
        {
            string friendly = device.FriendlyName ?? string.Empty;
            string deviceFriendly = device.DeviceFriendlyName ?? string.Empty;
            string text = $"{friendly} {deviceFriendly}";

            return text.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("PS5", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class SlotPlayback : IDisposable
        {
            private readonly WasapiOut output;
            private readonly BufferedWaveProvider provider;
            private readonly WaveFormat outputFormat;
            private readonly byte[] outputBuffer;

            public string EndpointId { get; }
            public byte SpeakerVolume { get; set; }

            public SlotPlayback(string endpointId, WasapiOut output, BufferedWaveProvider provider,
                WaveFormat outputFormat, byte speakerVolume)
            {
                EndpointId = endpointId;
                this.output = output;
                this.provider = provider;
                this.outputFormat = outputFormat;
                SpeakerVolume = speakerVolume;
                outputBuffer = new byte[4096 * outputFormat.BlockAlign];
            }

            public void WriteFromCapture(byte[] captureBuffer, int frames, WaveFormat captureFormat)
            {
                int framesToWrite = Math.Min(frames, outputBuffer.Length / outputFormat.BlockAlign);
                float volume = SpeakerVolume / 255.0f;
                Array.Clear(outputBuffer, 0, framesToWrite * outputFormat.BlockAlign);

                for (int frame = 0; frame < framesToWrite; frame++)
                {
                    int captureOffset = frame * captureFormat.BlockAlign;
                    float left = BitConverter.ToSingle(captureBuffer, captureOffset);
                    float right = captureFormat.Channels > 1 ?
                        BitConverter.ToSingle(captureBuffer, captureOffset + sizeof(float)) : left;
                    float mono = Math.Clamp((left + right) * 0.5f * volume, -1.0f, 1.0f);

                    int outputOffset = frame * outputFormat.BlockAlign;
                    if (outputFormat.Encoding == WaveFormatEncoding.IeeeFloat && outputFormat.BitsPerSample == 32)
                    {
                        int speakerChannel = outputFormat.Channels >= 4 ? 1 : 0;
                        Buffer.BlockCopy(BitConverter.GetBytes(mono), 0,
                            outputBuffer, outputOffset + speakerChannel * sizeof(float), sizeof(float));
                    }
                    else if (outputFormat.Encoding == WaveFormatEncoding.Pcm && outputFormat.BitsPerSample == 16)
                    {
                        short sample = (short)Math.Clamp(mono * short.MaxValue, short.MinValue, short.MaxValue);
                        int speakerChannel = outputFormat.Channels >= 4 ? 1 : 0;
                        Buffer.BlockCopy(BitConverter.GetBytes(sample), 0,
                            outputBuffer, outputOffset + speakerChannel * sizeof(short), sizeof(short));
                    }
                }

                provider.AddSamples(outputBuffer, 0, framesToWrite * outputFormat.BlockAlign);
            }

            public void Dispose()
            {
                try
                {
                    output.Stop();
                }
                catch { }

                output.Dispose();
            }
        }
    }
}
