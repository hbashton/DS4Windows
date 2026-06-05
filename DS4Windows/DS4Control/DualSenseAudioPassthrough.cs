using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DS4Windows.InputDevices;

namespace DS4Windows
{
    public sealed class DualSenseAudioPassthrough : IDisposable
    {
        private const int ControllerCount = ControlService.MAX_DS4_CONTROLLER_COUNT;
        private readonly object syncRoot = new object();
        private readonly SlotPlayback[] slots = new SlotPlayback[ControllerCount];
        private readonly SlotBtPlayback[] btSlots = new SlotBtPlayback[ControllerCount];
        private WasapiLoopbackCapture capture;
        private WaveFormat captureFormat;
        private string captureEndpointId = string.Empty;

        public void Start(int slot, DualSenseDevice device, byte speakerVolume, string requestedCaptureEndpointId,
            string requestedSpeakerEndpointId)
        {
            if (device != null && device.ConnectionType == ConnectionType.BT)
            {
                StartBluetooth(slot, device, speakerVolume, requestedCaptureEndpointId);
                return;
            }

            Start(slot, speakerVolume, requestedCaptureEndpointId, requestedSpeakerEndpointId);
        }

        public void Start(int slot, byte speakerVolume, string requestedCaptureEndpointId,
            string requestedSpeakerEndpointId)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            lock (syncRoot)
            {
                StopBluetooth(slot);
                requestedCaptureEndpointId ??= string.Empty;
                requestedSpeakerEndpointId ??= string.Empty;
                MMDevice endpoint = FindControllerEndpoint(slot, requestedSpeakerEndpointId);
                if (endpoint == null)
                {
                    AppLogger.LogToGui("DualSense audio passthrough could not find a controller speaker endpoint.", true);
                    Stop(slot);
                    return;
                }

                if (slots[slot] != null && string.Equals(slots[slot].EndpointId, endpoint.ID, StringComparison.Ordinal))
                {
                    slots[slot].SpeakerVolume = speakerVolume;
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

                    slots[slot] = new SlotPlayback(endpoint.ID, output, provider, outputFormat, speakerVolume);
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

        private void StartBluetooth(int slot, DualSenseDevice device, byte speakerVolume, string requestedCaptureEndpointId)
        {
            if (slot < 0 || slot >= btSlots.Length)
            {
                return;
            }

            lock (syncRoot)
            {
                StopWindowsEndpoint(slot);
                requestedCaptureEndpointId ??= string.Empty;

                StopBluetooth(slot);
                StopCapture();
                LogBluetoothAudioDiagnostics(slot, requestedCaptureEndpointId);
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
                StopWindowsEndpoint(slot);
                StopBluetooth(slot);

                if (!HasActivePlayback())
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
                    btSlots[i]?.Dispose();
                    btSlots[i] = null;
                }

                StopCapture();
            }
        }

        private void StopWindowsEndpoint(int slot)
        {
            SlotPlayback playback = slots[slot];
            slots[slot] = null;
            playback?.Dispose();
        }

        private void StopBluetooth(int slot)
        {
            SlotBtPlayback playback = btSlots[slot];
            btSlots[slot] = null;
            playback?.Dispose();
        }

        private bool HasActivePlayback()
        {
            return slots.Any(item => item != null) || btSlots.Any(item => item != null);
        }

        private void EnsureCaptureStarted(string requestedCaptureEndpointId, string speakerEndpointId)
        {
            requestedCaptureEndpointId ??= string.Empty;
            speakerEndpointId ??= string.Empty;

            if (capture != null && string.Equals(captureEndpointId, requestedCaptureEndpointId, StringComparison.Ordinal))
            {
                return;
            }

            StopCapture();

            MMDevice sourceEndpoint = FindCaptureEndpoint(requestedCaptureEndpointId, speakerEndpointId);
            if (sourceEndpoint == null && string.IsNullOrEmpty(requestedCaptureEndpointId) && DefaultEndpointMatches(speakerEndpointId))
            {
                AppLogger.LogToGui("DualSense audio passthrough cannot capture from the same endpoint it is playing to.", true);
                return;
            }

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
                if (captureFormat == null || captureFormat.Channels < 1)
                {
                    return;
                }

                int frames = e.BytesRecorded / captureFormat.BlockAlign;
                if (frames <= 0)
                {
                    return;
                }

                foreach (SlotPlayback slot in slots)
                {
                    slot?.WriteFromCapture(e.Buffer, frames, captureFormat);
                }

                foreach (SlotBtPlayback slot in btSlots)
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
                text.IndexOf("PS5", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("VirtualPad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Virtual DualSense", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeDualSenseAudioEndpoint(MMDevice device)
        {
            if (!IsDualSenseEndpoint(device))
            {
                return false;
            }

            try
            {
                WaveFormat format = device.AudioClient.MixFormat;
                return format != null && format.SampleRate == 48000 && format.Channels >= 4;
            }
            catch
            {
                return false;
            }
        }

        private static void LogBluetoothAudioDiagnostics(int slot, string requestedCaptureEndpointId)
        {
            AppLogger.LogToGui(
                $"DualSense Bluetooth speaker passthrough was requested for controller {slot + 1}, but the direct HID path only carries haptics packets. Audible Bluetooth speaker audio requires a Windows/Nefarius audio endpoint path.",
                true);

            LogVirtualPadRuntimeDiagnostics();
            LogAudioEndpointDiagnostics(requestedCaptureEndpointId);
        }

        private static void LogVirtualPadRuntimeDiagnostics()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string runtimeDll = Path.Combine(baseDir, "Nefarius.VirtualPad.Runtime.dll");
                string abstractionsDll = Path.Combine(baseDir, "Nefarius.VirtualPad.Runtime.Abstractions.dll");

                AppLogger.LogToGui(
                    $"DualSense BT audio diagnostics: VirtualPad runtime DLL present={File.Exists(runtimeDll)}, abstractions DLL present={File.Exists(abstractionsDll)}, baseDir={baseDir}",
                    false);
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"DualSense BT audio diagnostics: VirtualPad runtime probe failed: {ex.Message}", true);
            }
        }

        private static void LogAudioEndpointDiagnostics(string requestedCaptureEndpointId)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice defaultRender = null;
                try
                {
                    defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch { }

                AppLogger.LogToGui(
                    $"DualSense BT audio diagnostics: default render endpoint={(defaultRender == null ? "<none>" : EndpointSummary(defaultRender))}",
                    false);

                if (!string.IsNullOrEmpty(requestedCaptureEndpointId))
                {
                    try
                    {
                        MMDevice requestedCapture = enumerator.GetDevice(requestedCaptureEndpointId);
                        AppLogger.LogToGui(
                            $"DualSense BT audio diagnostics: selected capture/source render endpoint={EndpointSummary(requestedCapture)}",
                            false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogToGui($"DualSense BT audio diagnostics: selected capture/source endpoint missing: {ex.Message}", true);
                    }
                }

                var renderEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                AppLogger.LogToGui($"DualSense BT audio diagnostics: active render endpoint count={renderEndpoints.Count}", false);

                foreach (MMDevice endpoint in renderEndpoints)
                {
                    AppLogger.LogToGui(
                        $"DualSense BT audio diagnostics: render endpoint: {EndpointSummary(endpoint)}, dualsenseLike={IsDualSenseEndpoint(endpoint)}, dsAudioFormatLike={LooksLikeDualSenseAudioEndpoint(endpoint)}",
                        false);
                }

                if (!renderEndpoints.Any(LooksLikeDualSenseAudioEndpoint))
                {
                    AppLogger.LogToGui(
                        "DualSense BT audio diagnostics: no active 48 kHz 4-channel DualSense/VirtualPad render endpoint was found. This is the endpoint DSX-style Bluetooth speaker/haptics routing depends on.",
                        true);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"DualSense BT audio diagnostics failed while enumerating endpoints: {ex.Message}", true);
            }
        }

        private static string EndpointSummary(MMDevice endpoint)
        {
            string formatText = "<format unavailable>";
            try
            {
                WaveFormat format = endpoint.AudioClient.MixFormat;
                if (format != null)
                {
                    formatText = $"{format.SampleRate} Hz, {format.BitsPerSample}-bit, {format.Channels} channel, {format.Encoding}";
                }
            }
            catch { }

            return $"Name=\"{endpoint.FriendlyName}\", Device=\"{endpoint.DeviceFriendlyName}\", State={endpoint.State}, Format={formatText}, Id=\"{endpoint.ID}\"";
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
                    float left = ReadSample(captureBuffer, captureOffset, captureFormat);
                    float right = captureFormat.Channels > 1 ?
                        ReadSample(captureBuffer, captureOffset + BytesPerSample(captureFormat), captureFormat) : left;
                    float mono = Math.Clamp((left + right) * 0.5f * volume, -1.0f, 1.0f);

                    int outputOffset = frame * outputFormat.BlockAlign;
                    int speakerChannel = outputFormat.Channels >= 4 ? 1 : 0;
                    WriteSample(outputBuffer, outputOffset + speakerChannel * BytesPerSample(outputFormat),
                        outputFormat, mono);
                }

                provider.AddSamples(outputBuffer, 0, framesToWrite * outputFormat.BlockAlign);
            }

            public static int BytesPerSampleForBt(WaveFormat format)
            {
                return BytesPerSample(format);
            }

            public static float ReadSampleForBt(byte[] buffer, int offset, WaveFormat format)
            {
                return ReadSample(buffer, offset, format);
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

        private sealed class SlotBtPlayback : IDisposable
        {
            private readonly DualSenseBtAudioTransport transport;
            private readonly byte[] sampleBuffer = new byte[1024];
            private double sourceFramePosition;

            public DualSenseDevice Device { get; }
            public byte SpeakerVolume { get; set; }

            public SlotBtPlayback(DualSenseDevice device, byte speakerVolume)
            {
                Device = device;
                SpeakerVolume = speakerVolume;
                transport = new DualSenseBtAudioTransport(device);
            }

            public void WriteFromCapture(byte[] captureBuffer, int frames, WaveFormat captureFormat)
            {
                if (captureFormat.SampleRate <= 0 || captureFormat.Channels < 1)
                {
                    return;
                }

                double step = captureFormat.SampleRate / (double)DualSenseBtAudioTransport.TargetSampleRate;
                int outIndex = 0;
                float volume = SpeakerVolume / 255.0f;

                while ((int)sourceFramePosition < frames && outIndex + 1 < sampleBuffer.Length)
                {
                    int frame = (int)sourceFramePosition;
                    int captureOffset = frame * captureFormat.BlockAlign;
                    float left = SlotPlayback.ReadSampleForBt(captureBuffer, captureOffset, captureFormat) * volume;
                    float right = captureFormat.Channels > 1 ?
                        SlotPlayback.ReadSampleForBt(captureBuffer,
                            captureOffset + SlotPlayback.BytesPerSampleForBt(captureFormat), captureFormat) * volume :
                        left;

                    sampleBuffer[outIndex++] = FloatToSignedByte(left);
                    sampleBuffer[outIndex++] = FloatToSignedByte(right);
                    sourceFramePosition += step;
                }

                sourceFramePosition -= frames;
                if (sourceFramePosition < 0)
                {
                    sourceFramePosition = 0;
                }

                if (outIndex > 0)
                {
                    transport.AddSamples(sampleBuffer, 0, outIndex);
                }
            }

            private static byte FloatToSignedByte(float value)
            {
                sbyte sample = (sbyte)Math.Clamp(value * sbyte.MaxValue, sbyte.MinValue, sbyte.MaxValue);
                return unchecked((byte)sample);
            }

            public void Dispose()
            {
                transport.Dispose();
            }
        }
    }
}
