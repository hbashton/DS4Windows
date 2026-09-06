using System.Runtime.InteropServices;

namespace DS4WindowsTests;

// Explicit, test-process-only diagnostic. The native callback recorder is a
// Desktop lab tool, not a production dependency. No profiler loaded by default.
internal static class NativeAllocationMeasurement
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BeginCallback();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint EndCallback(long bytes);
    private static readonly BeginCallback begin;
    private static readonly EndCallback end;

    static NativeAllocationMeasurement()
    {
        string path = Environment.GetEnvironmentVariable("DS4W_ALLOCATION_PROFILER_PATH");
        if (string.IsNullOrEmpty(path)) return;
        nint library = NativeLibrary.Load(path);
        begin = Marshal.GetDelegateForFunctionPointer<BeginCallback>(NativeLibrary.GetExport(library, "BeginAllocationMeasurement"));
        end = Marshal.GetDelegateForFunctionPointer<EndCallback>(NativeLibrary.GetExport(library, "EndAllocationMeasurement"));
    }

    internal static bool IsEnabled => begin != null;
    internal static void Begin()
    {
        if (begin != null && begin() != 1)
            throw new InvalidOperationException("The requested allocation profiler did not initialize.");
    }
    internal static uint End(long bytes) => end?.Invoke(bytes) ?? 0;
}
