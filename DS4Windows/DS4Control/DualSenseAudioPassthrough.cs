using NAudio.CoreAudioApi;
using NAudio.Wave;
using DS4Windows.InputDevices;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    internal enum ControllerAudioEndpointKind
    {
        Any,
        DualShock4,
        DualSense,
    }

    public sealed class DualSenseAudioPassthrough : IDisposable
    {
        public const string AutoDetectGameAudioEndpointId = "DS4Windows:AutoDetectDualSenseGameAudio";
        public const string DefaultSystemAudioEndpointId = "DS4Windows:DefaultSystemAudio";

        private const string EndpointHistoryValueName = "{4b416b7d-8501-40c1-acfd-97aa9bdc17c8},1";
        private const string RenderEndpointRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\";
        private const int ControllerCount = ControlService.MAX_DS4_CONTROLLER_COUNT;
        private readonly object syncRoot = new object();
        private readonly SlotPlayback[] slots = new SlotPlayback[ControllerCount];
        private readonly DualSenseBluetoothSpeakerPassthrough[] bluetoothSlots = new DualSenseBluetoothSpeakerPassthrough[ControllerCount];
        private readonly int[] bluetoothStartGeneration = new int[ControllerCount];
        private WasapiLoopbackCapture capture;
        private WaveFormat captureFormat;
        private string captureEndpointId = string.Empty;
        private ControllerAudioEndpointKind captureEndpointKind;

        public void Start(int slot, DualSenseDevice dualSenseDevice, byte speakerVolume,
            DualSenseSpeakerCompression speakerCompression, byte speakerBassBoost,
            string requestedCaptureEndpointId, string requestedSpeakerEndpointId,
            OutContType emulatedControllerType)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            if (dualSenseDevice?.ConnectionType == ConnectionType.BT)
            {
                StartBluetooth(slot, dualSenseDevice, speakerVolume, speakerCompression,
                    speakerBassBoost, requestedCaptureEndpointId, emulatedControllerType);
                return;
            }

            StopBluetooth(slot);

            lock (syncRoot)
            {
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
                    EnsureCaptureStarted(requestedCaptureEndpointId, endpoint.ID,
                        GetEndpointKind(emulatedControllerType));
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
                    EnsureCaptureStarted(requestedCaptureEndpointId, endpoint.ID,
                        GetEndpointKind(emulatedControllerType));
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

                DualSenseBluetoothSpeakerPassthrough bluetoothPlayback = bluetoothSlots[slot];
                bluetoothSlots[slot] = null;
                bluetoothStartGeneration[slot]++;
                bluetoothPlayback?.Dispose();

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
                    bluetoothSlots[i]?.Dispose();
                    bluetoothSlots[i] = null;
                    bluetoothStartGeneration[i]++;
                }

                StopCapture();
            }
        }

        private void StartBluetooth(int slot, DualSenseDevice device, byte speakerVolume,
            DualSenseSpeakerCompression speakerCompression, byte speakerBassBoost,
            string requestedCaptureEndpointId, OutContType emulatedControllerType)
        {
            requestedCaptureEndpointId ??= string.Empty;
            ControllerAudioEndpointKind endpointKind = GetEndpointKind(emulatedControllerType);
            lock (syncRoot)
            {
                if (bluetoothSlots[slot]?.Matches(device, speakerVolume, speakerCompression,
                    speakerBassBoost, requestedCaptureEndpointId, endpointKind) == true)
                {
                    return;
                }

                SlotPlayback usbPlayback = slots[slot];
                slots[slot] = null;
                usbPlayback?.Dispose();
                if (!slots.Any(item => item != null))
                {
                    StopCapture();
                }

                DualSenseBluetoothSpeakerPassthrough previous = bluetoothSlots[slot];
                bluetoothSlots[slot] = null;
                previous?.Dispose();
                int generation = ++bluetoothStartGeneration[slot];
                _ = Task.Run(() => StartBluetoothWithRetry(slot, device, speakerVolume,
                    speakerCompression, speakerBassBoost, requestedCaptureEndpointId,
                    endpointKind, generation));
            }
        }

        private void StartBluetoothWithRetry(int slot, DualSenseDevice device, byte speakerVolume,
            DualSenseSpeakerCompression speakerCompression, byte speakerBassBoost,
            string requestedCaptureEndpointId, ControllerAudioEndpointKind endpointKind,
            int generation)
        {
            const int attempts = 10;
            Exception lastError = null;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                lock (syncRoot)
                {
                    if (generation != bluetoothStartGeneration[slot])
                    {
                        return;
                    }
                }

                var bluetoothPlayback = new DualSenseBluetoothSpeakerPassthrough(device,
                    speakerVolume, speakerCompression, speakerBassBoost,
                    requestedCaptureEndpointId, endpointKind);
                try
                {
                    bluetoothPlayback.Start();
                    lock (syncRoot)
                    {
                        if (generation != bluetoothStartGeneration[slot])
                        {
                            bluetoothPlayback.Dispose();
                            return;
                        }

                        bluetoothSlots[slot] = bluetoothPlayback;
                    }

                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    bluetoothPlayback.Dispose();
                    Thread.Sleep(500);
                }
            }

            AppLogger.LogToGui($"DualSense Bluetooth speaker passthrough could not start after waiting for the selected audio endpoint: {lastError?.Message}", true);
        }

        private void StopBluetooth(int slot)
        {
            lock (syncRoot)
            {
                DualSenseBluetoothSpeakerPassthrough bluetoothPlayback = bluetoothSlots[slot];
                bluetoothSlots[slot] = null;
                bluetoothStartGeneration[slot]++;
                bluetoothPlayback?.Dispose();
            }
        }

        private void EnsureCaptureStarted(string requestedCaptureEndpointId,
            string speakerEndpointId, ControllerAudioEndpointKind endpointKind)
        {
            requestedCaptureEndpointId ??= string.Empty;
            speakerEndpointId ??= string.Empty;

            if (capture != null && captureEndpointKind == endpointKind &&
                string.Equals(captureEndpointId, requestedCaptureEndpointId, StringComparison.Ordinal))
            {
                return;
            }

            StopCapture();

            MMDevice sourceEndpoint = FindCaptureEndpoint(requestedCaptureEndpointId,
                speakerEndpointId, endpointKind);
            bool expectsControllerEndpoint =
                string.Equals(requestedCaptureEndpointId,
                    AutoDetectGameAudioEndpointId, StringComparison.Ordinal) ||
                (string.IsNullOrEmpty(requestedCaptureEndpointId) &&
                    endpointKind != ControllerAudioEndpointKind.Any);
            if (sourceEndpoint == null && expectsControllerEndpoint)
            {
                AppLogger.LogToGui(
                    "Emulated controller audio endpoint is not available yet. Waiting for the virtual controller to enumerate.",
                    true);
                return;
            }

            if (sourceEndpoint == null && string.IsNullOrEmpty(requestedCaptureEndpointId) && DefaultEndpointMatches(speakerEndpointId))
            {
                AppLogger.LogToGui("DualSense audio passthrough cannot capture from the same endpoint it is playing to.", true);
                return;
            }

            capture = sourceEndpoint != null ? new WasapiLoopbackCapture(sourceEndpoint) : new WasapiLoopbackCapture();
            captureEndpointId = requestedCaptureEndpointId;
            captureEndpointKind = endpointKind;
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
            captureEndpointKind = ControllerAudioEndpointKind.Any;

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

        private static MMDevice FindCaptureEndpoint(string endpointId, string speakerEndpointId,
            ControllerAudioEndpointKind endpointKind)
        {
            bool useSystemDefault = string.Equals(endpointId,
                DefaultSystemAudioEndpointId, StringComparison.Ordinal) ||
                (string.IsNullOrEmpty(endpointId) &&
                endpointKind == ControllerAudioEndpointKind.Any);
            if (useSystemDefault)
            {
                return null;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                bool autoDetect = string.IsNullOrEmpty(endpointId) ||
                    string.Equals(endpointId, AutoDetectGameAudioEndpointId,
                        StringComparison.Ordinal);
                MMDevice endpoint = autoDetect ?
                    FindActiveGameAudioEndpoint(enumerator, null, endpointKind) :
                    enumerator.GetDevice(endpointId);
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
                AppLogger.LogToGui("Controller audio passthrough capture source was not found. Falling back to default audio endpoint.", true);
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
            ControllerAudioEndpointKind kind = ClassifyEndpoint(device);
            if (kind == ControllerAudioEndpointKind.DualSense)
            {
                return true;
            }

            // Older endpoint property stores can omit their USB instance ID.
            // Keep the historic generic fallback, but never mistake a positively
            // identified DS4 endpoint for a physical DualSense speaker.
            return kind == ControllerAudioEndpointKind.Any &&
                GetEndpointIdentity(device).IndexOf("Wireless Controller",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsControllerAudioEndpoint(MMDevice device)
        {
            string identity = GetEndpointIdentity(device);
            return ClassifyEndpointIdentity(identity) != ControllerAudioEndpointKind.Any ||
                identity.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("VIIPER", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static MMDevice FindActiveGameAudioEndpoint(MMDeviceEnumerator enumerator,
            string previousEndpointId = null,
            ControllerAudioEndpointKind preferredKind = ControllerAudioEndpointKind.Any)
        {
            IEnumerable<MMDevice> endpoints = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Where(IsControllerAudioEndpoint);

            if (preferredKind != ControllerAudioEndpointKind.Any)
            {
                endpoints = endpoints.Where(endpoint =>
                {
                    ControllerAudioEndpointKind kind = ClassifyEndpoint(endpoint);
                    return kind == preferredKind || kind == ControllerAudioEndpointKind.Any;
                });
            }

            return endpoints
                .OrderByDescending(endpoint => EndpointScore(endpoint, preferredKind,
                    previousEndpointId))
                .FirstOrDefault();
        }

        internal static ControllerAudioEndpointKind GetEndpointKind(OutContType outputType)
        {
            outputType = outputType.Normalize();
            return outputType switch
            {
                OutContType.ViiperDS4 => ControllerAudioEndpointKind.DualShock4,
                OutContType.ViiperDualSense or OutContType.ViiperDualSenseEdge =>
                    ControllerAudioEndpointKind.DualSense,
                _ => ControllerAudioEndpointKind.Any,
            };
        }

        internal static ControllerAudioEndpointKind ClassifyEndpointIdentity(string identity)
        {
            identity ??= string.Empty;
            string normalized = identity.Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty);

            if (ContainsSonyUsbIdentity(normalized, "05C4") ||
                ContainsSonyUsbIdentity(normalized, "09CC") ||
                identity.IndexOf("DualShock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("DS4", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ControllerAudioEndpointKind.DualShock4;
            }

            if (ContainsSonyUsbIdentity(normalized, "0CE6") ||
                ContainsSonyUsbIdentity(normalized, "0DF2") ||
                identity.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("PS5", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ControllerAudioEndpointKind.DualSense;
            }

            return ControllerAudioEndpointKind.Any;
        }

        private static ControllerAudioEndpointKind ClassifyEndpoint(MMDevice endpoint)
        {
            return ClassifyEndpointIdentity(GetEndpointIdentity(endpoint));
        }

        private static bool ContainsSonyUsbIdentity(string normalizedIdentity,
            string productId)
        {
            return normalizedIdentity.IndexOf("VID054C", StringComparison.OrdinalIgnoreCase) >= 0 &&
                normalizedIdentity.IndexOf("PID" + productId,
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetEndpointIdentity(MMDevice endpoint)
        {
            if (endpoint == null)
            {
                return string.Empty;
            }

            var values = new List<string>
            {
                endpoint.ID ?? string.Empty,
                endpoint.FriendlyName ?? string.Empty,
                endpoint.DeviceFriendlyName ?? string.Empty,
            };

            AddEndpointProperty(values, endpoint, PropertyKeys.PKEY_Device_InstanceId);
            AddEndpointProperty(values, endpoint, PropertyKeys.PKEY_Device_ControllerDeviceId);
            AddEndpointProperty(values, endpoint, PropertyKeys.PKEY_Device_InterfaceKey);
            return string.Join(" ", values);
        }

        private static void AddEndpointProperty(List<string> values, MMDevice endpoint,
            PropertyKey propertyKey)
        {
            try
            {
                object value = endpoint.Properties[propertyKey]?.Value;
                if (value != null)
                {
                    values.Add(value.ToString());
                }
            }
            catch
            {
                // Endpoint property availability differs by Windows audio driver.
            }
        }

        private static int EndpointScore(MMDevice endpoint,
            ControllerAudioEndpointKind preferredKind, string previousEndpointId)
        {
            int score = 0;
            ControllerAudioEndpointKind actualKind = ClassifyEndpoint(endpoint);
            if (preferredKind != ControllerAudioEndpointKind.Any && actualKind == preferredKind)
            {
                score += 100;
            }
            else if (actualKind != ControllerAudioEndpointKind.Any)
            {
                score += 20;
            }

            string identity = GetEndpointIdentity(endpoint);
            if (identity.IndexOf("VIIPER", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 10;
            }

            if (!string.IsNullOrEmpty(previousEndpointId) &&
                (string.Equals(endpoint.ID, previousEndpointId, StringComparison.Ordinal) ||
                EndpointReplaces(endpoint, previousEndpointId)))
            {
                score += 1000;
            }

            return score;
        }

        private static bool EndpointReplaces(MMDevice endpoint, string previousEndpointId)
        {
            string endpointId = endpoint?.ID ?? string.Empty;
            int keyStart = endpointId.LastIndexOf(".{", StringComparison.Ordinal);
            if (keyStart < 0)
            {
                return false;
            }

            try
            {
                string endpointKeyName = endpointId.Substring(keyStart + 1);
                using RegistryKey properties = Registry.LocalMachine.OpenSubKey(
                    RenderEndpointRegistryPath + endpointKeyName + @"\Properties");
                object history = properties?.GetValue(EndpointHistoryValueName);
                if (history is string[] endpointIds)
                {
                    return endpointIds.Any(id => string.Equals(id, previousEndpointId,
                        StringComparison.OrdinalIgnoreCase));
                }

                return history is string id && string.Equals(id, previousEndpointId,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
    }
}
