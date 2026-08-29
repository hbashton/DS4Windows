using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

const ushort NintendoVendorId = 0x057E;
const ushort Switch2ProProductId = 0x2069;
const int HidReportBytes = 64;

int count = 256;
int timeoutMilliseconds = 1_000;
string? outputPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--count" when i + 1 < args.Length &&
            int.TryParse(args[++i], out int parsedCount) && parsedCount > 0:
            count = parsedCount;
            break;
        case "--timeout-ms" when i + 1 < args.Length &&
            int.TryParse(args[++i], out int parsedTimeout) && parsedTimeout > 0:
            timeoutMilliseconds = parsedTimeout;
            break;
        case "--output" when i + 1 < args.Length:
            outputPath = Path.GetFullPath(args[++i]);
            break;
        default:
            Console.Error.WriteLine(
                "usage: Switch2UsbReadProbe [--count N] [--timeout-ms N] [--output FILE.jsonl]");
            return 2;
    }
}

List<HidInterface> devices = HidInterface.Enumerate()
    .Where(candidate => candidate.Attributes.VendorId == NintendoVendorId &&
        candidate.Attributes.ProductId == Switch2ProProductId)
    .ToList();
if (devices.Count != 1)
{
    Console.Error.WriteLine(
        $"Expected one Switch 2 Pro HID interface; found {devices.Count}.");
    foreach (HidInterface candidate in devices)
    {
        candidate.Dispose();
    }
    return 3;
}

using HidInterface device = devices[0];
TextWriter writer = outputPath is null ? Console.Out :
    new StreamWriter(new FileStream(outputPath, FileMode.CreateNew,
        FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

try
{
    string pathHash = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(device.Path)));
    WriteJsonLine(writer, new
    {
        schemaVersion = 1,
        recordType = "metadata",
        provenance = "project-owned passive hardware capture",
        evidenceStatus = "hardware-observed",
        redactionManifest = new[]
        {
            "HID device path replaced by SHA-256",
            "serial number not queried",
            "no MAC, key, host, console, or account identifier collected",
        },
        model = "Nintendo Switch 2 Pro Controller",
        transport = "USB HID interrupt IN",
        vendorId = $"0x{device.Attributes.VendorId:X4}",
        productId = $"0x{device.Attributes.ProductId:X4}",
        deviceVersion = $"0x{device.Attributes.VersionNumber:X4}",
        inputReportBytes = HidReportBytes,
        devicePathSha256 = pathHash,
        qpcFrequency = Stopwatch.Frequency,
        timingScope = "single-reader completion order; not a calibrated latency or USB interval measurement",
        requestedReports = count,
        timeoutMilliseconds,
        writesPerformed = false,
    });

    var stopwatch = Stopwatch.StartNew();
    for (int ordinal = 0; ordinal < count; ordinal++)
    {
        byte[] report = GC.AllocateUninitializedArray<byte>(HidReportBytes);
        string status;
        int bytesRead = 0;
        using (var timeout = new CancellationTokenSource(timeoutMilliseconds))
        {
            try
            {
                bytesRead = await device.Stream.ReadAsync(report.AsMemory(),
                    timeout.Token);
                status = bytesRead == HidReportBytes ? "Success" :
                    "UnexpectedLength";
            }
            catch (OperationCanceledException)
            {
                status = "WaitTimedOut";
                Array.Clear(report);
            }
            catch (IOException exception)
            {
                status = $"ReadError:{exception.HResult:X8}";
                Array.Clear(report);
            }
        }

        WriteJsonLine(writer, new
        {
            schemaVersion = 1,
            recordType = "input",
            ordinal,
            deviceGeneration = 1,
            hostQpcDelta = stopwatch.ElapsedTicks,
            reportId = $"0x{report[0]:X2}",
            status,
            bytesRead,
            exactBytes = Convert.ToHexString(report),
        });
        if (status != "Success")
        {
            writer.Flush();
            Console.Error.WriteLine($"Read stopped at record {ordinal}: {status}.");
            return 5;
        }
    }

    writer.Flush();
    return 0;
}
finally
{
    if (outputPath is not null)
    {
        writer.Dispose();
    }
}

static void WriteJsonLine(TextWriter writer, object value) =>
    writer.WriteLine(JsonSerializer.Serialize(value));

internal sealed class HidInterface : IDisposable
{
    private const int ReportBytes = 64;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private HidInterface(string path, SafeFileHandle handle,
        HiddAttributes attributes)
    {
        Path = path;
        Handle = handle;
        Attributes = attributes;
        Stream = new FileStream(handle, FileAccess.Read, ReportBytes,
            isAsync: true);
    }

    public string Path { get; }
    public HiddAttributes Attributes { get; }
    private SafeFileHandle Handle { get; }
    public FileStream Stream { get; }

    public static IEnumerable<HidInterface> Enumerate()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero,
            IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == InvalidHandleValue)
        {
            throw new InvalidOperationException(
                $"SetupDiGetClassDevs failed: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>(),
                };
                if (!SetupDiEnumDeviceInterfaces(deviceSet, IntPtr.Zero,
                    ref hidGuid, index, ref interfaceData))
                {
                    const int NoMoreItems = 259;
                    int error = Marshal.GetLastWin32Error();
                    if (error == NoMoreItems)
                    {
                        yield break;
                    }
                    throw new InvalidOperationException(
                        $"SetupDiEnumDeviceInterfaces failed: {error}.");
                }

                _ = SetupDiGetDeviceInterfaceDetail(deviceSet,
                    ref interfaceData, IntPtr.Zero, 0, out uint required,
                    IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(deviceSet,
                        ref interfaceData, detail, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    SafeFileHandle handle = CreateFile(path, GenericRead,
                        FileShareRead | FileShareWrite, IntPtr.Zero,
                        OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    var attributes = new HiddAttributes
                    {
                        Size = Marshal.SizeOf<HiddAttributes>(),
                    };
                    if (!HidD_GetAttributes(handle, ref attributes))
                    {
                        handle.Dispose();
                        continue;
                    }

                    yield return new HidInterface(path, handle, attributes);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    public void Dispose()
    {
        Stream.Dispose();
        Handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle,
        ref HiddAttributes attributes);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid,
        IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceSet,
        IntPtr deviceInfo, ref Guid interfaceClassGuid, uint memberIndex,
        ref SpDeviceInterfaceData interfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceSet, ref SpDeviceInterfaceData interfaceData,
        IntPtr detailData, uint detailSize, out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName,
        uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
