using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DS4Windows
{
    /// <summary>
    /// Optional NVIDIA Audio Effects denoiser. The proprietary runtime and
    /// model are never bundled or downloaded by DS4Windows; an installed SDK
    /// is discovered at runtime and RNNoise remains the safe fallback.
    /// </summary>
    internal sealed class NvidiaAudioNoiseSuppressor : IDisposable
    {
        private const int Success = 0;
        private const string EffectDenoiser = "denoiser";
        private const string InputSampleRate = "input_sample_rate";
        private const string SamplesPerInputFrame =
            "num_samples_per_input_frame";
        private const string ModelPath = "model_path";

        private static readonly Lazy<RuntimeFiles> discoveredRuntime =
            new(DiscoverRuntime, true);

        private IntPtr library;
        private IntPtr effect;
        private DestroyEffectDelegate destroyEffect;
        private RunDelegate run;
        private readonly float[] output =
            new float[DualSenseMicrophoneProcessor.FrameSize];

        public static bool IsRuntimeInstalled =>
            discoveredRuntime.Value.IsComplete;

        public static string RuntimeAvailability =>
            discoveredRuntime.Value.IsComplete
                ? "NVIDIA Audio Effects runtime detected"
                : "Install the NVIDIA Audio Effects SDK and its 48 kHz denoiser model to enable NVIDIA AI suppression.";

        public NvidiaAudioNoiseSuppressor()
        {
            RuntimeFiles files = discoveredRuntime.Value;
            if (!files.IsComplete)
            {
                throw new InvalidOperationException(RuntimeAvailability);
            }

            try
            {
                library = NativeLibrary.Load(files.LibraryPath);
                var create = LoadDelegate<CreateEffectDelegate>(
                    "NvAFX_CreateEffect");
                var setU32 = LoadDelegate<SetU32Delegate>("NvAFX_SetU32");
                var setString = LoadDelegate<SetStringDelegate>(
                    "NvAFX_SetString");
                var load = LoadDelegate<LoadEffectDelegate>("NvAFX_Load");
                destroyEffect = LoadDelegate<DestroyEffectDelegate>(
                    "NvAFX_DestroyEffect");
                run = LoadDelegate<RunDelegate>("NvAFX_Run");

                Check(create(EffectDenoiser, out effect),
                    "create denoiser");
                Check(setU32(effect, InputSampleRate,
                    DualSenseMicrophoneProcessor.SampleRate),
                    "set input sample rate");
                Check(setU32(effect, SamplesPerInputFrame,
                    DualSenseMicrophoneProcessor.FrameSize),
                    "set input frame size");
                Check(setString(effect, ModelPath, files.ModelPath),
                    "set denoiser model");
                Check(load(effect), "load denoiser");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public unsafe void Process(float[] frame)
        {
            if (effect == IntPtr.Zero || frame == null ||
                frame.Length < DualSenseMicrophoneProcessor.FrameSize)
            {
                throw new InvalidOperationException(
                    "NVIDIA audio denoiser is not ready.");
            }

            fixed (float* inputSamples = frame)
            fixed (float* outputSamples = output)
            {
                IntPtr* inputs = stackalloc IntPtr[1];
                IntPtr* outputs = stackalloc IntPtr[1];
                inputs[0] = (IntPtr)inputSamples;
                outputs[0] = (IntPtr)outputSamples;
                Check(run(effect, (IntPtr)inputs, (IntPtr)outputs,
                    DualSenseMicrophoneProcessor.FrameSize, 1),
                    "process microphone frame");
            }

            Array.Copy(output, frame,
                DualSenseMicrophoneProcessor.FrameSize);
        }

        public void Dispose()
        {
            if (effect != IntPtr.Zero)
            {
                try { destroyEffect?.Invoke(effect); } catch { }
                effect = IntPtr.Zero;
            }
            if (library != IntPtr.Zero)
            {
                NativeLibrary.Free(library);
                library = IntPtr.Zero;
            }
        }

        private T LoadDelegate<T>(string export) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(
                NativeLibrary.GetExport(library, export));

        private static void Check(int status, string operation)
        {
            if (status != Success)
            {
                throw new InvalidOperationException(
                    $"NVIDIA Audio Effects could not {operation} (status {status}).");
            }
        }

        private static RuntimeFiles DiscoverRuntime()
        {
            var roots = new List<string>
            {
                Environment.GetEnvironmentVariable(
                    "NVIDIA_MAXINE_AFX_SDK_DIR"),
                Environment.GetEnvironmentVariable("NVAFX_SDK_DIR"),
                Path.Combine(AppContext.BaseDirectory, "NVIDIA Audio Effects"),
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "NVIDIA Audio Effects SDK"),
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "Maxine"),
            };

            foreach (string root in roots.Where(item =>
                         !string.IsNullOrWhiteSpace(item)).Distinct(
                         StringComparer.OrdinalIgnoreCase))
            {
                RuntimeFiles files = FindRuntime(root);
                if (files.IsComplete) return files;
            }

            return default;
        }

        private static RuntimeFiles FindRuntime(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return default;
                string library = Directory.EnumerateFiles(root,
                    "NVAudioEffects.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                string model = Directory.EnumerateFiles(root,
                    "denoiser_48k.trtpkg", SearchOption.AllDirectories)
                    .FirstOrDefault();
                return new RuntimeFiles(library, model);
            }
            catch
            {
                return default;
            }
        }

        private readonly struct RuntimeFiles
        {
            public RuntimeFiles(string libraryPath, string modelPath)
            {
                LibraryPath = libraryPath ?? string.Empty;
                ModelPath = modelPath ?? string.Empty;
            }

            public string LibraryPath { get; }
            public string ModelPath { get; }
            public bool IsComplete => File.Exists(LibraryPath) &&
                File.Exists(ModelPath);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private delegate int CreateEffectDelegate(
            [MarshalAs(UnmanagedType.LPStr)] string selector,
            out IntPtr effect);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private delegate int SetU32Delegate(IntPtr effect,
            [MarshalAs(UnmanagedType.LPStr)] string parameter, uint value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        private delegate int SetStringDelegate(IntPtr effect,
            [MarshalAs(UnmanagedType.LPStr)] string parameter,
            [MarshalAs(UnmanagedType.LPStr)] string value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LoadEffectDelegate(IntPtr effect);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DestroyEffectDelegate(IntPtr effect);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RunDelegate(IntPtr effect, IntPtr input,
            IntPtr output, uint sampleCount, uint channelCount);
    }
}
