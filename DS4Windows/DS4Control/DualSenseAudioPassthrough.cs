using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using DS4Windows.InputDevices;

namespace DS4Windows
{
    public sealed class DualSenseAudioPassthrough : IDisposable
    {
        private const int ControllerCount = ControlService.MAX_DS4_CONTROLLER_COUNT;
        private readonly object syncRoot = new object();
        private readonly IAudioPassthroughSlot[] slots = new IAudioPassthroughSlot[ControllerCount];
        private WasapiLoopbackCapture capture;
        private WaveFormat captureFormat;
        private string captureEndpointId = string.Empty;
        private string captureSpeakerEndpointId = string.Empty;

        public void Start(int slot, DualSenseDevice device, byte speakerVolume, string requestedCaptureEndpointId,
            string requestedSpeakerEndpointId)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            lock (syncRoot)
            {
                requestedCaptureEndpointId ??= string.Empty;
                requestedSpeakerEndpointId ??= string.Empty;

                if (device?.getConnectionType() == ConnectionType.BT)
                {
                    if (slots[slot] is BluetoothHapticsSlot bluetoothSlot && ReferenceEquals(bluetoothSlot.Device, device))
                    {
                        bluetoothSlot.SpeakerVolume = speakerVolume;
                        EnsureCaptureStarted(requestedCaptureEndpointId, string.Empty);
                        return;
                    }

                    Stop(slot);
                    slots[slot] = new BluetoothHapticsSlot(device, speakerVolume);
                    EnsureCaptureStarted(requestedCaptureEndpointId, string.Empty);
                    AppLogger.LogToGui($"DualSense Bluetooth audio passthrough started for controller {slot + 1}", false);
                    return;
                }

                MMDevice endpoint = FindControllerEndpoint(slot, requestedSpeakerEndpointId);
                if (endpoint == null)
                {
                    AppLogger.LogToGui("DualSense audio passthrough could not find a controller speaker endpoint.", true);
                    Stop(slot);
                    return;
                }

                if (slots[slot] is EndpointPlaybackSlot endpointSlot &&
                    string.Equals(endpointSlot.EndpointId, endpoint.ID, StringComparison.Ordinal))
                {
                    endpointSlot.SpeakerVolume = speakerVolume;
                    EnsureCaptureStarted(requestedCaptureEndpointId, endpoint.ID);
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

                    slots[slot] = new EndpointPlaybackSlot(endpoint.ID, output, provider, outputFormat, speakerVolume);
                    EnsureCaptureStarted(requestedCaptureEndpointId, endpoint.ID);
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
                IAudioPassthroughSlot playback = slots[slot];
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

        private void EnsureCaptureStarted(string requestedCaptureEndpointId, string speakerEndpointId)
        {
            requestedCaptureEndpointId ??= string.Empty;
            speakerEndpointId ??= string.Empty;

            if (capture != null &&
                string.Equals(captureEndpointId, requestedCaptureEndpointId, StringComparison.Ordinal) &&
                string.Equals(captureSpeakerEndpointId, speakerEndpointId, StringComparison.Ordinal))
            {
                return;
            }

            StopCapture();

            MMDevice sourceEndpoint = FindCaptureEndpoint(requestedCaptureEndpointId, speakerEndpointId);
            if (sourceEndpoint == null && DefaultEndpointMatches(speakerEndpointId))
            {
                AppLogger.LogToGui("DualSense audio passthrough cannot capture from the same endpoint it is playing to.", true);
                return;
            }

            try
            {
                capture = sourceEndpoint != null ? new WasapiLoopbackCapture(sourceEndpoint) : new WasapiLoopbackCapture();
                captureEndpointId = requestedCaptureEndpointId;
                captureSpeakerEndpointId = speakerEndpointId;
                captureFormat = capture.WaveFormat;
                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;
                capture.StartRecording();

                string sourceName = sourceEndpoint?.FriendlyName ?? "Default";
                AppLogger.LogToGui($"DualSense audio passthrough capture source: {sourceName}", false);
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"DualSense audio passthrough capture failed to start: {ex.Message}", true);
                StopCapture();
            }
        }

        private void StopCapture()
        {
            WasapiLoopbackCapture oldCapture = capture;
            capture = null;
            captureFormat = null;
            captureEndpointId = string.Empty;
            captureSpeakerEndpointId = string.Empty;

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

            lock (syncRoot)
            {
                if (ReferenceEquals(sender, capture))
                {
                    capture = null;
                    captureFormat = null;
                    captureEndpointId = string.Empty;
                    captureSpeakerEndpointId = string.Empty;
                }
            }
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            lock (syncRoot)
            {
                if (captureFormat == null || captureFormat.Channels < 1)
                {
                    return;
                }

                int frames = e.BytesRecorded / captureFormat.BlockAlign;
                if (frames <= 0)
                {
                    return;
                }

                foreach (IAudioPassthroughSlot slot in slots)
                {
                    slot?.WriteFromCapture(e.Buffer, frames, captureFormat);
                }
            }
        }

        private MMDevice FindControllerEndpoint(int slot, string requestedSpeakerEndpointId)
        {
            HashSet<string> usedIds = slots
                .Where((item, index) => item != null && index != slot)
                .Select(item => item.EndpointId)
                .ToHashSet(StringComparer.Ordinal);

            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(requestedSpeakerEndpointId))
            {
                try
                {
                    MMDevice requested = enumerator.GetDevice(requestedSpeakerEndpointId);
                    if (requested.State == DeviceState.Active && !usedIds.Contains(requested.ID))
                    {
                        return requested;
                    }
                }
                catch
                {
                    AppLogger.LogToGui("Selected DualSense speaker endpoint was not found. Falling back to auto-detect.", true);
                }
            }

            MMDevice autoEndpoint = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(device => !usedIds.Contains(device.ID) && IsDualSenseEndpoint(device));

            if (autoEndpoint == null)
            {
                string endpointNames = string.Join(", ", enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Where(device => !usedIds.Contains(device.ID))
                    .Select(endpoint => endpoint.FriendlyName));

                AppLogger.LogToGui(
                    string.IsNullOrEmpty(endpointNames) ?
                        "No active Windows playback endpoints were found for DualSense speaker passthrough." :
                        $"DualSense speaker auto-detect failed. Active playback endpoints: {endpointNames}",
                    true);
            }

            return autoEndpoint;
        }

        private static MMDevice FindCaptureEndpoint(string endpointId, string speakerEndpointId)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                return null;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice endpoint = enumerator.GetDevice(endpointId);
                if (endpoint?.State != DeviceState.Active)
                {
                    return null;
                }

                if (string.Equals(endpoint.ID, speakerEndpointId, StringComparison.Ordinal))
                {
                    AppLogger.LogToGui("DualSense audio passthrough capture source cannot be the same as the speaker endpoint. Falling back to default audio endpoint.", true);
                    return null;
                }

                return endpoint;
            }
            catch
            {
                AppLogger.LogToGui("DualSense audio passthrough capture source was not found. Falling back to default audio endpoint.", true);
                return null;
            }
        }

        private static bool DefaultEndpointMatches(string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                return false;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return string.Equals(endpoint?.ID, endpointId, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsDualSenseEndpoint(MMDevice device)
        {
            string friendly = device.FriendlyName ?? string.Empty;
            string deviceFriendly = device.DeviceFriendlyName ?? string.Empty;
            string text = $"{friendly} {deviceFriendly}";

            return text.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("PS5", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private interface IAudioPassthroughSlot : IDisposable
        {
            string EndpointId { get; }
            byte SpeakerVolume { get; set; }
            void WriteFromCapture(byte[] captureBuffer, int frames, WaveFormat captureFormat);
        }

        private sealed class EndpointPlaybackSlot : IAudioPassthroughSlot
        {
            private readonly WasapiOut output;
            private readonly BufferedWaveProvider provider;
            private readonly WaveFormat outputFormat;
            private readonly byte[] outputBuffer;
            private double sourceFramePosition;

            public string EndpointId { get; }
            public byte SpeakerVolume { get; set; }

            public EndpointPlaybackSlot(string endpointId, WasapiOut output, BufferedWaveProvider provider,
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
                if (captureFormat.SampleRate <= 0 || outputFormat.SampleRate <= 0)
                {
                    return;
                }

                int maxFramesToWrite = outputBuffer.Length / outputFormat.BlockAlign;
                int framesToWrite = 0;
                double step = captureFormat.SampleRate / (double)outputFormat.SampleRate;
                float volume = SpeakerVolume / 255.0f;
                Array.Clear(outputBuffer, 0, outputBuffer.Length);

                while (sourceFramePosition < frames && framesToWrite < maxFramesToWrite)
                {
                    int frame = (int)sourceFramePosition;
                    int captureOffset = frame * captureFormat.BlockAlign;
                    float left = ReadSample(captureBuffer, captureOffset, captureFormat);
                    float right = captureFormat.Channels > 1 ?
                        ReadSample(captureBuffer, captureOffset + BytesPerSample(captureFormat), captureFormat) : left;
                    float mono = Math.Clamp((left + right) * 0.5f * volume, -1.0f, 1.0f);

                    int outputOffset = framesToWrite * outputFormat.BlockAlign;
                    int speakerChannel = outputFormat.Channels >= 4 ? 1 : 0;
                    WriteSample(outputBuffer, outputOffset + speakerChannel * BytesPerSample(outputFormat),
                        outputFormat, mono);

                    framesToWrite++;
                    sourceFramePosition += step;
                }

                sourceFramePosition = sourceFramePosition >= frames ? sourceFramePosition - frames : 0.0;

                if (framesToWrite > 0)
                {
                    provider.AddSamples(outputBuffer, 0, framesToWrite * outputFormat.BlockAlign);
                }
            }

            private static int BytesPerSample(WaveFormat format)
            {
                return Math.Max(1, format.BitsPerSample / 8);
            }

            private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
            {
                if (offset < 0 || offset + BytesPerSample(format) > buffer.Length)
                {
                    return 0.0f;
                }

                if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    if (format.BitsPerSample == 32)
                    {
                        return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1.0f, 1.0f);
                    }

                    if (format.BitsPerSample == 64)
                    {
                        return Math.Clamp((float)BitConverter.ToDouble(buffer, offset), -1.0f, 1.0f);
                    }
                }

                if (format.Encoding != WaveFormatEncoding.Pcm)
                {
                    return 0.0f;
                }

                return format.BitsPerSample switch
                {
                    16 => BitConverter.ToInt16(buffer, offset) / 32768.0f,
                    24 => ReadInt24(buffer, offset) / 8388608.0f,
                    32 => BitConverter.ToInt32(buffer, offset) / 2147483648.0f,
                    _ => 0.0f,
                };
            }

            private static int ReadInt24(byte[] buffer, int offset)
            {
                int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((sample & 0x800000) != 0)
                {
                    sample |= unchecked((int)0xFF000000);
                }

                return sample;
            }

            private static void WriteSample(byte[] buffer, int offset, WaveFormat format, float value)
            {
                if (offset < 0 || offset + BytesPerSample(format) > buffer.Length)
                {
                    return;
                }

                value = Math.Clamp(value, -1.0f, 1.0f);
                if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, sizeof(float));
                }
                else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
                {
                    short sample = (short)Math.Clamp(value * short.MaxValue, (float)short.MinValue, short.MaxValue);
                    Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, buffer, offset, sizeof(short));
                }
                else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
                {
                    int sample = (int)Math.Clamp(value * 8388607.0f, -8388608.0f, 8388607.0f);
                    buffer[offset] = (byte)(sample & 0xFF);
                    buffer[offset + 1] = (byte)((sample >> 8) & 0xFF);
                    buffer[offset + 2] = (byte)((sample >> 16) & 0xFF);
                }
                else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
                {
                    int sample = (int)Math.Clamp(value * int.MaxValue, (float)int.MinValue, int.MaxValue);
                    Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, buffer, offset, sizeof(int));
                }
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

        private sealed class BluetoothHapticsSlot : IAudioPassthroughSlot
        {
            private const int OutputSampleRate = 3000;
            private const int PacketSampleCount = 64;
            private const int MaxPendingSamples = OutputSampleRate / 2;
            private const int MaxReportsPerCaptureEvent = 8;
            private const int MaxConsecutiveWriteFailures = 10;
            private readonly List<byte> pendingSamples = new List<byte>(PacketSampleCount * 2);
            private double sourceFramePosition;
            private byte sequence;
            private int consecutiveWriteFailures;
            private bool disabled;

            public DualSenseDevice Device { get; }
            public string EndpointId => string.Empty;
            public byte SpeakerVolume { get; set; }

            public BluetoothHapticsSlot(DualSenseDevice device, byte speakerVolume)
            {
                Device = device;
                SpeakerVolume = speakerVolume;
            }

            public void WriteFromCapture(byte[] captureBuffer, int frames, WaveFormat captureFormat)
            {
                if (disabled || Device == null || Device.IsRemoved || Device.getConnectionType() != ConnectionType.BT ||
                    captureFormat.SampleRate <= 0)
                {
                    return;
                }

                double step = captureFormat.SampleRate / (double)OutputSampleRate;
                float volume = SpeakerVolume / 255.0f;

                while (sourceFramePosition < frames)
                {
                    int frame = (int)sourceFramePosition;
                    int captureOffset = frame * captureFormat.BlockAlign;
                    float left = ReadSample(captureBuffer, captureOffset, captureFormat);
                    float right = captureFormat.Channels > 1 ?
                        ReadSample(captureBuffer, captureOffset + BytesPerSample(captureFormat), captureFormat) : left;
                    float mono = Math.Clamp((left + right) * 0.5f * volume, -1.0f, 1.0f);
                    sbyte sample = (sbyte)Math.Clamp(mono * 127.0f, -128.0f, 127.0f);
                    pendingSamples.Add(unchecked((byte)sample));

                    if (pendingSamples.Count > MaxPendingSamples)
                    {
                        int samplesToDrop = pendingSamples.Count - PacketSampleCount;
                        pendingSamples.RemoveRange(0, samplesToDrop);
                    }

                    sourceFramePosition += step;
                }

                sourceFramePosition -= frames;

                int reportsWritten = 0;
                while (pendingSamples.Count >= PacketSampleCount && reportsWritten < MaxReportsPerCaptureEvent)
                {
                    byte[] packetSamples = new byte[PacketSampleCount];
                    pendingSamples.CopyTo(0, packetSamples, 0, PacketSampleCount);
                    pendingSamples.RemoveRange(0, PacketSampleCount);

                    if (Device.WriteBluetoothHapticsAudioReport(packetSamples, ++sequence))
                    {
                        consecutiveWriteFailures = 0;
                    }
                    else if (++consecutiveWriteFailures >= MaxConsecutiveWriteFailures)
                    {
                        disabled = true;
                        pendingSamples.Clear();
                        AppLogger.LogToGui("DualSense Bluetooth audio passthrough stopped after repeated HID write failures.", true);
                        return;
                    }

                    reportsWritten++;
                }

                if (reportsWritten >= MaxReportsPerCaptureEvent && pendingSamples.Count > PacketSampleCount)
                {
                    pendingSamples.RemoveRange(0, pendingSamples.Count - PacketSampleCount);
                }
            }

            public void Dispose()
            {
                pendingSamples.Clear();
            }

            private static int BytesPerSample(WaveFormat format)
            {
                return Math.Max(1, format.BitsPerSample / 8);
            }

            private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
            {
                if (offset < 0 || offset + BytesPerSample(format) > buffer.Length)
                {
                    return 0.0f;
                }

                if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    if (format.BitsPerSample == 32)
                    {
                        return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1.0f, 1.0f);
                    }

                    if (format.BitsPerSample == 64)
                    {
                        return Math.Clamp((float)BitConverter.ToDouble(buffer, offset), -1.0f, 1.0f);
                    }
                }

                if (format.Encoding != WaveFormatEncoding.Pcm)
                {
                    return 0.0f;
                }

                return format.BitsPerSample switch
                {
                    16 => BitConverter.ToInt16(buffer, offset) / 32768.0f,
                    24 => ReadInt24(buffer, offset) / 8388608.0f,
                    32 => BitConverter.ToInt32(buffer, offset) / 2147483648.0f,
                    _ => 0.0f,
                };
            }

            private static int ReadInt24(byte[] buffer, int offset)
            {
                int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((sample & 0x800000) != 0)
                {
                    sample |= unchecked((int)0xFF000000);
                }

                return sample;
            }
        }
    }
}
