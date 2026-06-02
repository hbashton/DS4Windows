using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Linq;

namespace DS4Windows
{
    public sealed class DualSenseMicrophonePassthrough : IDisposable
    {
        private readonly object syncRoot = new object();
        private WasapiCapture capture;
        private WasapiOut output;
        private BufferedWaveProvider provider;
        private string captureEndpointId = string.Empty;
        private string outputEndpointId = string.Empty;
        private byte volume;

        public void Start(byte microphoneVolume, string requestedCaptureEndpointId, string requestedOutputEndpointId)
        {
            requestedCaptureEndpointId ??= string.Empty;
            requestedOutputEndpointId ??= string.Empty;

            lock (syncRoot)
            {
                if (capture != null &&
                    string.Equals(captureEndpointId, requestedCaptureEndpointId, StringComparison.Ordinal) &&
                    string.Equals(outputEndpointId, requestedOutputEndpointId, StringComparison.Ordinal))
                {
                    volume = microphoneVolume;
                    return;
                }

                Stop();

                MMDevice micEndpoint = FindMicrophoneEndpoint(requestedCaptureEndpointId);
                if (micEndpoint == null)
                {
                    AppLogger.LogToGui("DualSense microphone passthrough could not find a controller microphone endpoint.", true);
                    return;
                }

                MMDevice targetEndpoint = FindRenderEndpoint(requestedOutputEndpointId);
                if (targetEndpoint == null)
                {
                    AppLogger.LogToGui("DualSense microphone passthrough needs a virtual audio render endpoint, such as VB-CABLE Input or Voicemeeter Input.", true);
                    return;
                }

                try
                {
                    capture = new WasapiCapture(micEndpoint);
                    provider = new BufferedWaveProvider(capture.WaveFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(250),
                        DiscardOnBufferOverflow = true,
                    };

                    output = new WasapiOut(targetEndpoint, AudioClientShareMode.Shared, true, 40);
                    output.Init(provider);
                    capture.DataAvailable += Capture_DataAvailable;
                    capture.RecordingStopped += Capture_RecordingStopped;

                    volume = microphoneVolume;
                    captureEndpointId = requestedCaptureEndpointId;
                    outputEndpointId = requestedOutputEndpointId;
                    output.Play();
                    capture.StartRecording();

                    AppLogger.LogToGui($"DualSense microphone passthrough started: {micEndpoint.FriendlyName} -> {targetEndpoint.FriendlyName}", false);
                }
                catch (Exception ex)
                {
                    AppLogger.LogToGui($"DualSense microphone passthrough failed to start: {ex.Message}", true);
                    Stop();
                }
            }
        }

        public void Stop()
        {
            lock (syncRoot)
            {
                WasapiCapture oldCapture = capture;
                WasapiOut oldOutput = output;
                capture = null;
                output = null;
                provider = null;
                captureEndpointId = string.Empty;
                outputEndpointId = string.Empty;

                if (oldCapture != null)
                {
                    oldCapture.DataAvailable -= Capture_DataAvailable;
                    oldCapture.RecordingStopped -= Capture_RecordingStopped;
                    try { oldCapture.StopRecording(); } catch { }
                    oldCapture.Dispose();
                }

                if (oldOutput != null)
                {
                    try { oldOutput.Stop(); } catch { }
                    oldOutput.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            lock (syncRoot)
            {
                WasapiCapture activeCapture = capture;
                if (provider == null || activeCapture == null)
                {
                    return;
                }

                if (volume >= 250)
                {
                    provider.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    return;
                }

                byte[] adjusted = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, adjusted, 0, e.BytesRecorded);
                ApplyVolume(adjusted, e.BytesRecorded, activeCapture.WaveFormat, volume / 255.0f);
                provider.AddSamples(adjusted, 0, adjusted.Length);
            }
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                AppLogger.LogToGui($"DualSense microphone passthrough capture stopped: {e.Exception.Message}", true);
            }
        }

        private static MMDevice FindMicrophoneEndpoint(string endpointId)
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(endpointId))
            {
                try
                {
                    MMDevice endpoint = enumerator.GetDevice(endpointId);
                    if (endpoint.State == DeviceState.Active)
                    {
                        return endpoint;
                    }
                }
                catch
                {
                    AppLogger.LogToGui("Selected DualSense microphone endpoint was not found. Falling back to auto-detect.", true);
                }
            }

            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(DualSenseAudioPassthrough.IsDualSenseEndpoint);
        }

        private static MMDevice FindRenderEndpoint(string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                return null;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice endpoint = enumerator.GetDevice(endpointId);
                return endpoint.State == DeviceState.Active ? endpoint : null;
            }
            catch
            {
                AppLogger.LogToGui("Selected microphone passthrough output endpoint was not found.", true);
                return null;
            }
        }

        private static void ApplyVolume(byte[] buffer, int byteCount, WaveFormat format, float gain)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                for (int offset = 0; offset + sizeof(float) <= byteCount; offset += sizeof(float))
                {
                    float sample = Math.Clamp(BitConverter.ToSingle(buffer, offset) * gain, -1.0f, 1.0f);
                    Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, buffer, offset, sizeof(float));
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                for (int offset = 0; offset + sizeof(short) <= byteCount; offset += sizeof(short))
                {
                    short sample = (short)Math.Clamp(BitConverter.ToInt16(buffer, offset) * gain,
                        (float)short.MinValue, short.MaxValue);
                    Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, buffer, offset, sizeof(short));
                }
            }
        }
    }
}
