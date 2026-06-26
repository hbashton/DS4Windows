param(
    [int]$Seconds = 8,
    [int]$DeviceIndex = -1,
    [ValidateSet("Combined36", "Standard31", "Both", "ReadOnly")]
    [string]$Mode = "Combined36",
    [string]$LogPath = "$env:USERPROFILE\Desktop\dualsense-bt-mic-probe.log",
    [switch]$RawHex
)

Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"

$source = @'
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DS4WindowsDiagnostics
{
    public static class DualSenseBtMicProbe
    {
        private const int DigcfPresent = 0x00000002;
        private const int DigcfDeviceInterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const ushort SonyVid = 0x054c;
        private const ushort DualSensePid = 0x0ce6;
        private const ushort DualSenseEdgePid = 0x0df2;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private sealed class DeviceInfo
        {
            public string Path;
            public ushort VendorId;
            public ushort ProductId;
            public ushort InputReportLength;
            public ushort OutputReportLength;
            public ushort FeatureReportLength;
        }

        private sealed class Counters
        {
            public long Total;
            public long Report31;
            public long HidBthMic;
            public long DirectPrefixMic;
            public long Other;
            public long ReadErrors;
            public long Writes;
            public long WriteErrors;
            public byte LastFlags1;
            public byte LastFlags2;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int bytesToWrite, out int bytesWritten, IntPtr overlapped);

        public static int Run(int seconds, int deviceIndex, string mode, string logPath, bool rawHex)
        {
            if (seconds < 1)
            {
                seconds = 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
            using (StreamWriter log = new StreamWriter(logPath, true, Encoding.UTF8))
            {
                Log(log, "============================================================");
                Log(log, "DualSense Bluetooth microphone raw probe start");
                Log(log, "utc=" + DateTime.UtcNow.ToString("O") + " seconds=" + seconds + " mode=" + mode + " rawHex=" + rawHex);
                Log(log, "Close DS4Windows before using this probe if the controller HID path is already open.");
                Log(log, "Note: references report mic enable can be sticky until the controller reconnects.");

                List<DeviceInfo> devices = EnumerateDualSenseDevices(log);
                if (devices.Count == 0)
                {
                    Log(log, "No Sony DualSense/DualSense Edge HID devices were found.");
                    return 2;
                }

                for (int i = 0; i < devices.Count; i++)
                {
                    DeviceInfo d = devices[i];
                    Log(log, "[" + i + "] vid=0x" + d.VendorId.ToString("X4") +
                        " pid=0x" + d.ProductId.ToString("X4") +
                        " in=" + d.InputReportLength +
                        " out=" + d.OutputReportLength +
                        " feature=" + d.FeatureReportLength +
                        " path=" + d.Path);
                }

                int first = deviceIndex >= 0 ? deviceIndex : 0;
                int last = deviceIndex >= 0 ? deviceIndex : devices.Count - 1;
                if (first < 0 || first >= devices.Count)
                {
                    Log(log, "Requested device index " + deviceIndex + " is out of range.");
                    return 3;
                }

                int result = 0;
                for (int i = first; i <= last; i++)
                {
                    result = Math.Max(result, ProbeDevice(devices[i], i, seconds, mode, rawHex, log));
                }

                Log(log, "DualSense Bluetooth microphone raw probe end");
                return result;
            }
        }

        private static List<DeviceInfo> EnumerateDualSenseDevices(StreamWriter log)
        {
            List<DeviceInfo> result = new List<DeviceInfo>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == IntPtr.Zero || set.ToInt64() == -1)
            {
                Log(log, "SetupDiGetClassDevs failed win32=" + Marshal.GetLastWin32Error());
                return result;
            }

            try
            {
                for (int index = 0; ; index++)
                {
                    SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
                    {
                        break;
                    }

                    int required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0)
                    {
                        continue;
                    }

                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero))
                        {
                            continue;
                        }

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path))
                        {
                            continue;
                        }

                        DeviceInfo info = ReadDeviceInfo(path);
                        if (info != null &&
                            info.VendorId == SonyVid &&
                            (info.ProductId == DualSensePid || info.ProductId == DualSenseEdgePid))
                        {
                            result.Add(info);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return result;
        }

        private static DeviceInfo ReadDeviceInfo(string path)
        {
            using (SafeFileHandle handle = OpenDeviceForInfo(path))
            {
                if (handle.IsInvalid)
                {
                    return null;
                }

                HIDD_ATTRIBUTES attributes = new HIDD_ATTRIBUTES();
                attributes.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                if (!HidD_GetAttributes(handle, ref attributes))
                {
                    return null;
                }

                DeviceInfo info = new DeviceInfo();
                info.Path = path;
                info.VendorId = attributes.VendorID;
                info.ProductId = attributes.ProductID;

                IntPtr preparsed;
                if (HidD_GetPreparsedData(handle, out preparsed))
                {
                    try
                    {
                        HIDP_CAPS caps;
                        if (HidP_GetCaps(preparsed, out caps) == 0)
                        {
                            info.InputReportLength = caps.InputReportByteLength;
                            info.OutputReportLength = caps.OutputReportByteLength;
                            info.FeatureReportLength = caps.FeatureReportByteLength;
                        }
                    }
                    finally
                    {
                        HidD_FreePreparsedData(preparsed);
                    }
                }

                return info;
            }
        }

        private static int ProbeDevice(DeviceInfo device, int index, int seconds, string mode, bool rawHex, StreamWriter log)
        {
            Log(log, "---- probe device " + index + " ----");
            using (SafeFileHandle handle = OpenDevice(device.Path))
            {
                if (handle.IsInvalid)
                {
                    Log(log, "CreateFile failed win32=" + Marshal.GetLastWin32Error() + " path=" + device.Path);
                    return 4;
                }

                int inputLength = Math.Max(78, (int)device.InputReportLength);
                Counters counters = new Counters();
                bool stop = false;
                Thread reader = new Thread(delegate()
                {
                    ReadLoop(handle, inputLength, counters, rawHex, log, ref stop);
                });
                reader.IsBackground = true;
                reader.Start();

                DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                int sequence = 0;
                while (DateTime.UtcNow < end)
                {
                    if (mode == "Combined36" || mode == "Both")
                    {
                        WriteReport(handle, BuildCombined36MicEnable(sequence++), "combined 0x36 mic enable", counters, log);
                    }

                    if (mode == "Standard31" || mode == "Both")
                    {
                        WriteReport(handle, BuildStandard31MicState(true), "standard 0x31 mic enable", counters, log);
                    }

                    Thread.Sleep(mode == "ReadOnly" ? 1000 : 250);
                }

                if (mode == "Standard31" || mode == "Both")
                {
                    WriteReport(handle, BuildStandard31MicState(false), "standard 0x31 mic disable", counters, log);
                }

                if (mode == "Combined36" || mode == "Both")
                {
                    WriteReport(handle, BuildCombined36MicDisable(sequence++), "combined 0x36 mic disable hint", counters, log);
                }

                stop = true;
                try { handle.Close(); } catch { }
                if (!reader.Join(750))
                {
                    Log(log, "Reader did not stop before timeout; handle was closed to unblock it.");
                }

                Log(log, "summary total=" + counters.Total +
                    " report31=" + counters.Report31 +
                    " hidbthMicFlagByte1=" + counters.HidBthMic +
                    " directMicFlagByte2=" + counters.DirectPrefixMic +
                    " other=" + counters.Other +
                    " readErrors=" + counters.ReadErrors +
                    " writes=" + counters.Writes +
                    " writeErrors=" + counters.WriteErrors +
                    " lastByte1=0x" + counters.LastFlags1.ToString("X2") +
                    " lastByte2=0x" + counters.LastFlags2.ToString("X2"));

                return counters.HidBthMic > 0 || counters.DirectPrefixMic > 0 ? 0 : 1;
            }
        }

        private static SafeFileHandle OpenDevice(string path)
        {
            return CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        }

        private static SafeFileHandle OpenDeviceForInfo(string path)
        {
            return CreateFile(path, 0, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        }

        private static void ReadLoop(SafeFileHandle handle, int inputLength, Counters counters, bool rawHex, StreamWriter log, ref bool stop)
        {
            byte[] buffer = new byte[inputLength];
            using (FileStream stream = new FileStream(handle, FileAccess.Read, inputLength, false))
            {
                while (!stop)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex)
                    {
                        counters.ReadErrors++;
                        if (!stop)
                        {
                            Log(log, "read error " + ex.GetType().Name + ": " + ex.Message);
                        }
                        return;
                    }

                    if (read <= 0)
                    {
                        continue;
                    }

                    counters.Total++;
                    bool report31 = buffer[0] == 0x31;
                    bool hidBthMic = report31 && read >= 74 && (buffer[1] & 0x02) != 0;
                    bool directPrefixMic = report31 && read >= 75 && (buffer[2] & 0x02) != 0;
                    counters.LastFlags1 = read > 1 ? buffer[1] : (byte)0;
                    counters.LastFlags2 = read > 2 ? buffer[2] : (byte)0;
                    if (report31) counters.Report31++;
                    if (hidBthMic) counters.HidBthMic++;
                    if (directPrefixMic) counters.DirectPrefixMic++;
                    if (!report31) counters.Other++;

                    if (counters.Total <= 40 ||
                        hidBthMic ||
                        directPrefixMic ||
                        counters.Total % 100 == 0)
                    {
                        string label = hidBthMic ? "MIC_HIDBTH" :
                            (directPrefixMic ? "MIC_DIRECT_PREFIX" :
                            (report31 ? "INPUT_31" : "OTHER"));
                        string line = "read " + counters.Total +
                            " len=" + read +
                            " kind=" + label +
                            " b0=0x" + buffer[0].ToString("X2") +
                            " b1=0x" + counters.LastFlags1.ToString("X2") +
                            " b2=0x" + counters.LastFlags2.ToString("X2") +
                            " first16=" + Hex(buffer, Math.Min(16, read));
                        if (rawHex)
                        {
                            line += " raw=" + Hex(buffer, read);
                        }

                        Log(log, line);
                    }
                }
            }
        }

        private static void WriteReport(SafeFileHandle handle, byte[] report, string description, Counters counters, StreamWriter log)
        {
            int written;
            bool ok = WriteFile(handle, report, report.Length, out written, IntPtr.Zero);
            if (ok && written == report.Length)
            {
                counters.Writes++;
            }
            else
            {
                counters.WriteErrors++;
                Log(log, "write failed description=\"" + description + "\" ok=" + ok +
                    " written=" + written + "/" + report.Length +
                    " win32=" + Marshal.GetLastWin32Error());
            }
        }

        private static byte[] BuildStandard31MicState(bool enabled)
        {
            byte[] report = new byte[78];
            report[0] = 0x31;
            report[1] = 0x02;
            report[2] = 0x40;
            report[3] = 0x03;
            report[8] = enabled ? (byte)0x40 : (byte)0x00;
            report[10] = 0x00;
            report[11] = enabled ? (byte)0x00 : (byte)0x10;
            uint crc = Crc32WithPrefix(0xA2, report, report.Length - 4);
            WriteUInt32Le(report, report.Length - 4, crc);
            return report;
        }

        private static byte[] BuildCombined36MicEnable(int sequence)
        {
            return BuildCombined36(sequence, true);
        }

        private static byte[] BuildCombined36MicDisable(int sequence)
        {
            return BuildCombined36(sequence, false);
        }

        private static byte[] BuildCombined36(int sequence, bool enabled)
        {
            byte[] report = new byte[398];
            report[0] = 0x36;
            report[1] = (byte)((sequence & 0x0F) << 4);
            report[2] = 0x91;
            report[3] = 0x07;
            report[4] = enabled ? (byte)0xFF : (byte)0xFE;
            report[5] = 0x40;
            report[6] = 0x40;
            report[7] = 0x40;
            report[8] = 0x40;
            report[9] = 0x40;
            report[10] = (byte)(sequence & 0xFF);

            report[11] = 0x90;
            report[12] = 63;
            report[13] = 0x40; // mic volume flag in embedded state block
            report[14] = 0x03; // mute LED + power save control
            report[19] = enabled ? (byte)0x40 : (byte)0x00;
            report[22] = enabled ? (byte)0x00 : (byte)0x10;

            report[76] = 0x92;
            report[77] = 64;
            uint crc = DualSenseBluetoothCrc32(report, report.Length - 4);
            WriteUInt32Le(report, report.Length - 4, crc);
            return report;
        }

        private static uint Crc32WithPrefix(byte prefix, byte[] data, int length)
        {
            uint crc = 0xFFFFFFFFu;
            crc = Crc32Step(crc, prefix);
            for (int i = 0; i < length; i++)
            {
                crc = Crc32Step(crc, data[i]);
            }

            return ~crc;
        }

        private static uint DualSenseBluetoothCrc32(byte[] data, int length)
        {
            uint crc = ~0xEADA2D49u;
            for (int i = 0; i < length; i++)
            {
                crc = Crc32Step(crc, data[i]);
            }

            return ~crc;
        }

        private static uint Crc32Step(uint crc, byte value)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }

            return crc;
        }

        private static void WriteUInt32Le(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static string Hex(byte[] data, int length)
        {
            StringBuilder builder = new StringBuilder(length * 3);
            for (int i = 0; i < length; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(data[i].ToString("X2"));
            }

            return builder.ToString();
        }

        private static void Log(StreamWriter log, string message)
        {
            log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + message);
            log.Flush();
            Console.WriteLine(message);
        }
    }
}
'@

try {
    [DS4WindowsDiagnostics.DualSenseBtMicProbe] | Out-Null
} catch {
    Add-Type -TypeDefinition $source -Language CSharp
}

[int][DS4WindowsDiagnostics.DualSenseBtMicProbe]::Run($Seconds, $DeviceIndex, $Mode, $LogPath, [bool]$RawHex)
